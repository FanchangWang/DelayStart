using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DelayStart.App.Services;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Models;

using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 「延时启动」页一行。<see cref="DelayedItem"/> 的只读展示包装。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="StartupEntryRow"/> 同样的思路：把文案拼装从 XAML 里挪出来。
/// 区别在于这一页的数据来自 <c>config.json</c>（用户配置）而不是系统扫描结果，
/// 所以行里多了一样东西 —— <see cref="IsStale"/>，"这条配置指向的系统项已经不存在了"。
/// </para>
/// <para>
/// 2026-09-21 批复：开关切换与组内 ↑↓ 改为**局部更新**，不再整表重建 ——
/// 因此 <see cref="Order"/> 与 <see cref="IsEnabled"/> 变成可通知属性，
/// 其余字段仍随行对象不可变（整表 <c>Load()</c> 时才重建行）。
/// </para>
/// <para>
/// 2026-09-22（D81）：失效条目不再是独立页面，而是**留在本列表里被标出来**。
/// 于是一行有两种形态：正常行（开关 + 编辑 / 移出延时）与失效行
/// （「已失效」文字 + 删除 / 转为手动）。三组显隐标志
/// （<see cref="IsNormal"/> / <see cref="IsStale"/> / <see cref="CanConvertToManual"/>）
/// 就是这件事在界面上的全部表达，XAML 只管照着摆。
/// </para>
/// </remarks>
public sealed class DelayRow : ObservableObject
{
    /// <summary>构造行。</summary>
    /// <param name="item">配置里的延时条目。</param>
    /// <param name="staleKind">失效类型；不是失效条目时为 <see langword="null"/>。</param>
    /// <param name="canConvertToManual">能否转成手动条目（要求目标程序还在，见 D81）。</param>
    /// <param name="iconPixels">该条目的图标像素；提取失败为 <see langword="null"/>（显示占位符）。</param>
    /// <param name="cycles">周期信息提供者（FR-15：徽标文案与"今天跳不跳"）。</param>
    public DelayRow(
        DelayedItem item,
        StaleKind? staleKind,
        bool canConvertToManual,
        IconPixels? iconPixels,
        CycleInfoProvider? cycles)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        StaleKind = staleKind;
        CanConvertToManual = canConvertToManual;
        Icon = IconRenderer.ToImageSource(iconPixels);
        _isEnabled = item.Enabled;

        if (cycles is null)
        {
            CycleText = string.Empty;
            CycleToolTip = string.Empty;
            SkipToolTip = string.Empty;
            return;
        }

        // 徽标：**所有**周期都显示，包括内置的「每天」（FR-15.21）——
        // "这一列空缺"会被读成"没设周期"，而默认档恰恰是最常见的值，不能让它看起来像异常。
        var name = cycles.NameOf(item.ScheduleCycleId);
        var skipped = cycles.IsSkipped(item.ScheduleCycleId, out var degraded, out var reason);

        CycleText = degraded ? $"{name} ≈" : name;
        CycleToolTip = CycleInfoProvider.IsDynamic(item.ScheduleCycleId)
            ? $"{name}：以 {cycles.Today.Year} 年国务院放假安排为准，不固定在某几个星期"
            : $"{name}：{cycles.DaysTextOf(item.ScheduleCycleId)}";

        IsSkippedToday = skipped;
        SkipToolTip = skipped
            ? $"今天 {cycles.Today:yyyy-MM-dd}（{CycleInfoProvider.NameOfDay(cycles.Today.DayOfWeek)}）不启动"
                + $"\n周期：{CycleText}\n原因：{reason}"
                + (degraded ? "\n（该年法定数据不可用，当前按星期规律近似判定）" : string.Empty)
            : string.Empty;
    }

    /// <summary>
    /// 周期徽标文案（含「每天」）。近似判定时带 <c>≈</c> 后缀 —— 那是"这不是官方答案"的意思。
    /// </summary>
    public string CycleText { get; }

    /// <summary>周期徽标的悬停说明（周期名 + 包含哪些天 / 随放假安排变动）。</summary>
    public string CycleToolTip { get; }

    /// <summary>
    /// 今天是否被周期跳过（FR-15.22）。
    /// </summary>
    /// <remarks>
    /// 🔴 这一行<b>不置灰</b>：整行变淡是「启用 = 关」的视觉语言，
    /// 两者语义不同（一个是"今天不跑"，一个是"永久不跑"），复用同一个视觉表达
    /// 只会让人分不清。替代做法是程序名后挂一个红圈斜杠标记，悬停给原因。
    /// </remarks>
    public bool IsSkippedToday { get; }

    /// <summary>跳过标记的悬停说明；未跳过时为空串。</summary>
    public string SkipToolTip { get; }

    /// <summary>原始条目，供移除 / 编辑 / 删除 / 转手动取用。</summary>
    public DelayedItem Item { get; }

    /// <summary>条目图标；提取失败为 <see langword="null"/>，XAML 用占位符代替。</summary>
    public ImageSource? Icon { get; }

    /// <summary>是否拿到了图标。XAML 据此在「真图标」与「占位符」间切换。</summary>
    public bool HasIcon => Icon is not null;

    /// <summary><see cref="HasIcon"/> 的反面。<c>x:Bind</c> 不支持取反表达式。</summary>
    public bool HasNoIcon => Icon is null;

    private int _order;

    /// <summary>
    /// 列表中的序号（1 起），与调度端的发起顺序一致。
    /// </summary>
    /// <remarks>
    /// 由 ViewModel 在灌列表 / 组内移动时填 —— 序号是"排序之后的位置"，
    /// 行对象本身不知道自己排第几。变化时联动 <see cref="OrderText"/>。
    /// </remarks>
    public int Order
    {
        get => _order;
        set
        {
            if (SetProperty(ref _order, value))
            {
                OnPropertyChanged(nameof(OrderText));
            }
        }
    }

    /// <summary>序号的文本形式。<c>x:Bind</c> 不做 <c>int → string</c> 的隐式转换。</summary>
    public string OrderText => Order.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>显示名。</summary>
    public string Name => Item.Name;

    /// <summary>路径 + 参数拼成的命令行。</summary>
    public string CommandLine => string.IsNullOrWhiteSpace(Item.Arguments)
        ? Item.Path
        : $"{Item.Path}  {Item.Arguments}";

    /// <summary>延时文案，如 <c>登录后 30 秒</c>。</summary>
    public string DelayText => DisplayText.DelayOf(Item.DelaySeconds);

    /// <summary>启动身份文案。</summary>
    public string IdentityText => DisplayText.IdentityOf(Item.RunAsAdmin);

    /// <summary>来源文案。手动条目显示「手动添加」，其余显示系统来源。</summary>
    public string SourceText => DisplayText.SourceOf(Item.Source);

    /// <summary>
    /// 该条目对应的系统项是否已失效（孤儿 / 目标程序不存在，见 <see cref="GuardStalePolicy"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 失效条目**仍留在列表里**，只是标注出来 —— 配置里留着它，用户才能看见
    /// "我配过这个，但它没了"并且主动清理。静默丢弃会让用户以为配置丢失。
    /// </para>
    /// <para>
    /// 判定**不能只看"扫描结果里找不到它"**：那只能发现孤儿，发现不了
    /// "启动项还在、程序文件没了"（FR-1.10）。两类都由 <see cref="GuardStalePolicy"/> 统一给出。
    /// </para>
    /// </remarks>
    public StaleKind? StaleKind { get; }

    /// <summary>是否为失效条目。</summary>
    public bool IsStale => StaleKind is not null;

    /// <summary>
    /// 是否为正常条目。
    /// </summary>
    /// <remarks>
    /// <c>x:Bind</c> 不支持取反表达式（<c>!IsStale</c>），所以给一个正面属性：
    /// 直接写 <c>{x:Bind !IsStale}</c> 是编译错误，而在 XAML 里做取反转换器
    /// 又是本项目已经踩过的坑（SettingsPage 那套转换器就为了同一件事存在）。
    /// </remarks>
    public bool IsNormal => StaleKind is null;

    /// <summary>失效原因的一句话（同时用作「已失效」文字的 Tooltip）。</summary>
    public string StaleReason => StaleKind switch
    {
        Core.Services.StaleKind.Orphan => "原本接管的系统启动项已被删除（软件卸载或被手动清理），无处可还原、也无人可启动。",
        Core.Services.StaleKind.Missing => $"目标程序已不存在：{Item.Path}",
        _ => string.Empty,
    };

    /// <summary>
    /// 能否转成手动条目（D81）。
    /// </summary>
    /// <remarks>
    /// 前提是**目标程序还在**：条目已经失去系统锚点，路径再失效的话转成手动
    /// 只会得到"每次登录失败一次"的定时炸弹 —— 那种情况下只该给「删除」。
    /// </remarks>
    public bool CanConvertToManual { get; }

    private bool _isEnabled;

    /// <summary>条目级开关状态（FR-4.6）。局部更新时由 ViewModel 直接改，联动开关 UI。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>局部更新启用状态（配置已成功落盘后调用）。</summary>
    public void SetEnabledState(bool enabled) => IsEnabled = enabled;

    /// <summary>
    /// 强制重发 <see cref="IsEnabled"/> 通知：落盘失败时让 OneWay 绑定把开关弹回真实值
    /// （此时属性值没变，只有重发通知才能纠正用户已经拨过去的开关位置）。
    /// </summary>
    public void RefreshEnabled() => OnPropertyChanged(nameof(IsEnabled));
}
