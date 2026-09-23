namespace DelayStart.Core.Models;

/// <summary>
/// 内置周期的判定种类。
/// </summary>
/// <remarks>
/// 🔴 **它不再是条目的持久化字段**（这是 v2 → v3 的关键改动）：条目只存
/// <c>ScheduleCycleId</c>（周期定义的引用）。本枚举只出现在
/// <see cref="Services.ScheduleCycleResolver"/> 的解析表里，
/// 作用是让"内置档"与"自定义档"在进入判定前统一成同一个 <c>(kind, days)</c> 对。
/// 少了这一层，"<c>c-</c> 开头按位判定、<c>b:</c> 开头按语义判定"的分叉会散布到每个调用点。
/// </remarks>
public enum ScheduleRuleKind
{
    /// <summary>每天（默认 = 升级前行为）。任何越界枚举值也按它处理。</summary>
    Everyday = 0,

    /// <summary>周一至周五（纯星期，不认节假日）。</summary>
    Weekdays = 1,

    /// <summary>周六日（纯星期）。</summary>
    Weekends = 2,

    /// <summary>法定工作日，含调休补班日。</summary>
    LegalWorkday = 3,

    /// <summary>法定节假日 = 官方日历上的休息日全集（D91 = 🅐）。</summary>
    LegalHoliday = 4,

    /// <summary>自定义星期集合。</summary>
    Custom = 5,
}
