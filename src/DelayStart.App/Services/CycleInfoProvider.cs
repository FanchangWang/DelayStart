using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.App.Services;

/// <summary>
/// 界面侧读「调度周期」的唯一入口：名称、包含哪些天、今天跳不跳（FR-15）。
/// </summary>
/// <remarks>
/// <para>
/// 放在这里而不是让页面各自去解析：周期名要显示给两处看（列表徽标 / 编辑器），
/// "今天跳不跳"要由**同一份**判定算出来 —— 判定一旦分叉，"列表说今天会启动、
/// 弹窗说不会"就成了只会互相消耗信任的 bug。判定本身来自 Core 的
/// <see cref="ScheduleRulePolicy"/>，这里只负责把结果翻成中文文案。
/// </para>
/// <para>
/// 一次 <c>Load</c> 构造一个实例并让全部行共用：今天与法定日历在本次刷新里必须是同一个快照，
/// 否则同一屏里上下两行可能不属于"同一天"。
/// </para>
/// </remarks>
public sealed class CycleInfoProvider
{
    private readonly IReadOnlyList<ScheduleCycle> _cycles;
    private readonly HolidayCalendar? _calendar;
    private readonly DateOnly _today;

    /// <summary>构造。</summary>
    /// <param name="cycles">配置里的自定义周期表。</param>
    /// <param name="calendar">本地法定日历；为 <see langword="null"/> 时法定两档退化为星期近似。</param>
    /// <param name="today">本次刷新的"今天"。</param>
    public CycleInfoProvider(IReadOnlyList<ScheduleCycle> cycles, HolidayCalendar? calendar, DateOnly today)
    {
        _cycles = cycles ?? [];
        _calendar = calendar;
        _today = today;
    }

    /// <summary>本次快照的日期（列表页顶部的「本次登录预览」用它）。</summary>
    public DateOnly Today => _today;

    /// <summary>法定日历是否覆盖今天（不覆盖 = 法定两档在用近似判定）。</summary>
    public bool HasCalendarForToday => _calendar is not null && _calendar.Covers(_today);

    /// <summary>本地法定日历是否覆盖某一年 —— 「这一年到底有没有数据」。</summary>
    /// <param name="year">年份。</param>
    /// <returns>有（哪怕只有一天）为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 用于 §6.5 的到期提醒：判断"次年数据到没到"。判定本身不用它 ——
    /// 判定走 <see cref="ScheduleRulePolicy"/> 的三级降级链，那里是逐日判覆盖的。
    /// </remarks>
    public bool HasCalendarFor(int year) => _calendar is not null && _calendar.Covers(new DateOnly(year, 1, 1));

    /// <summary>
    /// 周期名。
    /// </summary>
    /// <param name="cycleId">周期 id。</param>
    /// <returns>
    /// 内置档的固定名称，或自定义周期的名称；查不到时返回「每天」 ——
    /// 引用失效的兜底方向只能是每天，与 <see cref="ScheduleCycleResolver"/> 一致。
    /// </returns>
    /// <remarks>
    /// 内置档名册在 <see cref="CycleNames"/>（Core）—— 管理端拦"与内置档同名"的自定义周期时
    /// 要用同一份，所以这里只做"id → 名字"的查表 + 自定义周期的回落，不再自己写一套字面量。
    /// </remarks>
    public string NameOf(string? cycleId)
        => CycleNames.Of(cycleId)
            ?? (Find(cycleId)?.Name is { Length: > 0 } name ? name : CycleNames.Everyday);

    /// <summary>这个周期是否**不固定**在几个星期上（法定两档随当年放假安排变动）。</summary>
    public static bool IsDynamic(string? cycleId)
        => cycleId is BuiltinCycleIds.LegalWorkday or BuiltinCycleIds.LegalHoliday;

    /// <summary>
    /// 这个周期包含哪些天。
    /// </summary>
    /// <remarks>
    /// 法定两档不固定在某几个星期上，所以返回的是**今年实际落到的星期集合**
    /// （2026 年「法定工作日」会亮到周日 —— 09-20 是中秋前补班日）。
    /// 这不只是一个展示细节：用户心里"法定工作日 = 周一到周五"其实是不准确的，
    /// 而调休就是反例，把它摆出来才对得上真机行为。
    /// </remarks>
    public WeekdaySet DaysOf(string? cycleId)
    {
        if (cycleId is BuiltinCycleIds.LegalWorkday or BuiltinCycleIds.LegalHoliday)
        {
            return WeekdaysOfLegalCycle(cycleId == BuiltinCycleIds.LegalWorkday);
        }

        var resolved = ScheduleCycleResolver.Resolve(cycleId, _cycles);
        return resolved.Kind switch
        {
            ScheduleRuleKind.Weekdays => WeekdaySet.Monday | WeekdaySet.Tuesday | WeekdaySet.Wednesday
                | WeekdaySet.Thursday | WeekdaySet.Friday,
            ScheduleRuleKind.Weekends => WeekdaySet.Saturday | WeekdaySet.Sunday,
            ScheduleRuleKind.Custom => resolved.Days,
            _ => WeekdaySet.All,   // 每天，以及任何兜底结果
        };
    }

    /// <summary>星期集合的中文（如「周一、周三、周五」；七天天全选为「每天」）。</summary>
    public string DaysTextOf(string? cycleId) => DaysText(DaysOf(cycleId));

    /// <summary>把一组星期翻成中文（七天全选简写成「每天」）。</summary>
    /// <param name="days">星期集合。</param>
    /// <returns>中文文案。</returns>
    public static string DaysText(WeekdaySet days)
    {
        days = WeekdaySets.Sanitize(days);
        return days switch
        {
            WeekdaySet.None => "（未选）",
            WeekdaySet.All => "每天",
            _ => JoinDayNames(days),
        };
    }

    /// <summary>把一组星期逐天列出来，**不做「每天」简写**。</summary>
    /// <param name="days">星期集合。</param>
    /// <returns>如「周一、周三、周五」。</returns>
    /// <remarks>
    /// 🔴 与 <see cref="DaysText"/> 分开的理由：延时弹窗那句要回答的是
    /// 「「每天」到底包含<b>哪些天</b>」—— 简写会得到「「每天」包含每天」这种同义反复，
    /// 等于没回答。那一栏要的正是逐天清单。
    /// </remarks>
    public static string DaysListText(WeekdaySet days)
    {
        days = WeekdaySets.Sanitize(days);
        return days == WeekdaySet.None ? "（未选）" : JoinDayNames(days);
    }

    private static string JoinDayNames(WeekdaySet days)
    {
        var names = new List<string>(7);
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            if (WeekdaySets.Contains(days, day))
            {
                names.Add(NameOfDay(day));
            }
        }

        return string.Join("、", names);
    }

    /// <summary>某一天的中文名（周一…周日）。</summary>
    public static string NameOfDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>
    /// 今天是否被这个周期跳过。
    /// </summary>
    /// <param name="cycleId">周期 id。</param>
    /// <param name="degraded">法定两档是否因缺数据而用星期近似。</param>
    /// <param name="reason">中文原因，进 tooltip。</param>
    /// <returns>跳过为 <see langword="true"/>。</returns>
    public bool IsSkipped(string? cycleId, out bool degraded, out string reason)
    {
        var resolved = ScheduleCycleResolver.Resolve(cycleId, _cycles);
        var matched = ScheduleRulePolicy.Matches(
            resolved.Kind,
            resolved.Days,
            _today,
            _calendar,
            out degraded);

        reason = ReasonOf(resolved.Kind, degraded);
        return !matched;
    }

    private string ReasonOf(ScheduleRuleKind kind, bool degraded)
    {
        if (degraded)
        {
            return kind == ScheduleRuleKind.LegalWorkday
                ? $"缺 {_today.Year} 年法定数据，暂按周一至周五判定"
                : $"缺 {_today.Year} 年法定数据，暂按周六日判定";
        }

        return kind switch
        {
            ScheduleRuleKind.Weekdays or ScheduleRuleKind.Weekends
                => IsWeekday(_today) ? "今天是工作日" : "今天是周末",
            ScheduleRuleKind.LegalWorkday or ScheduleRuleKind.LegalHoliday
                when _calendar is not null && _calendar.Covers(_today)
                => _calendar.IsWorkday(_today) ? "今天是法定工作日（含调休补班）" : "今天是法定放假日",
            ScheduleRuleKind.Custom => "今天不在周期内",
            _ => "每天启动",
        };
    }

    private WeekdaySet WeekdaysOfLegalCycle(bool wantWorkday)
    {
        if (_calendar is null || !_calendar.Covers(_today))
        {
            // 没有数据：退回静态星期规律（与判定链的降级一致），界面上的说明由 degraded 负责。
            return wantWorkday
                ? WeekdaySet.Monday | WeekdaySet.Tuesday | WeekdaySet.Wednesday | WeekdaySet.Thursday | WeekdaySet.Friday
                : WeekdaySet.Saturday | WeekdaySet.Sunday;
        }

        var mask = WeekdaySet.None;
        var date = new DateOnly(_today.Year, 1, 1);
        var end = new DateOnly(_today.Year, 12, 31);
        while (date <= end)
        {
            var isWorkday = _calendar.IsWorkday(date);
            if (isWorkday == wantWorkday)
            {
                mask |= WeekdaySets.ToFlag(date.DayOfWeek);
            }

            date = date.AddDays(1);
        }

        return mask;
    }

    private ScheduleCycle? Find(string? cycleId)
    {
        if (string.IsNullOrWhiteSpace(cycleId))
        {
            return null;
        }

        foreach (var cycle in _cycles)
        {
            if (cycle is not null && string.Equals(cycle.Id, cycleId, StringComparison.Ordinal))
            {
                return cycle;
            }
        }

        return null;
    }

    private static bool IsWeekday(DateOnly date)
        => date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
}
