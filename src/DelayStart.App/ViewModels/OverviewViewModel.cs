using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.App.Services;
using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 总览页 ViewModel（UI v2，PowerToys 分节布局，<c>docs/ui-mockup-v2.html</c>）：
/// 延时列表来源计数 / 最大延时 / 开机调度任务开关 / 扫描来源计数 / 最近一次开机调度。
/// </summary>
/// <remarks>
/// D1（A）：模拟调度已删除。D3 的开关语义：开 = 注册计划任务，关 = 删除 ——
/// 原外壳状态条（只读）的职责合并进这里的开关。
/// </remarks>
public partial class OverviewViewModel : ObservableObject
{
    private readonly IAppConfigStore _configStore;
    private readonly IRunStateStore _runState;
    private readonly ISchedulerTaskRegistrar _registrar;
    private readonly ScanCacheService _scanCache;

    /// <summary>构造总览页 ViewModel。</summary>
    /// <param name="configStore">配置读取端。</param>
    /// <param name="runState">运行状态读取端。</param>
    /// <param name="registrar">调度计划任务注册端（开关直接注册 / 删除）。</param>
    /// <param name="scanCache">扫描缓存（来源计数，启动后已有）。</param>
    public OverviewViewModel(
        IAppConfigStore configStore,
        IRunStateStore runState,
        ISchedulerTaskRegistrar registrar,
        ScanCacheService scanCache)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(runState);
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(scanCache);

        _configStore = configStore;
        _runState = runState;
        _registrar = registrar;
        _scanCache = scanCache;
    }

    // ── 延时启动（来源计数 chips）──────────────────────────────────────────

    /// <summary>延时列表里来自注册表的条目数。</summary>
    [ObservableProperty]
    public partial int DelayedRegistry { get; set; }

    /// <summary>延时列表里来自启动文件夹的条目数。</summary>
    [ObservableProperty]
    public partial int DelayedFolder { get; set; }

    /// <summary>延时列表里来自计划任务的条目数。</summary>
    [ObservableProperty]
    public partial int DelayedTask { get; set; }

    /// <summary>延时列表里来自 UWP 的条目数。</summary>
    [ObservableProperty]
    public partial int DelayedUwp { get; set; }

    /// <summary>延时列表里手动添加的条目数（用户反馈 3：总览缺手动添加数量）。</summary>
    [ObservableProperty]
    public partial int DelayedManual { get; set; }

    // ── 最大延时（原「开机加载窗口」，按用户反馈改为直观口径）────────────

    /// <summary>最长条目的延时文案。</summary>
    [ObservableProperty]
    public partial string MaxDelayText { get; set; } = "—";

    // ── 开机调度任务开关 ──────────────────────────────────────────────────

    /// <summary>调度任务是否存在（开关的绑定值）。</summary>
    [ObservableProperty]
    public partial bool IsTaskRegistered { get; set; }

    /// <summary>开关旁的状态描述。</summary>
    [ObservableProperty]
    public partial string TaskStatusText { get; set; } = "正在检查…";

    /// <summary>开关操作是否正在进行（防止连点）。</summary>
    [ObservableProperty]
    public partial bool IsTogglingTask { get; set; }

    // ── 系统自启动项（扫描来源计数 chips）────────────────────────────────

    /// <summary>扫描到的注册表条目数。</summary>
    [ObservableProperty]
    public partial int ScannedRegistry { get; set; }

    /// <summary>扫描到的启动文件夹条目数。</summary>
    [ObservableProperty]
    public partial int ScannedFolder { get; set; }

    /// <summary>扫描到的计划任务条目数。</summary>
    [ObservableProperty]
    public partial int ScannedTask { get; set; }

    /// <summary>扫描到的 UWP 条目数。</summary>
    [ObservableProperty]
    public partial int ScannedUwp { get; set; }

    // ── 最近一次开机调度 ──────────────────────────────────────────────────

    /// <summary>「最近一次开机调度」的条目行；从未运行过为空集合。</summary>
    public ObservableCollection<RunItemRow> RecentRunRows { get; } = [];

    /// <summary>「最近一次开机调度」标题（带运行时刻）。</summary>
    [ObservableProperty]
    public partial string RecentRunTitle { get; set; } = "最近一次开机调度";

    /// <summary>「最近一次开机调度」摘要（成功 / 失败数）。</summary>
    [ObservableProperty]
    public partial string RecentRunSummary { get; set; } = "还没有调度记录 —— 接管条目并重启后，每次登录的启动结果会显示在这里";

    /// <summary>是否没有可展示的最近运行记录。</summary>
    /// <remarks>
    /// 🔴 必须是 <c>[ObservableProperty]</c> 而不是 <c>=&gt; Rows.Count == 0</c> 计算属性：
    /// x:Bind 的 OneWay 要求路径上有通知源，get-only 计算属性会让 XamlCompiler 报
    /// "OneWay bindings require ..."（增量 pass-2 下按错误处理，构建失败）。
    /// 在 <see cref="FillRecentRun"/> 里显式赋值。
    /// </remarks>
    [ObservableProperty]
    public partial bool RecentRunEmpty { get; set; } = true;

    /// <summary>上次运行横幅文案；全部成功或从未运行时为空（FR-6.7 / 9.3）。</summary>
    [ObservableProperty]
    public partial string LastRunBanner { get; set; } = string.Empty;

    /// <summary>横幅是否可见。同为 OneWay 绑定要求的通知源（见 <see cref="RecentRunEmpty"/>）。</summary>
    [ObservableProperty]
    public partial bool HasBanner { get; set; }

    /// <summary>刷新全部数据。页面进入时调用（全部读缓存 / 小文件，秒回）。</summary>
    /// <returns>异步任务。</returns>
    public async Task LoadAsync()
    {
        var config = _configStore.Load();

        // ── 延时列表来源计数 ─────────────────────────────────────────────
        DelayedRegistry = config.Items.Count(static item => item.Source == StartupSource.Registry);
        DelayedFolder = config.Items.Count(static item => item.Source == StartupSource.StartupFolder);
        DelayedTask = config.Items.Count(static item => item.Source == StartupSource.ScheduledTask);
        DelayedUwp = config.Items.Count(static item => item.Source == StartupSource.Uwp);
        DelayedManual = config.Items.Count(static item => item.Source == StartupSource.Manual);

        // ── 最大延时 ─────────────────────────────────────────────────────
        var enabledDelays = config.Items.Where(static item => item.Enabled).Select(static item => item.DelaySeconds).ToList();
        MaxDelayText = enabledDelays.Count > 0
            ? DisplayText.DelayOf(enabledDelays.Max())
            : "尚无启用的条目";

        // ── 扫描来源计数（读缓存：启动后已扫过一次，bug#7）────────────────
        var snapshot = await _scanCache.EnsureLoadedAsync().ConfigureAwait(true);
        ScannedRegistry = snapshot.Entries.Count(static entry => entry.Source == StartupSource.Registry);
        ScannedFolder = snapshot.Entries.Count(static entry => entry.Source == StartupSource.StartupFolder);
        ScannedTask = snapshot.Entries.Count(static entry => entry.Source == StartupSource.ScheduledTask);
        ScannedUwp = snapshot.Entries.Count(static entry => entry.Source == StartupSource.Uwp);

        RefreshTaskState();
        await FillRecentRun().ConfigureAwait(true);
    }

    /// <summary>开 / 关开机调度任务（用户点开关）：开 = 注册，关 = 删除。</summary>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 失败时把开关回拨并写明原因 —— 开关态必须永远与系统真实状态一致，
    /// 否则用户以为关了、下次登录调度器照样跑。
    /// </remarks>
    [RelayCommand]
    private async Task ToggleTaskAsync()
    {
        if (IsTogglingTask)
        {
            return;
        }

        IsTogglingTask = true;
        try
        {
            if (IsTaskRegistered)
            {
                await Task.Run(_registrar.Delete).ConfigureAwait(true);
                IsTaskRegistered = false;
                TaskStatusText = "未创建 · 延时启动不会生效";
            }
            else
            {
                await Task.Run(_registrar.RegisterOrUpdate).ConfigureAwait(true);
                IsTaskRegistered = true;
                TaskStatusText = "已创建 · 登录后按各条目延时分批启动";
            }
        }
        catch (Exception ex)
        {
            // 回拨开关并保留真实状态文案：查询一次兜底，查询也失败就按缺失处理。
            RefreshTaskState();
            TaskStatusText = $"操作失败：{ex.Message}";
        }
        finally
        {
            IsTogglingTask = false;
        }
    }

    /// <summary>查询调度任务状态（查询失败按缺失处理并保持界面可用）。</summary>
    private void RefreshTaskState()
    {
        try
        {
            IsTaskRegistered = _registrar.IsRegistered();
            TaskStatusText = IsTaskRegistered
                ? "已创建 · 登录后按各条目延时分批启动"
                : "未创建 · 延时启动不会生效";
        }
        catch
        {
            IsTaskRegistered = false;
            TaskStatusText = "无法确认 · 延时启动可能不会生效";
        }
    }

    /// <summary>把「最近一次运行」灌进日志区（读文件放后台，行灌入留在 UI 线程）。</summary>
    /// <returns>异步任务。</returns>
    private async Task FillRecentRun()
    {
        var current = await Task.Run(_runState.ReadCurrent).ConfigureAwait(true);

        RecentRunRows.Clear();
        if (current is null)
        {
            RecentRunTitle = "最近一次开机调度";
            RecentRunSummary = "还没有调度记录 —— 接管条目并重启后，每次登录的启动结果会显示在这里";
            RecentRunEmpty = RecentRunRows.Count == 0;
            return;
        }

        foreach (var item in current.Items)
        {
            RecentRunRows.Add(new RunItemRow(item));
        }

        var ok = current.Items.Count(static item => item.State == RunItemState.Done);
        var failed = current.Items.Count(static item => item.State == RunItemState.Failed);
        RecentRunTitle = $"最近一次开机调度 · {current.StartedAt:yyyy-MM-dd HH:mm:ss}";
        RecentRunSummary = failed > 0
            ? $"{ok}/{current.Items.Count} 成功 · 失败 {failed}"
            : $"{current.Items.Count}/{current.Items.Count} 全部成功";

        RecentRunEmpty = RecentRunRows.Count == 0;

        // 横幅（失败提示）与日志区共用一次读取。
        LastRunBanner = await Task.Run(BuildLastRunBanner).ConfigureAwait(true);
        HasBanner = LastRunBanner.Length > 0;
    }

    /// <summary>按 scheduler-design.md 9.3 的横幅文案矩阵生成「上次运行结果」。</summary>
    private string BuildLastRunBanner()
    {
        var current = _runState.ReadCurrent();
        if (current is null)
        {
            return string.Empty;
        }

        var failedItems = current.Items
            .Where(static item => item.State == RunItemState.Failed)
            .ToList();

        if (failedItems.Count == 0)
        {
            return string.Empty;
        }

        var runs = _runState.ReadRecent(FailureStreakService.DefaultMaxRunsScanned);

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
}
