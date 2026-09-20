using System.Diagnostics;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Scheduler;

/// <summary>
/// 调度引擎（机制 5–8，<c>docs/scheduler-design.md</c>）。
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

    /// <summary>完成通知气泡的近似存活时长。Win32 气泡的显示时长由系统控制拿不到确切值，
    /// 托盘「通知消失后退出」只能取这个近似（用户批复 2026-09-19）。</summary>
    private static readonly TimeSpan NotificationLifetime = TimeSpan.FromSeconds(10);

    private readonly IAppConfigStore _configStore;
    private readonly IRunStateStore _runState;
    private readonly IProcessLauncher _launcher;
    private readonly ILogSink _log;
    private readonly PathService _paths;
    private readonly Settings _settings;

    private readonly List<RuntimeItem> _items = [];
    private readonly Stopwatch _stopwatch = new();

    private TrayIconHost? _tray;
    private IconResources? _icons;
    private RunRecord _record = new();
    private bool _finishing;
    private TimeSpan _quitAt;

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

    /// <summary>单个条目的运行时状态。</summary>
    private sealed class RuntimeItem
    {
        public required DelayedItem Item { get; init; }
        public required RunItemResult Result { get; init; }
        public int? ProcessId { get; set; }
        public TimeSpan RecheckAt { get; set; }
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

        var enabled = config.Items
            .Where(static item => item.Enabled)
            .OrderBy(static item => item, StartupSortComparer.Instance)
            .ToList();

        if (enabled.Count == 0)
        {
            _log.Info("没有启用的延时条目，静默退出（FR-5.10）。");
            return false;
        }

        var now = DateTimeOffset.Now;
        _record = new RunRecord
        {
            RunId = RunStateService.CreateRunId(now),
            StartedAt = now,
            PlannedCount = enabled.Count,
            Items = enabled.Select(static item => new RunItemResult
            {
                Id = item.Id,
                Name = item.Name,
                Delay = item.DelaySeconds,
            }).ToList(),
        };

        for (var index = 0; index < enabled.Count; index++)
        {
            _items.Add(new RuntimeItem { Item = enabled[index], Result = _record.Items[index] });
        }

        // D40：调度端亲自降权，没有代理进程，也就没有预热这一步。
        _stopwatch.Start();
        PersistState();
        _log.Info($"调度开始：{_record.RunId}，共 {_record.PlannedCount} 项。");
        return true;
    }

    private TrayIconHost? CreateTrayHost(IconResources? icons)
    {
        // 托盘只属调度端、恒显示（用户批复 2026-09-19：托盘设置已移除，
        // 生命周期 = 调度期间显示 → 最后一条通知消失后退出）。
        var host = new TrayIconHost(icons, BuildPanelSnapshot, OpenRunLog);
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
                && DelayCalculator.IsDue(runtime.Item.DelaySeconds, elapsed))
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

        if (!_finishing && _items.All(static runtime => runtime.Result.State is RunItemState.Done or RunItemState.Failed))
        {
            Finish();
        }

        if (_finishing && _stopwatch.Elapsed >= _quitAt)
        {
            Quit();
        }
        else
        {
            _tray?.UpdateTip(BuildTooltip());
        }
    }

    /// <summary>发起一次启动；失败且仍有重试额度时立即重试（同一次运行内，FR-9.6）。</summary>
    private void Launch(RuntimeItem runtime)
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
    private void Evaluate(RuntimeItem runtime)
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

    private void MarkResult(RuntimeItem runtime, LaunchEvaluation evaluation)
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

    private void Finish()
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

        if (ShouldNotify(failedCount))
        {
            var (title, text) = BuildBalloon(failedCount);
            _tray?.ShowBalloon(title, text);
        }

        // 托盘生命周期跟随通知（用户批复 2026-09-19）：弹了通知就等气泡消失后再退
        // （Win32 气泡时长由系统控制，取约 10 秒的近似值）；没有通知则下一拍直接退出。
        _quitAt = ShouldNotify(failedCount)
            ? _stopwatch.Elapsed + NotificationLifetime
            : _stopwatch.Elapsed;
    }

    private bool ShouldNotify(int failedCount) => _settings.NotifyMode switch
    {
        NotifyMode.Never => false,
        NotifyMode.Always => true,
        _ => failedCount > 0,
    };

    private (string Title, string Text) BuildBalloon(int failedCount)
    {
        if (failedCount == 0)
        {
            return ("延时启动完成", $"{_record.PlannedCount} 个程序已全部启动");
        }

        var failedNames = _items
            .Where(static runtime => runtime.Result.State == RunItemState.Failed)
            .Select(static runtime => runtime.Result.Name)
            .ToList();

        var title = failedCount == _record.PlannedCount
            ? "延时启动全部失败"
            : $"{failedCount} 个程序启动失败";

        var text = failedNames.Count <= 2
            ? string.Join("、", failedNames)
            : $"{failedNames[0]}、{failedNames[1]} 等 {failedCount} 项";

        return (title, $"{text} · 点击查看详情");
    }

    private void Quit()
    {
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
        var done = _items.Count(static runtime => runtime.Result.State is RunItemState.Done or RunItemState.Failed);
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
            .OrderBy(static runtime => runtime.Result.Delay)
            .ToList();

        if (next.Count == 0)
        {
            return "延时启动 · 正在完成…";
        }

        var first = next[0];
        var remaining = DelayCalculator.Remaining(first.Item.DelaySeconds, _stopwatch.Elapsed);

        return remaining <= TimeSpan.FromSeconds(2)
            ? $"延时启动 {done}/{total} · 正在启动 {first.Result.Name}…"
            : $"延时启动 {done}/{total} · 下一项 {first.Result.Name}（{(int)Math.Ceiling(remaining.TotalSeconds)}s）";
    }

    private PanelSnapshot BuildPanelSnapshot()
    {
        var elapsed = _stopwatch.Elapsed;
        var done = _items.Count(static runtime => runtime.Result.State == RunItemState.Done);

        var rows = _items.Select(static runtime => new PanelItemRow(
            runtime.Result.State,
            runtime.Result.Name,
            runtime.Result.State switch
            {
                RunItemState.Done => $"{runtime.Result.Delay} 秒",
                RunItemState.Failed => runtime.Result.Reason ?? "启动失败",
                _ => $"{runtime.Result.Delay} 秒",
            })).ToList();

        string footer;
        if (_finishing)
        {
            footer = "已全部启动";
        }
        else
        {
            var next = _items
                .Where(static runtime => runtime.Result.State == RunItemState.Waiting)
                .OrderBy(static runtime => runtime.Result.Delay)
                .ToList();

            if (next.Count == 0)
            {
                footer = "正在完成…";
            }
            else
            {
                var remaining = DelayCalculator.Remaining(next[0].Item.DelaySeconds, elapsed);
                footer = $"下一项还有 {(int)Math.Ceiling(Math.Max(remaining.TotalSeconds, 0))} 秒";
            }
        }

        return new PanelSnapshot(
            $"延时启动 · 登录后 {(int)elapsed.TotalSeconds} 秒",
            done,
            _record.PlannedCount,
            rows,
            footer,
            HasFailure: rows.Exists(static row => row.State == RunItemState.Failed));
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
