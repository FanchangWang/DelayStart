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

        // ── 快捷方式解析（启动文件夹的两个来源要用）──────────────────────────
        services.AddSingleton<IShellLinkResolver, ShellLinkResolver>();

        // ── 来源：7 个实例位置 ───────────────────────────────────────────────
        AddStartupSources(services);

        // ── 领域服务 ─────────────────────────────────────────────────────────
        services.AddSingleton<ISchedulerTaskRegistrar, TaskRegistrationService>();
        services.AddSingleton<ScanService>();
        services.AddSingleton<TakeoverService>();

        // ── ViewModel（瞬态）────────────────────────────────────────────────
        services.AddTransient<ShellViewModel>();
        services.AddTransient<ItemsViewModel>();
        services.AddTransient<DelayViewModel>();

        // ── 页面（瞬态）─────────────────────────────────────────────────────
        // 页面注册进容器，导航时由 NavigationService 解析 —— 这样页面可以直接构造函数
        // 注入 ViewModel，不必在 code-behind 里静态取服务。
        // 主窗口也走容器：它的构造函数要注入 ShellViewModel 与 NavigationService，
        // 由 App.OnLaunched 解析。这样窗口本身不必知道容器存在。
        services.AddTransient<MainWindow>();
        services.AddTransient<ItemsPage>();
        services.AddTransient<DelayPage>();

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
    /// 顺序与 <c>architecture.md</c> 4.1 的表一致：注册表三项 → 启动文件夹两项 → 计划任务 → UWP。
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
