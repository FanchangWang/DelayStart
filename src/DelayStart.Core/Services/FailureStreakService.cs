using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 连续失败次数聚合（<c>docs/design.md</c> 八 / E13）。**纯函数**：只吃运行记录列表。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **本服务住在 Core 而不是 Management，是 D31 的结论。** 原设计把它安排在管理端，
/// 但 9.2 要求**调度端**的托盘角标按连续失败次数升级（≥3 次不自动消失），而调度端只引用 Core ——
/// 登录那一刻管理端根本没运行。计算本身是纯函数（无 IO / 无 COM / 无反射），AOT 安全，
/// 因此搬到 Core 由两端共用即可消解矛盾，且调度端仍保持**无状态**（每次现算、不维护计数器）。
/// </para>
/// <para>
/// **不存计数**还有第二个理由（9.1 原文）：存下来的计数在"用户手动移出条目"和
/// "归档被清理"时会与实际不一致，而现算永远和归档一致。
/// </para>
/// <para>
/// 归档的读取由调用方经 <c>IRunStateStore.ReadRecent</c> 完成后传入 —— 本服务不碰文件系统，
/// 这样测试可以直接喂构造好的列表，不需要造真实目录。
/// </para>
/// </remarks>
public static class FailureStreakService
{
    /// <summary>
    /// 默认最多回看多少份归档。与 <c>RunStateService.MaxArchivedRuns</c> 的保留上限一致 ——
    /// 超过保留数的归档反正也不存在了。
    /// </summary>
    public const int DefaultMaxRunsScanned = 30;

    /// <summary>
    /// 计算某个条目从左往右数（最新在前）的连续失败次数。
    /// </summary>
    /// <param name="runsNewestFirst">按开始时间**倒序**排列的运行记录（与 <c>IRunStateStore.ReadRecent</c> 一致）。</param>
    /// <param name="itemId">条目的稳定主键。</param>
    /// <param name="maxRunsToScan">最多回看的归档份数。</param>
    /// <returns>连续失败次数；最近一次成功、被跳过或从未出现则为 <c>0</c>。</returns>
    /// <remarks>
    /// <para>
    /// <b>中断条件（重要）</b>：只要某次运行里**没找到**这个条目，连击就中断。
    /// 不能"跳过"它 —— 那次运行没有调度它，说明它当时不在计划内（被关闭或已移出），
    /// 把它当作"没失败"以外的东西会让计数凭空延续，用户会看到一条早已移出的条目仍被催。
    /// </para>
    /// <para>
    /// 同理 <see cref="RunItemState.Waiting"/> / <see cref="RunItemState.Launching"/> 也中断：
    /// 它们表示那次运行被中断（E9），本轮根本没执行到该项，谈不上"又失败了一次"。
    /// </para>
    /// </remarks>
    public static int CountConsecutiveFailures(
        IReadOnlyList<RunRecord> runsNewestFirst,
        string itemId,
        int maxRunsToScan = DefaultMaxRunsScanned)
    {
        ArgumentNullException.ThrowIfNull(runsNewestFirst);
        ArgumentNullException.ThrowIfNull(itemId);

        var scanned = Math.Min(Math.Max(maxRunsToScan, 0), runsNewestFirst.Count);
        var streak = 0;

        for (var i = 0; i < scanned; i++)
        {
            var item = FindItem(runsNewestFirst[i], itemId);
            if (item is not { State: RunItemState.Failed })
            {
                break;
            }

            streak++;
        }

        return streak;
    }

    /// <summary>
    /// 计算全部仍在失败连击中的条目。
    /// </summary>
    /// <param name="runsNewestFirst">按开始时间倒序排列的运行记录。</param>
    /// <param name="maxRunsToScan">最多回看的归档份数。</param>
    /// <returns>
    /// 连续失败次数大于 0 的条目，按次数降序、主键升序排列（并列时次序稳定，便于断言与 UI 展示）。
    /// </returns>
    public static IReadOnlyList<FailureStreak> EvaluateAll(
        IReadOnlyList<RunRecord> runsNewestFirst,
        int maxRunsToScan = DefaultMaxRunsScanned)
    {
        ArgumentNullException.ThrowIfNull(runsNewestFirst);

        var scanned = Math.Min(Math.Max(maxRunsToScan, 0), runsNewestFirst.Count);
        if (scanned == 0)
        {
            return [];
        }

        // 倒序扫描，每个主键**第一次遇到**时记下的就是它最近一次的显示名。
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < scanned; i++)
        {
            foreach (var item in runsNewestFirst[i].Items)
            {
                if (string.IsNullOrEmpty(item.Id) || names.ContainsKey(item.Id))
                {
                    continue;
                }

                names[item.Id] = item.Name is { Length: > 0 } name ? name : item.Id;
            }
        }

        var streaks = new List<FailureStreak>();
        foreach (var (itemId, name) in names)
        {
            var count = CountConsecutiveFailures(runsNewestFirst, itemId, scanned);
            if (count > 0)
            {
                streaks.Add(new FailureStreak(itemId, name, count));
            }
        }

        streaks.Sort(static (left, right) =>
        {
            var byCount = right.ConsecutiveFailures.CompareTo(left.ConsecutiveFailures);
            return byCount != 0 ? byCount : string.CompareOrdinal(left.ItemId, right.ItemId);
        });

        return streaks;
    }

    /// <summary>
    /// 汇总出管理端横幅与调度端角标需要的全部信息。
    /// </summary>
    /// <param name="runsNewestFirst">按开始时间倒序排列的运行记录，来自 <c>IRunStateStore.ReadRecent</c>。</param>
    /// <param name="maxRunsToScan">最多回看的归档份数。</param>
    /// <returns>聚合结果；无归档时 <see cref="FailureSummary.HasRun"/> 为 <see langword="false"/>。</returns>
    public static FailureSummary Summarize(
        IReadOnlyList<RunRecord> runsNewestFirst,
        int maxRunsToScan = DefaultMaxRunsScanned)
    {
        ArgumentNullException.ThrowIfNull(runsNewestFirst);

        if (runsNewestFirst.Count == 0)
        {
            return new FailureSummary { HasRun = false };
        }

        var lastRun = runsNewestFirst[0];
        var streaks = EvaluateAll(runsNewestFirst, maxRunsToScan);

        return new FailureSummary
        {
            HasRun = true,
            LastRunId = lastRun.RunId,
            LastRunCompletedNormally = lastRun.CompletedNormally,
            FailedCountInLastRun = lastRun.Items.Count(static item => item.State == RunItemState.Failed),
            Streaks = streaks,
            // 列表已按次数降序排好，并列时主键序最小者在前，直接取首个即为最严重项。
            WorstStreak = streaks.Count > 0 ? streaks[0] : null,
        };
    }

    private static RunItemResult? FindItem(RunRecord record, string itemId)
    {
        foreach (var item in record.Items)
        {
            if (string.Equals(item.Id, itemId, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }
}
