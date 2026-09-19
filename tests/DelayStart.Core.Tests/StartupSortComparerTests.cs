using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="StartupSortComparer"/> 的单元测试：发起顺序的两级排序与确定性兜底（FR-5.4）。
/// </summary>
public sealed class StartupSortComparerTests
{
    [Fact]
    public void Compare_LowerDelay_ComesFirst()
    {
        // Arrange
        var earlier = CreateItem("a", delaySeconds: 10, sortOrder: 0);
        var later = CreateItem("b", delaySeconds: 60, sortOrder: 0);

        // Act
        var result = StartupSortComparer.Instance.Compare(earlier, later);

        // Assert
        Assert.True(result < 0);
    }

    [Fact]
    public void Compare_SameDelay_LowerSortOrderComesFirst()
    {
        // FR-4.7：延时值相同时才由 SortOrder 决定先后
        var first = CreateItem("a", delaySeconds: 30, sortOrder: 1);
        var second = CreateItem("b", delaySeconds: 30, sortOrder: 2);

        Assert.True(StartupSortComparer.Instance.Compare(first, second) < 0);
    }

    [Fact]
    public void Compare_LowerSortOrderWithHigherDelay_LosesToDelay()
    {
        // 延时是第一级判据：SortOrder 再小也不能越过更早的延时
        var earlierDelay = CreateItem("a", delaySeconds: 10, sortOrder: 99);
        var laterDelay = CreateItem("b", delaySeconds: 20, sortOrder: 0);

        Assert.True(StartupSortComparer.Instance.Compare(earlierDelay, laterDelay) < 0);
    }

    [Fact]
    public void Compare_SameDelayAndSortOrder_FallsBackToIdOrdinal()
    {
        // 第三级判据让排序在不同机器上可复现，便于比对日志
        var alpha = CreateItem("alpha", delaySeconds: 10, sortOrder: 0);
        var beta = CreateItem("beta", delaySeconds: 10, sortOrder: 0);

        Assert.True(StartupSortComparer.Instance.Compare(alpha, beta) < 0);
        Assert.True(StartupSortComparer.Instance.Compare(beta, alpha) > 0);
    }

    [Fact]
    public void Compare_SameInstance_ReturnsZero()
    {
        var item = CreateItem("a", delaySeconds: 10, sortOrder: 0);

        Assert.Equal(0, StartupSortComparer.Instance.Compare(item, item));
    }

    [Fact]
    public void Compare_NullIsSmallest()
    {
        var item = CreateItem("a", delaySeconds: 10, sortOrder: 0);

        Assert.True(StartupSortComparer.Instance.Compare(null, item) < 0);
        Assert.True(StartupSortComparer.Instance.Compare(item, null) > 0);
        Assert.Equal(0, StartupSortComparer.Instance.Compare(null, null));
    }

    [Fact]
    public void Sort_MixedItems_OrdersByDelayThenSortOrder()
    {
        // Arrange：刻意打乱输入顺序
        var items = new List<DelayedItem>
        {
            CreateItem("c", delaySeconds: 60, sortOrder: 0),
            CreateItem("a", delaySeconds: 10, sortOrder: 1),
            CreateItem("b", delaySeconds: 10, sortOrder: 0),
            CreateItem("d", delaySeconds: 30, sortOrder: 5),
            CreateItem("e", delaySeconds: 10, sortOrder: 2),
        };

        // Act
        items.Sort(StartupSortComparer.Instance);

        // Assert
        string[] expected = ["b", "a", "e", "d", "c"];
        Assert.Equal(expected, items.Select(item => item.Id));
    }

    [Fact]
    public void Sort_SameInputDifferentOrder_ProducesIdenticalResult()
    {
        // 确定性：无论输入顺序如何，相同集合排出来的顺序必须一致
        var first = new List<DelayedItem>
        {
            CreateItem("x", delaySeconds: 10, sortOrder: 0),
            CreateItem("y", delaySeconds: 10, sortOrder: 0),
        };

        var second = new List<DelayedItem>
        {
            CreateItem("y", delaySeconds: 10, sortOrder: 0),
            CreateItem("x", delaySeconds: 10, sortOrder: 0),
        };

        first.Sort(StartupSortComparer.Instance);
        second.Sort(StartupSortComparer.Instance);

        Assert.Equal(first.Select(i => i.Id), second.Select(i => i.Id));
    }

    private static DelayedItem CreateItem(string id, int delaySeconds, int sortOrder) => new()
    {
        Id = id,
        Name = id,
        DelaySeconds = delaySeconds,
        SortOrder = sortOrder,
    };
}
