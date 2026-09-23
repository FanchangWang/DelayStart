namespace DelayStart.Core.Models;

/// <summary>
/// 一个"调度周期"：决定引用它的条目在哪些天会被拉起（FR-15）。
/// </summary>
/// <remarks>
/// <para>
/// 周期定义在配置的**顶层集合**（<c>config.json</c> 的 <c>cycles</c>），不属于任何条目 ——
/// 条目只存 <c>ScheduleCycleId</c>。<b>这是一条引用关系，不是快照</b>（D87 = 🅑，用户批复）：
/// 改一个周期，所有引用它的条目同步生效。引用语义的代价由三条约束兜住 ——
/// 被引用不可删（FR-15.14）、引用失效回落「每天」而不是"永不启动"（FR-15.15）、
/// 改前告知影响面（FR-15.16）。
/// </para>
/// <para>
/// <see cref="Id"/> 的形态（D96 = 🅐）：内置周期是固定字面量 <c>b:xxx</c>，
/// 自定义周期是 <c>c-</c> 前缀 + 短随机串。用可读字符串而不是 Guid，
/// 是为了让手改过的坏配置在日志里能被一眼认出来。
/// </para>
/// </remarks>
public sealed class ScheduleCycle
{
    /// <summary>稳定 id。内置周期取 <see cref="BuiltinCycleIds"/> 的常量。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>展示名。内置周期的名称由代码给出，不依赖配置里的值。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 星期集合。🔴 **必须至少含一天**（FR-15.20）—— 空集不表达"永不启动"，
    /// 想让条目不跑请用条目的「启用」开关；空集在解析时被当作**无效周期**处理。
    /// </summary>
    public WeekdaySet Days { get; set; } = WeekdaySet.None;
}

/// <summary>
/// 内置五档的固定 id（写进配置也不会错：<c>b:</c> 前缀，不随语言或版本变）。
/// </summary>
/// <remarks>
/// 内置周期为**不可改不可删**（FR-15.13）：它们不出现在设置页的周期列表里，
/// 也不进配套文件；修改了配置里的值也不会被采用（名称与判定都由代码给出）。
/// </remarks>
public static class BuiltinCycleIds
{
    /// <summary>每天 —— 默认值，等价于升级前的行为。</summary>
    public const string Everyday = "b:everyday";

    /// <summary>周一至周五（纯星期）。</summary>
    public const string Weekdays = "b:weekdays";

    /// <summary>周六日（纯星期）。</summary>
    public const string Weekends = "b:weekends";

    /// <summary>法定工作日（含调休补班日）。</summary>
    public const string LegalWorkday = "b:legal-workday";

    /// <summary>法定节假日（官方日历休息日全集）。</summary>
    public const string LegalHoliday = "b:legal-holiday";

    /// <summary>自定义周期 id 的前缀。</summary>
    public const string CustomPrefix = "c-";

    /// <summary>
    /// 内置五档的展示顺序：每天 → 周一至周五 → 周六日 → 法定工作日 → 法定节假日。
    /// </summary>
    /// <remarks>
    /// 顺序本身是界面约定的一部分（延时编辑弹窗的胶囊行、启用周期时的默认档位……）。
    /// 放在这里而不是各界面各排一套：两处顺序不一致会让"惯用位置"失效。
    /// 名称不在这里 —— 名称是给人看的文案，在 <see cref="CycleNames"/> 里按 id 翻译，
    /// 界面与管理端共用同一份（管理端要靠它拦下"与内置档同名"的自定义周期，2026-09-23 批复 14）。
    /// </remarks>
    public static readonly string[] Ordered =
        [Everyday, Weekdays, Weekends, LegalWorkday, LegalHoliday];
}
