namespace DelayStart.Core.Models;

/// <summary>
/// 失败提醒的分级阈值与判定。**纯常量与纯函数**，供 <see cref="FailureStreak"/> 与
/// <c>FailureStreakService</c> 共用。
/// </summary>
/// <remarks>
/// 单独抽出来的理由：级别映射同时被数据模型（<see cref="FailureStreak.Level"/>）
/// 与聚合服务使用，若把常量挂在服务上会让 <c>Models</c> 反向依赖 <c>Services</c>。
/// </remarks>
public static class FailureAlertPolicy
{
    /// <summary>
    /// 升级为 <see cref="FailureStreakLevel.Escalated"/> 所需的连续失败次数
    /// （<c>docs/design.md</c> 八：「≥ 3 次 → 角标不自动消失」）。
    /// </summary>
    public const int EscalationThreshold = 3;

    /// <summary>按连续失败次数判定提醒级别。</summary>
    /// <param name="consecutiveFailures">连续失败次数，非正数视为无失败。</param>
    /// <returns>提醒级别。</returns>
    public static FailureStreakLevel LevelOf(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 0 => FailureStreakLevel.None,
        < EscalationThreshold => FailureStreakLevel.Warning,
        _ => FailureStreakLevel.Escalated,
    };
}
