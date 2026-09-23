using System.Diagnostics;
using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Models;
using DelayStart.Core.Serialization;
using DelayStart.Core.Services;

namespace DelayStart.Scheduler;

/// <summary>
/// 调度引擎（机制 5–8，<c>docs/design.md 八</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 单线程消息循环驱动：<c>WM_TIMER</c> 节拍里按
/// <see cref="DelayCalculator"/>（**绝对时间点语义**，非累加）逐项到期启动，
/// 复查窗口（1.5 秒）到了再探测进程状态判定成败。
/// 每次状态变化都原子重写 <c>current-run.json</c>，管理端零 IPC 读取（D19）。
/// </para>
/// <para>
/// 失败策略为 D17 = D：保持接管、本次内按设置重试、跨登录原延时重试；
/// 软件不改系统状态，出口只有用户在管理端手动移出。
/// </para>
/// </remarks>
internal sealed class SchedulerEngine
{
    private const uint TimerIntervalMilliseconds = 250;

    /// <summary>完成后面板的自动关闭倒计时：全部成功 10 秒。</summary>
    private const int AutoCloseSecondsOnSuccess = 10;

    /// <summary>完成后面板的自动关闭倒计时：存在失败 60 秒（留时间看失败原因）。</summary>
    private const int AutoCloseSecondsOnFailure = 60;

    /// <summary>
    /// 周期跳过条目写进运行日志的原因文案（FR-15.26）。
    /// </summary>
    /// <remarks>
    /// 🔴 三种"跳过"共用 <see cref="RunItemState.Skipped"/>，区别只剩下这一句话，
    /// 所以它必须同时说清两件事：**不是失败**、**也不是用户点了跳过**。
    /// 只说"已跳过"等于让用户去猜是自己按错了还是程序没跑。
    /// </remarks>
    private const string NotInCycleReason = "今天不在启动周期内，未启动";

    private readonly IAppConfigStore _configStore;
    private readonly IRunStateStore _runState;
    private readonly IProcessLauncher _launcher;
    private readonly ILogSink _log;
    private readonly PathService _paths;
    private readonly Settings _settings;

    private readonly List<SchedulerRuntimeItem> _items = [];
    private readonly Stopwatch _stopwatch = new();

    private TrayIconHost? _tray;
    private IconResources? _icons;
    private RunRecord _record = new();
    private bool _finishing;
    private TimeSpan _quitAt;

    /// <summary>完成后已弹出面板，正在等用户关闭（关闭即退出，期间不能自行退出）。</summary>
    private bool _awaitingPanelClose;

    /// <summary>退出已发起（面板倒计时与菜单「退出」可能重复触发，需要幂等）。</summary>
    private bool _quitRequested;

    /// <summary>上一次按秒刷新面板时用的「下一项剩余秒数」（<see cref="RefreshLiveCountdown"/> 的节流签名）。</summary>
    private int _lastCountdownSecond = int.MinValue;

    /// <summary>
    /// 收尾动作是**面板上**发起的（「立即启动剩余 N 项」/「跳过剩余任务」）。
    /// 用户已经手动弹出面板在看，完成时就必须给结果：面板切完成态并起倒计时，
    /// 不能点了之后面板反而消失（2026-09-21 批复）。N4 后它仍参与收尾判定
    /// （见 <see cref="CompletionPolicy"/>），但与通知策略再无关系。
    /// </summary>
    private bool _panelRequestedCompletion;

    /// <summary>通知中转器的降权拉起器（懒建：只有真要发通知时才需要）。</summary>
    private DeElevatedProcessLauncher? _notifyLauncher;

    /// <summary>构造调度引擎。</summary>
    public SchedulerEngine(
        IAppConfigStore configStore,
        IRunStateStore runState,
        IProcessLauncher launcher,
        PathService paths,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(runState);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _runState = runState;
        _launcher = launcher;
        _paths = paths;
        _log = log;
        _settings = _configStore.Load().Settings;
    }

    /// <summary>
    /// 执行一次完整调度。阻塞到消息循环结束，返回进程退出码。
    /// </summary>
    public int Run()
    {
        if (!Initialize())
        {
            return 0; // 无条目 / 配置失败：静默退出（FR-5.10）
        }

        _icons = IconResources.Load(
            typeof(SchedulerEngine).Assembly,
            "DelayStart.Scheduler.Assets.Scheduler.ico",
            "DelayStart.Scheduler.Assets.SchedulerWarning.ico");

        // 窗口回调异常兜底（2026-09-21 托盘左键闪退教训）：AOT 下异常冲出
        // UnmanagedCallersOnly 会 fail-fast，必须就地吞掉并留日志。
        NativeMethods.MessageCallbackExceptionLogger = ex =>
            _log.Error(ex, "窗口消息处理发生未捕获异常（已吞掉防闪退）。");

        _tray = CreateTrayHost(_icons);
        if (_tray is null)
        {
            _log.Error("托盘宿主创建失败，本次调度无法进行。");
            return 1;
        }

        _tray.TimerTick = Tick;
        _tray.StartTimer(TimerIntervalMilliseconds);
        _ = NativeMethods.RunMessageLoop();
        return 0;
    }

    /// <summary>
    /// 读取本地法定日历（FR-15 / NFR-x）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 **只读本地磁盘，绝不联网**：这是在登录链路上跑的进程，
    /// 此刻代理与 DNS 常常还没就绪，把"今天启不启动"押在一次 HTTP 请求上违反「判定可靠」。
    /// 数据的下载与更新属于管理端（人工看着界面做）的职责。
    /// </para>
    /// <para>
    /// 读取失败 → 返回 <see langword="null"/> 并记 error 日志，**不打断本次调度**：
    /// 法定两档会退化成星期判定，代价远小于"今天什么都不启动"。
    /// </para>
    /// </remarks>
    /// <returns>日历；没有可用数据时为 <see langword="null"/>。</returns>
    private HolidayCalendar? LoadHolidayCalendar()
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var result = HolidayCalendarStore.Load(_paths);

            foreach (var issue in result.Issues)
            {
                _log.Warn($"法定日历文件不可用：{issue.FilePath} —— {issue.Reason}");
            }

            if (!result.Calendar.Covers(today))
            {
                _log.Warn(
                    $"本地没有 {today.Year} 年的法定节假日数据，"
                    + "「法定工作日」「法定节假日」两档本次按星期规律近似判定（可在管理端设置页更新）。");
            }

            return result.Calendar;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取法定日历失败，法定两档本次按星期规律近似判定。");
            return null;
        }
    }

    private bool Initialize()
    {
        AppConfig config;
        try
        {
            config = _configStore.Load();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取配置失败，本次不调度（保持全部接管状态不变）。");
            return false;
        }

        // 计划生成（过滤 + 排序 + 到点时刻）下沉到 Core 的 SchedulePlan，便于单测（FR-5.3 / FR-5.4）。
        // 调度周期（FR-15）：today 与法定日历都在这一处取，全流程共用一个快照 ——
        // 跨午夜不重判（FR-15.5），否则 23:59 启动的计划会在零点整悄悄换一套规则。
        var calendar = LoadHolidayCalendar();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var outcome = SchedulePlan.BuildWithSkipped(config.Items, config.Cycles, today, calendar);
        var plan = outcome.Entries;

        if (plan.Count == 0)
        {
            if (outcome.SkippedToday.Count > 0)
            {
                // FR-15.26：今天一条都不启动时，"为什么"必须留下痕迹 ——
                // 照旧走 FR-5.10 静默退出的话，运行日志里连一行都不会有，
                // 用户看到的是一个没动静的早晨，而答案在延时页的一条徽标上。
                RecordAllSkippedRun(outcome.SkippedToday);
            }
            else
            {
                // D4 批复 A（2026-09-22）：空计划无特判 —— 配置能读出来就按通知策略发
                // 完成通知（内容即"0 项成功"），与有计划的收尾走同一条 <see cref="SendCompletionNotification"/>。
                // 配置读不出来的那条失败路径不通知（读不到策略，无从判定）。
                _log.Info("没有启用的延时条目，按通知策略通报后退出（FR-5.10 / N2-D4）。");
            }

            SendCompletionNotification(failedCount: 0);
            return false;
        }

        var now = DateTimeOffset.Now;
        var planned = plan.Select(static entry => new RunItemResult
        {
            Id = entry.Item.Id,
            Name = entry.Item.Name,
            Delay = entry.Item.DelaySeconds,
        }).ToList();

        _record = new RunRecord
        {
            RunId = RunStateService.CreateRunId(now),
            StartedAt = now,
            PlannedCount = planned.Count,
            Items = planned,
        };

        // FR-15.26：今天不在周期内的启用条目也进本次日志，排在计划项之后 ——
        // 先"这次启动了什么"，再"什么今天轮不到"。
        // 🔴 只进日志、**不进 _items**：它们没有到点时刻，不参与 Tick 与收尾判定，
        // 也不该出现在进度面板的计数里（面板回答的是"这次跑得怎么样"）。
        AppendNotInCycleItems(outcome.SkippedToday, _record.Items);

        for (var index = 0; index < plan.Count; index++)
        {
            _items.Add(new SchedulerRuntimeItem
            {
                Item = plan[index].Item,
                Result = planned[index],
                LaunchAt = plan[index].LaunchAt,
            });
        }

        // D40：调度端亲自降权，没有代理进程，也就没有预热这一步。
        _stopwatch.Start();
        PersistState();
        _log.Info($"调度开始：{_record.RunId}，共 {_record.PlannedCount} 项"
            + (outcome.SkippedToday.Count > 0
                ? $"，另有 {outcome.SkippedToday.Count} 项今天不在周期内（记入日志，不启动）。"
                : "。"));
        return true;
    }

    /// <summary>
    /// 把"今天不在周期内"的条目追加进运行日志（FR-15.26）。
    /// </summary>
    /// <param name="items">今天被周期跳过的启用条目。</param>
    /// <param name="target">要追加到的运行记录条目表。</param>
    /// <remarks>
    /// 条目的 <see cref="RunItemResult.Delay"/> 照填配置延时（日志页那一列照常显示），
    /// <see cref="RunItemResult.LaunchedAt"/> 留空 —— 它今天没有被发起过，
    /// 而"没有发起时刻"正是该字段的既有语义（等待中被终止同理）。
    /// </remarks>
    private static void AppendNotInCycleItems(IReadOnlyList<DelayedItem> items, List<RunItemResult> target)
    {
        foreach (var item in items)
        {
            target.Add(new RunItemResult
            {
                Id = item.Id,
                Name = item.Name,
                Delay = item.DelaySeconds,
                State = RunItemState.Skipped,
                Reason = NotInCycleReason,
            });
        }
    }

    /// <summary>
    /// 今天所有启用条目都不在周期内：写一份"只有跳过项"的运行记录（FR-15.26）。
    /// </summary>
    /// <param name="skipped">全部被跳过的启用条目。</param>
    /// <remarks>
    /// <para>
    /// 🔴 这份记录的**全部意义就是回答一个问题**："今天为什么什么都没启动？"
    /// 它没有任何条目被执行过，因此不进消息循环、不建托盘、不弹面板 ——
    /// 写完就与 FR-5.10 一样静默退出（通知照旧按策略发）。
    /// </para>
    /// <para>
    /// 实时状态（<c>current-run.json</c>）也一并写：总览页的"最近一次运行"读的是它，
    /// 只写归档会让总览页停在上一次、与运行日志页说两套话。
    /// </para>
    /// </remarks>
    private void RecordAllSkippedRun(IReadOnlyList<DelayedItem> skipped)
    {
        var now = DateTimeOffset.Now;
        var record = new RunRecord
        {
            RunId = RunStateService.CreateRunId(now),
            StartedAt = now,
            FinishedAt = now,
            CompletedNormally = true,
            PlannedCount = 0,
            Items = [],
        };

        AppendNotInCycleItems(skipped, record.Items);

        try
        {
            _runState.WriteCurrent(record);
            _runState.Archive(record);
            _log.Info($"今天没有条目在启动周期内：{skipped.Count} 个启用条目全部跳过，已写入运行日志。");
        }
        catch (Exception ex)
        {
            // 写不进去也不该让登录路径上的这个进程卡住或报错 —— 与 FR-5.10 的静默退出等价。
            _log.Warn(ex, "写入「今天全部条目不在周期内」的运行记录失败。");
        }
    }

    private TrayIconHost? CreateTrayHost(IconResources? icons)
    {
        // 托盘只属调度端、恒显示（用户批复 2026-09-19：托盘设置已移除）。
        // UI v2（2026-09-21）：生命周期 = 调度期间显示 → 完成态面板关闭后退出（托盘随之消失）。
        var host = new TrayIconHost(
            icons,
            BuildPanelSnapshot,
            OpenRunLog,
            OpenManager,
            LaunchRemainingNow,
            SkipRemaining,
            LaunchRemainingFromPanel,
            SkipRemainingFromPanel,
            RequestQuit,
            HasWaitingItems,
            () => _finishing,
            BuildMenuStatusText,
            () => _settings.Theme);
        if (!host.TryCreate(BuildTooltip(), withIcon: icons is not null))
        {
            host.Dispose();
            return null;
        }

        return host;
    }

    // ---- 节拍 ----

    private void Tick()
    {
        var elapsed = _stopwatch.Elapsed;
        var changed = false;

        foreach (var runtime in _items)
        {
            if (runtime.Result.State == RunItemState.Waiting
                && elapsed >= runtime.LaunchAt) // LaunchAt 初值=配置延时；「立即启动」把它拨到当下
            {
                Launch(runtime);
                changed = true;
            }
            else if (runtime.Result.State == RunItemState.Launching && elapsed >= runtime.RecheckAt)
            {
                Evaluate(runtime);
                changed = true;
            }
        }

        if (changed)
        {
            PersistState();
            _tray?.RefreshPanel();
        }

        if (!_finishing && _items.All(static runtime => runtime.Result.State is RunItemState.Done or RunItemState.Failed or RunItemState.Skipped))
        {
            Finish();
        }

        // 完成态且已弹面板时不能自行退出 —— 退出时机交给面板（倒计时归零或失焦关闭，N9-D3）。
        if (_finishing && !_awaitingPanelClose && _stopwatch.Elapsed >= _quitAt)
        {
            Quit();
            return;
        }

        _tray?.UpdateTip(BuildTooltip());
        RefreshLiveCountdown();
    }

    /// <summary>
    /// 面板开着时让启动中的实时数字**走秒**：「下一项 N 秒后启动」与当前项 ETA。
    /// 只在状态变化时才重绘的话，这些秒数要等到下一个条目启动才动一次，
    /// 看起来就像卡住几秒才跳（2026-09-21 实测反馈）。
    /// 节流：剩余秒数没变就不重绘（节拍 250ms，实际每秒一次）。
    /// 完成态不在此列 —— 那边由面板自带的 1 秒倒计时定时器驱动。
    /// </summary>
    private void RefreshLiveCountdown()
    {
        if (_finishing)
        {
            return;
        }

        var soonest = TimeSpan.MaxValue;
        foreach (var runtime in _items)
        {
            if (runtime.Result.State == RunItemState.Waiting && runtime.LaunchAt < soonest)
            {
                soonest = runtime.LaunchAt;
            }
        }

        var second = soonest == TimeSpan.MaxValue
            ? -1
            : (int)Math.Ceiling(Math.Max((soonest - _stopwatch.Elapsed).TotalSeconds, 0));

        if (second == _lastCountdownSecond)
        {
            return;
        }

        _lastCountdownSecond = second;
        _tray?.RefreshPanel();
    }

    /// <summary>发起一次启动；失败且仍有重试额度时立即重试（同一次运行内，FR-9.6）。</summary>
    /// <remarks>
    /// 🔴 <b>首轮先做防双启动判定</b>（D76，2026-09-22 用户批复）。
    /// "仅首轮"是刻意的：<see cref="Evaluate"/> 复查失败后的重试本来就可能撞上
    /// "上一次尝试真的把进程拉起来了"，若重试轮也判定，会把正常重试误判成"已存在"而放弃。
    /// </remarks>
    private void Launch(SchedulerRuntimeItem runtime)
    {
        if (runtime.Result.Attempts == 0 && ShouldSkipAsAlreadyRunning(runtime.Item, out var skipReason))
        {
            // 🔴 标 Skipped 而不是 Failed（D1 A）：FailureStreakService 只认 Failed，
            // 一次 Failed 就推进失败连击、触发托盘角标与失败横幅 —— 而"进程已经在跑"
            // 根本不是失败。Skipped 语义准确，且天然不污染连击统计。
            runtime.Result.State = RunItemState.Skipped;
            runtime.Result.Reason = skipReason;
            _log.Info($"『{runtime.Item.Name}』{skipReason}");
            return;
        }

        while (true)
        {
            runtime.Result.Attempts++;
            runtime.Result.State = RunItemState.Launching;
            runtime.Result.LaunchedAt = DateTimeOffset.Now;
            _log.Info($"正在启动『{runtime.Item.Name}』（第 {runtime.Result.Attempts} 次尝试）。");

            var outcome = _launcher.Launch(runtime.Item);
            if (!outcome.Created)
            {
                var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshotAfterDelay: null);
                MarkResult(runtime, evaluation);

                if (RetryPolicy.Decide(runtime.Result.Attempts, _settings.RetryCount) == RetryDecision.Fail)
                {
                    return;
                }

                continue; // 还有重试额度
            }

            runtime.ProcessId = outcome.ProcessId;
            runtime.RecheckAt = _stopwatch.Elapsed
                + TimeSpan.FromMilliseconds(LaunchResultEvaluator.RecheckDelayMilliseconds);
            return;
        }
    }

    /// <summary>
    /// 启动前判断目标进程是否已在运行（D76）。
    /// </summary>
    /// <param name="item">条目。</param>
    /// <param name="reason">命中时的原因文案；未命中为空串。</param>
    /// <returns>应放弃启动时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 整条链路都往"宁可多启动一次"一侧倒（见 <see cref="LaunchTargetResolver"/> /
    /// <see cref="DuplicateLaunchPolicy"/> 的类注释）：推导不出目标、枚举进程失败、
    /// 读不到模块路径一律**放行**。漏判只是多跑一个进程；误判"已存在"会让用户的条目
    /// 永远不启动 —— 后者是功能回归。
    /// </remarks>
    private bool ShouldSkipAsAlreadyRunning(DelayedItem item, out string reason)
    {
        reason = string.Empty;

        var target = LaunchTargetResolver.Resolve(item);
        if (target is null)
        {
            return false;
        }

        IReadOnlyList<RunningProcessInfo> processes;
        try
        {
            processes = CollectRunningProcesses(target.ProcessName);
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"『{item.Name}』防双启动检查失败，本次按「未运行」处理并照常启动。");
            return false;
        }

        if (!DuplicateLaunchPolicy.Decide(target, processes).Skip)
        {
            return false;
        }

        reason = "进程已存在，未重复启动";
        return true;
    }

    /// <summary>枚举同名进程并取各自的可执行模块路径。</summary>
    /// <param name="processName">进程名（不含扩展名）。</param>
    /// <returns>进程快照；模块路径读不到时为 <see langword="null"/>（该进程不参与判定）。</returns>
    private static List<RunningProcessInfo> CollectRunningProcesses(string processName)
    {
        var snapshot = new List<RunningProcessInfo>();

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                string? modulePath = null;
                try
                {
                    // 提权不足 / 受保护进程 / 进程刚退出都会在这里抛 —— 属预期，按"读不到"处理。
                    modulePath = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                    or InvalidOperationException
                    or NotSupportedException)
                {
                    // 故意留空：读不到模块路径的进程不算命中（不能退化成按名字匹配）。
                }

                snapshot.Add(new RunningProcessInfo(process.ProcessName, modulePath));
            }
        }

        return snapshot;
    }

    /// <summary>复查窗口到期：探测进程状态并做最终判定（机制 7）。</summary>
    private void Evaluate(SchedulerRuntimeItem runtime)
    {
        var snapshot = ProbeProcess(runtime.ProcessId);
        var evaluation = LaunchResultEvaluator.Evaluate(
            new LaunchOutcome { Created = true, ProcessId = runtime.ProcessId },
            snapshot);

        MarkResult(runtime, evaluation);

        if (!evaluation.IsSuccess
            && RetryPolicy.Decide(runtime.Result.Attempts, _settings.RetryCount) == RetryDecision.Retry)
        {
            Launch(runtime);
        }
    }

    private void MarkResult(SchedulerRuntimeItem runtime, LaunchEvaluation evaluation)
    {
        runtime.Result.State = evaluation.State;
        runtime.Result.FailureReason = evaluation.Reason;
        runtime.Result.Reason = evaluation.Message;

        if (evaluation.IsSuccess)
        {
            _log.Info($"『{runtime.Item.Name}』已启动。");
        }
        else
        {
            _log.Warn($"『{runtime.Item.Name}』启动失败：{evaluation.Message}");
        }
    }

    private static ProcessSnapshot? ProbeProcess(int? processId)
    {
        if (processId is not int value)
        {
            // UWP 经 shell 激活拿不到 PID（R12）—— 判定降级为乐观。
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(value);
            process.Refresh();

            return process.HasExited ? new ProcessSnapshot(true, process.ExitCode) : new ProcessSnapshot(false, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // 进程已消失且退出码不可得：按 E4 的宽容原则记成功
            // （大量程序是拉起已有实例后立即退出，严格判定会产生假失败）。
            return new ProcessSnapshot(true, 0);
        }
    }

    // ---- 收尾 ----

    /// <summary>收尾：落盘 + 归档 + 发通知 + 决定面板与退出时机。</summary>
    /// <param name="quitImmediately">
    /// 归档后**立刻**退出（菜单「跳过剩余任务并退出」专用，2026-09-21 批复）：
    /// 不等下一拍，也不等 Launching 条目的 1.5 秒复查窗口。
    /// </param>
    /// <remarks>
    /// N4–N7（2026-09-22 批复，<c>design.md</c> FR-14.1）：
    /// 通知与面板在此分道 —— **无论面板是否显示**都先按策略发通知（N5，D2：菜单跳过路径同样发），
    /// 然后面板在看的走 <see cref="CompletionExitMode.WaitForPanelClose"/>，否则同拍退出（N6）。
    /// 收尾**不再**自动弹面板（N4：面板仅手动弹出）。
    /// </remarks>
    private void Finish(bool quitImmediately = false)
    {
        _finishing = true;
        _record.FinishedAt = DateTimeOffset.Now;
        _record.CompletedNormally = true;
        PersistState();

        try
        {
            _runState.Archive(_record);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "运行归档写入失败（实时状态仍是完整的）。");
        }

        _log.Info($"调度结束：{_record.RunId}。");

        var failedCount = _items.Count(static runtime => runtime.Result.State == RunItemState.Failed);
        _tray?.SetIcon(IconFor(failedCount > 0));

        // N5：通知是通知，面板是面板 —— 无论面板是否显示都按策略发（失败只记日志，N12）。
        SendCompletionNotification(failedCount);

        // 收尾判据下沉到 Core 的 CompletionPolicy（判定表可单测；D71–D73 三个缺陷都出在这一处）。
        // N2 后判定表不再读通知策略：只看 QuitImmediately / 面板发起 / 面板可见。
        var decision = CompletionPolicy.Decide(new CompletionPolicyInput(
            QuitImmediately: quitImmediately,
            PanelRequestedCompletion: _panelRequestedCompletion,
            PanelVisible: _tray?.IsPanelVisible ?? false));

        if (decision.ExitMode == CompletionExitMode.WaitForPanelClose)
        {
            _awaitingPanelClose = true;
            _tray?.ShowCompletionPanel();
            return;
        }

        // 两种"不等面板"的速度：菜单「跳过剩余任务并退出」要立刻离开（不等 Launching 条目的
        // 1.5 秒复查窗口）；其余情况把 _quitAt 拨到当下，由紧随其后的同一次 Tick 判定退出。
        if (quitImmediately)
        {
            Quit();
            return;
        }

        _quitAt = _stopwatch.Elapsed;
    }

    /// <summary>
    /// 按 <see cref="NotifyDecision"/> 经通知中转器发"调度完成"系统通知（N1/N2，2026-09-22 批复）。
    /// </summary>
    /// <param name="failedCount">本批次失败条目数（供策略判定与文案）。</param>
    /// <remarks>
    /// <para>
    /// 🔴 best-effort（N12）：策略判"不发"只记一行日志；中转器缺失 / 拉起失败 / 任何异常
    /// 都只记日志 —— 通知绝不阻塞、绝不拖垮调度退出。fire-and-forget：不等中转器退出、不读回执。
    /// </para>
    /// <para>
    /// 🔴 必须**降权**拉起中转器：调度端提权运行，Win10/11 抑制提权进程的系统通知，
    /// 通知必须以中完整性发出（见 <c>design.md</c> FR-14）。
    /// </para>
    /// </remarks>
    private void SendCompletionNotification(int failedCount)
    {
        if (!NotifyDecision.Decide(_settings.NotifyMode, failedCount))
        {
            _log.Info($"通知策略为 {_settings.NotifyMode}，跳过完成通知。");
            return;
        }

        try
        {
            var broker = _paths.NotifyBrokerExecutablePath;
            if (!File.Exists(broker))
            {
                _log.Warn($"通知中转器缺失，本次不发完成通知：{broker}。");
                return;
            }

            var doneCount = _items.Count(static runtime => runtime.Result.State == RunItemState.Done);
            var skippedCount = _items.Count(static runtime => runtime.Result.State == RunItemState.Skipped);
            var failedNames = _items
                .Where(static runtime => runtime.Result.State == RunItemState.Failed)
                .Select(static runtime => runtime.Result.Name)
                .ToList();

            var job = new NotifyToastJob
            {
                // Aumid / Tag / Group 靠契约默认值（AUMID=DelayStart、Tag=schedule-done）；
                // 🔴 Launch **必须显式给**：它决定点击能否拉起管理端 —— 曾因只靠默认值、
                // 而契约默认值是空串导致发出去的 toast launch=""，点击无反应（2026-09-22 实锤）。
                // 值 = delaystart://runs-log（D82：点击直达运行日志页）。
                Launch = NotifyToastJob.ScheduleDoneLaunch,
                Title = ScheduleToastComposer.Title,
                Message = ScheduleToastComposer.ComposeMessage(doneCount, failedCount, skippedCount, failedNames),
            };

            // 作业走 %TEMP% 下的一次性目录（与 LaunchBroker 同款；无标签对象按 Medium 处理，
            // High 调度端创建的目录不挡 Medium 中转器读写）。fire-and-forget：本进程退出前
            // 中转器大概率还没读走文件，所以目录留给中转器，由下一轮的陈旧清理兜底回收。
            var jobDirectory = Path.Combine(
                Path.GetTempPath(), "DelayStart", "notify", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(jobDirectory);
            var jobFile = Path.Combine(jobDirectory, "job.json");
            File.WriteAllText(jobFile, JsonSerializer.Serialize(job, BrokerJsonContext.Default.NotifyToastJob));

            CleanStaleNotifyJobs();

            var launcher = _notifyLauncher ??= new DeElevatedProcessLauncher(_log);
            var outcome = launcher.LaunchAuxiliary("完成通知", broker, $"\"{jobFile}\"");
            if (outcome.Created)
            {
                _log.Info($"完成通知已委托中转器发送（作业 {jobFile}）。");
            }
            else
            {
                _log.Warn("完成通知发送失败（中转器拉起失败），不影响调度退出。");
            }
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "发送完成通知时发生异常，不影响调度退出。");
        }
    }

    /// <summary>清理超过 1 小时的旧通知作业目录（fire-and-forget 语义下没人删它们，这里兜底回收）。</summary>
    private void CleanStaleNotifyJobs()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "DelayStart", "notify");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (File.GetCreationTime(directory) < DateTimeOffset.Now.AddHours(-1))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (Exception)
                {
                    // 单个目录清理失败不影响其余，也不影响本次通知。
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "清理旧通知作业目录失败（不影响通知发送）。");
        }
    }

    /// <summary>
    /// UI v2（2026-09-21 批复）：完成态关闭面板（倒计时归零或用户点 ✕）与右键菜单「退出」都走这里 ——
    /// 退出调度端进程，托盘图标随 <see cref="TrayIconHost.Dispose"/> 一并移除。幂等。
    /// </summary>
    public void RequestQuit() => Quit();

    private void Quit()
    {
        if (_quitRequested)
        {
            return;
        }

        _quitRequested = true;
        _awaitingPanelClose = false;
        // D40：没有需要协同退出的代理进程（降权是进程内一次系统调用，无长生命周期对象）。
        _tray?.StopTimer();
        _tray?.Dispose();
        _tray = null;
        NativeMethods.PostQuitMessage(0);
    }

    // ---- 展示数据 ----

    private nint IconFor(bool hasFailure) => _icons?.Pick(hasFailure) ?? 0;

    private string BuildTooltip()
    {
        var done = _items.Count(static runtime => runtime.Result.State == RunItemState.Done);
        var failed = _items.Count(static runtime => runtime.Result.State == RunItemState.Failed);
        var total = _record.PlannedCount;

        if (_finishing)
        {
            return failed > 0
                ? $"延时启动完成 · {failed} 项失败 · 点击查看"
                : $"延时启动完成 · {total}/{total} 成功";
        }

        var next = _items
            .Where(static runtime => runtime.Result.State == RunItemState.Waiting)
            .OrderBy(static runtime => runtime.LaunchAt)
            .ToList();

        if (next.Count == 0)
        {
            return "延时启动 · 正在完成…";
        }

        // D3 批复（2026-09-21）：「n/m 已启动 · 下一项 n 秒」进度摘要，上限 127 字符。
        var first = next[0];
        var remaining = first.LaunchAt - _stopwatch.Elapsed;

        return remaining <= TimeSpan.FromSeconds(2)
            ? $"延时启动 {done}/{total} 已启动 · 正在启动 {first.Result.Name}…"
            : $"延时启动 {done}/{total} 已启动 · 下一项 {(int)Math.Ceiling(remaining.TotalSeconds)} 秒";
    }

    private PanelSnapshot BuildPanelSnapshot()
    {
        var elapsed = _stopwatch.Elapsed;
        var total = _record.PlannedCount;
        var done = _items.Count(static runtime => runtime.Result.State == RunItemState.Done);
        var failed = _items.Count(static runtime => runtime.Result.State == RunItemState.Failed);
        var skipped = _items.Count(static runtime => runtime.Result.State == RunItemState.Skipped);
        var launching = _items.Count(static runtime => runtime.Result.State == RunItemState.Launching);
        var waiting = _items.Count(static runtime => runtime.Result.State == RunItemState.Waiting);

        var rows = _items.Select(static runtime => new PanelItemRow(
            runtime.Result.State,
            runtime.Result.Name,
            runtime.Result.State switch
            {
                RunItemState.Done => $"{runtime.Result.Delay} 秒",
                RunItemState.Failed => runtime.Result.Reason ?? "启动失败",
                RunItemState.Skipped => "已跳过",
                _ => $"{runtime.Result.Delay} 秒",
            })).ToList();

        var nextList = _items
            .Where(static runtime => runtime.Result.State == RunItemState.Waiting)
            .OrderBy(static runtime => runtime.LaunchAt)
            .ToList();

        if (_finishing)
        {
            return new PanelSnapshot(
                ChipText: failed > 0 ? $"完成 · {failed} 项失败" : "启动完成",
                HasFailure: failed > 0,
                IsFinished: true,
                DoneCount: done,
                TotalCount: total,
                MetaText: failed > 0 || skipped > 0
                    ? $"成功 {done} · 失败 {failed}{(skipped > 0 ? $" · 跳过 {skipped}" : string.Empty)}"
                    : $"全部 {total} 项已启动",
                Items: rows,
                Current: new PanelCurrentRow(
                    failed > 0 ? RunItemState.Failed : RunItemState.Done,
                    failed > 0 ? $"{failed} 项启动失败" : "启动计划已完成",
                    failed > 0 ? FirstFailureDetail() : $"{total} 项全部启动 · 无失败",
                    EtaSeconds: null),
                NextName: "—",
                AutoCloseTotalSeconds: failed > 0 ? AutoCloseSecondsOnFailure : AutoCloseSecondsOnSuccess,
                PrimaryButtonText: "打开 DelayStart",
                StatusText: BuildMenuStatusText());
        }

        var active = _items.Find(static runtime => runtime.Result.State == RunItemState.Launching)
            ?? (nextList.Count > 0 ? nextList[0] : null);
        var eta = nextList.Count > 0
            ? (int)Math.Ceiling(Math.Max((nextList[0].LaunchAt - elapsed).TotalSeconds, 0))
            : 0;

        return new PanelSnapshot(
            ChipText: "启动中",
            HasFailure: failed > 0,
            IsFinished: false,
            DoneCount: done,
            TotalCount: total,
            MetaText: $"已启动 {done} · 启动中 {launching} · 等待 {waiting}",
            Items: rows,
            Current: active is null
                ? new PanelCurrentRow(RunItemState.Waiting, "正在完成…", string.Empty, EtaSeconds: null)
                : new PanelCurrentRow(
                    active.Result.State,
                    active.Result.Name,
                    $"{active.Result.Delay} 秒延时",
                    // 还有等待项就一直显示数字（含 0）—— 否则最后一秒右侧会空一下再启动。
                    EtaSeconds: nextList.Count > 0 ? eta : null),
            NextName: nextList.Count > 0 ? nextList[0].Result.Name : "—",
            AutoCloseTotalSeconds: 0,
            PrimaryButtonText: waiting > 0 ? $"立即启动剩余 {waiting} 项" : "已全部到期",
            StatusText: BuildMenuStatusText());
    }

    /// <summary>完成态卡片副文案：首个失败项的名称 + 原因（面板宽度有限，只报第一条）。</summary>
    private string FirstFailureDetail()
    {
        var first = _items.Find(static runtime => runtime.Result.State == RunItemState.Failed);
        return first is null
            ? "启动失败"
            : $"{first.Result.Name} — {first.Result.Reason ?? "启动失败"}";
    }

    /// <summary>托盘右键菜单顶部的状态头文案（灰显不可点）。</summary>
    private string BuildMenuStatusText()
    {
        var done = _items.Count(static runtime => runtime.Result.State == RunItemState.Done);
        var failed = _items.Count(static runtime => runtime.Result.State == RunItemState.Failed);
        var skipped = _items.Count(static runtime => runtime.Result.State == RunItemState.Skipped);
        var total = _record.PlannedCount;

        if (!_finishing)
        {
            var waiting = _items.Count(static runtime => runtime.Result.State == RunItemState.Waiting);
            return $"启动中 · {done}/{total} 已启动 · 剩余 {waiting} 项";
        }

        return failed > 0 || skipped > 0
            ? $"启动完成 · 成功 {done} · 失败 {failed}{(skipped > 0 ? $" · 跳过 {skipped}" : string.Empty)}"
            : $"启动完成 · {total} 项全部启动";
    }

    /// <summary>打开管理端运行日志（D18）。失败只记日志，不影响调度。</summary>
    private void OpenRunLog()
    {
        try
        {
            var manager = _paths.ManagerExecutablePath;
            if (!File.Exists(manager))
            {
                _log.Warn($"管理端不存在，无法打开运行日志：{manager}");
                return;
            }

            // 诊断日志（2026-09-20 气泡点击不唤起管理端问题）：接线正确但管理端未出现，
            // 需要留下"点没点到、启动是否抛错、起了哪个进程"的痕迹来定位真实走向。
            _log.Info($"正在启动管理端查看运行日志：{manager}");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = manager,
                Arguments = "--goto-log",
                UseShellExecute = true,
            });
            _log.Info(process is null
                ? "管理端启动调用已发出（无进程句柄返回）。"
                : $"管理端进程已创建：PID {process.Id}。");
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "启动管理端失败。");
        }
    }

    /// <summary>打开管理端主窗口（托盘右键菜单，2026-09-21 批复 D1=A）。</summary>
    /// <remarks>
    /// 与 <see cref="OpenRunLog"/> 的区别：不带 <c>--goto-log</c> 参数（进总览页）。
    /// 管理端缺失 / 启动失败只记日志（D5 批复 A：旧气泡已随本次退役，通知统一走中转器；
    /// 这类交互失败的兜底信息在日志里，不打扰用户）。
    /// </remarks>
    private void OpenManager()
    {
        try
        {
            var manager = _paths.ManagerExecutablePath;
            if (!File.Exists(manager))
            {
                _log.Warn($"管理端不存在，无法打开：{manager}");
                return;
            }

            _log.Info("正在启动管理端（托盘右键菜单）。");
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = manager,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "启动管理端失败。");
        }
    }

    /// <summary>是否还有等待条目（决定右键菜单后两项的可用态）。</summary>
    private bool HasWaitingItems() =>
        !_finishing && _items.Exists(static runtime => runtime.Result.State == RunItemState.Waiting);

    /// <summary>立即启动全部剩余条目 —— **右键菜单**入口（2026-09-21 批复）。
    /// N4 后：完成后不自动弹面板，只有面板已开着（或收尾由面板发起）才留在面板上；通知照按策略发。</summary>
    private void LaunchRemainingNow()
    {
        LaunchRemainingNowCore(fromPanel: false);
    }

    /// <summary>立即启动全部剩余条目 —— **面板按钮**入口（2026-09-21 批复）。
    /// 面板已经是用户手动打开的，完成后必须留在面板上给结果（切完成态 + 倒计时），
    /// 不能点了按钮面板反而消失。</summary>
    private void LaunchRemainingFromPanel()
    {
        LaunchRemainingNowCore(fromPanel: true);
        _tray?.RefreshPanel();
    }

    private void LaunchRemainingNowCore(bool fromPanel)
    {
        var count = 0;
        var now = _stopwatch.Elapsed;
        foreach (var runtime in _items)
        {
            if (runtime.Result.State == RunItemState.Waiting)
            {
                runtime.LaunchAt = now;
                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        if (fromPanel)
        {
            _panelRequestedCompletion = true;
        }

        _log.Info($"用户请求立即启动全部剩余条目（{count} 项，来源：{(fromPanel ? "面板" : "托盘菜单")}）。");
    }

    /// <summary>跳过剩余条目并结束调度 —— **右键菜单**入口（2026-09-21 批复 D2=不启动）。
    /// 等待条目标记 <see cref="RunItemState.Skipped"/> 落盘归档，但**不弹面板**，收尾后直接退出
    /// （即使用户设了「总是弹出面板」也不弹 —— 菜单这条语义就是"跳过并走人"）。</summary>
    private void SkipRemaining()
    {
        if (SkipRemainingCore(fromPanel: false) == 0)
        {
            return;
        }

        // 菜单语义 =「跳过并走人」：**不等** 1.5 秒复查窗口 —— 正在 Launching 的条目
        // 一并标记 Skipped（原因文案与未执行的等待项一致），落盘 + 归档后立刻退出。
        Finish(quitImmediately: true);
    }

    /// <summary>跳过剩余条目 —— **面板按钮**入口（2026-09-21 批复：文案去掉"并退出"）。
    /// 面板已开着，跳过后留在面板上看完成结果（切完成态 + 倒计时），不能直接消失。</summary>
    private void SkipRemainingFromPanel()
    {
        _ = SkipRemainingCore(fromPanel: true);
    }

    /// <returns>被跳过（标记 <see cref="RunItemState.Skipped"/>）的条目数；0 = 没有可跳过的条目。</returns>
    private int SkipRemainingCore(bool fromPanel)
    {
        // 可跳判定下沉到 Core 的 SkipPolicy；"入口 → 原因文案"的映射留在引擎层
        // （策略层不产出面向用户的文案）。
        var entry = fromPanel ? SkipEntry.Panel : SkipEntry.TrayMenu;
        var reason = fromPanel ? "用户跳过（进度面板）" : "用户跳过（托盘右键菜单）";

        var count = 0;
        foreach (var runtime in _items)
        {
            if (!SkipPolicy.Decide(runtime.Result.State, entry).CanSkip)
            {
                continue;
            }

            runtime.Result.State = RunItemState.Skipped;
            runtime.Result.Reason = reason;
            count++;
        }

        if (count > 0)
        {
            if (fromPanel)
            {
                // 面板发起 → 完成时必须留在面板上给结果（即使用户中途收起了面板也要再弹回来）。
                _panelRequestedCompletion = true;
            }
            else
            {
                // 菜单发起 → 不弹面板，归档后立刻退。判据不在这里：
                // SkipRemaining() 调的是 Finish(quitImmediately: true)，直接进退出分支，
                // 压根不经过下面的"要不要留在面板上"决策。
            }

            _log.Info($"用户跳过剩余 {count} 个条目，本次调度收尾（来源：{(fromPanel ? "面板" : "托盘菜单")}）。");
            PersistState();
        }

        // 直接退出的那条路径上刷新没有意义（托盘马上就没了）。
        if (fromPanel)
        {
            _tray?.RefreshPanel();
            _tray?.UpdateTip(BuildTooltip());
        }

        return count;
    }

    private void PersistState()
    {
        try
        {
            _runState.WriteCurrent(_record);
        }
        catch (Exception ex)
        {
            // 实时状态写失败不影响调度本身 —— 归档与日志仍然完整。
            _log.Warn(ex, "实时状态写入失败。");
        }
    }
}
