using System.Collections.ObjectModel;

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
/// 组本身仍是快照（整表 <c>Load()</c> 时重建），但 <see cref="Rows"/> 用
/// <see cref="ObservableCollection{T}"/> 承载 —— 2026-09-21 批复后，组内 ↑↓ 走
/// <c>Move</c> 做局部更新，不再整表重建（整表重建会让滚动位置与视觉状态归零）。
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
        Rows = new ObservableCollection<DelayRow>(rows);
    }

    /// <summary>本组的延时值（秒）。</summary>
    public int DelaySeconds { get; }

    /// <summary>组内条目，顺序与调度端的发起顺序一致。组内 ↑↓ 经它做局部更新。</summary>
    public ObservableCollection<DelayRow> Rows { get; }

    /// <summary>组标题。形如 <c>登录后 30 秒</c>；延时为 0 时是 <c>登录后立即</c>（无空格）。</summary>
    /// <remarks>
    /// <para>
    /// 0 秒单列一个分支、而不是直接拼 <see cref="DisplayText.DelayOf"/> —— 那个方法对 0 秒返回
    /// 「立即」，拼出来是「登录后 立即」，中间多一个空格，与同页其余标题（「登录后 30 秒」）的
    /// 节奏不一致；组标题是短标题，不必为列宽让路。
    /// </para>
    /// <para>
    /// 三处延时文案的 0 秒口径（2026-09-24 用户批复统一）：列表延时列（<see cref="DisplayText.DelayOf"/>）
    /// =「立即」（那一列按列宽排版，整列本来就无前缀）；设置页预设行（<see cref="DisplayText.LogonDelayOf"/>）
    /// 与<b>本组标题</b> =「登录后立即」。三处刻意分开实现，改一处必须想清楚改的是哪一份。
    /// </para>
    /// </remarks>
    public string Header => DelaySeconds <= 0 ? "登录后立即" : $"登录后 {DisplayText.DelayOf(DelaySeconds)}";

    /// <summary>组内条目数文案。</summary>
    public string CountText => $"{Rows.Count} 项";
}
