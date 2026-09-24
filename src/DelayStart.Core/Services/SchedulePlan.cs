using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 一次调度的执行计划中的一项。
/// </summary>
/// <param name="Item">原始配置条目（引用，不复制）。</param>
/// <param name="LaunchAt">相对本次调度开始的到点时刻（机制 5：相对登录时刻的绝对时间点，不是累加）。</param>
public sealed record ScheduleEntry(DelayedItem Item, TimeSpan LaunchAt);

/// <summary>
/// 一次调度的**完整判定结果**：进计划的条目 + 因周期今天不启动的启用条目。
/// </summary>
/// <param name="Entries">进计划的条目，按发起顺序排列。</param>
/// <param name="SkippedToday">
/// 启用了、但今天不在其调度周期内的条目（FR-15.26）。它们**不启动**，
/// 但调度端会把它们记进本次调度日志（状态 <see cref="RunItemState.Skipped"/>、原因写明"今天不在启动周期内"）。
/// </param>
/// <remarks>
/// 🔴 <paramref name="SkippedToday"/> 存在的唯一理由是**让"今天为什么什么都没启动"有个答案**。
/// 周期过滤本身不需要它 —— 少启动几个条目不会有任何故障表现，
/// 用户看到的只是一个安静的早晨，而原因（"这条设的是周一至周五"）藏在两次点击之外。
/// 所以它进调度日志，但不进执行计划：不进计划 = 不建到点时刻、不参与收尾判定。
/// </remarks>
public sealed record ScheduleOutcome(
    IReadOnlyList<ScheduleEntry> Entries,
    IReadOnlyList<DelayedItem> SkippedToday);

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
/// <para>
/// 🔴 **被周期跳过的条目不在这里消失，而是单列到 <see cref="ScheduleOutcome.SkippedToday"/>**
/// （FR-15.26）：计划的语义是"要启动什么"，而"今天什么都不启动"这句话也需要一个落点。
/// </para>
/// </remarks>
public static class SchedulePlan
{
    /// <summary>
    /// 生成执行计划（传入 <paramref name="today"/> 时按 FR-15 的调度周期过滤）。
    /// </summary>
    /// <param name="items">配置中的全部条目，无需预先过滤或排序。</param>
    /// <param name="cycles">配置里的自定义周期表；为 <see langword="null"/> 时视作空表。</param>
    /// <param name="today">本次调度当天。**为 <see langword="null"/> 时不施加周期过滤**（不传日子 = 无从判定）。</param>
    /// <param name="calendar">本地法定日历；法定两档缺它时退化为星期近似。</param>
    /// <returns>按发起顺序排列的计划；没有任何启用项或全部被周期跳过时返回空列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 只要计划本身、不要"谁被跳过了"时用它（历史调用点与大多数用例都是这种）。
    /// 想要完整判定结果（调度端写调度日志）用 <see cref="BuildWithSkipped"/>。
    /// </remarks>
    public static IReadOnlyList<ScheduleEntry> Build(
        IReadOnlyList<DelayedItem> items,
        IReadOnlyList<ScheduleCycle>? cycles = null,
        DateOnly? today = null,
        HolidayCalendar? calendar = null)
        => BuildWithSkipped(items, cycles, today, calendar).Entries;

    /// <summary>
    /// 生成执行计划，并把"今天不在周期内"的启用条目一并交出来（FR-15.26）。
    /// </summary>
    /// <param name="items">配置中的全部条目，无需预先过滤或排序。</param>
    /// <param name="cycles">配置里的自定义周期表；为 <see langword="null"/> 时视作空表。</param>
    /// <param name="today">本次调度当天；为 <see langword="null"/> 时不施加周期过滤。</param>
    /// <param name="calendar">本地法定日历；法定两档缺它时退化为星期近似。</param>
    /// <returns>进计划的条目（按发起顺序）+ 今天被跳过的启用条目（同样按发起顺序）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 🔴 **两层过滤的次序不可换**：<see cref="DelayedItem.Enabled"/> 必须排在周期判定之前（见
    /// <see cref="MatchesToday"/>）。调换次序会让被关闭的条目先被周期逻辑消费一次 ——
    /// 现在看只是多做一次无用功，但被关闭的条目会**混进"今天被跳过"的名单**，
    /// 于是调度日志把"用户主动关掉的条目"报成"周期决定今天不启动"，口径就歪了。
    /// </remarks>
    public static ScheduleOutcome BuildWithSkipped(
        IReadOnlyList<DelayedItem> items,
        IReadOnlyList<ScheduleCycle>? cycles = null,
        DateOnly? today = null,
        HolidayCalendar? calendar = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var ordered = items
            .Where(static item => item.Enabled)
            .OrderBy(static item => item, StartupSortComparer.Instance)
            .ToList();

        var entries = new List<ScheduleEntry>(ordered.Count);
        var skipped = new List<DelayedItem>();

        foreach (var item in ordered)
        {
            if (MatchesToday(item, cycles, today, calendar))
            {
                entries.Add(new ScheduleEntry(item, TimeSpan.FromSeconds(item.DelaySeconds)));
            }
            else
            {
                skipped.Add(item);
            }
        }

        return new ScheduleOutcome(entries, skipped);
    }

    /// <summary>
    /// 周期判定（FR-15.4 的第二层）。
    /// </summary>
    /// <remarks>
    /// 🔴 调用方必须已经过 <see cref="DelayedItem.Enabled"/> 过滤（见
    /// <see cref="BuildWithSkipped"/>）：这里**只回答"今天该不该启动"**，
    /// 不回答"用户想不想让它启动"。两个问题混在一个判据里，
    /// 调度日志就再也分不清"被关掉"与"今天轮不到"。
    /// </remarks>
    private static bool MatchesToday(
        DelayedItem item,
        IReadOnlyList<ScheduleCycle>? cycles,
        DateOnly? today,
        HolidayCalendar? calendar)
    {
        if (today is null)
        {
            return true;
        }

        var resolved = ScheduleCycleResolver.Resolve(item.ScheduleCycleId, cycles);
        return ScheduleRulePolicy.Matches(resolved.Kind, resolved.Days, today.Value, calendar);
    }
}
