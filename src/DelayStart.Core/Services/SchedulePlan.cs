using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 一次调度的执行计划中的一项。
/// </summary>
/// <param name="Item">原始配置条目（引用，不复制）。</param>
/// <param name="LaunchAt">相对本次调度开始的到点时刻（机制 5：相对登录时刻的绝对时间点，不是累加）。</param>
public sealed record ScheduleEntry(DelayedItem Item, TimeSpan LaunchAt);

/// <summary>
/// 生成一次调度的执行计划：过滤启用项 → 按 <see cref="StartupSortComparer"/> 排序 → 算出各自到点时刻。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来的理由：这段逻辑原本内嵌在调度端的 <c>Initialize()</c> 里，而调度端是 NativeAOT
/// 的 WinExe、没有单元测试工程，导致发起顺序（FR-5.4）只能靠真机验证。移到 Core 后可逐条单测。
/// </para>
/// <para>
/// 🔴 **顺序语义：返回列表的下标即发起顺序**，不额外引入 Order 字段。理由：
/// <see cref="DelayedItem.SortOrder"/> 是持久化的用户配置（同延时内的相对次序），
/// 与"本次调度中的位置"不是一回事，两套序号并存会被互相污染。
/// </para>
/// </remarks>
public static class SchedulePlan
{
    /// <summary>
    /// 生成执行计划。
    /// </summary>
    /// <param name="items">配置中的全部条目，无需预先过滤或排序。</param>
    /// <returns>按发起顺序排列的计划；没有任何启用项时返回空列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> 为 <see langword="null"/>。</exception>
    public static IReadOnlyList<ScheduleEntry> Build(IReadOnlyList<DelayedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var ordered = items
            .Where(static item => item.Enabled)
            .OrderBy(static item => item, StartupSortComparer.Instance)
            .ToList();

        var plan = new List<ScheduleEntry>(ordered.Count);
        foreach (var item in ordered)
        {
            plan.Add(new ScheduleEntry(item, TimeSpan.FromSeconds(item.DelaySeconds)));
        }

        return plan;
    }
}
