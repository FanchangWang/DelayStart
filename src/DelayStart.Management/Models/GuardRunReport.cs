using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Management.Models;

/// <summary>一条写回纠正的执行结果。</summary>
/// <param name="ItemId">条目稳定主键。</param>
/// <param name="Name">条目显示名。</param>
/// <param name="Succeeded">是否已确认重新禁用（复读校验通过）。</param>
/// <param name="Detail">一句话说明（日志与界面都用它）。</param>
public sealed record GuardCorrectionOutcome(string ItemId, string Name, bool Succeeded, string Detail);

/// <summary>
/// 一次守卫巡检的完整结果（D74）。
/// </summary>
/// <remarks>
/// 守卫进程只做两件事：把这份结果写进 <c>guard.log</c>，以及据
/// <see cref="HasNotifications"/> 决定要不要弹提示框。判定逻辑全在 Core 的三份策略里。
/// </remarks>
public sealed record GuardRunReport
{
    /// <summary>守卫在配置里是关闭状态，本次未执行任何巡检。</summary>
    public bool GuardDisabled { get; init; }

    /// <summary>本次扫描到的条目数（不全的扫描也会给出已扫到的数量）。</summary>
    public int ScannedCount { get; init; }

    /// <summary>写回纠正的结果。</summary>
    public IReadOnlyList<GuardCorrectionOutcome> Corrections { get; init; } = [];

    /// <summary>新出现的自启动项。</summary>
    public IReadOnlyList<StartupEntry> NewItems { get; init; } = [];

    /// <summary>失效 / 孤儿条目。</summary>
    public IReadOnlyList<StaleEntry> StaleItems { get; init; } = [];

    /// <summary>整体扫描失败的来源（列表不完整时的显式提示）。</summary>
    public IReadOnlyList<ScanFailure> Failures { get; init; } = [];

    /// <summary>
    /// 本次生效的通知策略（<c>settings.guardNotifyMode</c>，D80）。
    /// </summary>
    /// <remarks>
    /// 🔴 与 <see cref="HasNotifications"/> 是两件事：前者说"该不该打扰用户"，后者说"有没有变化"。
    /// 分开表达，是因为日志里那行巡检汇总**两种情况下都要写** ——
    /// 把它折进 <see cref="HasNotifications"/> 会让"策略=从不通知"与"本次没变化"在日志里长得一模一样。
    /// </remarks>
    public GuardNotifyMode NotifyMode { get; init; } = GuardNotifyMode.OnChange;

    /// <summary>是否有需要通报用户的内容（新增或失效）。</summary>
    public bool HasNotifications => NewItems.Count > 0 || StaleItems.Count > 0;

    /// <summary>纠正失败的条数（成功但无需纠正的不算）。</summary>
    public int CorrectionFailureCount => Corrections.Count(static outcome => !outcome.Succeeded);

    /// <summary>构造"守卫已关闭"的结果。</summary>
    /// <returns>未执行巡检的结果。</returns>
    public static GuardRunReport Disabled() => new() { GuardDisabled = true };
}
