using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.Core.Services;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 守卫日志页 ViewModel（D116，D3 批复）：读巡检归档（<c>guard\inspections\*.json</c>），
/// 按次分组，最新在前。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 数据源是**结构化归档**（<see cref="GuardInspectionStore"/>，D1=A 批复），不再是
/// guard.log 的文本解析 —— 一次巡检一组，组内能看到纠正了什么、新增了什么、失效了什么，
/// 这些明细此前只存在于系统通知里。guard.log 保留双轨（D4）：文本给"扫一眼"，
/// 归档给"查明细"；「打开日志文件」按钮仍指向日志目录（看文本日志）。
/// </para>
/// <para>
/// 分组版式与调度日志页同构（<see cref="RunsViewModel"/>）：Expander 分组、最新一组
/// 默认展开、组标题 = 时间 + 汇总。汇总文案与 guard.log「巡检完成」行同源
/// （<see cref="GuardRunSummaryText.Build"/>），口径天然一致。
/// </para>
/// </remarks>
public partial class GuardRunsViewModel : ObservableObject
{
    private readonly GuardInspectionStore _inspections;
    private readonly PathService _paths;

    /// <summary>归档全量（不过滤）。<see cref="Groups"/> 是它的筛选视图。</summary>
    private readonly List<GuardInspectionGroupRow> _allGroups = [];

    /// <summary>构造守卫日志页 ViewModel。</summary>
    /// <param name="inspections">巡检归档存储。</param>
    /// <param name="paths">路径服务：日志目录（「打开日志文件」按钮）来自这里。</param>
    public GuardRunsViewModel(GuardInspectionStore inspections, PathService paths)
    {
        ArgumentNullException.ThrowIfNull(inspections);
        ArgumentNullException.ThrowIfNull(paths);

        _inspections = inspections;
        _paths = paths;
    }

    /// <summary>巡检分组（最新在前，最多 30 次，已按筛选视图）。</summary>
    public ObservableCollection<GuardInspectionGroupRow> Groups { get; } = [];

    /// <summary>是否没有任何巡检归档。</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>是否正在加载。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>筛选序号：0 全部巡检 / 1 仅含变化与异常。</summary>
    [ObservableProperty]
    public partial int LevelFilterIndex { get; set; }

    /// <summary>筛选变化 → 重填分组视图。</summary>
    /// <param name="value">新的筛选序号。</param>
    partial void OnLevelFilterIndexChanged(int value) => RefillGroups();

    /// <summary>页头副标题。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; } = "正在读取…";

    /// <summary>在资源管理器中打开日志目录（guard.log 等文本日志在这里，D4 双轨保留）。</summary>
    /// <returns>异步任务。</returns>
    [RelayCommand]
    private Task OpenLogsAsync()
    {
        // 目录不存在（从未运行过任何端）时先补建，避免 explorer 弹"找不到路径"。
        if (!System.IO.Directory.Exists(_paths.LogsRoot))
        {
            System.IO.Directory.CreateDirectory(_paths.LogsRoot);
        }

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.LogsRoot,
                UseShellExecute = true,
            });
        return Task.CompletedTask;
    }

    /// <summary>加载巡检归档（读文件放后台）。页面进入时调用。</summary>
    /// <returns>异步任务。</returns>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var reports = await Task.Run(
                () => _inspections.ReadRecent(GuardInspectionStore.MaxRetainedInspections)).ConfigureAwait(true);

            _allGroups.Clear();
            foreach (var report in reports)
            {
                _allGroups.Add(new GuardInspectionGroupRow(report));
            }

            RefillGroups();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>按筛选重填 <see cref="Groups"/> 并同步副标题。</summary>
    /// <remarks>进入页面时最新一组默认展开；筛选重填后同样生效。</remarks>
    private void RefillGroups()
    {
        var onlyIssues = LevelFilterIndex == 1;

        Groups.Clear();
        foreach (var group in _allGroups)
        {
            if (!onlyIssues || group.HasIssues)
            {
                group.IsExpanded = Groups.Count == 0;
                Groups.Add(group);
            }
        }

        Subtitle = Groups.Count switch
        {
            0 when onlyIssues => $"最近 {GuardInspectionStore.MaxRetainedInspections} 次巡检没有变化与异常",
            0 => "还没有巡检记录 —— 守卫第一次巡检后这里会出现每次巡检的结果",
            _ when onlyIssues => $"仅含变化与异常 · 最近 {Groups.Count} 次",
            _ => $"最近 {Groups.Count} 次（保留最近 {GuardInspectionStore.MaxRetainedInspections} 次）",
        };
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>一次守卫巡检的分组行（D3 批复：组标题 = 时间 + 汇总，组内 = 明细）。</summary>
public sealed class GuardInspectionGroupRow
{
    private readonly GuardRunReport _report;

    /// <summary>构造分组行。</summary>
    /// <param name="report">本次巡检的归档记录。</param>
    public GuardInspectionGroupRow(GuardRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        _report = report;

        foreach (var outcome in report.Corrections)
        {
            Items.Add(new GuardInspectionItemRow(
                "纠正",
                outcome.Name,
                outcome.Detail,
                isWarning: !outcome.Succeeded));
        }

        foreach (var entry in report.NewItems)
        {
            Items.Add(new GuardInspectionItemRow(
                "新增",
                entry.Name,
                DisplayText.SourceOf(entry.Source),
                isWarning: false));
        }

        foreach (var stale in report.StaleItems)
        {
            Items.Add(new GuardInspectionItemRow(
                "失效",
                stale.Item.Name,
                DescribeStale(stale.Kind),
                isWarning: false));
        }

        foreach (var failure in report.Failures)
        {
            Items.Add(new GuardInspectionItemRow(
                "来源",
                failure.DisplayName,
                failure.Message,
                isWarning: true));
        }
    }

    /// <summary>组标题：完成时间 + 汇总（与 guard.log「巡检完成」行、总览卡同一口径）。</summary>
    public string Title
    {
        get
        {
            var time = _report.CompletedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return $"{time} · {GuardRunSummaryText.Build(_report)}";
        }
    }

    /// <summary>这次巡检是否含变化与异常（「仅含变化与异常」筛选的判据）。</summary>
    /// <remarks>变化 = 有新增 / 失效；异常 = 有纠正失败 / 来源失败。与通知的触发条件
    /// （<see cref="GuardRunReport.HasNotifications"/>）刻意不同：来源失败通知不了用户
    /// （没有条目可指），但在这里必须看得见 —— 列表不完整是要紧事（FR-1.4）。</remarks>
    public bool HasIssues =>
        _report.NewItems.Count > 0
        || _report.StaleItems.Count > 0
        || _report.CorrectionFailureCount > 0
        || _report.Failures.Count > 0;

    /// <summary>
    /// 分组是否默认展开。进入页面 / 重填筛选时最新一组（分组列表的首项）为
    /// <see langword="true"/>。与 <see cref="RunGroupRow.IsExpanded"/> 同款：OneTime 求值即可。
    /// </summary>
    public bool IsExpanded { get; set; }

    /// <summary>组内明细行（纠正 → 新增 → 失效 → 来源失败，与汇总行的口径顺序一致）。</summary>
    public ObservableCollection<GuardInspectionItemRow> Items { get; } = [];

    /// <summary>是否没有任何明细（本轮纯扫描、无变化无失败）—— 组内容区显示一句说明。</summary>
    public bool HasNoItems => Items.Count == 0;

    private static string DescribeStale(StaleKind kind) => kind switch
    {
        StaleKind.Orphan => "系统启动项已被删除",
        StaleKind.Missing => "目标程序已不存在",
        _ => string.Empty,
    };
}

/// <summary>巡检分组里的一条明细（类别 / 名称 / 说明）。</summary>
/// <param name="kindText">类别：纠正 / 新增 / 失效 / 来源。</param>
/// <param name="name">条目显示名（来源失败时为来源展示名）。</param>
/// <param name="detailText">说明：纠正结果、来源类型、失效原因或失败消息。</param>
/// <param name="isWarning">是否异常（纠正失败 / 来源失败标红）。</param>
public sealed class GuardInspectionItemRow(string kindText, string name, string detailText, bool isWarning)
{
    /// <summary>类别文案。</summary>
    public string KindText { get; } = kindText;

    /// <summary>条目名。</summary>
    public string Name { get; } = name;

    /// <summary>说明文案（过长截断，全文进 ToolTip）。</summary>
    public string DetailText { get; } = detailText;

    /// <summary>是否异常（标红）。</summary>
    public bool IsWarning { get; } = isWarning;
}
