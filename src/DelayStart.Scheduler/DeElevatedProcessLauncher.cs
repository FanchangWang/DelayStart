using System.Diagnostics;
using System.Runtime.InteropServices;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Scheduler;

/// <summary>
/// 调度端进程启动器（D40，2026-09-20 用户批复）：提权调度端**亲自降权**启动普通用户条目。
/// </summary>
/// <remarks>
/// <para>
/// <b>三条路由</b>：
/// <list type="bullet">
/// <item><description>管理员条目（<see cref="DelayedItem.RunAsAdmin"/>）：继承提权令牌直启 —— 有意提权，D20 的允许面。**UWP 除外**（见下）。</description></item>
/// <item><description>普通 <c>.exe</c>：<b>方案 7</b> —— 取外壳（explorer）令牌 → <c>DuplicateTokenEx</c>
/// 转主令牌 → <c>CreateProcessWithTokenW</c>，子进程为 0x2000 Medium（普通用户）。</description></item>
/// <item><description><c>.lnk</c> / UWP：<b>降权起 explorer.exe 委托</b> —— 解析名交给外壳，
/// 由外壳按自身普通用户令牌完成激活（D28=A，AOT 下零 COM）。</description></item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>为什么 explorer 令牌必须过 DuplicateTokenEx</b>（demo2 七方案真机实测 2026-09-20）：
/// 直接把 <c>OpenProcessToken</c> 拿到的 explorer 令牌交给 <c>CreateProcessWithTokenW</c>
/// 必返回 Win32Error=5；转成主令牌后成功且子进程完整性 0x2000。
/// 同一批实测还排除了两条常见错误答案：<c>CreateProcessAsUserW</c>=1314
/// （<c>SeAssignPrimaryToken</c> 只有 SYSTEM 有）、COM <c>Shell.Application.ShellExecute</c>
/// 子进程仍是 0x3000 High（**不降权，禁用**）。
/// </para>
/// <para>
/// 🔴 <b>UWP 必须经外壳解析名启动</b>：条目的 <see cref="DelayedItem.Path"/> 是<b>裸 AUMID</b>
/// （<c>&lt;PackageFamilyName&gt;!&lt;AppId&gt;</c>），它<b>不是文件路径</b> —— 直接交给
/// ShellExecute 会报"系统找不到指定的文件"（真机实锤）。必须补 <c>shell:AppsFolder\</c>
/// 前缀变成解析名，由外壳命名空间完成激活。所以 UWP 无论管理员与否，都走"起 explorer 委托"。
/// </para>
/// <para>
/// 🔴 <b>UWP 一律按普通用户身份启动</b>（D45 真机实测，推翻 D44 的机制假设）：
/// 即便按管理员身份委托外壳激活（提权 explorer + 解析名），**起来的 UWP 进程实测仍是
/// 普通用户身份** —— 打包应用进程的令牌由系统（AppContainer / 激活服务）决定，
/// 调度端用谁的令牌起中转外壳都不改结果。故 <see cref="DelayedItem.RunAsAdmin"/>
/// 对 UWP <b>无意义</b>：一律走降权委托，编辑器也不再给 UWP 条目提供身份选择。
/// </para>
/// <para>
/// （D44 已澄清、仍然成立的部分：UWP 报"系统找不到指定的文件"的真因是
/// <b>裸 AUMID 不是文件路径</b>，与提权无关 —— UWP 必须补 <c>shell:AppsFolder\</c>
/// 前缀交外壳解析。）
/// </para>
/// <para>
/// 🔴 <b>降权失败无回退（D20 红线）</b>：拿不到交互式桌面、令牌链任何一步失败、命令行超长，
/// 一律判本条目失败并继续下一条 —— 绝不退回"用提权令牌启动"。
/// </para>
/// </remarks>
internal sealed class DeElevatedProcessLauncher : IProcessLauncher
{
    /// <summary>等待交互式桌面（shell 窗口）就绪的上限（用户批复 D4：最多 10 秒）。</summary>
    private static readonly TimeSpan ShellWaitTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ShellPollInterval = TimeSpan.FromMilliseconds(250);

    private const string ShortcutExtension = ".lnk";

    private const string SeImpersonatePrivilege = "SeImpersonatePrivilege";

    /// <summary>
    /// <c>CreateProcessWithTokenW</c> 的命令行长度上限（MSDN 明示 1024 字符，含结尾 NUL，故取 1023）。
    /// </summary>
    private const int MaxCommandLineLength = 1023;

    private readonly ILogSink _log;

    /// <summary>特权只需开一次，失败也不阻断后续尝试（记录告警即可）。</summary>
    private bool _privilegeAttempted;

    /// <summary>构造启动器。</summary>
    /// <param name="log">日志。</param>
    public DeElevatedProcessLauncher(ILogSink log)
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

        // 🔴 UWP 必须先于 RunAsAdmin 判定：
        // ① 裸 AUMID 不是文件路径，走 LaunchDirect（ShellExecute）必然报"系统找不到指定的文件"
        //    （真机实锤：runs/current-run.json 里 SnipDo 与终端两条）；
        // ② UWP 进程实测恒为普通用户身份（D45），RunAsAdmin 对它无意义 —— 一律降权委托。
        if (IsUwpItem(item))
        {
            if (item.RunAsAdmin)
            {
                _log.Info($"『{item.Name}』标记为管理员身份，但 UWP 进程恒为普通用户身份（D45 实测）"
                    + " —— 已按普通用户身份经外壳委托启动。");
            }

            return LaunchViaShellDelegate(item, "UWP 应用");
        }

        if (item.RunAsAdmin)
        {
            return LaunchDirect(item);
        }

        if (item.Path.EndsWith(ShortcutExtension, StringComparison.OrdinalIgnoreCase))
        {
            return LaunchViaShellDelegate(item, "快捷方式");
        }

        return LaunchDeElevated(item);
    }

    /// <summary>
    /// UWP 判定（D41，2026-09-20 修复）。
    /// 🔴 <b>不能只看路径前缀</b>：UWP 条目的 <see cref="DelayedItem.Path"/> 存的是<b>裸 AUMID</b>
    /// （<c>&lt;PackageFamilyName&gt;!&lt;TaskId&gt;</c>），只有交给外壳前才补
    /// <c>shell:AppsFolder\</c> 前缀 —— 用前缀判恒为 false，UWP 会被误当成普通 exe
    /// （真机表现：走到 <c>File.Exists</c> 判"目标文件不存在"）。
    /// 故以 <see cref="DelayedItem.Source"/> 为主判据，路径前缀仅作兼容兜底
    /// （手工条目可能直接填了完整解析名，那时 <c>Source</c> 是 <c>Manual</c>）。
    /// </summary>
    private static bool IsUwpItem(DelayedItem item)
        => item.Source == StartupSource.Uwp || UwpParsingName.IsParsingName(item.Path);

    /// <summary>管理员条目：继承调度端提权令牌直接启动（有意提权）。</summary>
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

    /// <summary>
    /// 方案 7：shell 令牌 → DuplicateTokenEx 主令牌 → CreateProcessWithTokenW。
    /// 失败一律判失败，不提权回退。
    /// </summary>
    private LaunchOutcome LaunchDeElevated(DelayedItem item)
    {
        EnsurePrivilege();

        if (!File.Exists(item.Path))
        {
            return LaunchOutcome.Failure($"目标文件不存在：{item.Path}");
        }

        var primaryToken = AcquireShellPrimaryToken(item);
        if (primaryToken == 0)
        {
            return LaunchOutcome.Failure("取不到外壳主令牌，无法降权启动（原因见上方日志）。");
        }

        try
        {
            var outcome = CreateWithToken(
                item,
                primaryToken,
                item.Path,
                CommandLineService.Build(item.Path, item.Arguments),
                ResolveWorkingDirectory(item));

            if (outcome.Created)
            {
                _log.Info($"『{item.Name}』已降权启动（PID {outcome.ProcessId}）。");
            }

            return outcome;
        }
        finally
        {
            NativeMethods.CloseHandle(primaryToken);
        }
    }

    /// <summary>
    /// 经外壳委托启动（UWP / .lnk）：起一个 explorer.exe，把解析名交给它。
    /// </summary>
    /// <param name="item">条目。</param>
    /// <param name="kind">日志里用的类别措辞。</param>
    /// <remarks>
    /// <para>
    /// 降权起的 explorer（Medium）会把"打开这个解析名"转交给已在运行的那个同样 Medium
    /// 的外壳，由它完成 ShellExecute / UWP 激活 —— 目标拿到普通用户令牌。
    /// </para>
    /// <para>
    /// 🔴 explorer 不转发命令行参数，条目自带参数会丢失（D2 已知代价，仅记告警）。
    /// </para>
    /// </remarks>
    private LaunchOutcome LaunchViaShellDelegate(DelayedItem item, string kind)
    {
        var target = IsUwpItem(item) ? UwpParsingName.Build(item.Path) : item.Path;

        if (target.Length == 0)
        {
            return LaunchOutcome.Failure($"{kind}条目没有可交给外壳的解析名。");
        }

        if (!string.IsNullOrWhiteSpace(item.Arguments))
        {
            _log.Warn($"『{item.Name}』是{kind}，经外壳委托启动时命令行参数不会被转发（将忽略：{item.Arguments}）。");
        }

        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");

        EnsurePrivilege();

        var primaryToken = AcquireShellPrimaryToken(item);
        if (primaryToken == 0)
        {
            return LaunchOutcome.Failure("取不到外壳主令牌，无法委托启动（原因见上方日志）。");
        }

        try
        {
            var outcome = CreateWithToken(
                item,
                primaryToken,
                explorer,
                CommandLineService.Build(explorer, $"\"{target}\""),
                string.Empty);

            if (!outcome.Created)
            {
                return outcome;
            }

            _log.Info($"『{item.Name}』已降权委托外壳启动（{kind}：{target}；"
                + $"中转 explorer PID {outcome.ProcessId}，目标 PID 拿不到）。");

            // 拿到的 PID 是中转的 explorer，不是目标 —— 不做存活复查。
            return LaunchOutcome.Success(null);
        }
        finally
        {
            NativeMethods.CloseHandle(primaryToken);
        }
    }

    /// <summary>
    /// 取外壳（explorer）进程的<b>主令牌</b>。任一环失败返回 0，且已记日志。
    /// </summary>
    private nint AcquireShellPrimaryToken(DelayedItem item)
    {
        var shellWindow = WaitForShellWindow();
        if (shellWindow == 0)
        {
            var message = $"等待交互式桌面就绪超时（{(int)ShellWaitTimeout.TotalSeconds} 秒内 GetShellWindow 一直返回 0）"
                + " —— 按 D20 不提权回退。";
            _log.Warn($"『{item.Name}』{message}");
            return 0;
        }

        if (NativeMethods.GetWindowThreadProcessId(shellWindow, out var shellProcessId) == 0)
        {
            Fail("GetWindowThreadProcessId", item);
            return 0;
        }

        var shellProcess = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, false, shellProcessId);

        if (shellProcess == 0)
        {
            Fail("OpenProcess", item);
            return 0;
        }

        try
        {
            // 先只申请 TOKEN_DUPLICATE，最终权限在 DuplicateTokenEx 时再要（Chromium 同款组合）。
            if (!NativeMethods.OpenProcessToken(
                    shellProcess, NativeMethods.TokenDuplicate, out var shellToken))
            {
                Fail("OpenProcessToken", item);
                return 0;
            }

            try
            {
                const uint duplicateAccess =
                    NativeMethods.TokenQuery |
                    NativeMethods.TokenAssignPrimary |
                    NativeMethods.TokenDuplicate |
                    NativeMethods.TokenAdjustDefault |
                    NativeMethods.TokenAdjustSessionId;

                if (!NativeMethods.DuplicateTokenEx(
                        shellToken,
                        duplicateAccess,
                        0,
                        NativeMethods.SecurityImpersonation,
                        NativeMethods.TokenPrimaryType,
                        out var primaryToken))
                {
                    Fail("DuplicateTokenEx", item);
                    return 0;
                }

                return primaryToken;
            }
            finally
            {
                NativeMethods.CloseHandle(shellToken);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(shellProcess);
        }
    }

    /// <summary>真正创建进程的一步。成功与否的日志由调用方写（委托与直启措辞不同）。</summary>
    private LaunchOutcome CreateWithToken(
        DelayedItem item,
        nint primaryToken,
        string applicationName,
        string commandLine,
        string workingDirectory)
    {
        if (commandLine.Length > MaxCommandLineLength)
        {
            var message = $"命令行 {commandLine.Length} 字符，超过 CreateProcessWithTokenW 的 "
                + $"{MaxCommandLineLength + 1} 字符上限 —— 按 D5 判失败，不截断、不提权回退。";
            _log.Warn($"『{item.Name}』{message}");
            return LaunchOutcome.Failure(message);
        }

        // Desktop/Environment 都留 0：不指定 winsta0\default，也不加载用户配置 ——
        // 这两项正是实测里 CreateProcessWithTokenW 失败的诱因之一。
        var startupInfo = new NativeMethods.StartupInfoW
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.StartupInfoW>(),
        };

        unsafe
        {
            fixed (char* commandLinePointer = commandLine)
            {
                if (!NativeMethods.CreateProcessWithTokenW(
                        primaryToken,
                        0,
                        applicationName,
                        commandLinePointer,
                        0,
                        0,
                        workingDirectory.Length == 0 ? null : workingDirectory,
                        ref startupInfo,
                        out var processInfo))
                {
                    return Fail("CreateProcessWithTokenW", item);
                }

                NativeMethods.CloseHandle(processInfo.Thread);
                NativeMethods.CloseHandle(processInfo.Process);

                return LaunchOutcome.Success(checked((int)processInfo.ProcessId));
            }
        }
    }

    /// <summary>等交互式桌面就绪；已就绪则立即返回（正常路径不阻塞）。</summary>
    private nint WaitForShellWindow()
    {
        var window = NativeMethods.GetShellWindow();
        if (window != 0)
        {
            return window;
        }

        var deadline = DateTime.UtcNow + ShellWaitTimeout;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(ShellPollInterval);

            window = NativeMethods.GetShellWindow();
            if (window != 0)
            {
                _log.Info("交互式桌面已就绪（shell 窗口出现）。");
                return window;
            }
        }

        return 0;
    }

    /// <summary>
    /// 开启 <c>SeImpersonatePrivilege</c>。本机实测该特权默认已启用，这里是防御性动作：
    /// 开不出来也**不阻断**（CreateProcessWithTokenW 未必依赖它处于启用态）。
    /// </summary>
    private void EnsurePrivilege()
    {
        if (_privilegeAttempted)
        {
            return;
        }

        _privilegeAttempted = true;

        try
        {
            using var current = Process.GetCurrentProcess();

            if (!NativeMethods.OpenProcessToken(
                    current.Handle,
                    NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery,
                    out var token))
            {
                _log.Warn($"打开本进程令牌失败，跳过特权启用（Win32Error={Marshal.GetLastWin32Error()}）。");
                return;
            }

            try
            {
                if (!NativeMethods.LookupPrivilegeValueW(0, SeImpersonatePrivilege, out var luid))
                {
                    _log.Warn($"取 {SeImpersonatePrivilege} 的 LUID 失败（Win32Error={Marshal.GetLastWin32Error()}）。");
                    return;
                }

                var state = new NativeMethods.TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = NativeMethods.SePrivilegeEnabled,
                };

                NativeMethods.AdjustTokenPrivileges(
                    token,
                    false,
                    ref state,
                    (uint)Marshal.SizeOf<NativeMethods.TokenPrivileges>(),
                    0,
                    0);

                var error = Marshal.GetLastWin32Error();

                if (error == 0)
                {
                    _log.Info($"已启用 {SeImpersonatePrivilege}。");
                }
                else
                {
                    // 1300 = ERROR_NOT_ALL_ASSIGNED：令牌里没有该特权。不阻断。
                    _log.Warn($"启用 {SeImpersonatePrivilege} 返回 Win32Error={error}（不阻断降权尝试）。");
                }
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"启用 {SeImpersonatePrivilege} 时发生异常（不阻断降权尝试）。");
        }
    }

    private LaunchOutcome Fail(string api, DelayedItem item)
    {
        var error = Marshal.GetLastWin32Error();
        var message = $"{api} 失败，Win32Error={error}";

        _log.Warn($"『{item.Name}』降权启动失败：{message}（按 D20 不提权回退）。");
        return LaunchOutcome.Failure(message);
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
}
