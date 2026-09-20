using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.App.Services;
using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 总览页 ViewModel（UI v3，<c>docs/ui-mockup-v3.html</c>）：
/// 延时列表来源计数 / 开机调度任务状态卡 / 扫描来源计数 / 最近一次开机调度。
/// </summary>
/// <remarks>
/// <para>
/// 2026-09-21 批复（原型 v3）：本软件强依赖调度器，计划任务改为**强制存在** ——
/// 进入页面时检测，缺失即自动创建；创建失败给红色强提示 +「重试创建」按钮。
/// 原先的开关（D3：开 = 注册，关 = 删除）随之删除，配套的
/// <c>FirstRunBootstrap</c>「仅首启一次」不变量也一并废止（改为每次启动检测补建）。
/// </para>
/// <para>
/// D1（A）：模拟调度已删除。「上次运行有失败」横幅已删 —— 失败信息在
/// 「最近一次开机调度」的结果列里用红色呈现，不再重复摆一个提示框。
/// </para>
/// </remarks>
public partial class OverviewViewModel : ObservableObject
{
    private const string TaskReadyDetail =
        "登录时由计划任务拉起调度器，按延时依次启动条目。任务缺失时会自动重新创建，无需手动维护。";

    private const string TaskFailDetail =
        "没有调度任务，延时启动不会生效。点击右侧按钮重试；若反复失败，请查看运行日志中的错误详情。";

    private readonly IAppConfigStore _configStore;
    private readonly IRunStateStore _runState;
    private readonly ISchedulerTaskRegistrar _registrar;
    private readonly ScanCacheService _scanCache;

    /// <summary>构造总览页 ViewModel。</summary>
    /// <param name="configStore">配置读取端。</param>
    /// <param name="runState">运行状态读取端。</param>
    /// <param name="registrar">调度计划任务注册端（检测 / 补建）。</param>
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

    // ── 开机调度任务状态卡 ────────────────────────────────────────────────

    /// <summary>调度任务是否已就绪（决定状态卡显示正常态还是失败态）。</summary>
    /// <remarks>初值取 <see langword="true"/>：正常是常态，启动瞬间不让失败卡片闪一下。</remarks>
    [ObservableProperty]
    public partial bool TaskReady { get; set; } = true;

    /// <summary>状态卡里的描述文字（正常态固定文案；失败态带失败原因）。</summary>
    [ObservableProperty]
    public partial string TaskStatusDetail { get; set; } = TaskReadyDetail;

    /// <summary>检测 / 补建是否正在进行（防止连点）。</summary>
    [ObservableProperty]
    public partial bool IsWorkingOnTask { get; set; }

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

    /// <summary>是否没有可展示的最近运行记录。</summary>
    /// <remarks>
    /// 🔴 必须是 <c>[ObservableProperty]</c> 而不是 <c>=&gt; Rows.Count == 0</c> 计算属性：
    /// x:Bind 的 OneWay 要求路径上有通知源，get-only 计算属性会让 XamlCompiler 报
    /// "OneWay bindings require ..."（增量 pass-2 下按错误处理，构建失败）。
    /// 在 <see cref="FillRecentRun"/> 里显式赋值。
    /// </remarks>
    [ObservableProperty]
    public partial bool RecentRunEmpty { get; set; } = true;

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

        // ── 扫描来源计数（读缓存：启动后已扫过一次，bug#7）────────────────
        var snapshot = await _scanCache.EnsureLoadedAsync().ConfigureAwait(true);
        ScannedRegistry = snapshot.Entries.Count(static entry => entry.Source == StartupSource.Registry);
        ScannedFolder = snapshot.Entries.Count(static entry => entry.Source == StartupSource.StartupFolder);
        ScannedTask = snapshot.Entries.Count(static entry => entry.Source == StartupSource.ScheduledTask);
        ScannedUwp = snapshot.Entries.Count(static entry => entry.Source == StartupSource.Uwp);

        await EnsureTaskAsync().ConfigureAwait(true);
        await FillRecentRun().ConfigureAwait(true);
    }

    /// <summary>检测调度计划任务，缺失即补建；失败切到失败态（用户可点「重试创建」再来一遍）。</summary>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 页面进入与「重试创建」按钮共用本命令。2026-09-21 批复的语义：
    /// 本软件强依赖调度器，任务缺失不再是"用户可以关掉的状态"，而是要自动修复的故障。
    /// </remarks>
    [RelayCommand]
    private async Task EnsureTaskAsync()
    {
        if (IsWorkingOnTask)
        {
            return;
        }

        IsWorkingOnTask = true;
        try
        {
            try
            {
                var registered = await Task.Run(_registrar.IsRegistered).ConfigureAwait(true);
                if (registered)
                {
                    TaskReady = true;
                    TaskStatusDetail = TaskReadyDetail;
                    return;
                }

                await Task.Run(_registrar.RegisterOrUpdate).ConfigureAwait(true);
                TaskReady = true;
                TaskStatusDetail = TaskReadyDetail;
            }
            catch (Exception ex)
            {
                // 查询 / 注册失败都进失败态：原因原样摆出来，修复路径交给「重试创建」。
                TaskReady = false;
                TaskStatusDetail = $"{TaskFailDetail}\n失败原因：{ex.Message}";
            }
        }
        finally
        {
            IsWorkingOnTask = false;
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
            RecentRunEmpty = true;
            return;
        }

        foreach (var item in current.Items)
        {
            RecentRunRows.Add(new RunItemRow(item));
        }

        RecentRunEmpty = RecentRunRows.Count == 0;
    }
}
