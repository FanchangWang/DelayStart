namespace DelayStart.Core.Models;

/// <summary>
/// 本地法定日历：某"法定工作日"（含调休补班日）与"休息日"的集合（FR-15 / docs/schedule-cycle 三）。
/// </summary>
/// <remarks>
/// <para>
/// 只回答一个问题：**这一天在法定日历上是不是工作日**。它<b>不</b>回答"该不该启动"——
/// 那由 <c>ScheduleRulePolicy</c> 结合周期种类决定（"周六日"档刻意不读日历）。
/// </para>
/// <para>
/// 数据**只来自本地文件**（NFR-x）：调度端与守卫端禁止联网，
/// 下载与转换只在管理端做，落地后是归一化格式的 JSON（见 <c>HolidayCalendarDocument</c>）。
/// 这一层划分的意义：登录链路上的网络请求不可靠，把"今天启不启动"押在它上面违反「判定可靠」。
/// </para>
/// <para>
/// 不可变性是刻意的：一次调度内这份数据被读取几十次，中途变化会让同一天出现两种判定。
/// </para>
/// </remarks>
public sealed class HolidayCalendar
{
    /// <summary>空日历：任何年份都不覆盖。用于"没有任何可用数据"的情形。</summary>
    public static HolidayCalendar Empty { get; } = new([], []);

    private readonly HashSet<DateOnly> _workdays;
    private readonly HashSet<DateOnly> _restDays;
    private readonly HashSet<int> _coveredYears;

    private HolidayCalendar(IEnumerable<DateOnly> workdays, IEnumerable<DateOnly> restDays)
    {
        _workdays = [.. workdays];
        _restDays = [.. restDays];

        // CoveredYears 只用来判"这一年有没有数据"—— 降级链的第 ② 级与第 ③ 级靠它区分：
        // 有数据但今天不在表里 → 退回星期规律（无声）；没有数据 → 同上但要明确告诉用户（E-x4）。
        _coveredYears = [];
        foreach (var day in _workdays)
        {
            _coveredYears.Add(day.Year);
        }

        foreach (var day in _restDays)
        {
            _coveredYears.Add(day.Year);
        }
    }

    /// <summary>被数据覆盖的年份。</summary>
    public IReadOnlyCollection<int> CoveredYears => _coveredYears;

    /// <summary>是否一份数据都没有。</summary>
    public bool IsEmpty => _coveredYears.Count == 0;

    /// <summary>这一年是否有数据（不关心具体哪天）。</summary>
    /// <param name="date">待判断的日期。</param>
    /// <returns>有数据为 <see langword="true"/>。</returns>
    public bool Covers(DateOnly date) => _coveredYears.Contains(date.Year);

    /// <summary>这一天是否是法定工作日（含被占用的周末 → 调休补班日）。</summary>
    /// <param name="date">待判断的日期。</param>
    /// <returns>工作日为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="date"/> 所在年份不在 <see cref="CoveredYears"/> 里。
    /// 调用方必须先用 <see cref="Covers"/> 分流：那年没数据时该走降级，而不是问一个没有数据的日历。
    /// </exception>
    public bool IsWorkday(DateOnly date)
    {
        EnsureCovered(date);
        return _workdays.Contains(date) || !_restDays.Contains(date) && IsWeekdayBySystem(date);
    }

    /// <summary>这一天是否是休息日（法定假 + 调休放假 + 并入的周末）。</summary>
    /// <param name="date">待判断的日期。</param>
    /// <returns>休息日为 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">同 <see cref="IsWorkday"/>。</exception>
    public bool IsRestDay(DateOnly date)
    {
        EnsureCovered(date);
        return _restDays.Contains(date) || !_workdays.Contains(date) && !IsWeekdayBySystem(date);
    }

    /// <summary>
    /// 用两组日期构造日历。同一天同时出现在两个集合里时以 <c>restDays</c> 为准。
    /// </summary>
    /// <param name="workdays">调休补班日。</param>
    /// <param name="restDays">休息日。</param>
    /// <returns>日历实例。</returns>
    public static HolidayCalendar Create(IEnumerable<DateOnly> workdays, IEnumerable<DateOnly> restDays)
    {
        ArgumentNullException.ThrowIfNull(workdays);
        ArgumentNullException.ThrowIfNull(restDays);

        return new HolidayCalendar(workdays, restDays);
    }

    private static bool IsWeekdayBySystem(DateOnly date)
        => date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;

    private void EnsureCovered(DateOnly date)
    {
        if (!Covers(date))
        {
            throw new ArgumentOutOfRangeException(
                nameof(date),
                date,
                $"法定日历不含 {date.Year} 年的数据，应由调用方先按降级规则处理。");
        }
    }
}
