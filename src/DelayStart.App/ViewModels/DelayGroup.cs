namespace DelayStart.App.ViewModels;

/// <summary>
/// 「延时启动」页的一组**同延时**条目（2026-09-20 用户批复：列表按延时时间分组，组上方给子标题）。
/// </summary>
/// <remarks>
/// <para>
/// 分组把"这些条目其实挤在同一秒一起启停"这件事摆到明面上：平铺列表里它只能靠一行行扫
/// 「延时」列看出来，而组内的 ↑↓ 本来就只在这一组内生效（FR-4.7）—— 分组之后
/// "顺序只调得动同组"不再需要解释。
/// </para>
/// <para>
/// 组是不可变快照：任何改动都整表重建（与 <see cref="PresetRow"/> 同一思路），
/// 免得行内状态与配置半同步。
/// </para>
/// </remarks>
public sealed class DelayGroup
{
    /// <summary>构造一组。</summary>
    /// <param name="delaySeconds">本组的延时值（秒）。</param>
    /// <param name="rows">组内条目，已按组内顺序排列。</param>
    public DelayGroup(int delaySeconds, IReadOnlyList<DelayRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        DelaySeconds = delaySeconds;
        Rows = rows;
    }

    /// <summary>本组的延时值（秒）。</summary>
    public int DelaySeconds { get; }

    /// <summary>组内条目，顺序与调度端的发起顺序一致。</summary>
    public IReadOnlyList<DelayRow> Rows { get; }

    /// <summary>组标题。形如 <c>登录后 30 秒</c>；延时为 0 时是 <c>立即启动</c>（<c>登录后 立即</c> 读不通）。</summary>
    public string Header => DelaySeconds <= 0 ? "立即启动" : $"登录后 {DisplayText.DelayOf(DelaySeconds)}";

    /// <summary>组内条目数文案。</summary>
    public string CountText => $"{Rows.Count} 项";
}
