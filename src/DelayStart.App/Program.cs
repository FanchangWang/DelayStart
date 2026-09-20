using DelayStart.App.Cli;
using DelayStart.App.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DelayStart.App;

/// <summary>
/// 进程入口（D32）。取代 XamlCompiler 自动生成的 <c>Main</c>
/// （见 <c>DelayStart.App.csproj</c> 的 <c>DISABLE_XAML_GENERATED_MAIN</c>）。
/// </summary>
    /// <remarks>
    /// <para>
    /// 分流逻辑：命令行命中 headless 子命令 → 执行完直接返回退出码，**完全不初始化 WinUI**；
    /// 否则走标准的 WinUI 启动流程。
    /// </para>
    /// <para>
    /// 🔴 顺序不能颠倒。若先 <see cref="Application.Start"/> 再判命令行，headless 调用会
    /// 先弹出窗口再退出 —— 用户在卸载过程中会看到一个窗口一闪而过，而且
    /// <c>Application.Start</c> 是阻塞的，根本走不到后面的判断。
    /// </para>
    /// <para>
    /// 🔴 **容器在分流之前构建，且全进程只有一个。** CLI 与 GUI 共用
    /// <c>PathService</c> 与 <c>ILogSink</c> —— 两个 <c>FileLogger</c> 同时打开
    /// <c>manager.log</c> 会共享冲突。放在分流之前还保证 headless 路径
    /// 也不会漏掉"建目录树"这个副作用。
    /// </para>
    /// </remarks>
public static class Program
{
    /// <summary>管理端唤起事件的内核名（同会话内有效；见 <see cref="TrySignal"/>）。</summary>
    internal const string ActivationEventName = @"Local\DelayStart.Manager.Activate";

    /// <summary>管理端「仅前置窗口」事件的内核名（普通启动撞单实例互斥时用，不切页）。</summary>
    internal const string ShowEventName = @"Local\DelayStart.Manager.Show";

    /// <summary>管理端单实例互斥名（2026-09-20 用户批复：三进程都加互斥）。</summary>
    private const string ManagerMutexName = @"Local\DelayStart.Manager";

    private const string GotoLogArgument = "--goto-log";

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

        // 「唤起到运行日志」（调度端气泡点击，D18）。🔴 必须在 headless 分流**之前**：
        // 它以 -- 开头，会被 <see cref="CliHost"/> 当未知子命令吞掉，GUI 永远起不来。
        // 已有实例在跑 → 唤醒它然后本进程退出；没有 → 本次启动直接落在运行日志页。
        var gotoLog = args.Any(static a => string.Equals(a, GotoLogArgument, StringComparison.OrdinalIgnoreCase));
        if (gotoLog)
        {
            LogGotoLog("收到 --goto-log：准备唤起/启动管理端运行日志页。");
        }
        if (gotoLog && TrySignalRunningInstance())
        {
            LogGotoLog("已有管理端实例：唤起信号已发送，本进程退出。");
            return 0;
        }

        if (gotoLog)
        {
            LogGotoLog("没有运行中的管理端实例：本次启动直接落到运行日志页。");
        }

        if (CliHost.TryExecute(provider, args, out var exitCode))
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
            _ = new App(provider, gotoLog);
        });

        return 0;
    }

    /// <summary>
    /// 向已运行的管理端实例发送「唤起到运行日志」信号（命名事件，同会话内有效）。
    /// </summary>
    /// <returns>是否成功发号。<see langword="false"/> 表示当前没有实例在跑，调用方应正常启动。</returns>
    private static bool TrySignalRunningInstance() => TrySignal(ActivationEventName);

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
    /// 唤起链诊断日志写入 manager.log（2026-09-20 气泡点击不唤起问题）。
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
