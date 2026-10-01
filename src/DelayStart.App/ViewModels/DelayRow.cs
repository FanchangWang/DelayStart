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
            NotRunTodayToolTip = string.Empty;
            NotRunTomorrowToolTip = string.Empty;
            return;
        }

        // 周期名：**所有**周期都显示，包括内置的「每天」（FR-15.21）——
        // "这一格空缺"会被读成"没设周期"，而默认档恰恰是最常见的值，不能让它看起来像异常。
        var name = cycles.NameOf(item.ScheduleCycleId);
        var skippedToday = cycles.IsSkippedOn(item.ScheduleCycleId, cycles.Today, out var degradedToday, out var reasonToday);
        var skippedTomorrow = cycles.IsSkippedOn(item.ScheduleCycleId, cycles.Tomorrow, out var degradedTomorrow, out var reasonTomorrow);

        CycleText = degradedToday || degradedTomorrow ? $"{name} ≈" : name;
        CycleToolTip = CycleInfoProvider.IsDynamic(item.ScheduleCycleId)
            ? $"{name}：以 {cycles.Today.Year} 年国务院放假安排为准，不固定在某几个星期"
            : $"{name}：{cycles.DaysTextOf(item.ScheduleCycleId)}";

        // 🔴 **只提示不启动的那天**（v0.6.1 用户决策）：周期列第二、三行分别是
        // 「今天」「明天」，但"会启动"的那天**留空**。列的语义是"有什么需要注意的"，
        // 不是"报告全部"—— 每天启动的条目第二三行恒空，「每天」档连第二行都没有，
        // 所以行高自适应（1–3 行）而不是固定三行。
        HasNotRunToday = skippedToday;
        HasNotRunTomorrow = skippedTomorrow;

        NotRunTodayToolTip = skippedToday
            ? NotRunTip(cycles.Today, reasonToday, degradedToday)
            : string.Empty;
        NotRunTomorrowToolTip = skippedTomorrow
            ? NotRunTip(cycles.Tomorrow, reasonTomorrow, degradedTomorrow)
            : string.Empty;
    }

    /// <summary>
    /// 周期徽标文案（含「每天」）。近似判定时带 <c>≈</c> 后缀 —— 那是"这不是官方答案"的意思。
    /// </summary>
    public string CycleText { get; }

    /// <summary>周期徽标的悬停说明（周期名 + 包含哪些天 / 随放假安排变动）。</summary>
    public string CycleToolTip { get; }

    /// <summary>今天不启动时，周期列第二行显示「今天 + 图标」。</summary>
    /// <remarks>
    /// 🔴 **只有不启动才为真**。反向表达是刻意的：XAML 用它控制显隐，
    /// 而「会启动」不是一个需要告知用户的状态 —— 一行 8 个条目如果每天都写「今天 ✅」，
    /// 那一列就变成噪声，用户反而看不出哪几行值得注意。
    /// </remarks>
    public bool HasNotRunToday { get; }

    /// <summary>明天不启动时，周期列第三行显示「明天 + 图标」。</summary>
    /// <remarks>
    /// 🔴 明天判定因次年法定数据缺失而走「更宽松」（按会启动）时，这里为 <see langword="false"/>
    /// —— 12-31 看 1-1 必须这样，不能因为缺数据就报「明天不启动」（D87/D90：
    /// 兜底只能更宽松，静默停摆是最坏的一类失败）。
    /// </remarks>
    public bool HasNotRunTomorrow { get; }

    /// <summary>「今天不启动」那一行的悬停说明（写明为什么）；会启动时为空串。</summary>
    public string NotRunTodayToolTip { get; }

    /// <summary>「明天不启动」那一行的悬停说明（写明为什么）；会启动时为空串。</summary>
    public string NotRunTomorrowToolTip { get; }

    /// <summary>组装「某天不启动」的悬停说明。</summary>
    /// <param name="date">那一天。</param>
    /// <param name="reason">Core 判定给出的中文原因。</param>
    /// <param name="degraded">是否因缺该年法定数据而按星期近似。</param>
    /// <returns>tooltip 正文。</returns>
    /// <remarks>
    /// 🔴 **实例方法而不是 static**：正文里要用 <see cref="CycleText"/>,
    /// 而它带「≈」后缀（近似判定）—— 与列里显示的保持一致，
    /// tooltip 里如果说一个名字、列里显示另一个，用户会以为是两回事。
    /// </remarks>
    private string NotRunTip(DateOnly date, string reason, bool degraded)
        => $"{date:yyyy-MM-dd}（{CycleInfoProvider.NameOfDay(date.DayOfWeek)}）不启动"
            + $"\n周期：{CycleText}"
            + $"\n原因：{reason}"
            + (degraded ? "\n（该年法定数据不可用，当前按星期规律近似判定）" : string.Empty);

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

    /// <summary>失效原因的一句话（用作失效徽标的 Tooltip）。</summary>
    /// <remarks>
    /// 🔴 两档的措辞必须说清**还会不会启动它**：源丢失那档照常启动，只标"源已丢失"；
    /// 目标丢失那档才真的不再启动。旧文案把孤儿说成"无人可启动"是错的 ——
    /// 源没了但程序还在时，它每天都在被启动。
    /// </remarks>
    public string StaleReason => StaleKind switch
    {
        Core.Services.StaleKind.SourceLost =>
            "原本接管的系统启动项已被删除（软件卸载或被手动清理），但目标程序仍在，"
            + "所以仍会按你的设置启动它。如需恢复接管关系，请在对应的自启动项页重新接管。",
        Core.Services.StaleKind.TargetLost =>
            $"目标程序已不存在，本次不再启动它：{Item.Path}。装回后会自动恢复；也可在此删除或转为手动。",
        _ => string.Empty,
    };

    /// <summary>源丢失（注册表项 / 计划任务 / 启动文件夹里的那个对象不见了）。</summary>
    public bool IsSourceLost => StaleKind is Core.Services.StaleKind.SourceLost;

    /// <summary>目标丢失（被接管的程序本身不在了）。</summary>
    public bool IsTargetLost => StaleKind is Core.Services.StaleKind.TargetLost;

    /// <summary>失效徽标的文字（v0.6.1：徽标位置改到程序名之后）。</summary>
    /// <remarks>
    /// 🔴 两种失效要分开说，因为处置方向相反：源丢失 ⇒ 去自启动项页重新接管；
    /// 目标丢失 ⇒ 去装回软件或把条目删掉。合成一句话会让两种场景都变得不知道该做什么。
    /// </remarks>
    public string StaleBadgeText => StaleKind switch
    {
        Core.Services.StaleKind.SourceLost => "源已丢失",
        Core.Services.StaleKind.TargetLost => "目标已不存在",
        _ => string.Empty,
    };

    /// <summary>失效徽标的悬停说明（复用 <see cref="StaleReason"/>，它已写清"还会不会启动"）。</summary>
    public string StaleToolTip => StaleReason;

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
