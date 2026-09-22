using DelayStart.Core.Abstractions;
using DelayStart.Core.Logging;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

using Microsoft.Extensions.DependencyInjection;

namespace DelayStart.App.Cli;

/// <summary>
/// headless CLI 的依赖装配（D32）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **这里是容器的一层薄壳，不再是第二套装配。** 它在 D35 之前自己 <c>new</c> 一整套对象，
/// 那时只有 CLI 一个消费者；Phase 3 加上 GUI 之后就变成两个装配点，而 CLI 与界面必须拿到
/// **同一批实例** —— 否则会出现"命令行看到的配置和界面看到的不一致"这类极难复现的问题，
/// 更实际的是两个 <see cref="FileLogger"/> 同时打开同一个日志文件会直接产生共享冲突。
/// 所以装配统一收敛到 <see cref="DelayStart.App.Services.ServiceRegistration"/>，
/// 这里只做解析。
/// </para>
/// <para>
/// 保留这个 record 而不是让 <c>CliHost</c> 直接收 <see cref="IServiceProvider"/>：
/// CLI 的六个方法签名写的是"我需要什么"，不是"我去容器里取什么"，
/// 需要什么一眼可见，测试时也只需造这几个字段。
/// </para>
/// </remarks>
/// <param name="Paths">路径解析服务。</param>
/// <param name="Log">日志接收端。</param>
/// <param name="ConfigStore">配置读写端。</param>
/// <param name="Scanner">全量扫描服务。</param>
/// <param name="Takeover">接管 / 释放服务。</param>
/// <param name="TaskRegistrar">调度计划任务注册端。</param>
/// <param name="GuardRegistrar">守卫计划任务注册端（卸载时随 <c>--restore-all</c> 一并删除，D74）。</param>
internal sealed record CliServices(
    PathService Paths,
    ILogSink Log,
    IAppConfigStore ConfigStore,
    ScanService Scanner,
    TakeoverService Takeover,
    ISchedulerTaskRegistrar TaskRegistrar,
    IGuardTaskRegistrar GuardRegistrar)
{
    /// <summary>
    /// 从共享容器解析 CLI 需要的服务。
    /// </summary>
    /// <param name="services">由 <see cref="DelayStart.App.Services.ServiceRegistration"/> 构建的容器。</param>
    /// <returns>可直接使用的服务集合。</returns>
    /// <remarks>
    /// 来源集合不再在这里列举 —— 注册顺序即 <c>--scan</c> 的输出顺序这条语义
    /// 已随来源注册一起移到 <see cref="DelayStart.App.Services.ServiceRegistration"/>。
    /// </remarks>
    public static CliServices Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return new CliServices(
            services.GetRequiredService<PathService>(),
            services.GetRequiredService<ILogSink>(),
            services.GetRequiredService<IAppConfigStore>(),
            services.GetRequiredService<ScanService>(),
            services.GetRequiredService<TakeoverService>(),
            services.GetRequiredService<ISchedulerTaskRegistrar>(),
            services.GetRequiredService<IGuardTaskRegistrar>());
    }
}
