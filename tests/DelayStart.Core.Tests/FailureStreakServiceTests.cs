using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>FailureStreakService</c> 的单元测试（E13 / D31）。
/// </summary>
/// <remarks>
/// 覆盖重点是**中断语义**：连击必须在"某项缺席某次运行"、"该项那次只是等待/启动中"、
/// "该项那次成功"三种情况下归零。这三种情况对应真实世界里"用户关掉了条目"、
/// "上次调度被强杀"、"程序终于起来了"，判错会让用户看到早已修好的项仍被持续催。
/// </remarks>
public sealed class FailureStreakServiceTests
{
    private const string ItemId = "registry:hkcu:weixin";

    // ── CountConsecutiveFailures ────────────────────────────────────────────

    [Fact]
    public void CountConsecutiveFailures_EmptyRuns_ReturnsZero()
    {
        var count = FailureStreakService.CountConsecutiveFailures([], ItemId);

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountConsecutiveFailures_LatestRunFailed_ReturnsOne()
    {
        var runs = new[] { Run("20260919-090000", (ItemId, RunItemState.Failed)) };

        var count = FailureStreakService.CountConsecutiveFailures(runs, ItemId);

        Assert.Equal(1, count);
    }

    [Fact]
    public void CountConsecutiveFailures_ThreeRunsAllFailed_ReturnsThree()
    {
        var runs = new[]
        {
            Run("20260919-090000", (ItemId, RunItemState.Failed)),
            Run("20260918-090000", (ItemId, RunItemState.Failed)),
            Run("20260917-090000", (ItemId, RunItemState.Failed)),
        };

        var count = FailureStreakService.CountConsecutiveFailures(runs, ItemId);

        Assert.Equal(3, count);
    }

    [Fact]
    public void CountConsecutiveFailures_StopsAtSuccessfulRun()
    {
        var runs = new[]
        {
            Run("20260919-090000", (ItemId, RunItemState.Failed)),
            Run("20260918-090000", (ItemId, RunItemState.Failed)),
            Run("20260917-090000", (ItemId, RunItemState.Done)),
            Run("20260916-090000", (ItemId, RunItemState.Failed)),
        };

        var count = FailureStreakService.CountConsecutiveFailures(runs, ItemId);

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountConsecutiveFailures_StopsWhenItemAbsentFromRun()
    {
        var runs = new[]
        {
            Run("20260919-090000", (ItemId, RunItemState.Failed)),
            Run("20260918-090000", ("registry:hkcu:other", RunItemState.Done)),
            Run("20260917-090000", (ItemId, RunItemState.Failed)),
        };

        var count = FailureStreakService.CountConsecutiveFailures(runs, ItemId);

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(RunItemState.Waiting)]
    [InlineData(RunItemState.Launching)]
    public void CountConsecutiveFailures_StopsAtUnfinishedState(RunItemState unfinished)
    {
        var runs = new[]
        {
            Run("20260919-090000", (ItemId, unfinished)),
            Run("20260918-090000", (ItemId, RunItemState.Failed)),
        };

        var count = FailureStreakService.CountConsecutiveFailures(runs, ItemId);

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountConsecutiveFailures_UnknownItem_ReturnsZero()
    {
        var runs = new[] { Run("20260919-090000", (ItemId, RunItemState.Failed)) };

        var count = FailureStreakService.CountConsecutiveFailures(runs, "registry:hklm:nothing");

        Assert.Equal(0, count);
    }

    [Fact]
    public void CountConsecutiveFailures_RespectsScanLimit()
    {
        var runs = new[]
        {
            Run("20260919-090000", (ItemId, RunItemState.Failed)),
            Run("20260918-090000", (ItemId, RunItemState.Failed)),
            Run("20260917-090000", (ItemId, RunItemState.Failed)),
        };

        var count = FailureStreakService.CountConsecutiveFailures(runs, ItemId, maxRunsToScan: 2);

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountConsecutiveFailures_ItemIdMatchIsCaseSensitive()
    {
        var runs = new[] { Run("20260919-090000", (ItemId, RunItemState.Failed)) };

        var count = FailureStreakService.CountConsecutiveFailures(runs, ItemId.ToUpperInvariant());

        Assert.Equal(0, count);
    }

    // ── EvaluateAll ─────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateAll_EmptyRuns_ReturnsEmpty()
    {
        var streaks = FailureStreakService.EvaluateAll([]);

        Assert.Empty(streaks);
    }

    [Fact]
    public void EvaluateAll_OnlyReturnsItemsStillFailing()
    {
        var runs = new[]
        {
            Run("20260919-090000", (ItemId, RunItemState.Failed), ("registry:hkcu:ok", RunItemState.Done)),
        };

        var streaks = FailureStreakService.EvaluateAll(runs);

        var only = Assert.Single(streaks);
        Assert.Equal(ItemId, only.ItemId);
    }

    [Fact]
    public void EvaluateAll_OrdersByCountDescendingThenIdAscending()
    {
        var runs = new[]
        {
            Run(
                "20260919-090000",
                ("registry:hkcu:b", RunItemState.Failed),
                ("registry:hkcu:a", RunItemState.Failed),
                ("registry:hkcu:c", RunItemState.Failed)),
            Run(
                "20260918-090000",
                ("registry:hkcu:b", RunItemState.Failed),
                ("registry:hkcu:a", RunItemState.Failed)),
        };

        var streaks = FailureStreakService.EvaluateAll(runs);

        // b 与 a 都是连续 2 次，并列时按主键升序 → a 在前；c 只有 1 次，排最后。
        Assert.Equal(
            ["registry:hkcu:a", "registry:hkcu:b", "registry:hkcu:c"],
            streaks.Select(static streak => streak.ItemId));
    }

    [Fact]
    public void EvaluateAll_UsesMostRecentDisplayName()
    {
        var runs = new[]
        {
            RunWithNames("20260919-090000", (ItemId, "微信（新名）", RunItemState.Failed)),
            RunWithNames("20260918-090000", (ItemId, "微信（旧名）", RunItemState.Failed)),
        };

        var streaks = FailureStreakService.EvaluateAll(runs);

        var only = Assert.Single(streaks);
        Assert.Equal("微信（新名）", only.Name);
    }

    [Fact]
    public void EvaluateAll_FallsBackToItemIdWhenNameMissing()
    {
        var runs = new[] { Run("20260919-090000", (ItemId, RunItemState.Failed)) };

        var streaks = FailureStreakService.EvaluateAll(runs);

        var only = Assert.Single(streaks);
        Assert.Equal(ItemId, only.Name);
    }

    [Fact]
    public void EvaluateAll_IgnoresEntriesWithoutId()
    {
        var runs = new[] { Run("20260919-090000", (string.Empty, RunItemState.Failed)) };

        var streaks = FailureStreakService.EvaluateAll(runs);

        Assert.Empty(streaks);
    }

    // ── Summarize ───────────────────────────────────────────────────────────

    [Fact]
    public void Summarize_NoRuns_ReportsHasRunFalse()
    {
        var summary = FailureStreakService.Summarize([]);

        Assert.False(summary.HasRun);
        Assert.False(summary.NeedsAttention);
        Assert.Null(summary.LastRunId);
        Assert.Equal(0, summary.MaxConsecutiveFailures);
        Assert.Equal(FailureStreakLevel.None, summary.Level);
    }

    [Fact]
    public void Summarize_AllSucceeded_NeedsNoAttention()
    {
        var runs = new[] { Run("20260919-090000", (ItemId, RunItemState.Done)) };

        var summary = FailureStreakService.Summarize(runs);

        Assert.True(summary.HasRun);
        Assert.False(summary.HasFailure);
        Assert.False(summary.NeedsAttention);
        Assert.Equal(FailureStreakLevel.None, summary.Level);
    }

    [Fact]
    public void Summarize_CountsFailuresInLatestRunOnly()
    {
        var runs = new[]
        {
            Run("20260919-090000", (ItemId, RunItemState.Failed), ("registry:hkcu:ok", RunItemState.Done)),
            Run("20260918-090000", ("registry:hkcu:ok", RunItemState.Failed)),
        };

        var summary = FailureStreakService.Summarize(runs);

        Assert.Equal(1, summary.FailedCountInLastRun);
        Assert.True(summary.HasFailure);
        Assert.True(summary.NeedsAttention);
    }

    [Fact]
    public void Summarize_PicksWorstStreak()
    {
        var runs = new[]
        {
            Run(
                "20260919-090000",
                ("registry:hkcu:mild", RunItemState.Failed),
                ("registry:hkcu:severe", RunItemState.Failed)),
            Run(
                "20260918-090000",
                ("registry:hkcu:severe", RunItemState.Failed)),
            Run(
                "20260917-090000",
                ("registry:hkcu:severe", RunItemState.Failed)),
        };

        var summary = FailureStreakService.Summarize(runs);

        Assert.NotNull(summary.WorstStreak);
        Assert.Equal("registry:hkcu:severe", summary.WorstStreak.Value.ItemId);
        Assert.Equal(3, summary.MaxConsecutiveFailures);
        Assert.Equal(FailureStreakLevel.Escalated, summary.Level);
    }

    [Fact]
    public void Summarize_UnfinishedRunNeedsAttentionEvenWithoutFailure()
    {
        var record = Run("20260919-090000", (ItemId, RunItemState.Waiting));
        record.CompletedNormally = false;

        var summary = FailureStreakService.Summarize([record]);

        Assert.False(summary.HasFailure);
        Assert.True(summary.NeedsAttention);
    }

    [Fact]
    public void Summarize_ReportsLatestRunId()
    {
        var runs = new[]
        {
            Run("20260919-090000", (ItemId, RunItemState.Done)),
            Run("20260918-090000", (ItemId, RunItemState.Done)),
        };

        var summary = FailureStreakService.Summarize(runs);

        Assert.Equal("20260919-090000", summary.LastRunId);
    }

    // ── FailureAlertPolicy ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0, FailureStreakLevel.None)]
    [InlineData(-1, FailureStreakLevel.None)]
    [InlineData(1, FailureStreakLevel.Warning)]
    [InlineData(2, FailureStreakLevel.Warning)]
    [InlineData(3, FailureStreakLevel.Escalated)]
    [InlineData(9, FailureStreakLevel.Escalated)]
    public void LevelOf_MapsStreakToLevel(int consecutiveFailures, FailureStreakLevel expected)
    {
        Assert.Equal(expected, FailureAlertPolicy.LevelOf(consecutiveFailures));
    }

    [Fact]
    public void EscalationThreshold_MatchesDocumentedThree()
    {
        Assert.Equal(3, FailureAlertPolicy.EscalationThreshold);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static RunRecord Run(string runId, params (string Id, RunItemState State)[] items)
    {
        var record = new RunRecord { RunId = runId, CompletedNormally = true };
        foreach (var (id, state) in items)
        {
            // 显示名刻意留空 —— 服务应当在缺名时回退到主键（见 EvaluateAll_FallsBackToItemIdWhenNameMissing）。
            record.Items.Add(new RunItemResult { Id = id, Name = string.Empty, State = state });
        }

        return record;
    }

    private static RunRecord RunWithNames(string runId, params (string Id, string Name, RunItemState State)[] items)
    {
        var record = new RunRecord { RunId = runId, CompletedNormally = true };
        foreach (var (id, name, state) in items)
        {
            record.Items.Add(new RunItemResult { Id = id, Name = name, State = state });
        }

        return record;
    }
}
