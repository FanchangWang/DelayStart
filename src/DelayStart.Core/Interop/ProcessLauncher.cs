using System.Diagnostics;
using System.Runtime.InteropServices;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Models;

namespace DelayStart.Core.Interop;

/// <summary>
/// <see cref="IProcessLauncher"/> 的真机实现（机制 6 / D27=A，Phase 4 落地）。
/// </summary>
/// <remarks>
/// <para>
/// 三条启动路径，按条目配置分流（2026-09-19 用户批复收紧）：
/// </para>
/// <list type="number">
/// <item><description>UWP 条目（路径形如 <c>shell:AppsFolder\&lt;AUMID&gt;</c>）→ 经
/// <c>explorer.exe</c> 激活（D28=A），零 COM；拿不到目标 PID（R12），复查降级为乐观。</description></item>
/// <item><description>管理员条目（<see cref="DelayedItem.RunAsAdmin"/>）→ 继承调度端提升令牌直接启动。</description></item>
/// <item><description>普通条目 → **一律降权**：<c>.exe</c> 等可执行文件走
/// <c>CreateProcessAsUser</c>（交互用户令牌）；<c>.lnk</c> 无法被 <c>CreateProcessAsUser</c> 接受，
/// 经 <c>explorer.exe</c> 委托打开 —— 由以普通用户身份运行的外壳拉起，同样不提权。
/// 降权失败**没有回退**：直接判失败进日志，绝不允许以管理员身份启动普通用户级应用。</description></item>
/// </list>
/// <para>
/// 🔴 本类**不做成败判定**（那是 <c>LaunchResultEvaluator</c> 的职责），也**不抛异常**：
/// 创建失败一律折成 <see cref="LaunchOutcome.Failure"/>，由调度引擎记入运行记录（FR-5.5）。
/// </para>
/// </remarks>
public sealed partial class ProcessLauncher : IProcessLauncher
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint NormalPriorityClass = 0x00000020;

    private readonly ILogSink _log;

    /// <summary>构造进程启动器。</summary>
    /// <param name="log">日志接收端。</param>
    public ProcessLauncher(ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    /// <inheritdoc />
    public LaunchOutcome Launch(DelayedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return LaunchOutcome.Failure("条目没有目标路径。");
        }

        if (item.Path.StartsWith(UwpParsingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return LaunchViaExplorer(item, item.Path);
        }

        if (item.RunAsAdmin)
        {
            return LaunchDirect(item);
        }

        // 普通条目：一律降权，失败不回退（用户批复 2026-09-19）。
        if (item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return LaunchViaExplorer(item, item.Path);
        }

        return TryLaunchDeElevated(item);
    }

    /// <summary>UWP 条目的解析名前缀（机制 4 / D28=A）。</summary>
    public const string UwpParsingPrefix = "shell:AppsFolder\\";

    /// <summary>经 <c>explorer.exe</c> 委托打开 —— UWP 激活与 <c>.lnk</c> 降权共用的一条路：
    /// 提权进程只负责转交，真正的宿主是以普通用户身份运行的外壳，因此**不提权**（R12 / 用户批复）。</summary>
    private LaunchOutcome LaunchViaExplorer(DelayedItem item, string target)
    {
        try
        {
            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = $"\"{target}\"",
                UseShellExecute = false,
            });

            return LaunchOutcome.Success(processId: null);
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"经 explorer.exe 委托打开失败：{target}");
            return LaunchOutcome.Failure($"通过资源管理器委托打开失败：{ex.Message}");
        }
    }

    /// <summary>直接启动（继承调度端令牌）。<c>UseShellExecute</c> 兼容 <c>.lnk</c> 与裸路径。</summary>
    private static LaunchOutcome LaunchDirect(DelayedItem item)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = item.Path,
                Arguments = item.Arguments,
                UseShellExecute = true,
            };

            var workingDirectory = ResolveWorkingDirectory(item);
            if (workingDirectory.Length > 0)
            {
                start.WorkingDirectory = workingDirectory;
            }

            using var process = Process.Start(start);

            int? processId = null;
            try
            {
                if (process is not null)
                {
                    processId = process.Id;
                }
            }
            catch (InvalidOperationException)
            {
                // ShellExecute 启动关联类型时句柄可能拿不到 —— 不影响创建成功这个结论。
            }

            return LaunchOutcome.Success(processId);
        }
        catch (Exception ex)
        {
            return LaunchOutcome.Failure($"创建进程失败：{ex.Message}");
        }
    }

    /// <summary>降权启动可执行文件（交互用户令牌）。</summary>
    private unsafe LaunchOutcome TryLaunchDeElevated(DelayedItem item)
    {
        using var context = TokenHelper.AcquireInteractiveUserToken();
        if (!context.IsValid)
        {
            return LaunchOutcome.Failure("无法取得当前登录用户的令牌（会话未激活或已锁定）。");
        }

        var commandLine = $"\"{item.Path}\"";
        if (!string.IsNullOrWhiteSpace(item.Arguments))
        {
            commandLine = $"{commandLine} {item.Arguments}";
        }

        var buffer = new char[commandLine.Length + 1];
        commandLine.CopyTo(0, buffer, 0, commandLine.Length);

        var startupInfo = new StartupInfoW
        {
            Cb = Marshal.SizeOf<StartupInfoW>(),
        };

        var workingDirectory = ResolveWorkingDirectory(item);

        bool created;
        ProcessInformation processInformation;
        fixed (char* commandLinePointer = buffer)
        {
            created = CreateProcessAsUserW(
                context.Token,
                applicationName: 0,
                commandLine: (nint)commandLinePointer,
                processAttributes: 0,
                threadAttributes: 0,
                inheritHandles: false,
                creationFlags: CreateUnicodeEnvironment | NormalPriorityClass,
                environment: context.Environment,
                currentDirectory: workingDirectory,
                startupInfo: ref startupInfo,
                processInformation: out processInformation);
        }

        if (!created)
        {
            var error = Marshal.GetLastWin32Error();
            return LaunchOutcome.Failure($"降权创建进程失败（Win32 错误码 {error}）。");
        }

        _ = TokenHelper.CloseHandle(processInformation.ProcessHandle);
        _ = TokenHelper.CloseHandle(processInformation.ThreadHandle);

        return LaunchOutcome.Success((int)processInformation.ProcessId);
    }

    private static string ResolveWorkingDirectory(DelayedItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.WorkingDirectory))
        {
            return item.WorkingDirectory;
        }

        var directory = Path.GetDirectoryName(item.Path);
        return string.IsNullOrEmpty(directory) ? string.Empty : directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoW
    {
        public int Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public nint Reserved3;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint ProcessHandle;
        public nint ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessAsUserW(
        nint token,
        nint applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint         environment,
        [MarshalAs(UnmanagedType.LPWStr)] string currentDirectory,
        ref StartupInfoW startupInfo,
        out ProcessInformation processInformation);
}
