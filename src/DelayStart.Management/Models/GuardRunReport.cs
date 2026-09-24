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
    /// <summary>巡检完成时刻（D116）。归档文件名与守卫日志页组标题都取它。</summary>
    /// <remarks>
    /// 守卫关闭（<see cref="Disabled"/>）时不执行巡检，此值为默认值 —— 那条路径不归档。
    /// </remarks>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>守卫在配置里是关闭状态，本次未执行任何巡检。</summary>
    public bool GuardDisabled { get; init; }

    /// <summary>
    /// 配置不可用导致巡检**无法开始**。
    /// </summary>
    /// <remarks>
    /// 🔴 与 <see cref="GuardDisabled"/> 严格分开：那是"用户主动关闭"（正常状态、不用管），
    /// 这是"程序不知道该管什么"（异常状态、必须让人看见）。混为一谈会让日志把一次故障
    /// 记成用户的设置，下次翻日志的人根本看不出发生过什么。
    /// </remarks>
    public bool ConfigUnavailable { get; init; }

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

    /// <summary>构造"配置不可用、巡检无法开始"的结果。</summary>
    /// <returns>未执行任何扫描的结果。</returns>
    public static GuardRunReport ConfigUnavailableReport() => new() { ConfigUnavailable = true };
}

/// <summary>
/// 一次巡检的汇总文案（D116）：计数口径的**唯一**出处。
/// </summary>
/// <remarks>
/// <para>
/// 三处消费者共用同一份生成逻辑，文本行与归档/界面口径天然一致：
/// </para>
/// <list type="bullet">
/// <item><description>守卫的 <c>guard.log</c> 巡检完成行（原文 = 前缀 + 本文案）；</description></item>
/// <item><description>守卫日志页的分组标题；</description></item>
/// <item><description>总览页「上次守卫巡检」卡的第一行。</description></item>
/// </list>
/// <para>
/// 🔴 任何一处想改措辞都只能改这里 —— 此前 D115 的总览卡直接抄 guard.log 行的解析结果，
/// 口径靠"解析器与写入格式一一对应"维持，归档落地后这层间接不再需要。
/// </para>
/// </remarks>
public static class GuardRunSummaryText
{
    /// <summary>生成一次巡检的汇总文案（不带前缀）。</summary>
    /// <param name="report">巡检结果。</param>
    /// <returns>形如 <c>扫描 12 项 · 纠正 1（失败 0）· 新增 2 · 失效 1 · 来源失败 1（列表不完整）</c>；
    /// 来源扫描全部成功时省略末段。</returns>
    public static string Build(GuardRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var failedSources = report.Failures.Count == 0
            ? string.Empty
            : $" · 来源失败 {report.Failures.Count}（列表不完整）";

        return $"扫描 {report.ScannedCount} 项"
            + $" · 纠正 {report.Corrections.Count}（失败 {report.CorrectionFailureCount}）"
            + $" · 新增 {report.NewItems.Count}"
            + $" · 失效 {report.StaleItems.Count}{failedSources}";
    }
}
