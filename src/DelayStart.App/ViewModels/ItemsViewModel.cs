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
    private readonly IAppConfigStore _configStore;
    private readonly ILogSink _log;

    /// <summary>页头副标题，形如 `共 37 项 · 已接管 0 项 · 已禁用 15 项`。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; }

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
    /// <param name="configStore">配置读取端，只用于取延时预设值与上限（FR-4.2 / FR-4.3）。</param>
    /// <param name="log">日志接收端。</param>
    public ItemsViewModel(
        ScanService scanner,
        TakeoverService takeover,
        IAppConfigStore configStore,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(takeover);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(log);

        _scanner = scanner;
        _takeover = takeover;
        _configStore = configStore;
        _log = log;

        Subtitle = "正在扫描…";
        DelayPresets = new Settings().DelayPresets;
        MaxDelaySeconds = Settings.DefaultMaxDelaySeconds;
    }

    /// <summary>列表内容。已按「来源 → 名称」排序，排序由 <see cref="ScanService"/> 负责。</summary>
    public ObservableCollection<StartupEntryRow> Rows { get; } = [];

    /// <summary>列表是否为空（用于区分空状态与正常状态）。</summary>
    public bool IsEmpty => Rows.Count == 0;

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

            var result = await Task.Run(_scanner.Scan, cancellationToken).ConfigureAwait(true);
            Apply(result);
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
    private void Apply(ScanResult result)
    {
        Rows.Clear();
        foreach (var entry in result.Entries)
        {
            Rows.Add(new StartupEntryRow(entry));
        }

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
