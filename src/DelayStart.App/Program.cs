using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using DelayStart.App.Cli;
using DelayStart.App.Services;
using DelayStart.Core.Launch;
using DelayStart.Management.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DelayStart.App;

/// <summary>
/// 进程入口（D32 / D82）。取代 XamlCompiler 自动生成的 <c>Main</c>
/// （见 <c>DelayStart.App.csproj</c> 的 <c>DISABLE_XAML_GENERATED_MAIN</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 分流顺序（🔴 顺序本身就是正确性，不要重排）：
/// <b>① 写落点请求 → ② 提权门 → ③ 交给已有实例 → ④ CLI 子命令 → ⑤ GUI</b>。
/// </para>
/// <para>
/// 🔴 <b>① 必须排在 ②③ 之前</b>：请求文件是唯一能跨进程带载荷的通道（命名事件不带载荷）。
/// 先把它写下，后面"交给已有实例"与"自己启动"两条路都能直接消费，不必各写一遍；
/// 它还顺带承担了"清掉上一次残留请求"的职责 —— 新请求覆盖旧请求。
/// </para>
/// <para>
/// 🔴 <b>② 提权门（D82，2026-09-22 用户批复"入口自提权"）</b>：清单是 <c>asInvoker</c>，
/// 管理员权限由本方法自己申请。未提权时**只有在"没有实例可交"时才重拉提权**：
/// 已经有实例在跑就只写请求文件、不发 UAC —— 这就是"点系统通知不再弹 UAC"的全部秘密。
/// 旧方案是清单 <c>requireAdministrator</c>，提权发生在**进程创建时**（loader 决定，
/// 代码一行都没跑），所以 Shell 按协议拉起协议处理器就必然弹一次 UAC，无从规避。
/// </para>
/// <para>
/// 🔴 <b>④ 在 ⑤ 之前</b>：命令行命中 headless 子命令 → 执行完直接返回退出码，
/// <b>完全不初始化 WinUI</b>；反过来先 <see cref="Application.Start"/> 再判命令行，
/// headless 调用会先弹窗口再退出 —— 用户在卸载过程中会看到一个窗口一闪而过，
/// 而且 <c>Application.Start</c> 是阻塞的，根本走不到后面的判断。
/// </para>
/// <para>
/// 🔴 <b>容器在分流之前构建，且全进程只有一个。</b> CLI 与 GUI 共用
/// <c>PathService</c> 与 <c>ILogSink</c> —— 两个 <c>FileLogger</c> 同时打开
/// <c>manager.log</c> 会共享冲突。放在分流之前还保证 headless 路径
/// 也不会漏掉"建目录树"这个副作用。
/// </para>
/// <para>
/// 🔴 <b>定位参数（<c>--goto-log</c> / <c>--goto-startup</c>）必须在 <c>CliHost</c> 之前
/// 被"认领"</b>，否则它们会被当成未知子命令：那两个参数都以 <c>--</c> 开头，
/// 会被 <see cref="CliHost.TryExecute"/> 收走并返回"未知命令"（退出码 2），
/// 于是"直接落到目标页"这条路径变成"什么都不发生"。
/// 同理 <see cref="AppActivation.ElevationAttemptArgument"/> 也必须先摘掉再交出去。
/// </para>
/// </remarks>
public static partial class Program
{
    /// <summary>
    /// 管理端实例存活标记的内核名（同会话内有效；见 <see cref="IsInstanceAlive"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 由 <see cref="App"/> 在窗口建好后创建（提权进程 ⇒ 高完整性），本类只做**只读探测**。
    /// 🔴 D82 起它**不再当"唤起信号"用**（信号改由请求文件 + 实例侧的文件监听承担，
    /// 因为跨完整性级别写不了高完整性对象的状态）—— 保留这个对象的唯一理由是
    /// "实例在不在"需要一个**随进程消亡**的判据：文件会残留、互斥体在本进程里另有用途
    /// （单实例语义，且会被 `new Mutex` 的访问权要求拖住），只有内核对象是干净的。
    /// </para>
    /// <para>
    /// 🔴 探测**必须只申请 <c>Synchronize</c>**（读），不能沿用默认的
    /// <c>Synchronize | Modify</c>（写）：完整性级别是"只挡写、不挡读"，
    /// 申请写权限会拿到 <c>ERROR_ACCESS_DENIED</c>，而
    /// <c>TryOpenExisting</c> 把它**静默折叠成 false** —— 于是"实例在跑"被读成
    /// "没实例"，每次点通知都白弹一次 UAC（见 <c>pitfalls.md</c> 十一）。
    /// </para>
    /// </remarks>
    internal const string InstanceAliveEventName = @"Local\DelayStart.Manager.Activate";

    /// <summary>管理端「仅前置窗口」事件的内核名（普通启动撞单实例互斥时用，不切页）。</summary>
    internal const string ShowEventName = @"Local\DelayStart.Manager.Show";

    /// <summary>管理端单实例互斥名（2026-09-20 用户批复：三进程都加互斥）。</summary>
    private const string ManagerMutexName = @"Local\DelayStart.Manager";

    /// <summary>子进程命令行里 <c>--</c> 前缀（与 <see cref="CliHost"/> 的判据一致）。</summary>
    private const string CommandPrefix = "--";

    /// <summary><c>ERROR_CANCELLED</c>：用户在 UAC 对话框里点了「否」。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>进程入口。</summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>进程退出码。headless 子命令的退出码会被卸载脚本检查（D22）。</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddDelayStartServices();

        // using：Application.Start 是阻塞的，返回即进程结束，此时才释放容器。
        // 容器的 Dispose 会连带释放它创建的 IDisposable（含 FileLogger 的文件句柄）。
        using var provider = services.BuildServiceProvider();

        // 「唤起到运行日志」（调度端通知点击，D18）。🔴 必须在 headless 分流**之前**：
        // 它以 -- 开头，会被 <see cref="CliHost"/> 当未知子命令吞掉，GUI 永远起不来。
        var gotoLog = args.Any(static a => string.Equals(a, AppActivation.GotoLogArgument, StringComparison.OrdinalIgnoreCase));

        // 「唤起并定位」（守卫的系统通知点击，D74）。同样必须在 headless 分流之前。
        var gotoStartup = args.Any(static a => string.Equals(a, AppActivation.GotoStartupArgument, StringComparison.OrdinalIgnoreCase));

        // ① 落点意图先成文件。🔴 不带定位的 --goto-log 也要写（写的是 runs-log 令牌）——
        // 它表达的是"看日志"这个**更新的**意图，覆盖掉可能残留的定位请求，
        // 否则用户点了通知却落到上一次的定位位置。
        if (gotoLog || gotoStartup)
        {
            var target = gotoStartup ? ResolveNavigationTarget(args) : UiNavigationTarget.RunsLog;

            LogGotoLog(gotoStartup
                ? $"收到 {AppActivation.GotoStartupArgument}：目标令牌『{target}』。"
                : $"收到 {AppActivation.GotoLogArgument}：目标令牌『{target}』。");

            provider.GetRequiredService<UiRequestChannel>().Write(target);
        }

        // ② 提权门（D82）。
        if (!DelayStart.Core.Services.ElevationCheck.IsElevated())
        {
            // ②a 已有实例在跑 → 请求文件写下就完事，本进程不发 UAC 直接退出。
            // 实例侧靠 FileSystemWatcher 收到它（跨完整性级别**不能**用命名事件通知，
            // 见 App.StartRequestWatcher 的说明）。
            if ((gotoLog || gotoStartup) && IsInstanceAlive())
            {
                LogGotoLog("未提权但已有管理端实例：请求已写入，交由该实例处理，本进程不申请提权（零 UAC）。");
                return 0;
            }

            // ②b 防重拉死循环：子进程带着标记回来却仍未提权（UAC 被策略禁用、令牌异常……），
            // 再拉一次就是无限弹窗。
            if (args.Any(AppActivation.IsElevationAttempt))
            {
                LogGotoLog("以管理员身份重拉后仍未取得提权令牌：放弃启动（防止无限重拉）。");
                return 1;
            }

            // ②c 没有实例可交 → 自己申请提权（一次 UAC）。
            return RelaunchElevated(args, IsCliCommand(args, gotoLog, gotoStartup));
        }

        // ③ 已提权：同样先看有没有实例 —— 有就只留请求文件，让那个实例去落点。
        // （旧实现这里发命名事件；现在是高完整性进程自己看文件，见 App.StartRequestWatcher。）
        if ((gotoLog || gotoStartup) && IsInstanceAlive())
        {
            LogGotoLog("已有管理端实例：请求已写入，由该实例处理，本进程退出。");
            return 0;
        }

        if (gotoLog)
        {
            LogGotoLog("没有运行中的管理端实例：本次启动直接落到运行日志页。");
        }

        if (gotoStartup)
        {
            LogGotoLog("没有运行中的管理端实例：本次启动直接落到目标位置。");
        }

        // ④ headless 子命令。🔴 定位参数已被上面认领，不能再交给 CliHost ——
        // 它看到 args[0] 以 `--` 开头就会当成子命令执行（"--goto-startup" 不在白名单里
        // ⇒ 未知命令、退出码 2、TryExecute 返回 true），GUI 永远起不来。
        // 2026-09-22 修（此前无人传这两个参数，所以这条路径一直没被真跑过；
        // 守卫改用系统通知后它就是主路径了）。
        // ⚠️ 提权重拉标记同样必须先摘掉：它也是 `--` 开头。
        if (!gotoLog
            && !gotoStartup
            && CliHost.TryExecute(provider, WithoutElevationAttempt(args), out var exitCode))
        {
            return exitCode;
        }

        // 🔴 单实例互斥（2026-09-20 用户批复）在 headless 分流**之后**：CLI 子命令
        // （还原 / 重注册任务等）必须不受"管理端已在跑"限制。互斥只管 GUI 实例。
        // using：Application.Start 是阻塞的，返回即进程结束，此时才释放互斥。
        using var instanceMutex = new Mutex(initiallyOwned: true, ManagerMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            LogGotoLog("管理端已有实例（单实例互斥命中）：发送前台唤起信号后退出。");
            _ = TrySignal(ShowEventName);
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // ⚠️ lambda 参数**不能**命名为 `_`：那样下面本想"丢弃结果"的 `_ = new App()`
        // 会被编译器理解成给该参数赋值，报 CS0029。所以这里用 `(p)`。
        //
        // 另一处刻意：写 `_ = new App()` 而不是模板原样的裸 `new App();` —— 后者在本仓库的
        // 分析器设置下会报 CA1806（创建了实例却从未使用）。App 实例由 WinUI 内部持有
        // （Application.Current），这里的作用只是"把它启动起来"。
        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(provider, gotoLog, gotoStartup);
        });

        return 0;
    }

    /// <summary>
    /// 以管理员身份重新拉起自己（D82）。
    /// </summary>
    /// <param name="args">原始命令行（会原样转发，末尾追加防重拉标记）。</param>
    /// <param name="waitForChild">
    /// 是否等待子进程并回传它的退出码。CLI 子命令必须为 <see langword="true"/>
    /// （脚本与卸载器都靠退出码判断成败）；GUI 为 <see langword="false"/>。
    /// </param>
    /// <returns>本进程的退出码。</returns>
    /// <remarks>
    /// <para>
    /// 🔴 <b>GUI 不等待</b>：子进程就是用户要用的那个窗口，它要活到用户关窗为止 ——
    /// 父进程等它等于全程多驻留一个看不见的提权前进程（任务管理器里两个 DelayStart.exe）。
    /// 用户在 UAC 里点「否」时 <c>Process.Start</c> 会**同步**抛
    /// <see cref="Win32Exception"/>（1223），所以"不等待"并不失去"用户拒绝了"这个信息。
    /// </para>
    /// <para>
    /// 🔴 <b>CLI 要等待</b>：<c>--scan</c>/<c>--restore-all</c> 的成败就是退出码本身。
    /// 已知代价：提权子进程拿不到父进程的控制台，会开一个**新的控制台窗口**，
    /// 命令输出落在那儿（与改动前的 <c>requireAdministrator</c> 行为一致，不是本次引入的）。
    /// </para>
    /// <para>
    /// 用的是 <c>ShellExecute</c> 的 <c>runas</c> 动词（不是自己拼 <c>ShellExecuteEx</c>）：
    /// 提权由外壳/AppInfo 服务完成，同时天然获得"UAC 被拒 ⇒ 1223"这个可判别的失败。
    /// </para>
    /// </remarks>
    private static int RelaunchElevated(string[] args, bool waitForChild)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            LogGotoLog("提权重拉失败：取不到当前可执行文件路径（Environment.ProcessPath 为空）。");
            return 1;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = BuildElevationArguments(args),
            Verb = "runas",
            UseShellExecute = true,

            // 沿用调用者的工作目录：CLI 的 --result-file 可能是相对路径，
            // 换一个目录会让它落到用户意想不到的地方。
            WorkingDirectory = Environment.CurrentDirectory,
        };

        LogGotoLog($"未提权且没有实例可交：以管理员身份重新拉起自己（追加 {AppActivation.ElevationAttemptArgument} 防重拉）。");

        try
        {
            using var child = Process.Start(startInfo);
            if (child is null)
            {
                LogGotoLog("提权重拉失败：Process.Start 未返回进程对象。");
                return 1;
            }

            if (!waitForChild)
            {
                // GUI：子进程要活到用户关窗，不能等（见方法说明）。
                LogGotoLog($"已拉起提权实例（PID {child.Id}），本进程退出。");
                return 0;
            }

            child.WaitForExit();
            LogGotoLog($"提权子进程（PID {child.Id}）已结束，退出码 {child.ExitCode}。");
            return child.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // 用户点了「否」：这是他明确表达的意思，不是故障 —— 但必须留痕，
            // 否则"点了通知没反应"与"通知压根没发出来"在日志里长得一模一样。
            LogGotoLog("用户取消了 UAC 提权，本次启动放弃。");

            if (waitForChild)
            {
                NativeConsole.EnsureAttached();
                Console.Error.WriteLine("已取消管理员权限请求，程序未启动。");
            }

            return 1;
        }
        catch (Win32Exception ex)
        {
            LogGotoLog($"提权重拉失败：{ex.Message}（Win32 错误 {ex.NativeErrorCode}）。");

            if (waitForChild)
            {
                NativeConsole.EnsureAttached();
                Console.Error.WriteLine($"无法以管理员身份启动程序：{ex.Message}");
            }

            return 1;
        }
    }

    /// <summary>把原始参数拼成子进程命令行，并在末尾追加防重拉标记。</summary>
    /// <param name="args">原始命令行。</param>
    /// <returns>可直接交给 <see cref="ProcessStartInfo.Arguments"/> 的字符串。</returns>
    /// <remarks>
    /// 只做最小必要的引号处理（含空格/制表符/引号才加引号，内部引号与反斜杠转义）。
    /// 刻意不实现完整的 Windows 命令行解析规则：本程序自己定义的参数里没有
    /// "反斜杠紧邻引号"这种边界形态（文件路径可能带空格，但不以引号结尾）。
    /// </remarks>
    private static string BuildElevationArguments(string[] args)
    {
        var builder = new StringBuilder();
        foreach (var arg in args)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteArgument(arg));
        }

        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        return builder.Append(AppActivation.ElevationAttemptArgument).ToString();
    }

    /// <summary>必要时给一个参数加引号。</summary>
    /// <param name="argument">命令行参数。</param>
    /// <returns>可以安全拼进命令行的形式。</returns>
    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        return string.Concat("\"", argument.Replace("\\", "\\\\").Replace("\"", "\\\""), "\"");
    }

    /// <summary>摘掉提权重拉标记，避免它被 <see cref="CliHost"/> 当成未知子命令。</summary>
    /// <param name="args">原始命令行。</param>
    /// <returns>不含该标记的参数序列；没有该标记时原样返回入参。</returns>
    private static string[] WithoutElevationAttempt(string[] args)
        => args.Any(AppActivation.IsElevationAttempt)
            ? args.Where(static a => !AppActivation.IsElevationAttempt(a)).ToArray()
            : args;

    /// <summary>判断这次调用是不是一个 headless 子命令（决定提权重拉后要不要等子进程）。</summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="gotoLog">是否命中了 <c>--goto-log</c>。</param>
    /// <param name="gotoStartup">是否命中了 <c>--goto-startup</c>。</param>
    /// <returns>是 CLI 子命令时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 判据与 <see cref="CliHost.TryExecute"/> 一致（<c>args[0]</c> 以 <c>--</c> 开头），
    /// 但**先排除**两个定位参数：它们同样以 <c>--</c> 开头，却会打开 GUI（等 GUI 的
    /// 子进程 = 父进程陪着用户开到关窗）。
    /// </remarks>
    private static bool IsCliCommand(string[] args, bool gotoLog, bool gotoStartup)
        => !gotoLog
           && !gotoStartup
           && args.Length > 0
           && args[0].StartsWith(CommandPrefix, StringComparison.Ordinal);

    /// <summary>
    /// 探测是否已有管理端实例在跑（🔴 只申请读权限，见 <see cref="InstanceAliveEventName"/>）。
    /// </summary>
    /// <returns>已有实例时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 探测结论不是"活着"时一律按"没有实例"处理（对象不存在、访问被拒、名字非法）——
    /// 那种情况下的代价只是"白弹一次 UAC"：被拉起的子进程自己会再探一次
    /// （进程内同级别，必然成功），拿到实例就只留请求文件退出，落点不会丢。
    /// 所以这里的误判是安全的。实现（含"为什么必须下沉到原生 API"）见
    /// <see cref="Interop.InstanceProbe"/>。
    /// </para>
    /// <para>
    /// 🔴 <b>"访问被拒"必须留痕</b>：它是"中完整性进程能按只读权限打开高完整性事件"
    /// 这个前提**唯一**能在现场看到的判据。真机验收时若在 <c>manager.log</c> 里看到它，
    /// 说明前提不成立 —— 表现不是坏掉，而是"点通知仍然多弹一次 UAC"。
    /// </para>
    /// </remarks>
    private static bool IsInstanceAlive()
    {
        var result = Interop.InstanceProbe.Probe(InstanceAliveEventName);
        if (result == Interop.InstanceProbeResult.Alive)
        {
            return true;
        }

        if (result is Interop.InstanceProbeResult.AccessDenied or Interop.InstanceProbeResult.Failed)
        {
            LogGotoLog($"实例存活探测未得出结论（{result}）：无法按只读权限打开 {InstanceAliveEventName}，按「没有实例」继续。");
        }

        return false;
    }

    /// <summary>打开并置位一个命名事件；事件不存在（没有实例在跑）时返回 <see langword="false"/>。</summary>
    private static bool TrySignal(string eventName)
    {
        if (!EventWaitHandle.TryOpenExisting(eventName, out var existing))
        {
            return false;
        }

        using (existing)
        {
            _ = existing.Set();
        }

        return true;
    }

    /// <summary>
    /// 从命令行解析定位目标。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <returns>定位令牌，取值见 <see cref="UiNavigationTarget"/>。</returns>
    /// <remarks>
    /// <para>
    /// 优先级：<c>--stale</c> &gt; 协议 URI（<c>delaystart://&lt;令牌&gt;</c>）
    /// &gt; <c>--source=&lt;令牌&gt;</c> &gt; 注册表页。
    /// </para>
    /// <para>
    /// 🔴 协议 URI 是**系统通知点击**进来的形态：通知的 <c>launch</c> 属性写
    /// <c>delaystart://delay</c>，Shell 按注册的命令行把它当参数交给本进程
    /// （见 <c>ShellRegistrationService</c> —— 注册的是
    /// <c>"DelayStart.exe" --goto-startup "%1"</c>，所以 URI 与
    /// <see cref="AppActivation.GotoStartupArgument"/> 成对出现）。这里就是通知与界面之间
    /// 唯一的翻译点，改了 URI 形态要同步改那边。
    /// </para>
    /// <para>
    /// 未识别的值**原样透传**：翻译成导航标签是在界面侧
    /// （<c>UiTargetNavigation.TagFor</c>），那里对未知值的行为是"只前置窗口、不切页"。
    /// 在这里丢弃非法值反而会让"忘记加来源"与"来源拼错"表现成同一件事。
    /// </para>
    /// </remarks>
    private static string ResolveNavigationTarget(string[] args)
    {
        if (args.Any(static a => string.Equals(a, AppActivation.StaleArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return UiNavigationTarget.Stale;
        }

        foreach (var arg in args)
        {
            var uri = TryReadProtocolTarget(arg);
            if (uri is not null)
            {
                return uri;
            }
        }

        foreach (var arg in args)
        {
            if (!arg.StartsWith(AppActivation.SourceArgumentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = arg[AppActivation.SourceArgumentPrefix.Length..].Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        // 没指定：落到第一个来源页（与守卫通报的默认定位顺序一致）。
        return UiNavigationTarget.Registry;
    }

    /// <summary>
    /// 从一个参数里取出协议令牌。
    /// </summary>
    /// <param name="arg">命令行参数。</param>
    /// <returns>令牌（如 <c>delay</c>）；该参数不是本协议的 URI 时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 容忍 <c>delaystart://delay</c> / <c>delaystart://delay/</c> / <c>delaystart://delay?x=y</c>
    /// 三种形态：URI 的规范化由 Shell 决定，而"多一个斜杠就点不动"是那种
    /// 只能靠用户反馈才发现的失败。
    /// </remarks>
    private static string? TryReadProtocolTarget(string arg)
    {
        if (!arg.StartsWith(AppActivation.ProtocolUriPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = arg[AppActivation.ProtocolUriPrefix.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var token = (end < 0 ? rest : rest[..end]).Trim();

        return token.Length > 0 ? token : null;
    }

    /// <summary>
    /// 唤起链诊断日志写入 manager.log（2026-09-20 通知点击不唤起问题）。
    /// internal：供 <see cref="App.DispatchActivation"/> 与 <see cref="MainWindow.ShowRunsLog"/>
    /// 在同一条链上留痕，任何一环断掉都能从日志定位。写失败静默忽略 —— 诊断日志绝不能挡住启动。
    /// </summary>
    internal static void LogGotoLog(string message)
    {
        try
        {
            var paths = new DelayStart.Core.Services.PathService();
            paths.EnsureCreated();
            var log = new DelayStart.Core.Logging.FileLogger(
                paths.ManagerLogPath,
                "Manager",
                DelayStart.Core.Services.SystemClock.Instance);
            log.Write(DelayStart.Core.Abstractions.LogLevel.Info, message);
        }
        catch
        {
            // 诊断日志失败不影响启动。
        }
    }
}
