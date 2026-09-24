using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.App.Services;
using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 总览页 ViewModel（UI v3，2026-09-21 批复；D115 两张"上次记录"卡片；D117 分节合并；
/// D118 来源计数合并展示为「启动项」）：延时来源计数（蓝）/ 扫描来源计数（灰）/
/// 后台任务两卡（调度 + 守卫）/ 最近记录两卡。
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
/// 「最近记录」的调度卡里用红色呈现，不再重复摆一个提示框。
/// </para>
/// <para>
/// D115：原来的「最近一次开机调度」小表（逐条目 ListView）换成两张汇总卡片 ——
/// 列表页已经在调度日志页了，总览只回答"上次怎么样"；守卫此前没有对应的
/// 状态位，补一张「上次守卫巡检」。
/// </para>
/// <para>
/// D117（2026-09-24 批复）：「开机调度任务」+「自启动项守卫」合并为「后台任务」分节，
/// 「上次开机调度」+「上次守卫巡检」合并为「最近记录」分节；各卡**左侧最多两行文字**，
/// 卡片身份由右侧的操作 / 链接表达。档位说明行与任务注册细节删除（信息量不足，
/// 档位本身就在下拉里、状态就在右侧徽标上）。
/// </para>
/// </remarks>
public partial class OverviewViewModel : ObservableObject
{
    private readonly IAppConfigStore _configStore;
    private readonly IRunStateStore _runState;
    private readonly ISchedulerTaskRegistrar _registrar;
    private readonly ScanCacheService _scanCache;
    private readonly GuardTaskBootstrap _guardBootstrap;
    private readonly GuardInspectionStore _guardInspections;

    /// <summary>
    /// 回灌守卫：<see cref="LoadAsync"/> 里把配置值写进 <see cref="GuardSelectedIndex"/> 时，
    /// x:Bind 会把它推给 ComboBox 并触发一次 <c>SelectionChanged</c> —— 那次不是用户操作，
    /// 不能落盘（否则每次进总览页都会把档位"重存"一遍，并在失败时弹一条假错误）。
    /// 与设置页 <c>SettingsViewModel._loading</c> 同款守卫。
    /// </summary>
    private bool _loadingGuard;

    /// <summary>构造总览页 ViewModel。</summary>
    /// <param name="configStore">配置读取端。</param>
    /// <param name="runState">运行状态读取端。</param>
    /// <param name="registrar">调度计划任务注册端（检测 / 补建）。</param>
    /// <param name="scanCache">扫描缓存（来源计数，启动后已有）。</param>
    /// <param name="guardBootstrap">守卫计划任务同步端（档位变更 / 启动检测，D74）。</param>
    /// <param name="guardInspections">守卫巡检归档读取端（「上次守卫巡检」卡，D116）。</param>
    public OverviewViewModel(
        IAppConfigStore configStore,
        IRunStateStore runState,
        ISchedulerTaskRegistrar registrar,
        ScanCacheService scanCache,
        GuardTaskBootstrap guardBootstrap,
        GuardInspectionStore guardInspections)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(runState);
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(scanCache);
        ArgumentNullException.ThrowIfNull(guardBootstrap);
        ArgumentNullException.ThrowIfNull(guardInspections);

        _configStore = configStore;
        _runState = runState;
        _registrar = registrar;
        _scanCache = scanCache;
        _guardBootstrap = guardBootstrap;
        _guardInspections = guardInspections;
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

    /// <summary>失败态卡片里的失败原因（正常态不显示，留空即可）。</summary>
    [ObservableProperty]
    public partial string TaskStatusDetail { get; set; } = string.Empty;

    /// <summary>检测 / 补建是否正在进行（防止连点）。</summary>
    [ObservableProperty]
    public partial bool IsWorkingOnTask { get; set; }

    // ── 守卫设置（D74，2026-09-22 用户批复）───────────────────────────────

    /// <summary>守卫档位下拉的选项文案，顺序与 <see cref="GuardPresets.Options"/> 一一对应。</summary>
    /// <remarks>
    /// 🔴 由 <see cref="GuardPresets.Options"/> 生成而不是手写常量数组：
    /// 手写等于把"界面顺序"与"索引 ↔ 档位映射"分成两处维护，一旦漂移，
    /// 用户选"每隔 30 分钟"实际存的却是别的档位 —— 这种错不会报任何错。
    /// </remarks>
    public IReadOnlyList<string> GuardOptions { get; } = [.. GuardPresets.Options.Select(LabelOf)];

    /// <summary>当前选中的守卫档位索引（即 <see cref="GuardPresets.Options"/> 的下标）。</summary>
    [ObservableProperty]
    public partial int GuardSelectedIndex { get; set; }

    /// <summary>守卫设置保存 / 任务同步失败时的红字提示；空字符串表示正常。</summary>
    /// <remarks>
    /// 🔴 必须挂 <see cref="NotifyPropertyChangedForAttribute"/> 指向 <see cref="GuardHasError"/>：
    /// x:Bind 的 OneWay 要求路径上有通知源，get-only 派生属性不挂通知会让 XamlCompiler
    /// 拒绝编译（与设置页 <c>HasError</c> 同款，见 DelayViewModel.HasError）。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GuardHasError))]
    public partial string GuardError { get; set; } = string.Empty;

    /// <summary>是否显示失败提示（由 <see cref="GuardError"/> 通知驱动）。</summary>
    public bool GuardHasError => GuardError.Length > 0;

    /// <summary>守卫设置保存 / 任务同步是否正在进行（防重入）。</summary>
    [ObservableProperty]
    public partial bool IsWorkingOnGuard { get; set; }

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

    // ── 上次开机调度（D115：小表换成汇总卡片）────────────────────────────

    /// <summary>是否没有可展示的最近运行记录。</summary>
    /// <remarks>
    /// 🔴 必须是 <c>[ObservableProperty]</c> 而不是 <c>=&gt; …</c> 计算属性：
    /// x:Bind 的 OneWay 要求路径上有通知源，get-only 计算属性会让 XamlCompiler 报
    /// "OneWay bindings require ..."（增量 pass-2 下按错误处理，构建失败）。
    /// 在 <see cref="FillRecentRun"/> 里显式赋值。
    /// </remarks>
    [ObservableProperty]
    public partial bool RecentRunEmpty { get; set; } = true;

    /// <summary>卡片第一行：调度结果口径（成功 / 失败 / 跳过计数）。</summary>
    /// <remarks>
    /// 🔴 生成逻辑与调度日志页的分组标题（<see cref="RunGroupRow.Title"/>）是**同一份** ——
    /// 「成功 N · 跳过 N」不能读成"出事了"（FR-15.26）这类口径修正都留在那一处，卡片白拿。
    /// </remarks>
    [ObservableProperty]
    public partial string RecentRunSummary { get; set; } = string.Empty;

    /// <summary>卡片第二行：这次调度的开始时间。</summary>
    [ObservableProperty]
    public partial string RecentRunTimeText { get; set; } = string.Empty;

    /// <summary>这次调度是否要标红（含失败项，或上次调度未正常完成）。</summary>
    [ObservableProperty]
    public partial bool RecentRunHasFailure { get; set; }

    // ── 上次守卫巡检（D115：守卫此前没有任何状态位，补一张同构卡片）──────

    /// <summary>是否没有守卫巡检记录（从未巡检，或守卫一直处于关闭状态）。</summary>
    [ObservableProperty]
    public partial bool RecentGuardEmpty { get; set; } = true;

    /// <summary>卡片第一行：巡检汇总（与 guard.log「巡检完成」行、守卫日志页组标题同一口径）。</summary>
    [ObservableProperty]
    public partial string RecentGuardSummary { get; set; } = string.Empty;

    /// <summary>卡片第二行：这次巡检完成的时间。</summary>
    [ObservableProperty]
    public partial string RecentGuardTimeText { get; set; } = string.Empty;

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

        // ── 守卫档位（D74）────────────────────────────────────────────────
        // 回灌要置守卫：x:Bind 会把索引推给 ComboBox 并触发一次 SelectionChanged，
        // 那次事件不是用户操作（另一半守卫在 OverviewPage._initialized）。
        _loadingGuard = true;
        try
        {
            GuardSelectedIndex = GuardPresets.ToIndex(config.Settings.GuardMode, config.Settings.GuardMinutes);
        }
        finally
        {
            _loadingGuard = false;
        }

        // 每次进总览页同步一次守卫任务：与"调度任务缺失即补建"同款防丢失（D74 用户批复），
        // 另有"关闭即删除"与"档位变更即时生效"两条（见 GuardTaskBootstrap 的类注释）。
        SyncGuardTask();

        await EnsureTaskAsync().ConfigureAwait(true);
        await FillRecentRun().ConfigureAwait(true);
        await FillRecentGuard().ConfigureAwait(true);
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
                    TaskStatusDetail = string.Empty;
                    return;
                }

                await Task.Run(_registrar.RegisterOrUpdate).ConfigureAwait(true);
                TaskReady = true;
                TaskStatusDetail = string.Empty;
            }
            catch (Exception ex)
            {
                // 查询 / 注册失败都进失败态：原因原样摆出来，修复路径交给「重试创建」。
                TaskReady = false;
                TaskStatusDetail = ex.Message;
            }
        }
        finally
        {
            IsWorkingOnTask = false;
        }
    }

    /// <summary>守卫档位被用户改变：立即落盘 + 按新档位同步计划任务（D74）。</summary>
    /// <param name="index">下拉索引（由页面在 <c>SelectionChanged</c> 里传入）。</param>
    public void SetGuardIndex(int index)
    {
        // 回灌期（LoadAsync）不接受"用户操作" —— 那只是 x:Bind 的推送回声。
        if (_loadingGuard)
        {
            return;
        }

        SaveGuardAndSync(index);
    }

    /// <summary>失败提示旁的「重试」：用当前档位重新落盘并同步。</summary>
    [RelayCommand]
    private void RetryGuard() => SaveGuardAndSync(GuardSelectedIndex);

    /// <summary>把档位写进配置并同步计划任务；任一步失败只把原因摆到界面上，不抛。</summary>
    /// <param name="index">下拉索引。</param>
    /// <remarks>
    /// 顺序刻意是"先落盘、后同步任务"：<see cref="GuardTaskBootstrap.SyncWithSettings"/>
    /// 自己会重新读配置，所以只有配置先写好，任务的档位才会跟界面一致。
    /// </remarks>
    private void SaveGuardAndSync(int index)
    {
        if (IsWorkingOnGuard)
        {
            return;
        }

        IsWorkingOnGuard = true;
        try
        {
            var preset = GuardPresets.FromIndex(index);

            try
            {
                var config = _configStore.Load();
                config.Settings.GuardMode = preset.Mode;
                config.Settings.GuardMinutes = preset.Minutes;
                _configStore.Save(config);
            }
            catch (StartupOperationException ex)
            {
                GuardError = $"守卫设置保存失败：{ex.Message}";
                return;
            }

            // 让 VM 与控件状态一致。同值时 ComboBox 不会再次触发 SelectionChanged，
            // 因此不会回环（且上面已有 IsWorkingOnGuard 兜底）。
            GuardSelectedIndex = index;

            SyncGuardTask();
        }
        finally
        {
            IsWorkingOnGuard = false;
        }
    }

    /// <summary>按当前配置同步守卫计划任务，并把失败原因摆到界面上。</summary>
    /// <remarks>
    /// <see cref="GuardTaskBootstrap.SyncWithSettings"/> 不抛异常，失败也以结果对象返回，
    /// 所以这里不需要 try —— 那是它对外承诺的语义（跑在启动路径上）。
    /// </remarks>
    private void SyncGuardTask()
    {
        var outcome = _guardBootstrap.SyncWithSettings();
        GuardError = outcome.Result is GuardTaskSyncResult.Failed ? outcome.Message : string.Empty;
    }

    /// <summary>下拉文案：与 <see cref="GuardPresets.Options"/> 的顺序严格对应。</summary>
    private static string LabelOf(GuardPreset preset) => preset.Mode switch
    {
        GuardMode.Disabled => "不启动守卫",
        GuardMode.OnceAfterLogin => $"登录后 {preset.Minutes} 分钟启动一次",
        GuardMode.Periodic => $"每隔 {preset.Minutes} 分钟启动一次",
        _ => string.Empty,
    };

    /// <summary>把「上次开机调度」的汇总灌进卡片（读归档放后台，赋值留在 UI 线程）。</summary>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 数据取**最近一次已归档**的运行（<see cref="IRunStateStore.ReadRecent"/>)，
    /// 而不是实时状态 —— 卡片的语义是"上次怎么样"；一次还在进行中的调度，
    /// 等它归档了自然会顶上来。仅当从来没有归档（刚装好、第一次调度还在跑）时
    /// 回落读实时状态，避免"明明在跑、卡片却说没有"。
    /// </remarks>
    private async Task FillRecentRun()
    {
        var records = await Task.Run(() => _runState.ReadRecent(1)).ConfigureAwait(true);
        var record = records.Count > 0
            ? records[0]
            : await Task.Run(_runState.ReadCurrent).ConfigureAwait(true);

        if (record is null || record.Items.Count == 0)
        {
            RecentRunEmpty = true;
            return;
        }

        var group = new RunGroupRow(record);
        RecentRunSummary = group.Title;
        RecentRunTimeText = record.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        RecentRunHasFailure = group.HasFailure || !record.CompletedNormally;
        RecentRunEmpty = false;
    }

    /// <summary>把「上次守卫巡检」的汇总灌进卡片（读巡检归档放后台）。</summary>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 数据 = 最近一次巡检归档（<see cref="GuardInspectionStore.ReadRecent"/>，D116），
    /// 汇总行与守卫日志页组标题同源（<see cref="GuardRunSummaryText.Build"/>）。
    /// 没有归档（从未巡检 / 守卫一直关闭）就给空态 —— 守卫档位的状态由页面上方
    /// 「自启动项守卫」设置卡负责，这张卡只回答"上次巡检怎么样"。
    /// </remarks>
    private async Task FillRecentGuard()
    {
        var reports = await Task.Run(() => _guardInspections.ReadRecent(1)).ConfigureAwait(true);

        if (reports.Count == 0)
        {
            RecentGuardEmpty = true;
            return;
        }

        var report = reports[0];
        RecentGuardSummary = GuardRunSummaryText.Build(report);
        RecentGuardTimeText = report.CompletedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        RecentGuardEmpty = false;
    }
}
