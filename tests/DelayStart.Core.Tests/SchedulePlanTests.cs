using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="SchedulePlan"/> 的单元测试：启用过滤、发起顺序、到点时刻（FR-5.3 / FR-5.4）。
/// </summary>
public sealed class SchedulePlanTests
{
    private static DelayedItem Item(string id, int delaySeconds, int sortOrder = 0, bool enabled = true)
        => new()
        {
            Id = id,
            Name = id,
            DelaySeconds = delaySeconds,
            SortOrder = sortOrder,
            Enabled = enabled,
        };

    private static string[] Ids(IReadOnlyList<ScheduleEntry> plan)
        => [.. plan.Select(static entry => entry.Item.Id)];

    [Fact]
    public void Build_EmptyInput_ReturnsEmptyPlan()
    {
        var plan = SchedulePlan.Build(Array.Empty<DelayedItem>());

        Assert.Empty(plan);
    }

    [Fact]
    public void Build_AllDisabled_ReturnsEmptyPlan()
    {
        var items = new[] { Item("a", 10, enabled: false), Item("b", 20, enabled: false) };

        var plan = SchedulePlan.Build(items);

        Assert.Empty(plan);
    }

    [Fact]
    public void Build_ExcludesDisabledItems()
    {
        var items = new[] { Item("keep", 10), Item("drop", 5, enabled: false) };

        var plan = SchedulePlan.Build(items);

        Assert.Equal(["keep"], Ids(plan));
    }

    [Fact]
    public void Build_OrdersByDelayAscending()
    {
        var items = new[] { Item("c", 60), Item("a", 10), Item("b", 30) };

        var plan = SchedulePlan.Build(items);

        Assert.Equal(["a", "b", "c"], Ids(plan));
    }

    [Fact]
    public void Build_SameDelay_OrdersBySortOrder()
    {
        // StartupSortComparer 第二级：同延时按 SortOrder 升序
        var items = new[] { Item("second", 30, sortOrder: 2), Item("first", 30, sortOrder: 1) };

        var plan = SchedulePlan.Build(items);

        Assert.Equal(["first", "second"], Ids(plan));
    }

    [Fact]
    public void Build_SameDelayAndSortOrder_FallsBackToIdOrdinal()
    {
        // 第三级 tie-breaker：Id 序数比较，让不同机器上的日志顺序可比对
        var items = new[] { Item("b", 30, sortOrder: 1), Item("a", 30, sortOrder: 1) };

        var plan = SchedulePlan.Build(items);

        Assert.Equal(["a", "b"], Ids(plan));
    }

    [Fact]
    public void Build_ZeroDelay_LaunchAtIsZero()
    {
        var plan = SchedulePlan.Build([Item("now", 0)]);

        Assert.Equal(TimeSpan.Zero, Assert.Single(plan).LaunchAt);
    }

    [Fact]
    public void Build_LaunchAtIsAbsoluteFromRunStartNotCumulative()
    {
        // 机制 5：每个条目的到点时刻只由它自己的 DelaySeconds 决定，不与前序条目累加
        var items = new[] { Item("a", 10), Item("b", 10), Item("c", 60) };

        var plan = SchedulePlan.Build(items);

        Assert.Equal(
            [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)],
            plan.Select(static entry => entry.LaunchAt));
    }

    [Fact]
    public void Build_DoesNotReorderOrTrimInputCollection()
    {
        var items = new List<DelayedItem> { Item("c", 60), Item("a", 10), Item("b", 30) };
        var before = items.ToArray();

        _ = SchedulePlan.Build(items);

        Assert.Equal(before, items);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void Build_DoesNotModifyItemConfiguration()
    {
        var item = Item("a", 10, sortOrder: 7);

        _ = SchedulePlan.Build([item, Item("b", 5)]);

        // SortOrder 是持久化配置字段，不是本次调度的运行序号，不得被写
        Assert.Equal(7, item.SortOrder);
        Assert.Equal(10, item.DelaySeconds);
    }

    [Fact]
    public void Build_ReturnsOriginalItemReference()
    {
        var item = Item("a", 10);

        var plan = SchedulePlan.Build([item]);

        Assert.Same(item, Assert.Single(plan).Item);
    }

    [Fact]
    public void Build_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SchedulePlan.Build(null!));
    }
}
