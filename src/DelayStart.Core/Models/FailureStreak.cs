namespace DelayStart.Core.Models;

/// <summary>
/// 某个延时条目「从最近一次运行往前数、连续失败了几次」。
/// </summary>
/// <remarks>
/// <para>
/// 这是 <c>docs/design.md</c> 八 要求的**现算**结果，不是持久化字段 ——
/// 存计数会在"用户手动移出条目"和"归档被清理"时产生不一致（D31）。
/// </para>
/// <para>
/// <see cref="ConsecutiveFailures"/> 为 0 表示该项最近一次运行成功（或被跳过），
/// 这类条目通常不出现在给用户看的列表里。
/// </para>
/// </remarks>
/// <param name="ItemId">条目的稳定主键（机制 1）。</param>
/// <param name="Name">条目显示名，取最近一次出现时的名称。</param>
/// <param name="ConsecutiveFailures">连续失败次数。</param>
public readonly record struct FailureStreak(string ItemId, string Name, int ConsecutiveFailures)
{
    /// <summary>提醒级别。</summary>
    public FailureStreakLevel Level => FailureAlertPolicy.LevelOf(ConsecutiveFailures);
}
