using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ScheduleRulePolicy"/> 的单元测试：五个内置档 × 七天，以及用真实 2026 放假数据
/// 锁住补班日 / 法定假 / 降级三条最容易写错的路径（FR-15.1 / FR-15.3）。
/// </summary>
/// <remarks>
/// <para>
/// 判定错了是什么后果？不是报错，而是"某天它没启动" —— 用户得回想那天是不是周末、
/// 是不是调休，才能对上账。所以与其指望真机撞上一个补班日来验证，不如把那天写死在这里。
/// </para>
/// </remarks>
public sealed class ScheduleRulePolicyTests
{
    private static bool Matches(ScheduleRuleKind kind, DateOnly date, HolidayCalendar? calendar = null)
        => ScheduleRulePolicy.Matches(kind, WeekdaySet.None, date, calendar);

    [Theory]
    [InlineData(2026, 9, 21)]   // 周一
    [InlineData(2026, 9, 22)]
    [InlineData(2026, 9, 23)]
    [InlineData(2026, 9, 24)]
    [InlineData(2026, 9, 25)]   // 周五
    public void Everyday_MatchesAnyDay(int year, int month, int day)
    {
        var today = new DateOnly(year, month, day);

        Assert.True(Matches(ScheduleRuleKind.Everyday, today));
    }

    [Theory]
    [InlineData(2026, 9, 26)]   // 周六
    [InlineData(2026, 9, 27)]   // 周日
    public void Everyday_MatchesWeekendToo(int year, int month, int day)
    {
        var today = new DateOnly(year, month, day);

        Assert.True(Matches(ScheduleRuleKind.Everyday, today));
    }

    [Fact]
    public void Weekdays_MatchesMondayThroughFridayOnly()
    {
        Assert.True(Matches(ScheduleRuleKind.Weekdays, new DateOnly(2026, 9, 21)));
        Assert.True(Matches(ScheduleRuleKind.Weekdays, new DateOnly(2026, 9, 25)));
        Assert.False(Matches(ScheduleRuleKind.Weekdays, new DateOnly(2026, 9, 26)));
        Assert.False(Matches(ScheduleRuleKind.Weekdays, new DateOnly(2026, 9, 27)));
    }

    [Fact]
    public void Weekends_MatchesSaturdayAndSundayOnly()
    {
        Assert.True(Matches(ScheduleRuleKind.Weekends, new DateOnly(2026, 9, 26)));
        Assert.True(Matches(ScheduleRuleKind.Weekends, new DateOnly(2026, 9, 27)));
        Assert.False(Matches(ScheduleRuleKind.Weekends, new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public void LegalWorkday_MakeUpWorkSunday_Runs()
    {
        // 2026-09-20 是周日，但它是中秋前调休补班日 —— 法定档必须压过星期规律（第 ① 级）。
        var calendar = RealCalendar2026.Create();

        Assert.True(Matches(ScheduleRuleKind.LegalWorkday, RealCalendar2026.MakeUpWorkSunday, calendar));
    }

    [Fact]
    public void LegalHoliday_MakeUpWorkSunday_DoesNotRun()
    {
        // 同一天：这是上班日，法定节假日档不该启动。
        var calendar = RealCalendar2026.Create();

        Assert.False(Matches(ScheduleRuleKind.LegalHoliday, RealCalendar2026.MakeUpWorkSunday, calendar));
    }

    [Fact]
    public void LegalHoliday_NationalDay_Runs()
    {
        var calendar = RealCalendar2026.Create();

        Assert.True(Matches(ScheduleRuleKind.LegalHoliday, RealCalendar2026.NationalDay, calendar));
    }

    [Fact]
    public void LegalWorkday_NationalDay_DoesNotRun()
    {
        var calendar = RealCalendar2026.Create();

        Assert.False(Matches(ScheduleRuleKind.LegalWorkday, RealCalendar2026.NationalDay, calendar));
    }

    [Fact]
    public void LegalWorkday_OrdinaryWednesday_RunsByWeekdayFallback()
    {
        // 普通日子不在两个清单里 —— 这是**常态**而不是异常（源只收特殊日）。
        // 少了这一级，会得出"普通周三不是法定工作日"的荒谬结论。
        var calendar = RealCalendar2026.Create();

        Assert.True(Matches(ScheduleRuleKind.LegalWorkday, RealCalendar2026.OrdinaryWednesday, calendar));
    }

    [Fact]
    public void LegalHoliday_OrdinarySaturday_RunsByWeekdayFallback()
    {
        var calendar = RealCalendar2026.Create();

        Assert.True(Matches(ScheduleRuleKind.LegalHoliday, RealCalendar2026.OrdinarySaturday, calendar));
    }

    [Fact]
    public void LegalWorkday_WithDataForThisYear_IsNotDegraded()
    {
        var matched = ScheduleRulePolicy.Matches(
            ScheduleRuleKind.LegalWorkday,
            WeekdaySet.None,
            RealCalendar2026.MakeUpWorkSunday,
            RealCalendar2026.Create(),
            out var degraded);

        Assert.True(matched);
        Assert.False(degraded);
    }

    [Fact]
    public void LegalWorkday_NoCalendar_FallsBackToWeekdayAndReportsDegraded()
    {
        // 没有 2026 年数据：仍然按周一至周五近似判定（🅒"视作不启动"会让条目静默停摆），
        // 但必须把 degraded 打出去 —— 用户有权要求知道这是近似而不是官方答案。
        var matched = ScheduleRulePolicy.Matches(
            ScheduleRuleKind.LegalWorkday,
            WeekdaySet.None,
            RealCalendar2026.MakeUpWorkSunday,
            calendar: null,
            out var degraded);

        Assert.False(matched);   // 周日 → 不是工作日
        Assert.True(degraded);
    }

    [Fact]
    public void LegalHoliday_NoCalendar_FallsBackToWeekendAndReportsDegraded()
    {
        var matched = ScheduleRulePolicy.Matches(
            ScheduleRuleKind.LegalHoliday,
            WeekdaySet.None,
            RealCalendar2026.OrdinarySaturday,
            calendar: null,
            out var degraded);

        Assert.True(matched);
        Assert.True(degraded);
    }

    [Fact]
    public void LegalWorkday_CalendarWithoutThisYear_IsDegraded()
    {
        // 日历里有 2025 但今天是 2027 —— 数据不属于这一年，等同于没有。
        var otherYear = HolidayCalendar.Create(
            [new DateOnly(2025, 1, 2)],
            [new DateOnly(2025, 1, 1)]);

        _ = ScheduleRulePolicy.Matches(
            ScheduleRuleKind.LegalWorkday,
            WeekdaySet.None,
            new DateOnly(2027, 1, 4),
            otherYear,
            out var degraded);

        Assert.True(degraded);
    }

    [Fact]
    public void Custom_MatchesOnlySelectedDays()
    {
        var days = WeekdaySet.Monday | WeekdaySet.Wednesday | WeekdaySet.Friday;

        Assert.True(ScheduleRulePolicy.Matches(ScheduleRuleKind.Custom, days, new DateOnly(2026, 9, 21), null));
        Assert.False(ScheduleRulePolicy.Matches(ScheduleRuleKind.Custom, days, new DateOnly(2026, 9, 22), null));
        Assert.True(ScheduleRulePolicy.Matches(ScheduleRuleKind.Custom, days, new DateOnly(2026, 9, 23), null));
    }

    [Fact]
    public void Custom_EmptySet_NeverMatches()
    {
        // 空集不是合法的用户配置（FR-15.20 拦在创建阶段），但一旦流经判定，
        // 必须是"不启动"而不是悄悄变成每天 —— 这里选择把它当作最保守的答案。
        Assert.False(ScheduleRulePolicy.Matches(ScheduleRuleKind.Custom, WeekdaySet.None, new DateOnly(2026, 9, 21), null));
    }

    [Fact]
    public void OutOfRangeKind_TreatedAsEveryday()
    {
        // 被第三方工具写坏的枚举值：**按每天处理**，不抛异常也不停摆（FR-15.7）。
        Assert.True(Matches((ScheduleRuleKind)99, new DateOnly(2026, 9, 26)));
    }
}
