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
/// 三条启动路径，按条目配置分流：
/// </para>
/// <list type="number">
/// <item><description>UWP 条目（路径形如 <c>shell:AppsFolder\&lt;AUMID&gt;</c>）→ 经
/// <c>explorer.exe</c> 激活（D28=A），零 COM；拿不到目标 PID（R12），复查降级为乐观。</description></item>
/// <item><description>管理员条目（<see cref="DelayedItem.RunAsAdmin"/>）→ 继承调度端提升令牌直接启动。</description></item>
/// <item><description>普通条目 → <c>WTSQueryUserToken</c> 降权启动；失败时按设置回退直接启动（E6 / FR-5.7），
/// 回退在结果里标记，让用户知情。</description></item>
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

    private readonly bool _preferDeElevated;
    private readonly bool _fallbackOnDeElevationFailure;
    private readonly ILogSink _log;

    /// <summary>构造进程启动器。</summary>
    /// <param name="preferDeElevatedLaunch">普通条目是否优先降权启动（FR-9.9）。</param>
    /// <param name="fallbackOnDeElevationFailure">降权失败是否回退直接启动（FR-5.7）；关闭则判为失败。</param>
    /// <param name="log">日志接收端。</param>
    public ProcessLauncher(bool preferDeElevatedLaunch, bool fallbackOnDeElevationFailure, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _preferDeElevated = preferDeElevatedLaunch;
        _fallbackOnDeElevationFailure = fallbackOnDeElevationFailure;
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
            return LaunchUwp(item);
        }

        if (item.RunAsAdmin || !_preferDeElevated)
        {
            return LaunchDirect(item);
        }

        var (outcome, deElevated) = TryLaunchDeElevated(item);
        if (deElevated || !_fallbackOnDeElevationFailure)
        {
            return outcome;
        }

        var direct = LaunchDirect(item);
        if (direct.Created)
        {
            return LaunchOutcome.Success(direct.ProcessId, deElevationFellBack: true);
        }

        return LaunchOutcome.Failure(
            $"降权启动失败（{outcome.FailureMessage}），回退直接启动也失败：{direct.FailureMessage}");
    }

    /// <summary>UWP 条目的解析名前缀（机制 4 / D28=A）。</summary>
    public const string UwpParsingPrefix = "shell:AppsFolder\\";

    /// <summary>经 <c>explorer.exe</c> 激活 UWP 应用 —— 提权进程里唯一可靠的 UWP 启动方式（R12）。</summary>
    private LaunchOutcome LaunchUwp(DelayedItem item)
    {
        try
        {
            var explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = $"\"{item.Path}\"",
                UseShellExecute = false,
            });

            return LaunchOutcome.Success(processId: null);
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"经 explorer.exe 激活 UWP 失败：{item.Path}");
            return LaunchOutcome.Failure($"通过资源管理器激活失败：{ex.Message}");
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

    /// <summary>降权启动。返回值第二项表示**是否真的以降权方式创建**（区分失败与回退）。</summary>
    private unsafe (LaunchOutcome Outcome, bool DeElevated) TryLaunchDeElevated(DelayedItem item)
    {
        // CreateProcessAsUser 吃不了快捷方式；.lnk 条目只能回退直接启动。
        if (item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return (LaunchOutcome.Failure("快捷方式条目不支持降权启动。"), DeElevated: false);
        }

        using var context = TokenHelper.AcquireInteractiveUserToken();
        if (!context.IsValid)
        {
            return (LaunchOutcome.Failure("无法取得当前登录用户的令牌（会话未激活或已锁定）。"), DeElevated: false);
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
            return (LaunchOutcome.Failure($"降权创建进程失败（Win32 错误码 {error}）。"), DeElevated: false);
        }

        _ = TokenHelper.CloseHandle(processInformation.ProcessHandle);
        _ = TokenHelper.CloseHandle(processInformation.ThreadHandle);

        return (LaunchOutcome.Success((int)processInformation.ProcessId), DeElevated: true);
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
