namespace DelayStart.Core.Models;

/// <summary>
/// 连续失败聚合的完整结果，供管理端总览横幅（FR-6.7 / E13）与调度端托盘角标（D31）共用。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这个类型住在 <c>Core</c> 而不是 <c>Management</c> 是刻意的（D31）。原设计把它放在管理端，
/// 但 <c>docs/design.md</c> 八 要求**调度端**的托盘角标按连续失败次数升级，
/// 而调度端只引用 Core —— 登录那一刻管理端根本没运行，算不出这个数。
/// </para>
/// <para>
/// 两个消费者取用的字段不同，所以两类信息都留着：
/// 管理端读 <see cref="FailedCountInLastRun"/> 与 <see cref="WorstStreak"/> 拼横幅文案；
/// 调度端只读 <see cref="Level"/> 决定角标要不要自动消失。
/// </para>
/// </remarks>
public sealed class FailureSummary
{
    /// <summary>没有任何归档（从未成功跑过一次调度）。为 <see langword="true"/> 时其余字段无意义。</summary>
    public bool HasRun { get; init; }

    /// <summary>最近一次运行的标识。<see cref="HasRun"/> 为 <see langword="false"/> 时为 <see langword="null"/>。</summary>
    public string? LastRunId { get; init; }

    /// <summary>
    /// 最近一次运行是否正常跑完。为 <see langword="false"/> 时管理端显示
    /// 「上次调度未正常完成」（E9 / NFR-2.3），这一条**优先于**失败计数展示。
    /// </summary>
    public bool LastRunCompletedNormally { get; init; } = true;

    /// <summary>最近一次运行中判定为失败的条目数。</summary>
    public int FailedCountInLastRun { get; init; }

    /// <summary>连续失败次数 &gt; 0 的条目，按次数降序、主键升序排列。</summary>
    public IReadOnlyList<FailureStreak> Streaks { get; init; } = [];

    /// <summary>连续失败次数最多的条目；并列时取主键序最小的一条。无失败时为 <see langword="null"/>。</summary>
    public FailureStreak? WorstStreak { get; init; }

    /// <summary>所有条目中最大的连续失败次数。</summary>
    public int MaxConsecutiveFailures => WorstStreak?.ConsecutiveFailures ?? 0;

    /// <summary>按最大连续失败次数得出的提醒级别（调度端托盘角标据此决定是否自动消失）。</summary>
    public FailureStreakLevel Level => FailureAlertPolicy.LevelOf(MaxConsecutiveFailures);

    /// <summary>最近一次运行是否存在失败条目。</summary>
    public bool HasFailure => FailedCountInLastRun > 0;

    /// <summary>是否需要向用户显示横幅。调度未正常完成也算「有事要说」（E9）。</summary>
    public bool NeedsAttention => HasRun && (HasFailure || !LastRunCompletedNormally);
}
