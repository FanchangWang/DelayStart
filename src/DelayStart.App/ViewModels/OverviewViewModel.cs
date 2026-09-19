using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 总览页 ViewModel：统计 + 「上次运行结果」横幅（FR-6.7）+ 模拟调度（D37 改进度列表）。
/// </summary>
public partial class OverviewViewModel : ObservableObject
{
    private readonly ScanService _scanner;
    private readonly IAppConfigStore _configStore;
    private readonly IRunStateStore _runState;
    private readonly ISchedulerTaskRegistrar _registrar;

    /// <summary>构造总览页 ViewModel。</summary>
    /// <param name="scanner">全量扫描服务。</param>
    /// <param name="configStore">配置读取端。</param>
    /// <param name="runState">运行状态读取端（统计卡与「最近一次开机调度」）。</param>
    /// <param name="registrar">调度任务查询端（统计卡用，只读不注册 —— 与外壳状态条同一原则）。</param>
    public OverviewViewModel(
        ScanService scanner,
        IAppConfigStore configStore,
        IRunStateStore runState,
        ISchedulerTaskRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(runState);
        ArgumentNullException.ThrowIfNull(registrar);

        _scanner = scanner;
        _configStore = configStore;
        _runState = runState;
        _registrar = registrar;
    }

    /// <summary>系统里扫到的自启动项总数。</summary>
    [ObservableProperty]
    public partial int TotalEntries { get; set; }

    /// <summary>已接管（软禁用 + 进延时列表）的条目数。</summary>
    [ObservableProperty]
    public partial int TakenOverEntries { get; set; }

    /// <summary>延时列表里的条目数（含手动添加与已停用）。</summary>
    [ObservableProperty]
    public partial int DelayedEntries { get; set; }

    /// <summary>开机加载窗口文案（D5）：从登录到最后一个程序发起启动的时长。</summary>
    [ObservableProperty]
    public partial string BootWindowText { get; set; } = "—";

    /// <summary>开机调度任务统计卡文案（D5）：已就绪 / 未创建 / 无法确认。</summary>
    [ObservableProperty]
    public partial string TaskReadyText { get; set; } = "—";

    /// <summary>调度任务是否缺失或查询失败（统计卡转警示色）。</summary>
    [ObservableProperty]
    public partial bool IsTaskMissing { get; set; }

    /// <summary>「最近一次开机调度」的条目行（D5）；从未运行过为空集合。</summary>
    public ObservableCollection<RunItemRow> RecentRunRows { get; } = [];

    /// <summary>「最近一次开机调度」卡片标题（带运行时刻）。</summary>
    [ObservableProperty]
    public partial string RecentRunTitle { get; set; } = "最近一次开机调度";

    /// <summary>是否没有可展示的最近运行记录（卡片显示占位文案）。</summary>
    public bool RecentRunEmpty => RecentRunRows.Count == 0;

    /// <summary>上次运行横幅文案；全部成功或从未运行时为空（FR-6.7 / 9.3）。</summary>
    [ObservableProperty]
    public partial string LastRunBanner { get; set; } = string.Empty;

    /// <summary>横幅是否可见。</summary>
    public bool HasBanner => LastRunBanner.Length > 0;

    /// <summary>模拟调度的行集合（D37：时间轴改进度列表）。</summary>
    public ObservableCollection<SimulationRow> SimulationRows { get; } = [];

    /// <summary>模拟调度是否在进行中。</summary>
    [ObservableProperty]
    public partial bool Simulating { get; set; }

    /// <summary>模拟调度的进度标题。</summary>
    [ObservableProperty]
    public partial string SimulationTitle { get; set; } = string.Empty;

    /// <summary>模拟按钮文案（运行中变「停止」）。页面只读绑定，不放 code-behind。</summary>
    [ObservableProperty]
    public partial string SimulateButtonText { get; set; } = "开始模拟调度";

    /// <summary>模拟时钟（加速流逝的"登录后秒数"）。</summary>
    private double _simulatedSeconds;

    /// <summary>刷新统计与横幅。页面进入时调用。</summary>
    /// <returns>异步任务。</returns>
    public async Task LoadAsync()
    {
        var config = _configStore.Load();

        TotalEntries = await Task.Run(_scanner.Scan).ConfigureAwait(true) is { } scan
            ? scan.TotalCount
            : 0;
        TakenOverEntries = config.Items.Count(static item => !item.IsManual);
        DelayedEntries = config.Items.Count;

        var banner = await Task.Run(BuildLastRunBanner).ConfigureAwait(true);
        LastRunBanner = banner;
        OnPropertyChanged(nameof(HasBanner));

        BootWindowText = config.Items.Any(static item => item.Enabled)
            ? DisplayText.DelayOf(config.Items.Where(static item => item.Enabled).Max(static item => item.DelaySeconds))
            : "尚无启用的条目";

        RefreshTaskCard();
        var current = await Task.Run(() => _runState.ReadCurrent()).ConfigureAwait(true);

        // ⚠️ 行的灌入必须在 UI 线程：ObservableCollection 的集合变更事件被
        // x:Bind / ListView 直接消费，在后台线程 Clear/Add 会当场抛
        // "使用来自其他线程的 CollectionView"。文件读取已由上面的 Task.Run 承担。
        RecentRunRows.Clear();
        if (current is not null)
        {
            foreach (var item in current.Items)
            {
                RecentRunRows.Add(new RunItemRow(item));
            }

            RecentRunTitle = $"最近一次开机调度 · {current.StartedAt:yyyy-MM-dd HH:mm:ss}";
        }
        else
        {
            RecentRunTitle = "最近一次开机调度";
        }

        OnPropertyChanged(nameof(RecentRunEmpty));
    }

    /// <summary>查询开机调度任务状态（只读；查询失败按缺失处理并保持界面可用）。</summary>
    private void RefreshTaskCard()
    {
        try
        {
            IsTaskMissing = !_registrar.IsRegistered();
            TaskReadyText = IsTaskMissing ? "未创建" : "已就绪";
        }
        catch
        {
            IsTaskMissing = true;
            TaskReadyText = "无法确认";
        }
    }

    /// <summary>按 scheduler-design.md 9.3 的横幅文案矩阵生成「上次运行结果」。</summary>
    private string BuildLastRunBanner()
    {
        var current = _runState.ReadCurrent();
        if (current is null)
        {
            return string.Empty; // 从未运行：不显示横幅
        }

        var failedItems = current.Items
            .Where(static item => item.State == RunItemState.Failed)
            .ToList();

        if (failedItems.Count == 0)
        {
            return string.Empty; // 全部成功：不显示横幅（设计稿 9.3 第一行）
        }

        var runs = _runState.ReadRecent(FailureStreakService.DefaultMaxRunsScanned);

        // 取连续失败次数最高的条目决定横幅等级（9.2 升级机制）。
        var worstStreak = 0;
        string worstName = string.Empty;
        foreach (var item in failedItems)
        {
            var streak = FailureStreakService.CountConsecutiveFailures(runs, item.Id);
            if (streak > worstStreak)
            {
                worstStreak = streak;
                worstName = item.Name;
            }
        }

        if (worstStreak >= 3)
        {
            return $"『{worstName}』已连续 {worstStreak} 次启动失败，建议检查该程序是否仍然可用";
        }

        if (worstStreak == 2)
        {
            return $"『{worstName}』已连续 2 次启动失败";
        }

        return $"上次登录：{failedItems.Count} 个程序启动失败";
    }

    /// <summary>开始模拟调度：按调度端同样的排序与绝对时间点语义，10 倍速推进。</summary>
    /// <returns>异步任务。</returns>
    [RelayCommand]
    public void ToggleSimulation()
    {
        if (Simulating)
        {
            Simulating = false;
            SimulateButtonText = "开始模拟调度";
            return;
        }

        var config = _configStore.Load();
        var items = config.Items
            .Where(static item => item.Enabled)
            .OrderBy(static item => item, StartupSortComparer.Instance)
            .ToList();

        SimulationRows.Clear();
        foreach (var item in items)
        {
            SimulationRows.Add(new SimulationRow(item.Name, item.DelaySeconds));
        }

        Simulating = items.Count > 0;
        _simulatedSeconds = 0;
        SimulationTitle = "模拟调度 · 登录后 0 秒";
        SimulateButtonText = Simulating ? "停止模拟" : "开始模拟调度";
    }

    /// <summary>推进模拟时钟（由页面的 DispatcherTimer 每 100ms 调用 = 10 倍速）。</summary>
    /// <returns>模拟是否仍在进行。</returns>
    public bool AdvanceSimulation()
    {
        if (!Simulating)
        {
            return false;
        }

        _simulatedSeconds += 0.1;

        var allDone = true;
        foreach (var row in SimulationRows)
        {
            if (row.State != RunItemState.Done && _simulatedSeconds >= row.DelaySeconds)
            {
                row.State = RunItemState.Done;
                row.StatusText = "已启动";
            }

            if (row.State != RunItemState.Done)
            {
                allDone = false;
            }
        }

        SimulationTitle = $"模拟调度 · 登录后 {(int)_simulatedSeconds} 秒";

        if (allDone)
        {
            Simulating = false;
            SimulateButtonText = "开始模拟调度";
            return false;
        }

        return true;
    }
}

/// <summary>模拟调度的一行。</summary>
public partial class SimulationRow : ObservableObject
{
    /// <summary>构造一行模拟。</summary>
    /// <param name="name">条目名。</param>
    /// <param name="delaySeconds">配置延时（秒）。</param>
    public SimulationRow(string name, int delaySeconds)
    {
        ArgumentNullException.ThrowIfNull(name);

        Name = name;
        DelaySeconds = delaySeconds;
    }

    /// <summary>条目名。</summary>
    public string Name { get; }

    /// <summary>配置延时（秒），模拟时钟据此判断「是否到点」。</summary>
    public int DelaySeconds { get; }

    /// <summary>配置延时文案。</summary>
    public string DelayText => $"{DelaySeconds} 秒";

    /// <summary>模拟状态。</summary>
    [ObservableProperty]
    public partial RunItemState State { get; set; } = RunItemState.Waiting;

    /// <summary>状态文案。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "等待中";
}
