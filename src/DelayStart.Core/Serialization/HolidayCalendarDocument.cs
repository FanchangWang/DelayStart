using System.Globalization;

using DelayStart.Core.Models;

namespace DelayStart.Core.Serialization;

/// <summary>
/// 法定日历的**归一化**落盘格式（<c>%LOCALAPPDATA%\DelayStart\holidays\{year}.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要归一化：源文件（holiday-cn）的 schema 是第三方定的 —— 它现在是
/// <c>{ year, papers, days: [{ name, date, isOffDay }] }</c>，明天可能改字段名或多套一层。
/// 归一化把这层不确定性关在**管理端的转换器**里：<see cref="HolidayCalendar"/> 的判定代码
/// 与磁盘格式都不受牵连。
/// </para>
/// <para>
/// <c>workdays</c> / <c>restDays</c> 存 <b>ISO 日期字符串</b>（<c>yyyy-MM-dd</c>）而不是第 N 天这样的数字：
/// 配置文件是给人看的，<c>"2026-09-20"</c> 一眼能认出来是中秋前补班，<c>17898</c> 不能。
/// </para>
/// <para>
/// 🔴 这是本仓库里唯一一个**跨三层共用**的数据文件：管理端写、调度端与守卫端读。
/// 因此读取它的代码必须"失败即降级"，绝不能抛异常打断登录。
/// </para>
/// </remarks>
public sealed class HolidayCalendarDocument
{
    /// <summary>日期解析格式。写入方与读取方都固定用它，手工编辑也按它。</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>年份。必须与文件名一致，否则视为损坏。</summary>
    public int Year { get; set; }

    /// <summary>地区代码，当前恒为 <c>CN</c>；预留给将来扩展。</summary>
    public string? Region { get; set; }

    /// <summary>数据源标识（如 <c>holiday-cn</c>）。</summary>
    public string? Source { get; set; }

    /// <summary>面向用户的数据源说明（如「国务院办公厅 2026 年放假安排」）。</summary>
    public string? SourceLabel { get; set; }

    /// <summary>来源原文链接（gov.cn 公告）。</summary>
    public string? SourceRef { get; set; }

    /// <summary>数据更新时间，ISO 8601。</summary>
    public string? UpdatedAt { get; set; }

    /// <summary>调休补班日（含被占用的周末）。</summary>
    public List<string> Workdays { get; set; } = [];

    /// <summary>休息日（法定假 + 调休放假 + 并入的周末）。</summary>
    public List<string> RestDays { get; set; } = [];

    /// <summary>
    /// 转成判定用的日历。无法解析的日期**跳过**而不是抛异常 —— 一条坏数据不该让整年失效。
    /// </summary>
    /// <returns>日历实例；两个集合都无法解析时得到空日历。</returns>
    public HolidayCalendar ToCalendar()
    {
        return HolidayCalendar.Create(Parse(Workdays), Parse(RestDays));
    }

    private static List<DateOnly> Parse(List<string>? values)
    {
        var result = new List<DateOnly>();
        if (values is null)
        {
            return result;
        }

        foreach (var value in values)
        {
            if (DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                result.Add(date);
            }
        }

        return result;
    }
}
