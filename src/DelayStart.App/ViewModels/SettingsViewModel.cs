using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;

using DelayStart.App.Interop;
using DelayStart.App.Services;
using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Serialization;
using DelayStart.Core.Services;
using DelayStart.Management.Serialization;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 预设延时列表的一行（FR-9.2，2026-09-19 用户批复的新编辑形态）。
/// </summary>
/// <remarks>
/// <para>
/// 左「登录后 …」时间文案 + 右侧「设为默认 / 当前默认」与「删除」；
/// 整行是不可变快照 —— 任何增删改都整表重建，避免行内状态与配置半同步。
/// </para>
/// <para>
/// 🔴 选中形态从"点整行 = 那个是默认"改成两个显式按钮（2026-09-23 批复 24）：
/// 整行可点的代价是行内其余控件都要防冒泡，而"点一下这一行"到底会发生什么
/// 在界面上没有任何字说出来 —— 摆明按钮，代价与收益都写在脸上。
/// </para>
/// </remarks>
public sealed class PresetRow
{
    /// <summary>构造一行。</summary>
    /// <param name="seconds">延时秒数。</param>
    /// <param name="isDefault">是否为默认预设（列表中被选中的那一项）。</param>
    /// <param name="canDelete">当前能否删除（列表只剩一项时不允许）。</param>
    public PresetRow(int seconds, bool isDefault, bool canDelete)
    {
        Seconds = seconds;
        IsDefault = isDefault;
        CanDelete = canDelete;
        Display = DisplayText.LogonDelayOf(seconds);
    }

    /// <summary>延时秒数。</summary>
    public int Seconds { get; }

    /// <summary>是否为默认预设（是 → 行右侧显示「当前默认」）。</summary>
    public bool IsDefault { get; }

    /// <summary>不是默认（否 → 行右侧显示「设为默认」按钮）。</summary>
    /// <remarks>
    /// 🔴 与 <see cref="IsDefault"/> 成对存在：<c>x:Bind</c> **不支持取反**，
    /// 两个互斥形态各绑一个真值，界面不做判断、也不摆一颗永远点不动的按钮。
    /// </remarks>
    public bool IsNotDefault => !IsDefault;

    /// <summary>能否删除（只剩一项时为 <see langword="false"/>）。</summary>
    public bool CanDelete { get; }

    /// <summary>时间文案（如「登录后 2 分 30 秒（150 秒）」）。</summary>
    public string Display { get; }
}

/// <summary>
/// 设置页「周期」一节里的一行（内置 5 档与自定义周期**共用**同一行类型，FR-15.13）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="PresetRow"/> 一样是不可变快照：任何增删改都整表重建。
/// 「能不能编辑 / 能不能删」的结论在这里就定下来（内置档恒不行；自定义周期由引用数决定），
/// 界面只负责照着画 —— 界面再算一遍的话，判定分叉时会出现"按钮亮着但点了报错"这种事。
/// </para>
/// <para>
/// 🔴 <c>daysText</c> 由调用方（<c>SettingsViewModel.RebuildCycles</c>）翻好传进来，
/// 而不是在这里拿 <see cref="WeekdaySet"/> 现算：法定两档要显示「按国务院通知」而不是
/// 一串星期 —— 而"它是不是法定档"只有拿着 id 的调用方知道。行对象因此不需要认识
/// <see cref="CycleInfoProvider"/>，也就不会因为"两处各翻一遍"而分叉。
/// </para>
/// <para>
/// 🔴 内置 5 档之所以也走这一行类型（2026-09-23 批复 23 之前它们不出现在设置页）：
/// 用户要在一个列表里看见"总共有哪些周期可选"。给它们单独一套只读控件，等于把
/// 「内置 / 自建」这条**将来能不能改**的差别，放大成一整套视觉差异。
/// </para>
/// </remarks>
public sealed class CycleRow
{
    /// <summary>构造一行。</summary>
    /// <param name="id">周期 id。</param>
    /// <param name="name">周期名。</param>
    /// <param name="days">包含哪些天（内置法定两档是"今年实际落到的星期"，仅供编辑面板回填）。</param>
    /// <param name="daysText">「包含哪些天」的文案（已由调用方翻好）。</param>
    /// <param name="references">引用它的延时条目数。</param>
    /// <param name="isBuiltin">是否内置档。</param>
    public CycleRow(string id, string name, WeekdaySet days, string daysText, int references, bool isBuiltin)
    {
        Id = id;
        Name = name;
        Days = days;
        DaysText = daysText;
        References = references;
        IsBuiltin = isBuiltin;
    }

    /// <summary>周期 id。</summary>
    public string Id { get; }

    /// <summary>周期名。</summary>
    public string Name { get; }

    /// <summary>包含哪些天（编辑面板回填七宫格用）。</summary>
    public WeekdaySet Days { get; }

    /// <summary>「包含哪些天」的文案（「周一、周三、周五」；法定两档是「按国务院通知」）。</summary>
    public string DaysText { get; }

    /// <summary>引用它的条目数。</summary>
    public int References { get; }

    /// <summary>是否内置档（内置不可改、不可删，FR-15.13）。</summary>
    public bool IsBuiltin { get; }

    /// <summary>能否编辑（内置档恒不能）。</summary>
    public bool CanEdit => !IsBuiltin;

    /// <summary>能否删除（内置档恒不能；自定义周期被引用时不能，FR-15.14）。</summary>
    public bool CanDelete => !IsBuiltin && References == 0;

    /// <summary>引用数文案（右列）。</summary>
    /// <remarks>
    /// <para>
    /// 不能删的时候直接把原因写在右列（2026-09-23 批复 20）：原先只写「N 个条目在用」，
    /// 而"删除"按钮变灰是**要用户自己把两件事连起来**才知道的 —— 灰按钮配一句解释，
    /// 比让用户猜"为什么点不动"省一次完整的困惑。
    /// </para>
    /// <para>
    /// 内置档的措辞是用户点名的原文（2026-09-23 批复 23）。
    /// </para>
    /// </remarks>
    public string UsageText => IsBuiltin
        ? "内置周期，禁止删除"
        : References > 0 ? $"{References} 个条目在用，禁止删除" : "未使用";

    /// <summary>删除按钮的悬停说明 —— 置灰时解释**为什么**不能删。</summary>
    public string DeleteToolTip => IsBuiltin
        ? "内置周期不能删除"
        : References > 0
            ? $"{References} 个条目正在使用，先把它们改成别的周期才能删除"
            : "删除这个周期";

    /// <summary>编辑按钮的悬停说明（带上影响面，FR-15.16）。</summary>
    public string EditToolTip => IsBuiltin
        ? "内置周期不能修改"
        : References > 0
            ? $"改它会影响 {References} 个正在使用它的条目"
            : "修改这个周期";

    /// <summary>名称 + 星期（删除二次确认里引用它）。</summary>
    public string Display => $"{Name}（{DaysText}）";
}

/// <summary>
/// 设置页「节假日数据」一节里的一行（一个年份，FR-15 / §6.5）。
/// </summary>
/// <remarks>
/// 状态文案的三种形态对应三件不同的事：有数据（报条目数）/ 还没有这份数据（报"未下载"，
/// 并说明这是常态 —— 次年的安排每年约 11 月才公布）/ 文件坏了（报"不可用"）。
/// 把它们压成一句话会让用户分不清"该等"还是"该修"。
/// </remarks>
public sealed class HolidayYearRow
{
    /// <summary>构造一行。</summary>
    /// <param name="year">年份。</param>
    /// <param name="document">本地归一化数据；文件缺失或损坏时为 <see langword="null"/>。</param>
    /// <param name="issue">文件存在但不可用时的原因；正常时为 <see langword="null"/>。</param>
    public HolidayYearRow(int year, HolidayCalendarDocument? document, string? issue = null)
    {
        Year = year;
        Document = document;
        Issue = issue;

        if (document is null)
        {
            StatusText = issue is null ? "未下载" : "本地数据不可用";
            DetailText = issue
                ?? "每年约 11 月公布次年放假安排；在那之前只能按星期规律近似判定。";
            return;
        }

        StatusText = $"{document.RestDays.Count} 放假日 / {document.Workdays.Count} 补班日";
        DetailText = BuildDetail(document);
    }

    /// <summary>年份。</summary>
    public int Year { get; }

    /// <summary>本地数据；不可用时为 <see langword="null"/>。</summary>
    public HolidayCalendarDocument? Document { get; }

    /// <summary>不可用原因。</summary>
    public string? Issue { get; }

    /// <summary>卡片标题（「2026 年」）。</summary>
    public string Header => string.Create(CultureInfo.InvariantCulture, $"{Year} 年");

    /// <summary>卡片描述位（来源与更新时间，或"为什么还没有"）。</summary>
    public string DetailText { get; }

    /// <summary>右侧状态文案。</summary>
    public string StatusText { get; }

    /// <summary>
    /// 能否导出（本地得先有可用数据，才有东西可导）。
    /// </summary>
    /// <remarks>
    /// 🔴 结论在行对象里给，不在 XAML 里判断：<c>x:Bind</c> 不支持取反，
    /// 而"这一年到底有没有数据"只有这里知道（<see cref="Document"/> 为
    /// <see langword="null"/> 就是没有）。摆一个点下去只会说"不行"的按钮，
    /// 用户会当成程序坏了。
    /// </remarks>
    public bool CanExport => Document is not null;

    private static string BuildDetail(HolidayCalendarDocument document)
    {
        var source = string.IsNullOrWhiteSpace(document.SourceLabel) ? "来源未知" : document.SourceLabel;
        var updated = FormatUpdatedAt(document.UpdatedAt);

        return updated is null ? source : $"{source} · 更新于 {updated}";
    }

    private static string? FormatUpdatedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }
}

/// <summary>
/// 设置页 ViewModel（FR-9，2026-09-19 用户批复重做）：
/// 所有操作**立即落盘生效**，没有「保存」按钮；改某项就存某项，失败只在状态条提示该项。
/// </summary>
/// <remarks>
/// 本轮批复的关键语义：
/// ① 降权两开关移除 —— 普通条目一律降权启动，失败记日志，绝不回退提权；
/// ② 托盘两设置移除 —— 托盘只属调度端，生命周期跟随通知；
/// ③ 新增主题（跟随系统 / 浅色 / 深色），切换即时生效；
/// ④ 预设延时改为列表编辑：左选中（=默认）+ 中时间 + 右删除，删默认时默认前移（无前则后移），
///    只剩一项不允许删除；支持输入秒数添加。
/// </remarks>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppConfigStore _configStore;
    private readonly ThemeService _theme;
    private readonly ToastService _toast;
    private readonly CycleCatalogService _cycles;
    private readonly HolidayCalendarUpdateService _holidays;
    private readonly WindowHandleProvider _handles;
    private readonly PathService _paths;
    private readonly HolidayAutoCheckService _autoCheck;
    private readonly ILogSink _log;

    /// <summary>加载期守卫：Load 期间对可观察属性的回灌不得触发落盘。</summary>
    private bool _loading;

    /// <summary>节假日更新是否正在进行（防重入：按钮点两下不该发两轮请求）。</summary>
    private bool _updatingHolidays;

    /// <summary>构造设置页 ViewModel。</summary>
    /// <param name="configStore">配置读写端。</param>
    /// <param name="theme">主题服务（切主题即时落盘并广播到主窗口）。</param>
    /// <param name="toast">应用内通知（2026-09-21 批复：添加成功改走右下角自动消失的通知）。</param>
    /// <param name="cycles">周期目录服务（FR-15：周期一节的读 / 增 / 改 / 删）。</param>
    /// <param name="holidays">节假日抓取服务（FR-15 / NFR-x：全仓库唯一联网的服务）。</param>
    /// <param name="handles">主窗口句柄（导入 / 导出的文件对话框需要属主窗口）。</param>
    /// <param name="paths">路径服务（读本地节假日文件、打开数据目录）。</param>
    /// <param name="autoCheck">启动后的自动检查服务（开关打开时立刻触发一次、并读"上次检查"的事实）。</param>
    /// <param name="holidayStatus">节假日更新的共享可见状态（进度与上次结果）。</param>
    /// <param name="log">日志接收端。</param>
    public SettingsViewModel(
        IAppConfigStore configStore,
        ThemeService theme,
        ToastService toast,
        CycleCatalogService cycles,
        HolidayCalendarUpdateService holidays,
        WindowHandleProvider handles,
        PathService paths,
        HolidayAutoCheckService autoCheck,
        HolidayUpdateStatus holidayStatus,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(toast);
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(holidays);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(autoCheck);
        ArgumentNullException.ThrowIfNull(holidayStatus);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _theme = theme;
        _toast = toast;
        _cycles = cycles;
        _holidays = holidays;
        _handles = handles;
        _paths = paths;
        _autoCheck = autoCheck;
        _log = log;

        HolidayStatus = holidayStatus;
    }

    /// <summary>
    /// 节假日更新的共享状态（进度 / 上次检查结果），供 <c>x:Bind</c> 直接绑。
    /// </summary>
    /// <remarks>
    /// 🔴 直接暴露共享对象而不是在 ViewModel 里再抄一份 <c>[ObservableProperty]</c>：
    /// 抄一份就意味着"谁是权威"变成了两个问题 —— 自动检查（后台）与手动更新（本页）
    /// 都只往共享对象上报，这里只是把它转给界面。本页被销毁也不影响它继续工作。
    /// </remarks>
    public HolidayUpdateStatus HolidayStatus { get; }

    /// <summary>自定义周期列表（FR-15.13：内置 5 档不出现在这里）。</summary>
    public ObservableCollection<CycleRow> Cycles { get; } = [];

    /// <summary>节假日数据年份列表（今年 + 次年）。</summary>
    public ObservableCollection<HolidayYearRow> HolidayYears { get; } = [];

    /// <summary>
    /// 除某个 id 之外的自定义周期名 —— 新建 / 改名面板的即时查重（2026-09-23 批复 14）。
    /// </summary>
    /// <param name="exceptCycleId">要排除的周期 id（改名时是自己）；新建时传 <see langword="null"/>。</param>
    /// <returns>现有自定义周期名。</returns>
    /// <remarks>
    /// 转交 <see cref="CycleCatalogService.CustomCycleNames"/>，而不是就地扫 <see cref="Cycles"/>：
    /// 后者是界面行列表、可能落后于刚落盘的配置，而查重要以**落盘的那份**为准 ——
    /// 它必须与 <c>ConfigEditService.EnsureNameAvailable</c> 看到的是同一份数据。
    /// </remarks>
    public IReadOnlyList<string> OtherCycleNames(string? exceptCycleId = null)
        => _cycles.CustomCycleNames(exceptCycleId);

    /// <summary>管理端启动时是否自动检查节假日数据（§6.5）。</summary>
    [ObservableProperty]
    public partial bool AutoCheckHolidayUpdates { get; set; } = true;

    /// <summary>预设延时列表（选中项 = 默认）。</summary>
    public ObservableCollection<PresetRow> Presets { get; } = [];

    /// <summary>
    /// 「延时」一节的展开状态（**默认收起**，2026-09-23 批复 23）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 收起是默认值：设置页上这两节各自可能攒下十几行（一列延时 + 一列周期），
    /// 常驻展开会把「外观 / 节假日数据 / 调度 / 守卫」几节都挤到需要滚动才看得见。
    /// 节头那行已经说清了这一节是干什么的，想看细节再展开 —— 收起不等于隐藏信息。
    /// </para>
    /// <para>
    /// 🔴 状态记在 ViewModel 而不是控件的 <c>IsChecked</c> 上：三角按钮与节头左半边的
    /// 透明按钮是两个入口，只有一个真值才不会出现"点了左边、三角还是朝下的"。
    /// </para>
    /// </remarks>
    [ObservableProperty]
    public partial bool IsDelaySectionExpanded { get; set; }

    /// <summary>「周期」一节的展开状态（默认收起，2026-09-23 批复 23）。</summary>
    [ObservableProperty]
    public partial bool IsCycleSectionExpanded { get; set; }

    /// <summary>「延时」节三角的图标字形（展开朝上、收起朝下）。</summary>
    public string DelaySectionGlyph => IsDelaySectionExpanded ? "\uE70E" : "\uE70D";

    /// <summary>「周期」节三角的图标字形（展开朝上、收起朝下）。</summary>
    public string CycleSectionGlyph => IsCycleSectionExpanded ? "\uE70E" : "\uE70D";

    /// <summary>展开状态变化时补发字形通知（<c>[ObservableProperty]</c> 不会替派生属性发）。</summary>
    /// <param name="value">新的展开状态。</param>
    partial void OnIsDelaySectionExpandedChanged(bool value) => OnPropertyChanged(nameof(DelaySectionGlyph));

    /// <summary>展开状态变化时补发字形通知。</summary>
    /// <param name="value">新的展开状态。</param>
    partial void OnIsCycleSectionExpandedChanged(bool value) => OnPropertyChanged(nameof(CycleSectionGlyph));

    /// <summary>翻转「延时」节的展开状态（节头左半边与三角按钮共用）。</summary>
    public void ToggleDelaySection() => IsDelaySectionExpanded = !IsDelaySectionExpanded;

    /// <summary>翻转「周期」节的展开状态。</summary>
    public void ToggleCycleSection() => IsCycleSectionExpanded = !IsCycleSectionExpanded;

    /// <summary>重试次数（0–5），NumberBox 的数值形式。</summary>
    [ObservableProperty]
    public partial double RetryCount { get; set; }

    /// <summary>通知策略下拉框下标，与 <see cref="NotifyMode"/> 枚举值一致。</summary>
    [ObservableProperty]
    public partial int NotifyModeIndex { get; set; }

    /// <summary>守卫通知策略下拉框下标，与 <see cref="GuardNotifyMode"/> 枚举值一致。</summary>
    [ObservableProperty]
    public partial int GuardNotifyModeIndex { get; set; }

    /// <summary>主题下拉框下标，与 <see cref="ThemePreference"/> 枚举值一致。</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary>页面底部状态条的错误文案（2026-09-21 批复：状态条只报错；成功类提示走右下角通知）。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>状态条是否可见。现在只有错误才会置文案。</summary>
    /// <remarks>
    /// 🔴 必须是 <c>[ObservableProperty]</c> 的 <see cref="StatusText"/> + 下面那个通知，
    /// 而不是把可见性做成独立字段：x:Bind OneWay 要求路径上有通知源。
    /// </remarks>
    public bool HasError => StatusText.Length > 0;

    /// <summary><see cref="StatusText"/> 变化时补发 <see cref="HasError"/> 的通知。</summary>
    /// <param name="value">新文案。</param>
    /// <remarks>
    /// 🔴 <c>[ObservableProperty]</c> 只为它自己生成的属性发通知，**不会**替 get-only 的派生属性发。
    /// 漏掉本方法的后果不是"提示不好看"，而是**所有 <c>Fail()</c> 全部静默**：
    /// StatusText 与 InfoBar 的 Message 都变了，但 IsOpen 永远停在初始的 false，
    /// 于是设置页上每一个本该报错的按钮都表现为"点了没反应"（2026-09-23 用户真机实测）。
    /// </remarks>
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>通知策略下拉框的选项。</summary>
    /// <remarks>
    /// N3（2026-09-22 批复）：通知策略语义从"收尾弹不弹面板"改为"调度结束后发不发
    /// Windows 系统通知"（经通知中转器发出，见 <c>design.md</c> FR-14）。
    /// 枚举取值与下标对应关系不变，旧配置零迁移。
    /// </remarks>
    public ObservableCollection<string> NotifyModes { get; } = ["有失败时通知", "总是通知", "从不通知"];

    /// <summary>守卫通知策略下拉框的选项（D80）。</summary>
    /// <remarks>
    /// 🔴 与 <see cref="NotifyModes"/> 是**两回事**，不能合并：那边管的是"调度结束后
    /// 发不发系统通知"，这边管的是"守卫巡检发现自启动项变化时发不发系统通知"。
    /// 触发方也不同（调度端 vs 守卫进程），枚举类型因此各有一个
    /// （见 <see cref="GuardNotifyMode"/> 的备注）。
    /// </remarks>
    public ObservableCollection<string> GuardNotifyModes { get; } = ["有变化时通知", "从不通知"];

    /// <summary>主题下拉框的选项。</summary>
    public ObservableCollection<string> ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];

    /// <summary>加载当前设置。页面进入时调用。</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var settings = _configStore.Load().Settings;

            RebuildPresets(settings);
            // 夹取范围引用 Core 常量，不在本文件另写一份数字（x:Bind 的字面量除外，见 SettingsPage.xaml 注释）。
            RetryCount = Math.Clamp(
                settings.RetryCount,
                ConfigService.MinimumRetryCount,
                ConfigService.MaximumRetryCount);
            NotifyModeIndex = (int)settings.NotifyMode;
            GuardNotifyModeIndex = (int)settings.GuardNotifyMode;
            ThemeIndex = (int)settings.Theme;
            RebuildCycles();
            RebuildHolidayYears();
            AutoCheckHolidayUpdates = settings.AutoCheckHolidayUpdates;

            // 🔴 订阅共享状态：自动检查（后台）跑完的那一刻，本页的年份行要跟着更新 ——
            // 否则会出现"数据已经下来了，列表还写着未下载"，用户只能靠重进设置页刷新。
            // 订阅在 Load 里挂、在 DetachHolidayStatus 里摘（页面 Unloaded 调用），成对出现。
            AttachHolidayStatus();
            RefreshHolidayStatus();

            StatusText = string.Empty;
        }
        catch (StartupOperationException ex)
        {
            // 🔴 配置不可用：页面上每个控件都会是默认值，用户改任何一项都会被 SaveOrReport
            // 拒绝。把原因说清楚，别让它看起来像"设置都是空的"。
            _log.Error(ex, "配置无法加载");
            Fail(ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>通知策略被用户改变：立即落盘。</summary>
    /// <param name="index">下拉框下标。</param>
    public void SetNotifyMode(int index)
    {
        if (_loading || index < 0)
        {
            return;
        }

        Persist(settings => settings.NotifyMode = (NotifyMode)index);
    }

    /// <summary>守卫通知策略被用户改变：立即落盘（D80）。</summary>
    /// <param name="index">下拉框下标。</param>
    /// <remarks>
    /// 只影响"发现变化后**要不要发通知**"，不影响守卫本身是否巡检：
    /// 选「从不通知」时守卫照样扫描、纠正写回、更新基线，只是不打扰用户
    /// （见 <c>Program.Notify</c> 的 Never 分支）。
    /// </remarks>
    public void SetGuardNotifyMode(int index)
    {
        if (_loading || index < 0)
        {
            return;
        }

        Persist(settings => settings.GuardNotifyMode = (GuardNotifyMode)index);
    }

    /// <summary>重试次数被用户改变：立即落盘（收敛到 0–5）。</summary>
    /// <param name="value">输入值。</param>
    public void SetRetryCount(double value)
    {
        if (_loading || double.IsNaN(value))
        {
            return;
        }

        Persist(settings => settings.RetryCount = (int)Math.Clamp(value, 0, 5));
    }

    /// <summary>主题被用户改变：经 <see cref="ThemeService"/> 落盘并广播。</summary>
    /// <param name="index">下拉框下标。</param>
    public void SetTheme(int index)
    {
        if (_loading || index < 0)
        {
            return;
        }

        try
        {
            _theme.Set((ThemePreference)index);
            StatusText = string.Empty;
        }
        catch (StartupOperationException ex)
        {
            Fail($"主题保存失败：{ex.Message}");
        }
    }

    /// <summary>把某个预设设为默认：立即落盘。</summary>
    /// <param name="row">选中的行。</param>
    public void SetDefaultPreset(PresetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_loading)
        {
            return;
        }

        Persist(settings => settings.DefaultPreset = row.Seconds);
        ReloadPresetsFromConfig();
    }

    /// <summary>删除一个预设。</summary>
    /// <param name="row">要删除的行。</param>
    /// <remarks>
    /// 用户批复的规则：删除的是默认时，默认**向前一个**（无前则向后一个）；
    /// 列表只剩一项时不允许删除（行上的删除按钮已禁用，这里再兜一层）。
    /// </remarks>
    public void DeletePreset(PresetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_loading)
        {
            return;
        }

        Settings settings;
        try
        {
            settings = _configStore.Load().Settings;
        }
        catch (StartupOperationException ex)
        {
            _log.Error(ex, "配置无法加载，未执行删除延时预设");
            Fail(ex.Message);
            return;
        }

        var list = settings.DelayPresets.ToList();
        if (list.Count <= 1 || !list.Contains(row.Seconds))
        {
            return;
        }

        var index = list.IndexOf(row.Seconds);
        list.RemoveAt(index);
        settings.DelayPresets = [.. list];

        if (settings.DefaultPreset == row.Seconds)
        {
            // 默认随删除前移；删的是第一项就后移（剩下的列表此时必然非空）。
            settings.DefaultPreset = list[Math.Max(index - 1, 0)];
        }

        SaveOrReport(settings);
        ReloadPresetsFromConfig();
    }

    /// <summary>添加一个预设延时（值来自「＋ 新建延时」弹窗，2026-09-23 批复 23）。</summary>
    /// <param name="seconds">延时秒数。</param>
    /// <returns>成功为 <see langword="null"/>；否则是交给弹窗红字显示的失败原因。</returns>
    /// <remarks>
    /// 🔴 返回值是**错误原因**而不是 <see cref="bool"/>：调用方是一层 <c>ContentDialog</c>，
    /// 失败时它要 ① 不关窗 ② 当场说清哪儿不对。走页面状态条或右下角通知都不行 ——
    /// 弹窗还盖在上面，那两条通道用户根本看不见（批复 23 之前是页面上的内联输入框，
    /// 报错落在页面上没问题；换成弹窗之后这一条就不成立了）。
    /// </remarks>
    public string? AddPreset(int seconds)
    {
        if (_loading)
        {
            return null;
        }

        Settings settings;
        try
        {
            settings = _configStore.Load().Settings;
        }
        catch (StartupOperationException ex)
        {
            _log.Error(ex, "配置无法加载，未添加延时预设");
            return ex.Message;
        }

        if (seconds < 0)
        {
            return "延时不能是负数。";
        }

        if (settings.DelayPresets.Contains(seconds))
        {
            return $"「{DisplayText.DelayOf(seconds)}」已经在列表里了。";
        }

        Persist(settings => settings.DelayPresets = [.. settings.DelayPresets.Append(seconds).Order()]);
        ReloadPresetsFromConfig();

        // 成功提示仍走右下角自动消失的应用内通知（2026-09-21 批复）—— 此刻弹窗正在关闭。
        _toast.Show($"已添加预设延时 {DisplayText.DelayOf(seconds)}");
        return null;
    }

    // ── 周期（FR-15.10–15.14，§6.4）─────────────────────────────────────────

    /// <summary>新建一个自定义周期：立即落盘并重建列表。</summary>
    /// <param name="name">周期名。</param>
    /// <param name="days">包含哪些天。</param>
    /// <returns>成功为 <see langword="true"/>；失败时原因已写进状态条。</returns>
    /// <remarks>校验在界面侧（<c>CycleEditorForm.Validate</c>）已经做过一遍，这里只兜底。</remarks>
    public bool AddCycle(string name, WeekdaySet days)
        => RunCycleChange(() => _ = _cycles.Create(name, days), $"已新建周期「{name.Trim()}」");

    /// <summary>修改一个自定义周期：引用它的条目同步生效（FR-15.12）。</summary>
    /// <param name="cycleId">周期 id。</param>
    /// <param name="name">新名称。</param>
    /// <param name="days">新的星期集合。</param>
    /// <returns>成功为 <see langword="true"/>。</returns>
    public bool UpdateCycle(string cycleId, string name, WeekdaySet days)
        => RunCycleChange(() => _ = _cycles.Update(cycleId, name, days), $"已保存周期「{name.Trim()}」");

    /// <summary>删除一个自定义周期（FR-15.14：被引用时拒绝 —— 界面已置灰，这里是第二道防线）。</summary>
    /// <param name="row">要删除的行。</param>
    /// <returns>成功为 <see langword="true"/>。</returns>
    public bool DeleteCycle(CycleRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return RunCycleChange(() => _ = _cycles.Delete(row.Id), $"已删除周期「{row.Name}」");
    }

    /// <summary>
    /// 按配置现状重建周期行：**内置 5 档在前，自定义周期在后**（整表快照，见 <see cref="CycleRow"/>）。
    /// </summary>
    /// <remarks>
    /// 两段合在一张列表里、顺序固定（内置顺序由 <see cref="BuiltinCycleIds.Ordered"/> 决定）：
    /// 分批展示会让用户以为"我的周期"是另一类东西；按名字排序则会让新加的周期插进列表中间，
    /// 下次进来找不到刚建的那一个。
    /// </remarks>
    private void RebuildCycles()
    {
        Cycles.Clear();

        // 一份快照给所有行共用：「包含哪些天」对法定两档要扫一整年日历，
        // 逐行各建一次提供者就是逐行各读一次盘、各扫一年。
        var info = _cycles.CreateProvider();

        foreach (var cycleId in BuiltinCycleIds.Ordered)
        {
            Cycles.Add(new CycleRow(
                cycleId,
                info.NameOf(cycleId),
                info.DaysOf(cycleId),
                DaysTextOf(cycleId, info),
                references: 0,
                isBuiltin: true));
        }

        foreach (var cycle in _cycles.LoadCycles())
        {
            Cycles.Add(new CycleRow(
                cycle.Id,
                cycle.Name,
                cycle.Days,
                info.DaysTextOf(cycle.Id),
                _cycles.CountReferences(cycle.Id),
                isBuiltin: false));
        }
    }

    /// <summary>周期「包含哪些天」的文案：法定两档只说依据，其余**逐天列出来**。</summary>
    /// <param name="cycleId">周期 id。</param>
    /// <param name="info">本次快照的信息提供者（今天 + 法定日历 + 周期表）。</param>
    /// <returns>文案。</returns>
    /// <remarks>
    /// <para>
    /// 与延时弹窗里那句（<c>DelayEditorDialog.BuildCycleDaysText</c>）同一个口径：
    /// 法定两档"落在哪几天"由国务院通知定义，摆一串星期既不准确（调休会把周日点亮）
    /// 也没人看 —— 想知道具体哪天休，看日历比看这行字有用（2026-09-23 批复 19/22）。
    /// </para>
    /// <para>
    /// 🔴 这里用 <see cref="CycleInfoProvider.DaysListText"/> 而不是 <c>DaysTextOf</c>
    /// （2026-09-23 批复 24）：这一列要和上下文里的其它周期并排比较，
    /// 「每天」和「周一、周二」放在一起是两种粒度 —— 简化名省下的几个字，
    /// 换来的是用户得先在脑子里把「每天」翻译成七个星期，才能回答"这两个周期差在哪"。
    /// </para>
    /// <para>
    /// 列表徽标那边仍然用简写（那里是窄徽标，七个星期会撑爆列宽）——
    /// 同一份数据、两种宽度，各自取合适的那一种，不是"两处不一致"。
    /// </para>
    /// </remarks>
    private static string DaysTextOf(string cycleId, CycleInfoProvider info)
        => CycleInfoProvider.IsDynamic(cycleId) ? "按国务院通知" : CycleInfoProvider.DaysListText(info.DaysOf(cycleId));

    /// <summary>执行一次周期增删改：成功重建列表 + 右下角通知，失败转状态条。</summary>
    /// <param name="action">实际动作（转交 <see cref="CycleCatalogService"/>）。</param>
    /// <param name="successMessage">成功提示。</param>
    /// <returns>成功为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 失败时也重建列表：配置可能已经被写过一半（比如保存成功但日志失败），
    /// 让界面回到"配置里真实的样子"比留住用户改动过的假象更安全。
    /// </remarks>
    private bool RunCycleChange(Action action, string successMessage)
    {
        try
        {
            action();
            StatusText = string.Empty;
            RebuildCycles();
            _toast.Show(successMessage);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or StartupOperationException)
        {
            Fail(ex.Message);
            RebuildCycles();
            return false;
        }
    }

    // ── 节假日数据（FR-15 / §6.5）───────────────────────────────────────────

    /// <summary>按本地文件重建节假日年份行（今年 + 次年）。</summary>
    private void RebuildHolidayYears()
    {
        var thisYear = DateTime.Now.Year;

        HolidayYears.Clear();
        HolidayYears.Add(ReadHolidayYear(thisYear));
        HolidayYears.Add(ReadHolidayYear(thisYear + 1));
    }

    /// <summary>读某一年的本地节假日数据。</summary>
    /// <param name="year">年份。</param>
    /// <returns>该年的行；文件缺失 → 「未下载」，内容不合规 / 读不出来 → 带原因。</returns>
    /// <remarks>
    /// 🔴 「文件不存在」与「文件坏了」必须分开说：前者是常态（次年安排每年约 11 月才公布），
    /// 后者要用户动手（重新下载 / 手工修）。合成一句"不可用"等于把两件事都说不清。
    /// </remarks>
    private HolidayYearRow ReadHolidayYear(int year)
    {
        var path = _paths.GetHolidayFilePath(year);
        if (!File.Exists(path))
        {
            return new HolidayYearRow(year, null);
        }

        var name = System.IO.Path.GetFileName(path);

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize(json, HolidayJsonContext.Default.HolidayCalendarDocument);

            return document is null || !HolidayCalendarStore.IsValid(document)
                ? new HolidayYearRow(year, null, $"{name} 内容不合规（条目数或日期格式不符），建议重新下载。")
                : new HolidayYearRow(year, document);
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or UnauthorizedAccessException)
        {
            _log.Error(ex, $"读取 {year} 年法定日历失败");
            return new HolidayYearRow(year, null, $"{name} 读取失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 更新节假日数据（§6.5）。
    /// </summary>
    /// <param name="force">
    /// <see langword="true"/> = 覆盖已有数据（该年的「重新下载」）；
    /// <see langword="false"/> = 只补齐缺失的年份（「补齐缺失年份」，不会白跑网络）。
    /// </param>
    /// <param name="onlyYear">只更新某一年；为 <see langword="null"/> 时更新列表里的全部年份。</param>
    /// <returns>更新完成的 <see cref="Task"/>。</returns>
    /// <remarks>
    /// 下载只发生在**用户按下去**的那一刻（NFR-x）：设置页不自动联网，
    /// 调度端与守卫端更是完全不碰网络。
    /// </remarks>
    public async Task UpdateHolidaysAsync(bool force, int? onlyYear = null)
    {
        if (_updatingHolidays)
        {
            return;
        }

        _updatingHolidays = true;

        // 进度上报到**共享**状态（而不是本页的私有标志）：自动检查也在往同一个对象上报，
        // 两个触发方共用一条进度线，界面才不必区分"这次是谁发起的"。
        using var busy = HolidayStatus.Begin();
        try
        {
            var years = onlyYear is { } single
                ? [single]
                : HolidayYears.Select(static row => row.Year).ToArray();

            IReadOnlyList<HolidayUpdateResult> results;
            try
            {
                results = await _holidays.UpdateAsync(years, force).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                Fail($"节假日数据更新失败：{ex.Message}");
                return;
            }

            // 新数据要立刻对判定生效：丢掉日历快照，年份行重读（否则用户会看到
            // "下载成功了但列表还说未下载"）。
            _cycles.InvalidateCalendar();
            RebuildHolidayYears();
            ReportHolidayResults(results);
        }
        finally
        {
            _updatingHolidays = false;
            RefreshHolidayStatus();
        }
    }

    /// <summary>
    /// 把更新结果翻成用户看得懂的话：失败进状态条，成功 / 尚未公布进右下角通知。
    /// </summary>
    /// <param name="results">逐年结果。</param>
    /// <remarks>
    /// 🔴 「尚未公布」（源的 days 为空，通常发生在 11 月之前拉次年）<b>不是错误</b> ——
    /// 它走通知而不是红条。把它当错误报，用户会去查一个并不存在的问题。
    /// </remarks>
    private void ReportHolidayResults(IReadOnlyList<HolidayUpdateResult> results)
    {
        var problems = results
            .Where(static result => result.Outcome is HolidayUpdateOutcome.NetworkFailure or HolidayUpdateOutcome.InvalidContent)
            .ToArray();

        if (problems.Length > 0)
        {
            Fail(string.Join("\n", problems.Select(static problem => $"{problem.Year} 年：{problem.Message}")));
            return;
        }

        StatusText = string.Empty;

        var updated = results
            .Where(static result => result.Outcome == HolidayUpdateOutcome.Updated)
            .Select(static result => result.Year.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        if (updated.Length > 0)
        {
            _toast.Show($"节假日数据已就绪：{string.Join("、", updated)} 年");
            return;
        }

        _toast.Show(results.Count > 0 ? results[0].Message : "没有需要更新的年份");
    }

    /// <summary>在资源管理器里打开节假日数据目录（手工替换 / 校正数据用，§6.5）。</summary>
    public void OpenHolidayFolder()
    {
        try
        {
            // 目录由"写入方按需创建"，全新机器上它可能还不存在 —— 打开一个不存在的路径会直接失败。
            Directory.CreateDirectory(_paths.HolidaysRoot);
            Process.Start(new ProcessStartInfo { FileName = _paths.HolidaysRoot, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Fail($"打开数据目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 从一个 JSON 文件导入节假日数据（§6.5「离线机器用 U 盘传，或手工修正数据」）。
    /// </summary>
    /// <returns>导入成功为 <see langword="true"/>；用户取消时也是 <see langword="false"/>，但不报错。</returns>
    /// <remarks>
    /// <para>
    /// 🔴 年份取自**文件内容**（<c>year</c> 字段）而不是文件名：用户可能把它改名成
    /// 「2027年放假安排.json」，按文件名猜年份会把数据写到错的地方。
    /// </para>
    /// <para>
    /// 🔴 **两种格式都收**（<see cref="HolidaySourceConverter"/> 自动识别）：
    /// holiday-cn 的原始格式与本程序导出的归一化格式。用户手上那份
    /// <c>2026.json</c> 就是从上游仓库直接下载的原始文件 —— 要求他先自己归一化，
    /// 等于把"离线机器该有一条活路"这件事又堵回去了。
    /// </para>
    /// <para>
    /// 校验不通过就**不写**：留下本地原有的好数据，比换上一份坏数据强。
    /// </para>
    /// </remarks>
    public bool ImportHolidayFile()
    {
        var path = Win32FilePicker.PickFile(_handles.Handle, "导入节假日数据", [".json"], "节假日数据");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"导入失败：读取文件时出错 —— {ex.Message}");
            return false;
        }

        var conversion = HolidaySourceConverter.Convert(json);
        if (conversion.Document is not { } document)
        {
            Fail($"导入失败：{conversion.Message}");
            return false;
        }

        try
        {
            HolidayCalendarStore.Write(_paths, document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail($"导入失败：写入本地数据时出错 —— {ex.Message}");
            return false;
        }

        _cycles.InvalidateCalendar();
        RebuildHolidayYears();
        StatusText = string.Empty;

        // 提示里带上识别结果（原始格式还是归一化格式、各多少条）——
        // 用户拿的是上游文件时，"它被认出来了"本身就是他需要确认的东西。
        _toast.Show(string.Create(
            CultureInfo.InvariantCulture,
            $"已导入 {document.Year} 年节假日数据（{document.RestDays.Count} 个放假日 / {document.Workdays.Count} 个调休补班日）"));
        return true;
    }

    /// <summary>把某一年的本地数据导出成 JSON 文件（备份 / 搬到离线机器）。</summary>
    /// <param name="year">年份。</param>
    /// <returns>导出成功为 <see langword="true"/>；用户取消时也是 <see langword="false"/>，但不报错。</returns>
    public bool ExportHolidayFile(int year)
    {
        var source = _paths.GetHolidayFilePath(year);
        if (!File.Exists(source))
        {
            Fail($"{year} 年还没有本地数据，先更新一次再导出。");
            return false;
        }

        var target = Win32FilePicker.PickSaveFile(
            _handles.Handle,
            "导出节假日数据",
            string.Create(CultureInfo.InvariantCulture, $"{year}.json"),
            [".json"],
            "节假日数据");

        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail($"导出失败：{ex.Message}");
            return false;
        }

        StatusText = string.Empty;
        _toast.Show($"已导出 {year} 年数据到 {System.IO.Path.GetFileName(target)}");
        return true;
    }

    /// <summary>「自动检查更新」开关被改变：立即落盘；打开时**立刻检查一次**。</summary>
    /// <param name="value">是否开启。</param>
    /// <returns>开/关处理完成的 <see cref="Task"/>。</returns>
    /// <remarks>
    /// <para>
    /// 关掉之后**已经下载到本地的数据照常使用**（判定完全离线），只是不再自动去补 ——
    /// 想手动补，用同节的「补齐缺失年份 / 重新下载」。
    /// </para>
    /// <para>
    /// 🔴 打开时不等下一次启动（2026-09-23 批复 20）：用户按下开关是一个明确的意图，
    /// 他期待"现在就生效"。等下次启动的话，一台开着不关的机器可能几天都不动一次，
    /// 用户唯一能得到的结论就是"这个开关没用"。这次的触发绕过节流（用户动作，NFR-x 允许），
    /// 但**依然不会重复下载**：本地已有可用数据时它只是把状态说清楚。
    /// </para>
    /// </remarks>
    public async Task SetAutoCheckHolidayUpdatesAsync(bool value)
    {
        if (_loading)
        {
            return;
        }

        Persist(settings => settings.AutoCheckHolidayUpdates = value);

        if (value)
        {
            await _autoCheck.RunNowAsync().ConfigureAwait(true);
            RebuildHolidayYears();
        }

        RefreshHolidayStatus();
    }

    /// <summary>把共享状态的一句话说明读到界面上。</summary>
    /// <remarks>
    /// 已有一条更新在跑时**不覆盖正文**：那条进行中的文案比"上次什么时候查的"更该被看见。
    /// </remarks>
    private void RefreshHolidayStatus()
    {
        if (!HolidayStatus.IsBusy)
        {
            HolidayStatus.Report(_autoCheck.Describe());
        }
    }

    /// <summary>订阅共享状态的变化（自动检查在后台跑完后，本页的年份行要跟着变）。</summary>
    private void AttachHolidayStatus() => HolidayStatus.PropertyChanged += OnHolidayStatusChanged;

    /// <summary>摘掉订阅。页面 <c>Unloaded</c> 时调用 —— 不摘就是"瞬态 ViewModel 被单例长期引用"。</summary>
    public void DetachHolidayStatus() => HolidayStatus.PropertyChanged -= OnHolidayStatusChanged;

    /// <summary>共享状态变化：一轮更新结束时重读年份行。</summary>
    /// <param name="sender">状态对象。</param>
    /// <param name="e">变化详情。</param>
    /// <remarks>
    /// 🔴 只在 <c>IsBusy</c> 转为 false 时重读 —— 那是"数据可能已经变了"的唯一时刻。
    /// 每次属性变化都重读的话，一轮更新里会白读好几次磁盘。
    /// </remarks>
    private void OnHolidayStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HolidayUpdateStatus.IsBusy) && !HolidayStatus.IsBusy)
        {
            RebuildHolidayYears();
            _cycles.InvalidateCalendar();
        }
    }

    /// <summary>统一落盘：读最新配置 → 局部改 → 原子写。失败转状态条。</summary>
    /// <param name="mutate">对 <c>settings</c> 节点的局部修改。</param>
    private void Persist(Action<Settings> mutate)
    {
        try
        {
            var config = _configStore.Load();
            mutate(config.Settings);
            SaveOrReport(config.Settings);
        }
        catch (StartupOperationException ex)
        {
            Fail(ex.Message);
        }
    }

    private void SaveOrReport(Settings settings)
    {
        try
        {
            var config = _configStore.Load();
            config.Settings = settings;
            _configStore.Save(config);
            StatusText = string.Empty;
        }
        catch (StartupOperationException ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>按配置现状重建预设行（整表快照，见 <see cref="PresetRow"/>）。</summary>
    private void ReloadPresetsFromConfig() => RebuildPresets(_configStore.Load().Settings);

    private void RebuildPresets(Settings settings)
    {
        var presets = settings.DelayPresets;
        Presets.Clear();
        foreach (var seconds in presets)
        {
            Presets.Add(new PresetRow(
                seconds,
                isDefault: seconds == settings.DefaultPreset,
                canDelete: presets.Length > 1));
        }
    }

    private void Fail(string message)
    {
        StatusText = message;
    }
}
