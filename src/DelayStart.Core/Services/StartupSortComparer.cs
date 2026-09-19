using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 延时条目的发起顺序比较器：先按延时升序，同延时按 <see cref="DelayedItem.SortOrder"/> 升序（FR-5.4）。
/// </summary>
/// <remarks>
/// <para>
/// 排序**只决定发起顺序**，不保证前一个程序已完成初始化 —— 有依赖关系的程序必须配置
/// 不同的延时值。UI 有责任把这一点提示给用户（坑 7）。
/// </para>
/// <para>
/// 第三级用 <see cref="DelayedItem.Id"/> 做最终 tie-breaker：前两级相同时排序是稳定的，
/// 但"稳定"依赖输入顺序，而输入顺序来自文件读取，不保证跨机器一致。
/// 补一个确定性判据可以让不同机器上的日志顺序可比对。
/// </para>
/// </remarks>
public sealed class StartupSortComparer : IComparer<DelayedItem>
{
    /// <summary>共享实例。</summary>
    public static StartupSortComparer Instance { get; } = new();

    private StartupSortComparer()
    {
    }

    /// <inheritdoc />
    public int Compare(DelayedItem? x, DelayedItem? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var byDelay = x.DelaySeconds.CompareTo(y.DelaySeconds);
        if (byDelay != 0)
        {
            return byDelay;
        }

        var bySortOrder = x.SortOrder.CompareTo(y.SortOrder);
        if (bySortOrder != 0)
        {
            return bySortOrder;
        }

        return string.CompareOrdinal(x.Id, y.Id);
    }
}
