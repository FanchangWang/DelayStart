using DelayStart.App.Services;
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

    /// <summary>本次启动是否由 <c>--goto-startup</c> 触发（守卫提示框点「查看」且当时无实例在跑）。</summary>
    private readonly bool _openStartupOnLaunch;

    private Window? _window;

    /// <summary>
    /// 本实例的存活标记（D82）。**只创建、从不 Set** —— 它是给**另一个进程**看的
    /// "这里有一个管理端在跑"，见 <see cref="Program.InstanceAliveEventName"/>。
    /// </summary>
    private EventWaitHandle? _instanceAliveEvent;

    /// <summary>跨进程「仅前置窗口」事件：普通启动撞单实例互斥时发号（不切页）。</summary>
    private EventWaitHandle? _showEvent;

    /// <summary>跨进程定位请求文件的监听（D82：未提权的点击方写文件，本实例读）。</summary>
    private FileSystemWatcher? _requestWatcher;

    /// <summary>被监听的请求文件完整路径（用于在事件里比对，避免被 .tmp 干扰）。</summary>
    private string? _requestPath;

    private RegisteredWaitHandle? _showWait;

    /// <summary>构造应用对象。</summary>
    /// <param name="services">共享容器，由 <see cref="Program"/> 构建一次后传入。</param>
    /// <param name="openRunsLogOnLaunch">启动后是否直接切到「运行日志」页。</param>
    /// <param name="openStartupOnLaunch">启动后是否按待处理的定位请求切到「自启动项」的指定位置。</param>
    public App(
        IServiceProvider services,
        bool openRunsLogOnLaunch = false,
        bool openStartupOnLaunch = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _openRunsLogOnLaunch = openRunsLogOnLaunch;
        _openStartupOnLaunch = openStartupOnLaunch;
        InitializeComponent();
    }

    /// <summary>
    /// 应用启动时调用；解析主窗口并激活。
    /// </summary>
    /// <param name="args">启动参数。</param>
    /// <remarks>
    /// <para>
    /// 启动后常驻监听请求文件（<see cref="StartRequestWatcher"/>）：之后每次
    /// <c>DelayStart.exe --goto-log</c>（调度端气泡点击）或通知点击（<c>delaystart://…</c>）
    /// 都会把本窗口拉到前台并切到目标页，而不是再开一个进程。
    /// </para>
    /// <para>
    /// 🔴 顺序：**先装文件监听、再建存活标记**。外面（<see cref="Program.Main"/> 的提权门）
    /// 的判据是"标记在不在"，如果标记先存在而监听还没装，就会出现
    /// "外面探到实例存活、把请求写成文件，而实例根本没在看"的丢单窗口。
    /// 反过来（监听已装、标记未建）最坏只是对方白弹一次 UAC，由子进程自己纠正。
    /// </para>
    /// </remarks>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 调度计划任务启动期保障（2026-09-21 批复）：每次启动检测一次，缺失即自动补建。
        // 🔴 放在解析主窗口**之前** —— 总览页进入时也会检测补建（状态卡），
        //    这里先跑一轮可以把最常见的"任务被删"在页面显示前就修掉。
        //    本调用幂等（任务已存在时只查一次就返回），且内部不抛异常。
        _services.GetRequiredService<SchedulerTaskBootstrap>().EnsureSchedulerTask();

        // 守卫计划任务（D74，2026-09-22 用户批复）：与调度任务同款"每次启动检测、缺失即补建"，
        // 另加"按档位同步"与"关闭即删除"。同样放在解析主窗口**之前** —— 用户可能根本不进总览页，
        // 而守卫任务丢失是最难被用户察觉的故障（它不常驻、不弹窗，没了就是没了）。
        // 本调用幂等、不抛异常；界面侧的失败提示由总览页进入时再同步一次呈现。
        _services.GetRequiredService<GuardTaskBootstrap>().SyncWithSettings();

        // 系统侧身份注册（D79）：开始菜单快捷方式的 AUMID + delaystart 协议处理器。
        // 两件事都只关系"系统通知能不能弹、点击能不能拉起管理端"，所以：
        //   · 放在启动期（幂等、不抛），让用户装完第一次打开管理端就把身份登记好，
        //     不必等到守卫第一次巡检；
        //   · 失败只记日志 —— 通知是旁路能力，不该拖住一个必须能开的管理端。
        _ = _services.GetRequiredService<ShellRegistrationService>().EnsureRegistered();

        _window = _services.GetRequiredService<MainWindow>();
        _window.Activate();

        // 节假日数据自动检查（FR-15 / design.md FR-15）：后台跑、不挡启动、不弹窗。
        // 🔴 触发条件很窄（当年数据不可用 + 距上次检查超过 7 天），且服务内部吞掉全部异常 ——
        // 它只是"补一份可选的缓存"，失败的时候法定两档会自己降级，不影响任何主功能。
        _ = _services.GetRequiredService<HolidayAutoCheckService>().RunIfDueAsync();

        // 跨进程落点入口（D82）。两个动作的相对顺序见上面的 remarks。
        StartRequestWatcher();
        CreateInstanceAliveMarker();

        _showEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            Program.ShowEventName,
            createdNew: out _);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            static (state, timedOut) => ((App)state!).DispatchActivation(
                openRunsLog: false,
                applyNavigationRequest: false),
            this,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        // 启动期的落点：本次进程自己就要落在目标位置（当时没有已运行的实例可被唤起）。
        // 🔴 applyNavigationRequest 对两者都开：`--goto-log` 现在也靠请求文件表达
        // （写的是 runs-log 令牌），不开就等于把它刚写的请求留在盘上，
        // 下一次任意唤起都会莫名跳到运行日志页。
        if (_openRunsLogOnLaunch || _openStartupOnLaunch)
        {
            DispatchActivation(
                openRunsLog: _openRunsLogOnLaunch,
                applyNavigationRequest: true);
        }
    }

    /// <summary>
    /// 建一个"本实例活着"的内核标记（D82，只建不 Set）。
    /// </summary>
    /// <remarks>
    /// 为什么需要一个**新**对象而不是复用单实例互斥体：互斥体的语义是"谁在跑"，
    /// 而提权门问的是"有没有一个**管理端界面**在跑"；两者今天恰好同生共死，但
    /// 互斥体在 <see cref="Program"/> 里是 `new Mutex(initiallyOwned: true, …)` 拿的，
    /// 外部按名打开它要申请互斥体权限，语义与访问权都比一个事件更容易踩坑。
    /// 事件只有一个作用、只有一条访问路径，判据干净。
    /// </remarks>
    private void CreateInstanceAliveMarker()
    {
        _instanceAliveEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            Program.InstanceAliveEventName,
            createdNew: out _);
    }

    /// <summary>
    /// 起文件监听，接收"未提权进程"写来的跨进程定位请求（D82）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>为什么不能用命名事件唤醒</b>：唤起方（Shell 按 <c>delaystart:</c> 协议拉起的进程）
    /// 是**中完整性**，本进程是**高完整性**。完整性级别的默认策略是"只挡写、不挡读"
    /// （高完整性对象带 <c>NO_WRITE_UP</c>），所以中完整性进程连打开该事件都拿不到写权限，
    /// 更别说 <c>Set()</c> 它。文件没有这个问题：<c>ui-request.json</c> 落在用户目录里、
    /// 标签是中完整性，两边都写得进去 —— 于是"写文件"本身就成了信号。
    /// </para>
    /// <para>
    /// 监听起不来（目录被 ACL 拒、路径畸形）只降级成"另一个进程的点击落不到本实例"，
    /// 本实例自己照常工作 —— 所以失败只记日志。
    /// </para>
    /// <para>
    /// 🔴 事件只负责"叫醒"，**不解析载荷**：真正的载荷一律由
    /// <see cref="DispatchActivation"/> 现读现删（<see cref="UiRequestChannel.Consume"/>
    /// 读后即删）。所以重复事件、乱序事件都无害 —— 第二次读到的是"没有请求"。
    /// </para>
    /// </remarks>
    private void StartRequestWatcher()
    {
        // 解析 PathService 有"建目录树"的副作用（注册工厂里调 EnsureCreated）——
        // 目录必须先存在，否则 FileSystemWatcher 的构造会抛。
        _requestPath = _services.GetRequiredService<DelayStart.Core.Services.PathService>().UiRequestFilePath;
        var directory = Path.GetDirectoryName(_requestPath);

        if (string.IsNullOrEmpty(directory))
        {
            Program.LogGotoLog($"跨进程定位请求监听未启动：请求文件路径没有目录段（{_requestPath}）。");
            return;
        }

        try
        {
            // ⚠️ 刻意**不设 Filter**：改名事件里"过滤器比对的是旧名还是新名"没有可靠约定，
            // 而原子写恰恰是"临时名 → 目标名"的改名（`AtomicFileWriter`）。目录里直接
            // 只有一个请求文件（其余都是子目录，`IncludeSubdirectories` 为 false 不受影响），
            // 全收下来再按完整路径比对既便宜又不会漏。
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            };

            watcher.Created += OnRequestFileChanged;
            watcher.Changed += OnRequestFileChanged;
            watcher.Renamed += OnRequestFileChanged;

            // EnableRaisingEvents 是同步的（内部此刻就打开了目录句柄），所以这一行返回之后
            // 写进来的文件不会再漏 —— 上面"先装监听再建标记"的顺序就靠这一点成立。
            watcher.EnableRaisingEvents = true;

            _requestWatcher = watcher;
            Program.LogGotoLog($"跨进程定位请求监听已启动：{_requestPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Program.LogGotoLog($"跨进程定位请求监听启动失败：{ex.Message}");
        }
    }

    /// <summary>请求文件出现 / 被替换 → 投递一次定位。</summary>
    /// <param name="sender">事件源（本类不关心）。</param>
    /// <param name="e">变更信息。</param>
    /// <remarks>
    /// 🔴 必须比对**完整路径**而不是文件名：原子写会先在**同目录**落一个
    /// <c>ui-request.json.tmp</c> 再改名 / 替换（<c>AtomicFileWriter</c>），
    /// 只比文件名的实现在"临时文件先出现"那一刻就会白唤醒一次。
    /// </remarks>
    private void OnRequestFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_requestPath is null || !string.Equals(e.FullPath, _requestPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DispatchActivation(openRunsLog: false, applyNavigationRequest: true);
    }

    /// <summary>把唤起信号投递到 UI 线程（注册回调在线程池线程上触发）。</summary>
    /// <param name="openRunsLog">没有定位请求时，是否回落到「运行日志」页。</param>
    /// <param name="applyNavigationRequest">
    /// 是否优先应用跨进程定位请求（<c>--goto-startup</c> 写的一次性文件）。
    /// </param>
    /// <remarks>
    /// <para>
    /// 🔴 <b>定位请求优先于 <paramref name="openRunsLog"/></b>：请求文件里带着确切页面
    /// （守卫点「查看」要落到具体的来源页），而命名事件本身不带载荷。两者同时存在时，
    /// 请求文件表达的是"更新的意图"，应当胜出。
    /// </para>
    /// <para>
    /// 调用方有三处：文件监听（未提权进程点击）、「仅前置窗口」事件（普通二次启动）、
    /// 以及启动期的自我落点。三者都只提供意图，载荷统一从这里现读现删。
    /// </para>
    /// </remarks>
    private void DispatchActivation(bool openRunsLog, bool applyNavigationRequest)
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

            if (applyNavigationRequest
                && _services.GetRequiredService<UiRequestChannel>().Consume() is { Length: > 0 } target)
            {
                window.ShowStartup(target);
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
            // 窗口还没建好或队列不可用。请求文件此刻仍在盘上（文件不像事件那样"过期即失"），
            // 启动期的自我落点或下一次唤起仍会读到它，不值得为它做排队机制。
            Program.LogGotoLog("唤起信号投递失败：窗口尚未创建或 DispatcherQueue 不可用。");
        }
    }

    /// <summary>释放唤起监听。进程正常退出时由容器终结路径调用；提前退出则防句柄泄漏。</summary>
    public void Dispose()
    {
        if (_requestWatcher is not null)
        {
            // 先断事件再停：EnableRaisingEvents=false 之后仍可能有一个已排队的回调在飞，
            // 而它引用的是本对象的字段 —— 断掉订阅比赌时序可靠。
            _requestWatcher.Created -= OnRequestFileChanged;
            _requestWatcher.Changed -= OnRequestFileChanged;
            _requestWatcher.Renamed -= OnRequestFileChanged;
            _requestWatcher.EnableRaisingEvents = false;
            _requestWatcher.Dispose();
            _requestWatcher = null;
        }

        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _instanceAliveEvent?.Dispose();
        _requestPath = null;
        _showWait = null;
        _showEvent = null;
        _instanceAliveEvent = null;
        GC.SuppressFinalize(this);
    }
}
