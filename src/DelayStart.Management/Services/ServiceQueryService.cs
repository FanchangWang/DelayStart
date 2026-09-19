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
            var (displayName, startType, delayedAuto, binaryPath) = ReadRegistryInfo(name);
            results.Add(new ServiceInfo(
                name,
                string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                startType,
                DescribeStatus(states.GetValueOrDefault(name)),
                delayedAuto,
                isDriver,
                binaryPath));
        }

        return results
            .OrderBy(static info => info.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static (string DisplayName, string StartType, bool DelayedAuto, string BinaryPath) ReadRegistryInfo(string serviceName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is null)
            {
                return (string.Empty, "未知", false, string.Empty);
            }

            var displayName = key.GetValue("DisplayName") as string ?? string.Empty;
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

            return (displayName, startType, delayed && start == 2, binaryPath);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return (string.Empty, "未知", false, string.Empty);
        }
    }

    /// <summary>把 <c>ImagePath</c> 规整成"去引号的映像路径"（展开环境变量、剥掉参数）。</summary>
    /// <param name="raw">注册表原值（可能是 <c>REG_EXPAND_SZ</c>、带引号、带参数）。</param>
    /// <returns>形如 <c>C:\Windows\system32\svchost.exe</c> 的路径；不可解析为空串。</returns>
    private static string ExpandImagePath(object? raw)
    {
        if (raw is not string value || value.Trim().Length == 0)
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value).Trim();

        // ImagePath 常见三种形态："C:\x\y.exe" -arg / C:\x\y.exe / C:\x\y.exe -arg。
        // 有引号按引号取；没引号取第一个空白前段（目录含空格的服务几乎必带引号）。
        if (expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            return end > 0 ? expanded[1..end] : expanded.Trim('"');
        }

        var space = expanded.IndexOf(' ');
        return space > 0 ? expanded[..space] : expanded;
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
