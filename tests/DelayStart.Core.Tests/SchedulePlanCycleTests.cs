using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="SchedulePlan"/> 的周期过滤（FR-15.3 / FR-15.4）。
/// </summary>
/// <remarks>
/// <para>
/// 单独一个文件而不是往 <c>SchedulePlanTests</c> 里塞：那份测的是"延时与顺序"，
/// 这份测的是"今天该不该进计划"，两者的失败指向完全不同的排查方向。
/// </para>
/// <para>
/// 核心不变式只有一条：**周期与 <see cref="DelayedItem.Enabled"/> 是两层独立的过滤**，
/// 次序不可换，也不合并成一个条件表达式 —— 将来任何给周期加告警的逻辑，
/// 都不该把"用户主动关掉的条目"算成被跳过。
/// </para>
/// </remarks>
public sealed class SchedulePlanCycleTests
{
    private static readonly List<ScheduleCycle> Cycles =
    [
        new() { Id = "c-a1", Name = "上一休一", Days = WeekdaySet.Monday | WeekdaySet.Wednesday | WeekdaySet.Friday },
        new() { Id = "c-b2", Name = "周末班", Days = WeekdaySet.Saturday | WeekdaySet.Sunday },
    ];

    private static DelayedItem Item(string id, int delaySeconds = 10, string cycleId = BuiltinCycleIds.Everyday)
        => new()
        {
            Id = id,
            Name = id,
            DelaySeconds = delaySeconds,
            ScheduleCycleId = cycleId,
        };

    private static string[] Ids(IReadOnlyList<ScheduleEntry> plan)
        => [.. plan.Select(static entry => entry.Item.Id)];

    [Fact]
    public void Build_WithoutToday_AppliesNoCycleFilter()
    {
        // 不传日期 = 无从判定。此时行为与 FR-15 之前完全一致（历史调用点不会被悄悄改变）。
        var items = new[] { Item("weekend-only", 5, BuiltinCycleIds.Weekends) };

        var plan = SchedulePlan.Build(items, Cycles);

        Assert.Equal(["weekend-only"], Ids(plan));
    }

    [Fact]
    public void Build_Saturday_KeepsWeekendCycleAndDropsWeekdayCycle()
    {
        var items = new[]
        {
            Item("weekday", 10, BuiltinCycleIds.Weekdays),
            Item("weekend", 20, BuiltinCycleIds.Weekends),
            Item("daily", 30, BuiltinCycleIds.Everyday),
        };

        var plan = SchedulePlan.Build(items, Cycles, new DateOnly(2026, 9, 26));

        Assert.Equal(["weekend", "daily"], Ids(plan));
    }

    [Fact]
    public void Build_Wednesday_RespectsCustomCycle()
    {
        var items = new[]
        {
            Item("a1", 10, "c-a1"),       // 一三五 → 命中
            Item("b2", 20, "c-b2"),       // 周六日 → 跳过
        };

        var plan = SchedulePlan.Build(items, Cycles, new DateOnly(2026, 9, 23));

        Assert.Equal(["a1"], Ids(plan));
    }

    [Fact]
    public void Build_LegalCycles_UseRealCalendar()
    {
        var items = new[]
        {
            Item("workday", 10, BuiltinCycleIds.LegalWorkday),
            Item("holiday", 20, BuiltinCycleIds.LegalHoliday),
        };

        // 2026-09-20 周日是中秋前补班日 → 只有法定工作日档进计划。
        var plan = SchedulePlan.Build(items, Cycles, RealCalendar2026.MakeUpWorkSunday, RealCalendar2026.Create());

        Assert.Equal(["workday"], Ids(plan));
    }

    [Fact]
    public void Build_AllItemsSkipped_ReturnsEmptyPlan()
    {
        var items = new[]
        {
            Item("a", 10, BuiltinCycleIds.Weekdays),
            Item("b", 20, BuiltinCycleIds.Weekdays),
        };

        var plan = SchedulePlan.Build(items, Cycles, new DateOnly(2026, 9, 27));

        Assert.Empty(plan);   // 空计划走 FR-5.10 静默退出，这条路径没有被改。
    }

    [Fact]
    public void Build_DisabledItemIsExcludedBeforeCycleMatters()
    {
        // 关闭的条目不因周期而复活，也不应该占用一次周期判定。
        var items = new[]
        {
            new DelayedItem { Id = "off", DelaySeconds = 5, Enabled = false, ScheduleCycleId = BuiltinCycleIds.Everyday },
            Item("on", 10, BuiltinCycleIds.Weekdays),
        };

        var plan = SchedulePlan.Build(items, Cycles, new DateOnly(2026, 9, 23));

        Assert.Equal(["on"], Ids(plan));
    }

    [Fact]
    public void Build_UnknownCycleId_FallsBackToEveryday()
    {
        var items = new[] { Item("orphan", 10, "c-deleted-by-hand") };

        var plan = SchedulePlan.Build(items, Cycles, new DateOnly(2026, 9, 27));   // 周日

        Assert.Equal(["orphan"], Ids(plan));
    }

    [Fact]
    public void Build_UsesDefaultEverydayWhenNothingConfigured()
    {
        // 旧配置升级：条目上没有任何周期字段 → 默认每天，行为不变（FR-15.6）。
        var item = new DelayedItem { Id = "legacy", DelaySeconds = 10 };

        var plan = SchedulePlan.Build([item], null, new DateOnly(2026, 9, 27));

        Assert.Equal(["legacy"], Ids(plan));
        Assert.Equal(BuiltinCycleIds.Everyday, item.ScheduleCycleId);
    }

    [Fact]
    public void Build_NullInput_StillThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => SchedulePlan.Build(null!, Cycles, DateOnly.FromDateTime(DateTime.Now)));
    }

    // ── FR-15.26：被周期跳过的条目要能被单列出来（调度端靠它写调度日志） ──────────────

    private static string[] SkippedIds(ScheduleOutcome outcome)
        => [.. outcome.SkippedToday.Select(static item => item.Id)];

    [Fact]
    public void BuildWithSkipped_Saturday_SeparatesPlanFromSkippedItems()
    {
        var items = new[]
        {
            Item("weekday", 10, BuiltinCycleIds.Weekdays),
            Item("weekend", 20, BuiltinCycleIds.Weekends),
            Item("daily", 30, BuiltinCycleIds.Everyday),
        };

        var outcome = SchedulePlan.BuildWithSkipped(items, Cycles, new DateOnly(2026, 9, 26));

        Assert.Equal(["weekend", "daily"], Ids(outcome.Entries));
        Assert.Equal(["weekday"], SkippedIds(outcome));
    }

    [Fact]
    public void BuildWithSkipped_DoesNotPlanTheSkippedItems()
    {
        // 被跳过的条目**不得**混进计划：一旦混进去，调度端就会给它建到点时刻并真的启动它。
        var items = new[]
        {
            Item("skip", 5, BuiltinCycleIds.Weekends),
            Item("run", 10, BuiltinCycleIds.Weekdays),
        };

        var outcome = SchedulePlan.BuildWithSkipped(items, Cycles, new DateOnly(2026, 9, 23));

        Assert.Equal(["run"], Ids(outcome.Entries));
        Assert.DoesNotContain(outcome.Entries, static entry => entry.Item.Id == "skip");
    }

    [Fact]
    public void BuildWithSkipped_AllSkipped_ReturnsEmptyEntriesWithFullSkippedList()
    {
        // 全部跳过 = 计划空。此时"谁被跳过了"是调度日志里唯一能回答"今天为什么没启动"的信息，
        // 所以它必须一条不少（这条路径以前只会静默退出）。
        var items = new[]
        {
            Item("a", 10, BuiltinCycleIds.Weekdays),
            Item("b", 20, BuiltinCycleIds.Weekdays),
        };

        var outcome = SchedulePlan.BuildWithSkipped(items, Cycles, new DateOnly(2026, 9, 27));

        Assert.Empty(outcome.Entries);
        Assert.Equal(["a", "b"], SkippedIds(outcome));
    }

    [Fact]
    public void BuildWithSkipped_SkippedItemKeepsItsDelay()
    {
        // 日志页的「延时」列照常要有值 —— 跳过的条目也是配置里的条目，延时是它的属性。
        var items = new[] { Item("a", 45, BuiltinCycleIds.Weekdays) };

        var outcome = SchedulePlan.BuildWithSkipped(items, Cycles, new DateOnly(2026, 9, 27));

        var skipped = Assert.Single(outcome.SkippedToday);
        Assert.Equal(45, skipped.DelaySeconds);
    }

    [Fact]
    public void BuildWithSkipped_DisabledItemIsNeitherPlannedNorSkipped()
    {
        // 🔴 两层过滤的次序不变式：被用户关掉的条目**不算"今天被跳过"**。
        // 算进去的话，调度日志会把"你自己关的"报成"周期决定今天不启动"，口径就歪了。
        var items = new[]
        {
            new DelayedItem { Id = "off", DelaySeconds = 5, Enabled = false, ScheduleCycleId = BuiltinCycleIds.Weekdays },
            Item("on", 10, BuiltinCycleIds.Weekdays),
        };

        var outcome = SchedulePlan.BuildWithSkipped(items, Cycles, new DateOnly(2026, 9, 27));

        Assert.Empty(outcome.Entries);
        Assert.Equal(["on"], SkippedIds(outcome));
    }

    [Fact]
    public void BuildWithSkipped_WithoutToday_ReportsNothingSkipped()
    {
        // 不传日子 = 不做周期判定，因此也没有"被跳过"这回事（历史调用点的行为不变）。
        var items = new[] { Item("weekend-only", 5, BuiltinCycleIds.Weekends) };

        var outcome = SchedulePlan.BuildWithSkipped(items, Cycles);

        Assert.Equal(["weekend-only"], Ids(outcome.Entries));
        Assert.Empty(outcome.SkippedToday);
    }

    [Fact]
    public void BuildWithSkipped_UnknownCycleId_IsNotSkipped()
    {
        // 引用失效（手改 / 备份还原不一致）回落「每天」—— 它今天照样进计划，
        // 不该被记成"被周期跳过"（FR-15.15：兜底方向永远是更宽松）。
        var items = new[] { Item("orphan", 10, "c-deleted-by-hand") };

        var outcome = SchedulePlan.BuildWithSkipped(items, Cycles, new DateOnly(2026, 9, 27));

        Assert.Equal(["orphan"], Ids(outcome.Entries));
        Assert.Empty(outcome.SkippedToday);
    }

    [Fact]
    public void BuildWithSkipped_NullInput_StillThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => SchedulePlan.BuildWithSkipped(null!, Cycles, DateOnly.FromDateTime(DateTime.Now)));
    }

    [Fact]
    public void Build_IsTheEntriesHalfOfBuildWithSkipped()
    {
        var items = new[]
        {
            Item("weekday", 10, BuiltinCycleIds.Weekdays),
            Item("weekend", 20, BuiltinCycleIds.Weekends),
        };
        var saturday = new DateOnly(2026, 9, 26);

        var plan = SchedulePlan.Build(items, Cycles, saturday);
        var outcome = SchedulePlan.BuildWithSkipped(items, Cycles, saturday);

        Assert.Equal(Ids(plan), Ids(outcome.Entries));
    }
}
