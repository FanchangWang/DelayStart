using System.Globalization;

using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// 2026 年真实放假数据的共享样本（FR-15 判定测试的事实依据）。
/// </summary>
/// <remarks>
/// <para>
/// 数据取自 holiday-cn / 2026.json（国务院办公厅 2026 年放假安排）：
/// 33 个休息日 + 6 个调休补班日。**逐个硬敲进这里**而不是在测试里联网拉，
/// 因为单测绝不能依赖外部服务。
/// </para>
/// <para>
/// 刻意挑了三个易错样本进注释：补班日（周日上班）、法定假中的工作日（周四放假）、
/// 以及普通日子（不在清单里 —— 降级链第 ② 级的触发条件）。
/// </para>
/// </remarks>
internal static class RealCalendar2026
{
    /// <summary>农历新年前后：2/15–2/23 放假，2/14 与 2/28 补班（都是周六/周日之外的日子）。</summary>
    public static readonly string[] RestDays =
    [
        "2026-01-01", "2026-01-02", "2026-01-03",
        "2026-02-15", "2026-02-16", "2026-02-17", "2026-02-18", "2026-02-19", "2026-02-20",
        "2026-02-21", "2026-02-22", "2026-02-23",
        "2026-04-04", "2026-04-05", "2026-04-06",
        "2026-05-01", "2026-05-02", "2026-05-03", "2026-05-04", "2026-05-05",
        "2026-06-19", "2026-06-20", "2026-06-21",
        "2026-09-25", "2026-09-26", "2026-09-27",
        "2026-10-01", "2026-10-02", "2026-10-03", "2026-10-04", "2026-10-05",
        "2026-10-06", "2026-10-07",
    ];

    /// <summary>调休补班日：其中有周日（09-20 中秋前补班）与周六（10-10 国庆后补班）。</summary>
    public static readonly string[] Workdays =
    [
        "2026-01-04", "2026-02-14", "2026-02-28", "2026-05-09", "2026-09-20", "2026-10-10",
    ];

    /// <summary>构造一份可直接判定的日历。</summary>
    public static HolidayCalendar Create()
        => HolidayCalendar.Create(
            Workdays.Select(static value => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            RestDays.Select(static value => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture)));

    /// <summary>2026-09-20，周日 —— **中秋前调休补班日**（证明法定档压过星期规律）。</summary>
    public static DateOnly MakeUpWorkSunday => new(2026, 9, 20);

    /// <summary>2026-10-01，周四 —— 国庆节，法定放假日。</summary>
    public static DateOnly NationalDay => new(2026, 10, 1);

    /// <summary>2026-09-23，周三 —— 普通工作日，不在两个清单里（第 ② 级星期回退）。</summary>
    public static DateOnly OrdinaryWednesday => new(2026, 9, 23);

    /// <summary>2026-10-17，周六 —— 普通周末。</summary>
    public static DateOnly OrdinarySaturday => new(2026, 10, 17);
}
