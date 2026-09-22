using DelayStart.App.ViewModels;
using DelayStart.App.Views;
using DelayStart.Core.Abstractions;
using DelayStart.Core.Logging;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;
using DelayStart.Management.Sources;

using Microsoft.Extensions.DependencyInjection;

namespace DelayStart.App.Services;

/// <summary>
/// 管理端的依赖装配（D35）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **CLI 与 GUI 共用这一个组合根。** 原先 <c>CliComposition</c> 自己 <c>new</c> 一套，
/// 那时只有 CLI 一个消费者；现在加上 GUI 就是两个装配点 —— 而它们必须装配**同一批对象**
/// （同一个 <c>PathService</c>、同一份日志、同一组来源），否则会出现"CLI 扫到的和界面看到的不是一回事"
/// 这类极难定位的问题。所以这里改成容器，<c>CliComposition</c> 退化成它的一个薄壳。
/// </para>
/// <para>
/// 生命周期只有三档，都是有理由的：
/// </para>
/// <list type="bullet">
/// <item><description><c>Singleton</c> —— <see cref="PathService"/>（构造期有建目录的副作用，只能跑一次）、
/// 日志、配置、7 个来源实例、<see cref="ScanService"/> / <see cref="TakeoverService"/>。
/// 它们本身无状态或状态就是全局的。</description></item>
/// <item><description><c>Transient</c> —— 全部 ViewModel 与页面。每进一次页面拿一套新的，
/// 否则上一页留下的搜索词、选中项会带到下一次进入。</description></item>
/// </list>
/// </remarks>
internal static class ServiceRegistration
{
    /// <summary>把管理端的全部服务、ViewModel 与页面注册进容器。</summary>
    /// <param name="services">目标容器。</param>
    /// <returns>同一个容器实例，便于链式调用。</returns>
    public static IServiceCollection AddDelayStartServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ── 路径与日志 ────────────────────────────────────────────────────────
        // PathService 用工厂而不是类型注册：EnsureCreated() 有副作用（建 %APPDATA% /
        // %LOCALAPPDATA% 下的目录树），必须恰好跑一次。类型注册会由容器决定实例化时机，
        // 那样"什么时候建的目录"就不可见了。
        services.AddSingleton(static _ =>
        {
            var paths = new PathService();
            paths.EnsureCreated();
            return paths;
        });
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton<ILogSink>(static sp => new FileLogger(
            sp.GetRequiredService<PathService>().ManagerLogPath,
            "Manager",
            sp.GetRequiredService<IClock>()));

        // ── 配置 ─────────────────────────────────────────────────────────────
        services.AddSingleton<IAppConfigStore, ConfigService>();

        // 运行状态（Phase 5 总览页 / 运行日志页读它；调度端经同一实现写它）。
        // 状态文件是全局共享的，GUI 与 headless CLI 必须看到同一份 —— 单例。
        services.AddSingleton<IRunStateStore, RunStateService>();

        // ── 快捷方式解析（启动文件夹的两个来源要用）──────────────────────────
        services.AddSingleton<IShellLinkResolver, ShellLinkResolver>();

        // ── 来源：7 个实例位置 ───────────────────────────────────────────────
        AddStartupSources(services);

        // ── 领域服务 ─────────────────────────────────────────────────────────
        services.AddSingleton<ISchedulerTaskRegistrar, TaskRegistrationService>();
        services.AddSingleton<ScanService>();
        services.AddSingleton<TakeoverService>();

        // 图标提取（D30）。带内存缓存，必须单例 —— 每页各一份等于缓存失效。
        services.AddSingleton<IconProvider>();

        // 条目级编辑（改延时 / 切开关 / 调顺序 / 手动添加）。它要注册计划任务，
        // 因此属 Management 层而不是 Core —— 与 TakeoverService 同理。
        services.AddSingleton<ConfigEditService>();

        // 调度计划任务启动期保障（2026-09-21 批复）：每次启动检测，缺失即自动补建。
        // "每次都跑"就是它的语义，不靠容器保证只解析一次；Singleton 与配置 / 日志同一批。
        services.AddSingleton<SchedulerTaskBootstrap>();

        // 守卫计划任务（D74，2026-09-22 批复）：注册端 + 启动期同步。
        // 与调度任务同款"每次启动检测、缺失即补建"，另加档位同步与"关闭即删除"，
        // 因为守卫的档位是可配的、还能整体关掉（见 GuardTaskBootstrap 的注释）。
        services.AddSingleton<IGuardTaskRegistrar, GuardTaskRegistrar>();
        services.AddSingleton<GuardTaskBootstrap>();

        // 守卫巡检本体（D74）：扫描 → 纠正 → 新增/失效 → 更新基线。
        // 基线存储单例 —— 它只是一个文件读写器，多份实例没有意义；
        // GuardService 单例与 ScanService 一致（两者都无状态）。
        services.AddSingleton<GuardBaselineStore>();
        services.AddSingleton<GuardService>();

        // 应用内右下角通知（2026-09-21 批复）：成功类提示经它广播到主窗口的通知面板。
        services.AddSingleton<ToastService>();

        // 系统通知的身份登记（D80，2026-09-22 批复）：开始菜单快捷方式写 AUMID +
        // 注册 delaystart:// 协议处理器。守卫进程发 Toast 需要 AUMID 先存在，
        // 而它可能在任何一次 GUI 启动之前就被计划任务唤起 —— 所以管理端每次启动
        // 都做一次幂等登记（见 App.OnLaunched），不能只在设置页里做。
        services.AddSingleton<ShellRegistrationService>();

        // 主窗口句柄。unpackaged 的文件选择器必须知道自己的宿主窗口，
        // 否则 PickSingleFileAsync 直接抛异常。
        services.AddSingleton<WindowHandleProvider>();

        // ── 扫描缓存（UI v2，bug#7）：启动后扫一次，页面直接读缓存 ────────
        services.AddSingleton<ScanCacheService>();

        // ── 跨页导航（UI v2）：总览的计数 chip 跳菜单用 ────────────────────
        services.AddSingleton<ShellNavigator>();

        // ── 跨进程定位请求（D74）：守卫点「查看」/ CLI --goto-startup 写，主窗口读 ──
        // 单例：它只是一个文件读写器，多处各持一份没有意义。
        services.AddSingleton<UiRequestChannel>();

        // ── 主题（FR-9 主题设置）：读配置 + 切换广播 ────────────────────────
        services.AddSingleton<ThemeService>();

        // ── 只读展示与运行记录（Phase 5）───────────────────────────────────
        services.AddSingleton<ServiceQueryService>();

        // ── ViewModel（瞬态）────────────────────────────────────────────────
        services.AddTransient<ItemsViewModel>();
        services.AddTransient<DelayViewModel>();
        services.AddTransient<OverviewViewModel>();
        services.AddTransient<SystemViewModel>();
        services.AddTransient<RunsViewModel>();
        services.AddTransient<SettingsViewModel>();

        // ── 页面（瞬态）─────────────────────────────────────────────────────
        // 页面注册进容器，导航时由 NavigationService 解析 —— 这样页面可以直接构造函数
        // 注入 ViewModel，不必在 code-behind 里静态取服务。
        // 主窗口也走容器：它的构造函数要注入 NavigationService 与 ShellNavigator，
        // 由 App.OnLaunched 解析。这样窗口本身不必知道容器存在。
        services.AddTransient<MainWindow>();
        services.AddTransient<ItemsPage>();
        services.AddTransient<DelayPage>();
        services.AddTransient<OverviewPage>();
        services.AddTransient<SystemPage>();
        services.AddTransient<RunsPage>();
        services.AddTransient<SettingsPage>();

        services.AddSingleton<NavigationService>();

        return services;
    }

    /// <summary>
    /// 注册全部自启动来源实例（单个只读列表）。
    /// </summary>
    /// <remarks>
    /// 🔴 七个实例的**构造顺序是语义的一部分**，它同时决定自启动项页的来源分组顺序与
    /// <c>--scan</c> 的输出顺序。顺序本身连同构造过程一起搬到了
    /// <see cref="StartupSourceFactory"/>（2026-09-22，D74）—— 守卫进程要用同一批实例，
    /// 而它引用 Management、不引用 App，所以唯一的构造点必须在 Management 侧。
    /// </remarks>
    /// <param name="services">目标容器。</param>
    private static void AddStartupSources(IServiceCollection services)
    {
        services.AddSingleton<IReadOnlyList<IStartupSource>>(static sp => StartupSourceFactory.Create(
            sp.GetRequiredService<IShellLinkResolver>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogSink>()));
    }
}
