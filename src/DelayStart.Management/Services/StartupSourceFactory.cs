using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Sources;

namespace DelayStart.Management.Services;

/// <summary>
/// 七个自启动来源实例的**唯一**构造点（D74，2026-09-22 批复）。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来的直接原因：守卫进程（<c>DelayStart.Guard.exe</c>）需要与管理端**完全同一批**来源实例，
/// 而它引用 Management、**不引用 App**。原先这七个实例建在
/// <c>App/Services/ServiceRegistration.cs</c>：守卫若照抄一遍，就必然会漏掉某个实例 ——
/// 最可能漏的正是 <see cref="Core.Models.StartupScope.HklmWow"/>（32 位视图），
/// 而漏掉它的后果是"32 位自启动项被写回时守卫看不见、不纠正"，**不报错、也看不出来**。
/// </para>
/// <para>
/// 🔴 **顺序是语义的一部分，不要重排。** 它同时决定两件事：
/// ① 自启动项页按来源分组的展示顺序；② <c>--scan</c> 的输出顺序。
/// </para>
/// </remarks>
public static class StartupSourceFactory
{
    /// <summary>构造全部来源实例。</summary>
    /// <param name="resolver">快捷方式解析器（启动文件夹的两个来源要用）。</param>
    /// <param name="clock">时间源。</param>
    /// <param name="log">日志接收端。</param>
    /// <returns>按展示顺序排列的七个实例。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<IStartupSource> Create(
        IShellLinkResolver resolver,
        IClock clock,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        return
        [
            // 注册表三处。⚠️ 32 位视图单独一个实例，它的软禁用标记要写 Run32 而不是 Run（机制 4）。
            new RegistryStartupSource(StartupScope.Hkcu, clock, log),
            new RegistryStartupSource(StartupScope.Hklm, clock, log),
            new RegistryStartupSource(StartupScope.HklmWow, clock, log),

            // 启动文件夹两处。系统级那个需要提权才能读写。
            new StartupFolderSource(StartupScope.UserFolder, resolver, clock, log),
            new StartupFolderSource(StartupScope.SystemFolder, resolver, clock, log),

            // 计划任务。内部会跳过 \Microsoft\*、调度端与守卫自己的任务（B3）。
            new ScheduledTaskSource(log),

            // UWP 应用（AppModel 的 State）。
            new UwpStartupSource(log),
        ];
    }
}
