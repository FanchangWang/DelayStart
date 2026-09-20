using DelayStart.Management.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace DelayStart.App;

/// <summary>
/// 管理端的应用对象。
/// </summary>
/// <remarks>
/// 🔴 <see cref="IServiceProvider"/> 由构造函数注入，**不是**静态单例。
/// 静态定位器会让"界面用了哪些服务"从类型签名里消失，而且容器生命周期
/// （随进程退出释放）就没有明确的持有者了。
/// </remarks>
public partial class App : Application, IDisposable
{
    private readonly IServiceProvider _services;

    /// <summary>本次启动是否由 <c>--goto-log</c> 触发（调度端气泡点击且当时无实例在跑）。</summary>
    private readonly bool _openRunsLogOnLaunch;

    private Window? _window;

    /// <summary>跨进程唤起事件：调度端 <c>DelayStart.exe --goto-log</c> 时发号。</summary>
    private EventWaitHandle? _activationEvent;

    /// <summary>跨进程「仅前置窗口」事件：普通启动撞单实例互斥时发号（不切页）。</summary>
    private EventWaitHandle? _showEvent;

    private RegisteredWaitHandle? _activationWait;

    private RegisteredWaitHandle? _showWait;

    /// <summary>构造应用对象。</summary>
    /// <param name="services">共享容器，由 <see cref="Program"/> 构建一次后传入。</param>
    /// <param name="openRunsLogOnLaunch">启动后是否直接切到「运行日志」页。</param>
    public App(IServiceProvider services, bool openRunsLogOnLaunch = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _openRunsLogOnLaunch = openRunsLogOnLaunch;
        InitializeComponent();
    }

    /// <summary>
    /// 应用启动时调用；解析主窗口并激活。
    /// </summary>
    /// <param name="args">启动参数。</param>
    /// <remarks>
    /// 启动后常驻监听 <see cref="Program.ActivationEventName"/> 唤起事件：
    /// 之后每次 <c>DelayStart.exe --goto-log</c>（调度端气泡点击）都会把本窗口
    /// 拉到前台并切到运行日志页，而不是再开一个进程。
    /// </remarks>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 调度计划任务启动期保障（2026-09-21 批复）：每次启动检测一次，缺失即自动补建。
        // 🔴 放在解析主窗口**之前** —— 总览页进入时也会检测补建（状态卡），
        //    这里先跑一轮可以把最常见的"任务被删"在页面显示前就修掉。
        //    本调用幂等（任务已存在时只查一次就返回），且内部不抛异常。
        _services.GetRequiredService<SchedulerTaskBootstrap>().EnsureSchedulerTask();

        _window = _services.GetRequiredService<MainWindow>();
        _window.Activate();

        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            Program.ActivationEventName,
            createdNew: out _);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, timedOut) => ((App)state!).DispatchActivation(openRunsLog: true),
            this,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        _showEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            Program.ShowEventName,
            createdNew: out _);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            static (state, timedOut) => ((App)state!).DispatchActivation(openRunsLog: false),
            this,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        if (_openRunsLogOnLaunch)
        {
            DispatchActivation(openRunsLog: true);
        }
    }

    /// <summary>把唤起信号投递到 UI 线程（注册回调在线程池线程上触发）。</summary>
    private void DispatchActivation(bool openRunsLog)
    {
        Program.LogGotoLog(openRunsLog
            ? "管理端实例收到唤起信号（运行日志页），投递到 UI 线程。"
            : "管理端实例收到前台唤起信号，投递到 UI 线程。");

        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
        {
            var window = (MainWindow?)_window;
            if (window is null)
            {
                return;
            }

            if (openRunsLog)
            {
                window.ShowRunsLog();
            }
            else
            {
                window.ShowForeground();
            }
        }))
        {
            // 窗口还没建好或队列不可用：信号本身是 AutoReset，丢了就丢了 ——
            // 用户重试点一次气泡即可，不值得为它做排队机制。
            Program.LogGotoLog("唤起信号投递失败：窗口尚未创建或 DispatcherQueue 不可用。");
        }
    }

    /// <summary>释放唤起监听。进程正常退出时由容器终结路径调用；提前退出则防句柄泄漏。</summary>
    public void Dispose()
    {
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _activationWait = null;
        _activationEvent = null;
        _showWait = null;
        _showEvent = null;
        GC.SuppressFinalize(this);
    }
}
