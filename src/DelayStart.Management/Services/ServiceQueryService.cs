using System.Runtime.InteropServices;

using DelayStart.Core.Abstractions;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 系统服务 / 驱动的只读查询（FR-7.1 / FR-7.3，D30 推后项）。
/// </summary>
/// <remarks>
/// <para>
/// 走 SCM（Service Control Manager）API + 注册表读值，**零 COM、零 WMI**。
/// 只读：本类不做任何 <c>ChangeServiceConfig</c> —— FR-7.2 的"延迟自动启动"切换
/// 不在本期范围（与 D36 同批推后）。
/// </para>
/// <para>
/// 启动类型与"延迟自动"从注册表 <c>HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;</c>
/// 的 <c>Start</c> / <c>DelayedAutostart</c> 读 —— 比逐个 <c>QueryServiceConfig</c> 快一个数量级。
/// 运行状态从 <c>EnumServicesStatusEx</c> 的快照取。
/// </para>
/// </remarks>
public sealed partial class ServiceQueryService
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceStateAll = 0x00000003;
    private const uint ScEnumProcessInfo = 0;
    private const uint ServiceKernelDriver = 0x00000001;
    private const uint ServiceFileSystemDriver = 0x00000002;

    private const uint ServiceRunning = 0x00000004;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServicePausePending = 0x00000006;
    private const uint ServicePaused = 0x00000007;

    private readonly ILogSink _log;

    /// <summary>构造服务查询服务。</summary>
    /// <param name="log">日志接收端。</param>
    public ServiceQueryService(ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    /// <summary>查询全部 Win32 服务（含延迟自动标注）。</summary>
    /// <returns>按显示名排序的服务列表；查询失败时返回空列表并记日志（FR-1.4 同一原则）。</returns>
    public IReadOnlyList<ServiceInfo> QueryWin32Services() => Query(ServiceWin32, isDriver: false);

    /// <summary>查询全部内核 / 文件系统驱动（FR-7.3，只读展示）。</summary>
    /// <returns>按显示名排序的驱动列表。</returns>
    public IReadOnlyList<ServiceInfo> QueryDrivers() => Query(ServiceDriver, isDriver: true);

    private List<ServiceInfo> Query(uint serviceType, bool isDriver)
    {
        var names = new List<string>(256);
        var states = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        var manager = OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (manager == 0)
        {
            return [];
        }

        try
        {
            var needed = 0u;
            var returned = 0u;
            var resume = 0u;
            var bufferSize = 64u * 1024u;
            var buffer = Marshal.AllocHGlobal((nint)bufferSize);

            try
            {
                while (!EnumServicesStatusExW(
                           manager,
                           ScEnumProcessInfo,
                           serviceType,
                           ServiceStateAll,
                           buffer,
                           bufferSize,
                           out needed,
                           out returned,
                           out resume,
                           0)
                       && Marshal.GetLastWin32Error() == 234 /* ERROR_MORE_DATA */)
                {
                    Marshal.FreeHGlobal(buffer);
                    bufferSize = needed;
                    buffer = Marshal.AllocHGlobal((nint)bufferSize);
                }

                // ENUM_SERVICE_STATUS_PROCESS（x64）：LPWSTR lpServiceName + LPWSTR lpDisplayName
                // + SERVICE_STATUS_PROCESS。⚠️ 后者是 **9 个 DWORD**（Type/CurrentState/
                // ControlsAccepted/Win32ExitCode/ServiceSpecificExitCode/CheckPoint/WaitHint/
                // ProcessId/ServiceFlags = 36 字节），且结构体按 8 字节对齐 →
                // sizeof = 8 + 8 + 36 向上取整到 8 的倍数 = **56**。
                // 曾经写成 7 * 4 = 44：每条漂移 12 字节，前几条正常、往后指针读到垃圾值，
                // 在 PtrToStringUni 上直接 AV —— 而 AV 是损坏状态异常，外层 catch 拦不住。
                // ⚠️ 这个 56 是 **64 位布局**（两个 8 字节指针 + 36 字节 SERVICE_STATUS_PROCESS，
                //    向上对齐到 8 的倍数）。32 位进程下两个指针各 4 字节 → 应为 4+4+36 = 44。
                //    D60 决策：本项目只产出 win-x64 / win-arm64 安装包，故保持 56；
                //    🔴 若日后要支持 win-x86，**必须先改这里**（换 Marshal.SizeOf<结构体>()
                //    或按 IntPtr.Size 分支），否则每条漂移 12 字节。
                const int entrySize = 8 + 8 + 9 * 4 + 4; // 56 = 两个指针 + 36 字节 + 尾部对齐填充

                for (var index = 0; index < returned; index++)
                {
                    var entry = buffer + index * entrySize;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry)) ?? string.Empty;
                    var state = Marshal.ReadInt32(entry, 8 + 8 + 1 * 4); // dwCurrentState 是子结构第 2 个 DWORD
                    names.Add(name);
                    states[name] = (uint)state;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"枚举{(isDriver ? "驱动" : "服务")}失败（只读展示降级为空列表）。");
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }

        var results = new List<ServiceInfo>(names.Count);
        foreach (var name in names)
        {
            var (displayName, description, startType, delayedAuto, binaryPath) = ReadRegistryInfo(name);
            var state = states.GetValueOrDefault(name);

            results.Add(new ServiceInfo(
                name,
                string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                description,
                startType,
                DescribeStatus(state),
                state == ServiceRunning,
                delayedAuto,
                isDriver,
                binaryPath,
                ServiceInfo.ComputeIsBuiltin(binaryPath)));
        }

        return results
            .OrderBy(static info => info.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static (string DisplayName, string Description, string StartType, bool DelayedAuto, string BinaryPath) ReadRegistryInfo(string serviceName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is null)
            {
                return (string.Empty, string.Empty, "未知", false, string.Empty);
            }

            // 批复 11：DisplayName / Description 大多是 MUI 间接字符串
            // （@%SystemRoot%\System32\xxx.dll,-nnn），直接显示就是一串 @% 开头的乱码。
            // SHLoadIndirectString 一个 API 同时解析 "@路径,-资源ID" 与 "@{包?资源}" 两种格式。
            var displayName = ResolveMuiString(key.GetValue("DisplayName") as string);
            var description = ResolveMuiString(key.GetValue("Description") as string);
            var start = key.GetValue("Start") is int value ? value : -1;
            var delayed = key.GetValue("DelayedAutostart") is int flag && flag == 1;
            var binaryPath = ExpandImagePath(key.GetValue(
                "ImagePath",
                null,
                Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames));

            var startType = start switch
            {
                0 => "系统引导",
                1 => "系统启动",
                2 => delayed ? "自动（延迟）" : "自动",
                3 => "手动",
                4 => "已禁用",
                _ => "未知",
            };

            return (displayName, description, startType, delayed && start == 2, binaryPath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return (string.Empty, string.Empty, "未知", false, string.Empty);
        }
    }

    /// <summary>把 MUI 间接字符串解析成可读文本；不是间接引用或解析失败时原样返回。</summary>
    /// <remarks>
    /// 2026-09-20 批复 1：解析管道搬进 <see cref="Interop.MuiString"/> ——
    /// SH 直解 → 环境变量展开重试（%ProgramFiles% 等 SH 不认）→ LoadLibraryEx+LoadString
    /// 直读 → INF 分号回退。此前残留 <c>@%</c> 开头的正是前一类。
    /// </remarks>
    private static string ResolveMuiString(string? raw) => Interop.MuiString.Resolve(raw);

    /// <summary>把 <c>ImagePath</c> 规整成"去引号的映像路径"（展开环境变量、剥掉参数）。</summary>
    /// <param name="raw">注册表原值（可能是 <c>REG_EXPAND_SZ</c>、带引号、带参数）。</param>
    /// <returns>形如 <c>C:\Windows\system32\svchost.exe</c> 的路径；不可解析为空串。</returns>
    /// <remarks>
    /// <para>
    /// 2026-09-20 批复 5：驱动的 ImagePath 是内核写法，<see cref="Environment.ExpandEnvironmentVariables"/>
    /// 展不开 —— <c>\SystemRoot\System32\drivers\x.sys</c>（= %SystemRoot%\…）、
    /// <c>\??\C:\…</c>（NT 对象管理器前缀）、相对路径 <c>System32\drivers\x.sys</c>。
    /// </para>
    /// <para>
    /// 2026-09-20 批复 1：先剥引号 / 按扩展名截参数、**再**做前缀归一化 —— 此前顺序反了，
    /// 带引号的全路径 <c>"C:\Program Files\x.exe" -k</c> 被当相对路径，错误拼上 C:\Windows\ 前缀。
    /// 无引号带空格的路径靠扩展名（.exe/.sys/.dll）截参数，不再被第一个空格切碎。
    /// </para>
    /// </remarks>
    private static string ExpandImagePath(object? raw)
    {
        if (raw is not string value || value.Trim().Length == 0)
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value).Trim();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // ① 先取"纯路径段"：带引号按引号取；无引号按扩展名截参数。
        string segment;
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            segment = end > 0 ? expanded[1..end] : expanded.Trim('"');
        }
        else if (FindImageExtension(expanded) is { } extension)
        {
            segment = expanded[..(extension + 4)];
        }
        else
        {
            var space = expanded.IndexOf(' ');
            segment = space > 0 ? expanded[..space] : expanded;
        }

        // ② 再归一化内核路径写法（\SystemRoot / \??\ / 相对路径）。
        if (segment.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            segment = windows + segment[@"\SystemRoot".Length..];
        }
        else if (segment.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            segment = segment[4..];
        }
        else if (segment.StartsWith(@"\?", StringComparison.OrdinalIgnoreCase))
        {
            segment = segment[3..];
        }
        else if (windows.Length > 0 && !Path.IsPathRooted(segment))
        {
            segment = Path.Combine(windows, segment);
        }

        return segment;
    }

    /// <summary>在路径里找映像扩展名（.exe / .sys / .dll），返回扩展名起点；找不到为 <see langword="null"/>。</summary>
    private static int? FindImageExtension(string path)
    {
        int? found = null;

        var index = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (index > 0)
        {
            found = index;
        }

        index = path.IndexOf(".sys", StringComparison.OrdinalIgnoreCase);
        if (index > 0 && (found is null || index < found))
        {
            found = index;
        }

        index = path.IndexOf(".dll", StringComparison.OrdinalIgnoreCase);
        if (index > 0 && (found is null || index < found))
        {
            found = index;
        }

        return found;
    }

    private static string DescribeStatus(uint state) => state switch
    {
        ServiceRunning => "正在运行",
        ServiceStopPending => "正在停止",
        ServicePausePending => "正在暂停",
        ServicePaused => "已暂停",
        _ => "已停止",
    };

    private static readonly uint ServiceWin32 = 0x00000030; // PROCESS + INTERACTIVE
    private static readonly uint ServiceDriver = 0x0000000B; // KERNEL + FILE_SYSTEM

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(
        nint manager,
        uint infoLevel,
        uint serviceType,
        uint serviceState,
        nint buffer,
        uint bufferSize,
        out uint bytesNeeded,
        out uint servicesReturned,
        out uint resumeHandle,
        nint groupName);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
