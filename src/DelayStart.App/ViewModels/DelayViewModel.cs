using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 「延时启动」页的 ViewModel。数据来自 <c>config.json</c>，不是系统扫描结果。
/// </summary>
/// <remarks>
/// <para>
/// 与「自启动项」页的关键区别：那一页回答"系统里有什么"，这一页回答"我接管了什么、
/// 打算怎么启动它们"。所以本页可以包含**手动添加**的条目 —— 它在系统里没有对应物。
/// </para>
/// <para>
/// 本页只有列表一种视图。时间轴视图已按 <c>D37 = B</c> 整体删除
/// （原设计的双视图 + 搜索态同步过滤是本页最容易长 bug 的分支）。
/// </para>
/// </remarks>
public sealed partial class DelayViewModel : ObservableObject
{
    private readonly IAppConfigStore _configStore;
    private readonly ScanService _scanner;
    private readonly TakeoverService _takeover;
    private readonly ILogSink _log;

    /// <summary>页头副标题，形如 `共 5 项（含手动添加 1 项）· 最后一个在登录后 1 分 00 秒启动`。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; }

    /// <summary>是否正在读取 / 刷新。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>读取配置或扫描失败时的提示；正常时为 <see langword="null"/>。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    /// <summary>是否存在错误提示。XAML 的 <c>InfoBar.IsOpen</c> 只吃布尔值。</summary>
    /// <remarks>计算属性而不是独立的可观察字段，理由同 <c>ItemsViewModel.HasFailure</c>。</remarks>
    public bool HasError => ErrorText is not null;

    /// <summary>构造延时启动页 ViewModel。</summary>
    /// <param name="configStore">配置读写端。</param>
    /// <param name="scanner">扫描服务，仅用于判断条目是否已失效（E3）。</param>
    /// <param name="takeover">接管 / 释放服务。</param>
    /// <param name="log">日志接收端。</param>
    public DelayViewModel(
        IAppConfigStore configStore,
        ScanService scanner,
        TakeoverService takeover,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(takeover);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _scanner = scanner;
        _takeover = takeover;
        _log = log;

        Subtitle = "正在读取配置…";
    }

    /// <summary>列表内容，按「延时 → 顺序」排序（与调度端的发起顺序一致）。</summary>
    public ObservableCollection<DelayRow> Rows { get; } = [];

    /// <summary>列表是否为空（用于区分两种空状态）。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>读取配置并刷新列表。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>刷新完成的 <see cref="Task"/>。</returns>
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
            await Task.Run(() => _scanner.Scan(), cancellationToken).ConfigureAwait(true);
            Load();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取延时启动配置失败");
            ErrorText = $"读取配置失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>同步读取配置并刷新列表。页面首次进入时调用。</summary>
    public void Load()
    {
        AppConfig config;
        try
        {
            config = _configStore.Load();
        }
        catch (StartupOperationException ex)
        {
            // 配置版本高于本程序时的唯一正确行为是**拒绝加载**而不是尽力解析（会把用户配置写坏）。
            // 但界面必须把这件事说出来，否则用户看到的是"我的配置全没了"。
            _log.Error(ex, "配置无法加载");
            Rows.Clear();
            Subtitle = "配置不可用";
            ErrorText = ex.Message;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        // 失效判定需要"系统里还有没有这一项"。扫描整体失败时无法判断，
        // 此时**一律不标失效** —— 误标会让用户把好条目删掉，代价远大于漏标。
        var known = LoadKnownKeys(out var canJudgeStaleness);

        Rows.Clear();
        foreach (var item in config.Items.OrderBy(static item => item, StartupSortComparer.Instance))
        {
            var stale = canJudgeStaleness && !item.IsManual && !known.Contains(item.Id);
            Rows.Add(new DelayRow(item, stale));
        }

        Subtitle = BuildSubtitle(config.Items);
        ErrorText = canJudgeStaleness
            ? null
            : "系统扫描未完全成功，暂时无法判断哪些条目已失效。";
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>移除一个条目：恢复系统项 + 删除配置。</summary>
    /// <param name="row">要移除的行。</param>
    /// <returns>移除结果；调用方据此给出成功 / 失败反馈。</returns>
    /// <remarks>
    /// 交给 <see cref="TakeoverService.Release"/> 而不是自己删配置：释放动作是有顺序的
    /// （先恢复系统项、成功后才删配置），顺序错了会永久丢失还原依据。
    /// </remarks>
    public TakeoverOutcome Release(DelayRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var outcome = _takeover.Release(row.Item);
        if (outcome.Succeeded)
        {
            Load();
        }

        return outcome;
    }

    /// <summary>扫描一次系统，取出仍然存在的条目标识。</summary>
    /// <param name="canJudgeStaleness">扫描是否成功到可以下"失效"结论。</param>
    /// <returns>系统里存在的条目稳定主键集合。</returns>
    private HashSet<string> LoadKnownKeys(out bool canJudgeStaleness)
    {
        try
        {
            var scan = _scanner.Scan();
            canJudgeStaleness = !scan.HasFailures;
            return new HashSet<string>(scan.Entries.Select(static entry => entry.Id), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "扫描失败，本次不判断条目是否失效");
            canJudgeStaleness = false;
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>拼页头副标题。</summary>
    /// <param name="items">配置里的全部条目。</param>
    /// <returns>形如 `共 5 项（含手动添加 1 项）· 最后一个在登录后 1 分 00 秒启动`。</returns>
    /// <remarks>
    /// 形参用具体 <see cref="List{T}"/> 而不是 <c>IReadOnlyCollection</c>：调用点传的永远是
    /// <c>AppConfig.Items</c>（就是 <see cref="List{T}"/>），接口分发在这里是纯开销（CA1859）。
    /// </remarks>
    private static string BuildSubtitle(List<DelayedItem> items)
    {
        if (items.Count == 0)
        {
            return "还没有延时启动项";
        }

        var manual = items.Count(static item => item.IsManual);
        var head = manual > 0 ? $"共 {items.Count} 项（含手动添加 {manual} 项）" : $"共 {items.Count} 项";

        // 「最后一个」按**延时值**取最大，不是按列表末行 —— 列表按延时排序，
        // 末行确实是最大延时，但显式取 Max 才不依赖排序实现的稳定性。
        var last = items.Max(static item => item.DelaySeconds);
        return $"{head} · 最后一个在{DisplayText.DelayOf(last)}启动";
    }
}
