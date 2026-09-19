using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 「自启动项」页的 ViewModel。
/// </summary>
/// <remarks>
/// <para>
/// 本页只读系统里**已经存在**的自启动项。手动添加的程序属于调度任务而非系统自启动项，
/// 只出现在「延时启动」页 —— 所以这里没有也不需要"添加"入口（design-spec 页面 2）。
/// </para>
/// <para>
/// 扫描放在后台线程（NFR-1.3）：全量扫描要读三个注册表 hive、两个启动文件夹、
/// 计划任务库与 UWP 的 `SystemAppData`，同步跑会把窗口冻住。
/// </para>
/// </remarks>
public sealed partial class ItemsViewModel : ObservableObject
{
    private readonly ScanService _scanner;
    private readonly TakeoverService _takeover;
    private readonly IconProvider _icons;
    private readonly IAppConfigStore _configStore;
    private readonly ILogSink _log;

    /// <summary>页头副标题，形如 `共 37 项 · 已接管 0 项 · 已禁用 15 项`。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; }

    /// <summary>搜索词。匹配名称 / 命令行 / 位置三处（大小写不敏感，子串匹配）。</summary>
    /// <remarks>过滤的是**显示**，不重扫系统；清空搜索词立刻回到全量列表。</remarks>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>来源筛选的下拉序号：0 全部 / 1 注册表 / 2 启动文件夹 / 3 计划任务 / 4 UWP。</summary>
    /// <remarks>用序号而不是枚举值绑定：ComboBox 的 <c>SelectedIndex</c> 是它唯一
    /// 不需要转换器就能 <c>x:Bind</c> 的形态。</remarks>
    [ObservableProperty]
    public partial int SourceFilterIndex { get; set; }

    /// <summary>是否正在扫描。界面据此禁用刷新按钮并显示进度条。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>来源级失败提示；无失败时为 <see langword="null"/>（FR-1.4）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial string? FailureText { get; set; }

    /// <summary>是否存在来源级失败。XAML 的 <c>InfoBar.IsOpen</c> 只吃布尔值。</summary>
    /// <remarks>
    /// 计算属性而不是独立的可观察字段：它与 <see cref="FailureText"/> 永远同真同假，
    /// 存两份迟早出现"文案更新了、开关没更新"。改用
    /// <c>NotifyPropertyChangedFor</c> 把联动交给源生成器。
    /// </remarks>
    public bool HasFailure => FailureText is not null;

    /// <summary>构造自启动项页 ViewModel。</summary>
    /// <param name="scanner">全量扫描服务。</param>
    /// <param name="takeover">接管服务，处理本页的「延时启动」动作。</param>
    /// <param name="icons">图标提取服务（D30）。提取在同一次后台扫描里顺带完成。</param>
    /// <param name="configStore">配置读取端，只用于取延时预设值与上限（FR-4.2 / FR-4.3）。</param>
    /// <param name="log">日志接收端。</param>
    public ItemsViewModel(
        ScanService scanner,
        TakeoverService takeover,
        IconProvider icons,
        IAppConfigStore configStore,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(takeover);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(log);

        _scanner = scanner;
        _takeover = takeover;
        _icons = icons;
        _configStore = configStore;
        _log = log;

        Subtitle = "正在扫描…";
        DelayPresets = new Settings().DelayPresets;
        MaxDelaySeconds = Settings.DefaultMaxDelaySeconds;
    }

    /// <summary>全部条目。已按「来源 → 名称」排序，排序由 <see cref="ScanService"/> 负责。</summary>
    /// <remarks>
    /// 筛选数据源与筛选视图分开：<see cref="Rows"/> 永远是全量（页头统计、筛选谓词都吃它），
    /// <see cref="FilteredRows"/> 才是列表绑定的东西。直接在 <see cref="Rows"/> 上增删
    /// 会让"当前过滤条件"没有一个可靠的回算来源。
    /// </remarks>
    public ObservableCollection<StartupEntryRow> Rows { get; } = [];

    /// <summary>筛选与搜索后的可见条目，列表实际绑定的集合。</summary>
    public ObservableCollection<StartupEntryRow> FilteredRows { get; } = [];

    /// <summary>全部条目是否为空（真正"一个都没有"，而不是被筛光了）。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>可见列表是否为空。空状态提示的显隐吃这个值。</summary>
    public bool FilteredEmpty => FilteredRows.Count == 0;

    /// <summary>空状态文案：区分"系统里没有"与"被筛选光了"。</summary>
    /// <remarks>
    /// 这两句话指向的动作完全不同（重新扫描 vs 放宽条件），合并成一句会让
    /// 筛光了数据的用户去点刷新 —— 白扫一遍什么也不会变。
    /// </remarks>
    public string EmptyText => IsEmpty
        ? "此位置没有自启动项\n点「刷新扫描」重新检查全部来源"
        : "没有符合当前搜索 / 筛选条件的条目\n试着清空搜索词或选「全部来源」";

    /// <summary>延时预设值（秒），驱动编辑器的快选按钮（FR-4.2）。</summary>
    /// <remarks>每次刷新时随配置一起更新，用户在设置页改过的预设值立刻生效。</remarks>
    public int[] DelayPresets { get; private set; }

    /// <summary>单条目延时上限；<c>0</c> 表示不限制（FR-4.3）。</summary>
    public int MaxDelaySeconds { get; private set; }

    /// <summary>重新扫描全部来源。</summary>
    /// <param name="cancellationToken">取消令牌，连续点刷新时取消上一条命令。</param>
    /// <returns>扫描完成的 <see cref="Task"/>。</returns>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            LoadSettings();

            // 图标提取和扫描放同一个后台任务：提取单个图标 5~15ms，37 项约几百毫秒，
            // 放 UI 线程会把刷新冻住；单独开任务又多一次线程切换。顺路做完最划算。
            var scan = await Task.Run(
                    () =>
                    {
                        var result = _scanner.Scan();
                        var pixels = new Dictionary<StartupEntry, IconPixels?>(capacity: result.Entries.Count);
                        foreach (var entry in result.Entries)
                        {
                            var source = IconSourceOf(entry);
                            pixels[entry] = source is null ? null : _icons.TryGetIcon(source);
                        }

                        return (Result: result, Pixels: pixels);
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            Apply(scan.Result, scan.Pixels);
        }
        catch (OperationCanceledException)
        {
            // 用户连续点刷新造成上一条命令被取消，属正常路径，不记日志也不提示。
        }
        catch (Exception ex)
        {
            _log.Error(ex, "自启动项扫描失败");
            FailureText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>接管一个系统自启动项。</summary>
    /// <param name="entry">要接管的条目。</param>
    /// <param name="options">用户在编辑器里做的选择。</param>
    /// <returns>接管结果；失败时 <see cref="TakeoverOutcome.Message"/> 可直接呈现给用户。</returns>
    /// <remarks>
    /// 直接调 <see cref="TakeoverService"/> 而不是自己写配置：接管是四步事务
    /// （记录原件 → 存配置 → 软禁用 → 注册计划任务），任一步失败都要按逆序回滚，
    /// 界面层复制这套顺序迟早会漏。
    /// </remarks>
    public TakeoverOutcome Takeover(StartupEntry entry, TakeoverOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        return _takeover.Takeover(entry, options);
    }

    /// <summary>读取延时预设值与上限；失败时保持上一次的值，不打扰用户。</summary>
    /// <remarks>
    /// 这两个值只影响编辑器的可选档位，读不到就用默认值 —— 为它弹错误框属于
    /// "把内部问题变成用户的问题"。
    /// </remarks>
    private void LoadSettings()
    {
        try
        {
            var settings = _configStore.Load().Settings;
            DelayPresets = settings.DelayPresets;
            MaxDelaySeconds = settings.MaxDelaySeconds;
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "读取延时预设失败，沿用默认值");
        }
    }

    /// <summary>把扫描结果灌进列表与页头。</summary>
    /// <param name="result">扫描结果。</param>
    /// <param name="pixels">每个条目的图标像素（后台阶段提取）。</param>
    private void Apply(ScanResult result, Dictionary<StartupEntry, IconPixels?> pixels)
    {
        Rows.Clear();
        foreach (var entry in result.Entries)
        {
            Rows.Add(new StartupEntryRow(entry, pixels[entry]));
        }

        ApplyFilters();

        Subtitle = BuildSubtitle(result);

        // 来源级失败必须显示成**列表可能不完整**，而不是一句"扫描完成" ——
        // 否则用户会把缺失的条目当成"系统里没有"，进而以为程序漏扫了某项。
        if (result.HasFailures)
        {
            var names = string.Join("、", result.Failures.Select(static failure => failure.DisplayName));
            FailureText = $"以下来源扫描失败，列表可能不完整：{names}。详情见运行日志。";
        }
        else
        {
            FailureText = null;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>搜索词变化 → 重算可见列表。</summary>
    /// <param name="value">新的搜索词。</param>
    /// <remarks>
    /// 源生成器（CommunityToolkit.Mvvm）在 <c>SearchText</c> 的 setter 里调用本方法，
    /// 不必手写订阅 PropertyChanged。
    /// </remarks>
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    /// <summary>来源筛选变化 → 重算可见列表。</summary>
    /// <param name="value">新的下拉序号。</param>
    partial void OnSourceFilterIndexChanged(int value) => ApplyFilters();

    /// <summary>按当前搜索词与来源筛选重建 <see cref="FilteredRows"/>。</summary>
    private void ApplyFilters()
    {
        var keyword = SearchText.Trim();

        FilteredRows.Clear();
        foreach (var row in Rows)
        {
            if (MatchesSourceFilter(row) && MatchesKeyword(row, keyword))
            {
                FilteredRows.Add(row);
            }
        }

        OnPropertyChanged(nameof(FilteredEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>判断一行是否通过来源筛选。</summary>
    /// <param name="row">候选行。</param>
    /// <returns>通过为 <see langword="true"/>。</returns>
    private bool MatchesSourceFilter(StartupEntryRow row) => SourceFilterIndex switch
    {
        1 => row.Entry.Source == StartupSource.Registry,
        2 => row.Entry.Source == StartupSource.StartupFolder,
        3 => row.Entry.Source == StartupSource.ScheduledTask,
        4 => row.Entry.Source == StartupSource.Uwp,
        _ => true,
    };

    /// <summary>判断一行是否命中搜索词。</summary>
    /// <param name="row">候选行。</param>
    /// <param name="keyword">已去空白的关键词；空串恒通过。</param>
    /// <returns>命中为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 三个字段全参与匹配：用户记住的可能是程序名（"微信"）、也可能是
    /// 可执行文件名（"WeChat"）或它藏身的位置（"Run"）—— 只匹配名称会把
    /// 后两种常见排查路径堵死。
    /// </remarks>
    private static bool MatchesKeyword(StartupEntryRow row, string keyword)
    {
        if (keyword.Length == 0)
        {
            return true;
        }

        return row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || row.CommandLine.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || row.LocationText.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>条目的图标解析名（D30）。</summary>
    /// <param name="entry">扫描结果中的条目。</param>
    /// <returns>
    /// UWP 条目没有文件路径，用 <c>shell:AppsFolder\&lt;AUMID&gt;</c> 解析名 ——
    /// 与调度端启动 UWP 的路径约定同源（R12：零 COM 的启动方式，图标提取恰好也吃这个名字）。
    /// 其余条目用路径本身（<c>.lnk</c> 会被 shell 自动解析到目标图标）。
    /// </returns>
    private static string? IconSourceOf(StartupEntry entry)
    {
        if (entry.Source == StartupSource.Uwp)
        {
            return string.IsNullOrWhiteSpace(entry.SourceKey)
                ? null
                : $"shell:AppsFolder\\{entry.SourceKey}";
        }

        return string.IsNullOrWhiteSpace(entry.Path) ? null : entry.Path;
    }

    /// <summary>拼页头副标题。</summary>
    /// <param name="result">扫描结果。</param>
    /// <returns>形如 `共 37 项 · 已接管 0 项 · 已禁用 15 项`。</returns>
    /// <remarks>
    /// "已禁用"排除已接管的条目：被接管项的禁用状态是**本程序造成的**，
    /// 把它算进"已禁用"会让用户以为系统里本来就有这么多禁用项。
    /// </remarks>
    private static string BuildSubtitle(ScanResult result)
    {
        var disabled = result.Entries.Count(
            static entry => !entry.IsEnabled && !entry.IsTakenOver && !entry.IsMissing);

        return $"共 {result.TotalCount} 项 · 已接管 {result.TakenOverCount} 项 · 已禁用 {disabled} 项";
    }
}
