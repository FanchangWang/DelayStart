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

        // 应用内右下角通知（2026-09-21 批复）：成功类提示经它广播到主窗口的通知面板。
        services.AddSingleton<ToastService>();

        // 主窗口句柄。unpackaged 的文件选择器必须知道自己的宿主窗口，
        // 否则 PickSingleFileAsync 直接抛异常。
        services.AddSingleton<WindowHandleProvider>();

        // ── 扫描缓存（UI v2，bug#7）：启动后扫一次，页面直接读缓存 ────────
        services.AddSingleton<ScanCacheService>();

        // ── 跨页导航（UI v2）：总览的计数 chip 跳菜单用 ────────────────────
        services.AddSingleton<ShellNavigator>();

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
    /// 注册 7 个来源实例。
    /// </summary>
    /// <remarks>
    /// 🔴 **注册顺序是语义的一部分，不要重排。** 它同时决定两件事：
    /// ① 自启动项页按来源分组的展示顺序；② <c>--scan</c> 的输出顺序。
    /// <see cref="ServiceCollectionServiceExtensions"/> 解析 <c>IEnumerable&lt;T&gt;</c>
    /// 时保持注册顺序，所以改这里的次序会同时改掉两处行为。
    /// 顺序与 <c>design.md</c> 7.4 的表一致：注册表三项 → 启动文件夹两项 → 计划任务 → UWP。
    /// </remarks>
    /// <param name="services">目标容器。</param>
    private static void AddStartupSources(IServiceCollection services)
    {
        // 注册表三处。⚠️ 32 位视图单独一个实例，它的软禁用标记要写 Run32 而不是 Run（机制 4）。
        services.AddSingleton<IStartupSource>(static sp => new RegistryStartupSource(
            StartupScope.Hkcu,
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogSink>()));
        services.AddSingleton<IStartupSource>(static sp => new RegistryStartupSource(
            StartupScope.Hklm,
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogSink>()));
        services.AddSingleton<IStartupSource>(static sp => new RegistryStartupSource(
            StartupScope.HklmWow,
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogSink>()));

        // 启动文件夹两处。系统级那个需要提权才能读写。
        services.AddSingleton<IStartupSource>(static sp => new StartupFolderSource(
            StartupScope.UserFolder,
            sp.GetRequiredService<IShellLinkResolver>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogSink>()));
        services.AddSingleton<IStartupSource>(static sp => new StartupFolderSource(
            StartupScope.SystemFolder,
            sp.GetRequiredService<IShellLinkResolver>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogSink>()));

        // 计划任务。内部会跳过 \Microsoft\* 与调度端自己的任务。
        services.AddSingleton<IStartupSource>(static sp => new ScheduledTaskSource(
            sp.GetRequiredService<ILogSink>()));

        // UWP 应用（AppModel 的 State）。
        services.AddSingleton<IStartupSource>(static sp => new UwpStartupSource(
            sp.GetRequiredService<ILogSink>()));
    }
}
