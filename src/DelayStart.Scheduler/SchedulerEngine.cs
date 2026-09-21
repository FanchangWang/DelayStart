using System.Diagnostics;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Models;
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
    /// 不能点了之后面板反而消失（2026-09-21 批复）。因此它**覆盖** <see cref="NotifyMode"/> 的静默策略。
    /// </summary>
    private bool _panelRequestedCompletion;

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
        var plan = SchedulePlan.Build(config.Items);

        if (plan.Count == 0)
        {
            _log.Info("没有启用的延时条目，静默退出（FR-5.10）。");
            return false;
        }

        var now = DateTimeOffset.Now;
        _record = new RunRecord
        {
            RunId = RunStateService.CreateRunId(now),
            StartedAt = now,
            PlannedCount = plan.Count,
            Items = plan.Select(static entry => new RunItemResult
            {
                Id = entry.Item.Id,
                Name = entry.Item.Name,
                Delay = entry.Item.DelaySeconds,
            }).ToList(),
        };

        for (var index = 0; index < plan.Count; index++)
        {
            _items.Add(new SchedulerRuntimeItem
            {
                Item = plan[index].Item,
                Result = _record.Items[index],
                LaunchAt = plan[index].LaunchAt,
            });
        }

        // D40：调度端亲自降权，没有代理进程，也就没有预热这一步。
        _stopwatch.Start();
        PersistState();
        _log.Info($"调度开始：{_record.RunId}，共 {_record.PlannedCount} 项。");
        return true;
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

        // 完成态且已弹面板时不能自行退出 —— 退出时机交给面板（倒计时归零或用户点 ✕）。
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
    private void Launch(SchedulerRuntimeItem runtime)
    {
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

                if (runtime.Result.Attempts > _settings.RetryCount)
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

    /// <summary>复查窗口到期：探测进程状态并做最终判定（机制 7）。</summary>
    private void Evaluate(SchedulerRuntimeItem runtime)
    {
        var snapshot = ProbeProcess(runtime.ProcessId);
        var evaluation = LaunchResultEvaluator.Evaluate(
            new LaunchOutcome { Created = true, ProcessId = runtime.ProcessId },
            snapshot);

        MarkResult(runtime, evaluation);

        if (!evaluation.IsSuccess && runtime.Result.Attempts <= _settings.RetryCount)
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

    /// <summary>收尾：落盘 + 归档 + 决定面板与退出时机。</summary>
    /// <param name="quitImmediately">
    /// 归档后**立刻**退出（菜单「跳过剩余任务并退出」专用，2026-09-21 批复）：
    /// 不等下一拍，也不等 Launching 条目的 1.5 秒复查窗口。
    /// </param>
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

        // 收尾判据下沉到 Core 的 CompletionPolicy（判定表可单测；D71–D73 三个缺陷都出在这一处）。
        var decision = CompletionPolicy.Decide(new CompletionPolicyInput(
            QuitImmediately: quitImmediately,
            PanelRequestedCompletion: _panelRequestedCompletion,
            PanelVisible: _tray?.IsPanelVisible ?? false,
            FailedCount: failedCount,
            NotifyMode: _settings.NotifyMode));

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

    /// <summary>打开管理端主窗口（托盘右键菜单，2026-09-21 批复 D1=A / D5=气泡报错）。</summary>
    /// <remarks>
    /// 与 <see cref="OpenRunLog"/> 的区别：不带 <c>--goto-log</c> 参数（进总览页）；
    /// 管理端缺失时按 D5 批复发气泡报错（运行日志入口保持静默降级 —— 那里还有日志文件兜底）。
    /// </remarks>
    private void OpenManager()
    {
        try
        {
            var manager = _paths.ManagerExecutablePath;
            if (!File.Exists(manager))
            {
                _log.Warn($"管理端不存在，无法打开：{manager}");
                _tray?.ShowBalloon("管理端缺失", $"未找到管理端程序：{Path.GetFileName(manager)}");
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
            _tray?.ShowBalloon("管理端启动失败", ex.Message);
        }
    }

    /// <summary>是否还有等待条目（决定右键菜单后两项的可用态）。</summary>
    private bool HasWaitingItems() =>
        !_finishing && _items.Exists(static runtime => runtime.Result.State == RunItemState.Waiting);

    /// <summary>立即启动全部剩余条目 —— **右键菜单**入口（2026-09-21 批复）。
    /// 完成时机遵循设置-调度-通知：该弹面板就弹，不该弹就直接退。</summary>
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
        var count = 0;
        foreach (var runtime in _items)
        {
            var state = runtime.Result.State;

            // 面板入口只跳 Waiting —— Launching 的照常复查出结果（用户还要在面板上看）；
            // 菜单入口连 Launching 一起跳（用户不想等复查窗口）。
            var skippable = state == RunItemState.Waiting
                || (!fromPanel && state == RunItemState.Launching);
            if (!skippable)
            {
                continue;
            }

            runtime.Result.State = RunItemState.Skipped;
            runtime.Result.Reason = fromPanel ? "用户跳过（进度面板）" : "用户跳过（托盘右键菜单）";
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
