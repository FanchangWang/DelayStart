using System.Text.Json.Serialization;

namespace DelayStart.Core.Models;

/// <summary>
/// 一周七天的集合，位掩码（D88 = 🅐）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **位序是本项目自定义的（周一 = bit0），绝不能直接复用 <see cref="DayOfWeek"/> 的数值。**
/// .NET 的 <c>DayOfWeek.Sunday == 0</c>（周日起头），而业务习惯从周一起头；
/// 一旦把两者混淆，会得到一个"每个星期都成立、且不报错"的错位 bug ——
/// 这类 bug 只在真机的某几天才显得"有点怪"，是最难查的那一类。所以位序在这里独占定义，
/// 任何 <c>DayOfWeek</c> ↔ 掩码的转换都必须走 <see cref="WeekdaySets"/>，并由单测逐位锁死。
/// </para>
/// <para>
/// JSON 里写的是**整数**（由特性上的数值枚举转换器保证），不是字符串：避开了
/// "中文逗号分隔的组合枚举"在反序列化时被 trim 出错的可能，也让手改配置的人一眼看懂。
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonNumberEnumConverter<WeekdaySet>))]
[Flags]
public enum WeekdaySet
{
    /// <summary>空集合。周期的星期集合**不允许**取这个值（FR-15.20）。</summary>
    None = 0,

    /// <summary>周一（bit0）。</summary>
    Monday = 1 << 0,

    /// <summary>周二（bit1）。</summary>
    Tuesday = 1 << 1,

    /// <summary>周三（bit2）。</summary>
    Wednesday = 1 << 2,

    /// <summary>周四（bit3）。</summary>
    Thursday = 1 << 3,

    /// <summary>周五（bit4）。</summary>
    Friday = 1 << 4,

    /// <summary>周六（bit5）。</summary>
    Saturday = 1 << 5,

    /// <summary>周日（bit6）。</summary>
    Sunday = 1 << 6,

    /// <summary>全部七天。</summary>
    All = Monday | Tuesday | Wednesday | Thursday | Friday | Saturday | Sunday,
}

/// <summary>
/// <see cref="WeekdaySet"/> 与 <see cref="DayOfWeek"/> 之间**唯一**的转换入口。
/// </summary>
/// <remarks>
/// 集中在一处的理由与上面的位序说明是同一件事：转换散落各处，迟早有人写出
/// <c>1 &lt;&lt; (int)dayOfWeek</c> 这种"看起来很对"的错位代码。
/// </remarks>
public static class WeekdaySets
{
    /// <summary>把 <see cref="DayOfWeek"/> 转成掩码位。</summary>
    /// <param name="day">待转换的星期。</param>
    /// <returns>对应位；入参是非法枚举值时返回 <see cref="WeekdaySet.None"/>。</returns>
    public static WeekdaySet ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekdaySet.Monday,
        DayOfWeek.Tuesday => WeekdaySet.Tuesday,
        DayOfWeek.Wednesday => WeekdaySet.Wednesday,
        DayOfWeek.Thursday => WeekdaySet.Thursday,
        DayOfWeek.Friday => WeekdaySet.Friday,
        DayOfWeek.Saturday => WeekdaySet.Saturday,
        DayOfWeek.Sunday => WeekdaySet.Sunday,
        _ => WeekdaySet.None,
    };

    /// <summary>集合是否包含这一天。</summary>
    /// <param name="set">星期集合。</param>
    /// <param name="day">待判断的星期。</param>
    /// <returns>包含为 <see langword="true"/>。</returns>
    public static bool Contains(WeekdaySet set, DayOfWeek day) => (set & ToFlag(day)) != 0;

    /// <summary>清掉越界位（手改配置可能写出 0x80 之类的脏位）。</summary>
    /// <param name="set">原值。</param>
    /// <returns>仅保留周一至周日七位的结果。</returns>
    public static WeekdaySet Sanitize(WeekdaySet set) => set & WeekdaySet.All;

    /// <summary>集合里的天数。</summary>
    /// <param name="set">星期集合。</param>
    /// <returns>0–7。</returns>
    public static int Count(WeekdaySet set)
    {
        var value = (int)Sanitize(set);
        var count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }

        return count;
    }
}
