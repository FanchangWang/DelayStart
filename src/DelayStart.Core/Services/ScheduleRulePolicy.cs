using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// "今天该不该启动"的**唯一**判定入口（FR-15.1 / FR-15.3 / FR-15.5）。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数：无状态、无 I/O、无网络、无反射，调度端（NativeAOT）与管理端共用同一份。
/// 这是 NFR-z 的直接要求 —— 判定一旦分叉，"预览里说会启动、实际没启动"就成了常态。
/// </para>
/// <para>
/// 判定时刻 = **本次调度的当天日期**，一次取用、全流程一致（FR-15.5）：
/// 跨午夜不重判，否则 23:59 启动的计划会在零点整换一套规则，行为不可解释。
/// </para>
/// </remarks>
public static class ScheduleRulePolicy
{
    /// <summary>
    /// 今天是否命中。
    /// </summary>
    /// <param name="kind">周期种类（来自 <see cref="ScheduleCycleResolver"/>）。</param>
    /// <param name="days">自定义周期的星期集合。</param>
    /// <param name="today">本次调度当天；调度端启动时取一次。</param>
    /// <param name="calendar">本地法定日历；为 <see langword="null"/> 或不覆盖今天时按 §4.3 降级。</param>
    /// <param name="degraded">
    /// 出参：法定两档是否因缺数据而退化为星期近似。🔴 为真时调用方**必须**让用户看见（E-x4）。
    /// </param>
    /// <returns>命中为 <see langword="true"/>。</returns>
    public static bool Matches(
        ScheduleRuleKind kind,
        WeekdaySet days,
        DateOnly today,
        HolidayCalendar? calendar,
        out bool degraded)
    {
        degraded = false;

        switch (kind)
        {
            case ScheduleRuleKind.Weekdays:
                return IsWeekday(today);

            case ScheduleRuleKind.Weekends:
                return !IsWeekday(today);

            case ScheduleRuleKind.LegalWorkday:
            case ScheduleRuleKind.LegalHoliday:
                return MatchesLegalDay(kind, today, calendar, ref degraded);

            case ScheduleRuleKind.Custom:
                return WeekdaySets.Contains(days, today.DayOfWeek);

            default:
                // Everyday，以及任何**越界枚举值** —— 后者同样按"每天"处理而不是报错：
                // 被第三方工具写坏的配置不该让条目停摆（FR-15.7 的取值方向与此一致）。
                return true;
        }
    }

    /// <summary>
    /// 今天是否命中（不需要降级信息时用这个重载）。
    /// </summary>
    /// <param name="kind">周期种类。</param>
    /// <param name="days">自定义周期的星期集合。</param>
    /// <param name="today">本次调度当天。</param>
    /// <param name="calendar">本地法定日历。</param>
    /// <returns>命中为 <see langword="true"/>。</returns>
    public static bool Matches(ScheduleRuleKind kind, WeekdaySet days, DateOnly today, HolidayCalendar? calendar)
        => Matches(kind, days, today, calendar, out _);

    private static bool MatchesLegalDay(
        ScheduleRuleKind kind,
        DateOnly today,
        HolidayCalendar? calendar,
        ref bool degraded)
    {
        var wantWorkday = kind is ScheduleRuleKind.LegalWorkday;

        if (calendar is not null && calendar.Covers(today))
        {
            // ① 准：以官方日历为准。调休数据完全统治星期规律 ——
            // 2026-09-20 是周日，但它是中秋前补班日，法定工作日档必须启动。
            var isWorkday = calendar.IsWorkday(today);
            return wantWorkday ? isWorkday : !isWorkday;
        }

        // 第 ② 级（有数据但今天不在两个清单里 → 退回星期规律）由 calendar.IsWorkday 内部处理，
        // 走到这里只可能是第 ③ 级：**这一年没有数据**（未下载 / 下载失败 / 官方尚未发布 / 文件损坏）。
        // 判定结果与第 ② 级一致，但必须打标记 —— 用户得知道这是近似而不是官方答案。
        degraded = true;
        var weekday = IsWeekday(today);
        return wantWorkday ? weekday : !weekday;
    }

    private static bool IsWeekday(DateOnly date)
        => date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
}
