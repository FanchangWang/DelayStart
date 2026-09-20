using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 运行日志页 ViewModel（FR-8.1 / FR-8.2 / FR-8.3）。
/// </summary>
public partial class RunsViewModel : ObservableObject
{
    private readonly IRunStateStore _runState;
    private readonly PathService _paths;

    /// <summary>归档全量（不过滤）。<see cref="Groups"/> 是它的级别筛选视图。</summary>
    private readonly List<RunGroupRow> _allGroups = [];

    /// <summary>构造运行日志页 ViewModel。</summary>
    /// <param name="runState">运行状态读取端。</param>
    /// <param name="paths">路径服务，只用来取日志目录（打开日志文件按钮）。</param>
    public RunsViewModel(IRunStateStore runState, PathService paths)
    {
        ArgumentNullException.ThrowIfNull(runState);
        ArgumentNullException.ThrowIfNull(paths);

        _runState = runState;
        _paths = paths;
    }

    /// <summary>运行分组（最新在前，最多 30 次，已按级别筛选）。</summary>
    public ObservableCollection<RunGroupRow> Groups { get; } = [];

    /// <summary>是否没有任何归档。</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>是否正在加载归档。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>级别筛选序号：0 全部级别 / 1 仅含失败（FR-8 的最小可用筛选 —— 本页数据只有成功 / 失败两档，映射到"这次运行有没有失败项"）。</summary>
    [ObservableProperty]
    public partial int LevelFilterIndex { get; set; }

    /// <summary>级别筛选变化 → 重填分组视图。</summary>
    /// <param name="value">新的级别序号。</param>
    partial void OnLevelFilterIndexChanged(int value) => RefillGroups();

    /// <summary>页头副标题。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; } = "正在读取…";

    /// <summary>在资源管理器中打开日志目录（调度器日志 / 管理端日志都在这里）。</summary>
    /// <returns>异步任务。</returns>
    [RelayCommand]
    private Task OpenLogsAsync()
    {
        // 目录不存在（从未运行过 CLI/GUI）时先补建，避免 explorer 弹"找不到路径"。
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

    /// <summary>加载运行归档。页面进入时调用。</summary>
    /// <returns>异步任务。</returns>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var records = await Task.Run(() => _runState.ReadRecent(30)).ConfigureAwait(true);

            _allGroups.Clear();
            foreach (var record in records)
            {
                var group = new RunGroupRow(record);
                foreach (var item in record.Items)
                {
                    group.Items.Add(new RunItemRow(item));
                }

                _allGroups.Add(group);
            }

            RefillGroups();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>按级别筛选重填 <see cref="Groups"/> 并同步副标题。</summary>
    /// <remarks>
    /// 进入页面时最新一组默认展开（2026-09-21 批复）：首组 <see cref="RunGroupRow.IsExpanded"/>
    /// 置真，其余收起。筛选重填后同样生效 —— 「仅失败」视图下第一组就是最新的失败记录。
    /// </remarks>
    private void RefillGroups()
    {
        var onlyFailed = LevelFilterIndex == 1;

        Groups.Clear();
        foreach (var group in _allGroups)
        {
            if (!onlyFailed || group.HasFailure)
            {
                group.IsExpanded = Groups.Count == 0;
                Groups.Add(group);
            }
        }

        Subtitle = Groups.Count > 0
            ? (onlyFailed ? "含失败项的调度 · " : string.Empty) + $"最近 {Groups.Count} 次（保留最近 30 次）"
            : onlyFailed
                ? "最近 30 次调度没有失败项"
                : "还没有调度记录 —— 接管条目并重启后这里会出现每次登录的启动结果";
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>一次调度运行的分组行。</summary>
public sealed class RunGroupRow
{
    private readonly RunRecord _record;

    /// <summary>构造分组行。</summary>
    /// <param name="record">运行记录。</param>
    public RunGroupRow(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _record = record;
    }

    /// <summary>运行标识。</summary>
    public string RunId => _record.RunId;

    /// <summary>标题：时间 + 成功/失败数。</summary>
    public string Title
    {
        get
        {
            var ok = _record.Items.Count(static item => item.State == RunItemState.Done);
            var failed = _record.Items.Count(static item => item.State == RunItemState.Failed);

            if (!_record.CompletedNormally)
            {
                return $"{_record.StartedAt:yyyy-MM-dd HH:mm:ss} · 上次调度未正常完成";
            }

            return failed > 0
                ? $"{_record.StartedAt:yyyy-MM-dd HH:mm:ss} · 成功 {ok} · 失败 {failed}"
                : $"{_record.StartedAt:yyyy-MM-dd HH:mm:ss} · {ok}/{_record.Items.Count} 全部成功";
        }
    }

    /// <summary>这次运行是否含失败项（级别筛选「仅失败」的判据）。</summary>
    public bool HasFailure => _record.Items.Any(static item => item.State == RunItemState.Failed);

    /// <summary>
    /// 分组是否默认展开。进入页面 / 重填筛选时最新一组（分组列表的首项）为
    /// <see langword="true"/>。行容器随筛选重填会重新生成，x:Bind OneTime 在生成时求值即可，
    /// 不需要 INPC。
    /// </summary>
    public bool IsExpanded { get; set; }

    /// <summary>各条目结果。</summary>
    public ObservableCollection<RunItemRow> Items { get; } = [];
}

/// <summary>运行分组里的一条条目结果（FR-8.2）。</summary>
public sealed class RunItemRow
{
    private readonly RunItemResult _result;

    /// <summary>构造条目行。</summary>
    /// <param name="result">运行结果。</param>
    public RunItemRow(RunItemResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        _result = result;
    }

    /// <summary>条目名。</summary>
    public string Name => _result.Name;

    /// <summary>延时文案。</summary>
    public string DelayText => $"{_result.Delay} 秒";

    /// <summary>结果文案。</summary>
    public string StateText => _result.State switch
    {
        RunItemState.Done => "✓ 成功",
        RunItemState.Failed => "✗ 失败",
        RunItemState.Launching => "◐ 启动中",
        _ => "· 未执行",
    };

    /// <summary>是否失败（失败项标红）。</summary>
    public bool IsFailed => _result.State == RunItemState.Failed;

    /// <summary>失败原因。</summary>
    public string ReasonText => _result.Reason ?? string.Empty;

    /// <summary>实际发起时刻。</summary>
    public string LaunchedAtText => _result.LaunchedAt?.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
}
