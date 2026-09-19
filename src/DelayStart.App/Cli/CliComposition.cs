using DelayStart.Core.Abstractions;
using DelayStart.Core.Logging;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;
using DelayStart.Management.Sources;

namespace DelayStart.App.Cli;

/// <summary>
/// headless CLI 的依赖装配（D32）。
/// </summary>
/// <remarks>
/// 单独放一个类型而不是塞进 <c>CliHost</c>：Phase 3 的管理端也要装配同一批依赖，
/// 届时这里会演化成共享的组合根。现在先按"命令行的最小需要"装配，不做过度抽象。
/// </remarks>
/// <param name="Paths">路径解析服务。</param>
/// <param name="Log">日志接收端。</param>
/// <param name="ConfigStore">配置读写端。</param>
/// <param name="Scanner">全量扫描服务。</param>
/// <param name="Takeover">接管 / 释放服务。</param>
/// <param name="TaskRegistrar">调度计划任务注册端。</param>
internal sealed record CliServices(
    PathService Paths,
    ILogSink Log,
    IAppConfigStore ConfigStore,
    ScanService Scanner,
    TakeoverService Takeover,
    ISchedulerTaskRegistrar TaskRegistrar)
{
    /// <summary>
    /// 按正式运行路径装配全部依赖。
    /// </summary>
    /// <returns>可直接使用的服务集合。</returns>
    /// <remarks>
    /// 来源的注册顺序即 <c>--scan</c> 的输出顺序，与 <c>architecture.md</c> 4.1 的表一致：
    /// 注册表三项 → 启动文件夹两项 → 计划任务 → UWP。
    /// </remarks>
    public static CliServices Create()
    {
        var paths = new PathService();
        paths.EnsureCreated();

        var clock = SystemClock.Instance;
        var log = new FileLogger(paths.ManagerLogPath, "Manager", clock);
        var configStore = new ConfigService(paths, log, clock);
        var resolver = new ShellLinkResolver(log);

        IStartupSource[] sources =
        [
            new RegistryStartupSource(StartupScope.Hkcu, clock, log),
            new RegistryStartupSource(StartupScope.Hklm, clock, log),
            new RegistryStartupSource(StartupScope.HklmWow, clock, log),
            new StartupFolderSource(StartupScope.UserFolder, resolver, clock, log),
            new StartupFolderSource(StartupScope.SystemFolder, resolver, clock, log),
            new ScheduledTaskSource(log),
            new UwpStartupSource(log),
        ];

        var registrar = new TaskRegistrationService(paths, log);

        return new CliServices(
            paths,
            log,
            configStore,
            new ScanService(sources, configStore, log),
            new TakeoverService(configStore, registrar, sources, log),
            registrar);
    }
}
