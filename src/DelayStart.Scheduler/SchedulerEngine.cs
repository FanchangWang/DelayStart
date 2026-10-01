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
/// 🔴 每次状态变化原子重写 <c>current-run.json</c>（v0.6.1 已删，D125）：它的唯一消费者是进度面板，
/// 面板取消后没有消费方，而留着它意味着每个状态变化都要原子写一次磁盘、收益为零。
/// 收尾只写归档 <c>scheduler\archive\{runId}.json</c>；"调度端是否在跑"改由单实例互斥体回答。
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
    /// 周期跳过条目写进调度日志的原因文案（FR-15.26）。
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

    /// <summary>
    /// 当前配置里的设置。由 <see cref="Initialize"/> 读入；读失败时调度端整体不启动
    /// （<see cref="Run"/> 提前返回），因此正常使用路径下必非 <see langword="null"/>。
    /// </summary>
    private Settings? _settings;

    /// <summary>
    /// <see cref="_settings"/> 的非空视图。只能在 <see cref="Initialize"/> 成功之后访问 ——
    /// 它是"配置已读到、调度已启动"这个不变式的单一表达点。
    /// </summary>
    private Settings Settings => _settings
        ?? throw new InvalidOperationException("配置尚未加载：Run() 尚未通过 Initialize()。");

    private readonly List<SchedulerRuntimeItem> _items = [];
    private readonly Stopwatch _stopwatch = new();

    private TrayIconHost? _tray;
    private IconResources? _icons;
    private RunRecord _record = new();
    private bool _finishing;
    private TimeSpan _quitAt;

    /// <summary>完成后已弹出面板，正在等用户关闭（关闭即退出，期间不能自行退出）。</summary>

    /// <summary>退出已发起（菜单「退出」与收尾到点可能重复触发，需要幂等）。</summary>
    private bool _quitRequested;

    /// <summary>
    /// 辅助进程拉起器（懒建）：既发"发系统通知"（<b>降权</b>拉起通知中转器）、
    /// 也发"启动守卫"（<b>继承提权</b>拉起 <c>DelayStart.Guard.exe</c>）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>本类不是 <see cref="IProcessLauncher"/></b>：接口住在 Core 且只有
    /// <c>Launch(DelayedItem)</c> 一个成员，够不着 <c>LaunchResultEvaluator</c> 的纯判定要能单测；
    /// 而 Core 的 AOT 门禁不允许它引用 <c>Process.Start</c>。降权探路需要大量
    /// <c>LaunchAuxiliary</c> / <c>LaunchElevatedAuxiliary</c> 的强类型参数和几处
    /// <c>CreateProcess</c> 的细节，薄封装更适合只留在 Scheduler 里，依赖方向上零破例。
    /// <para>
    /// 字段懒建还有个并发理由：调度线程与 fire-and-forget 的收尾路径都可能在首次使用时
    /// 撞上，构造提前到构造函数里既无必要也容易在它之前就去碰配置。
    /// </para>
    /// </remarks>
    private DeElevatedProcessLauncher? _auxiliaryLauncher;

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
            // 🔴 只有**消息窗口建不起来**（RegisterClass / CreateWindowExW 失败）才走到这里 ——
   // 而定时器就挂在这个窗口上，没有它就没有 Tick 的驱动源。
   //
            // 所以这里**不能**"降级为继续调度然后进消息循环等着"：那样进程会安静地挂到用户注销，
            // 一个条目都不会启动，而日志里只有一行「按无托盘运行」—— 症状是最坏的那一类
            // （静默停摆），且用户无从判断是程序坏了还是没装全。
   // 明确失败退出，让计划任务的重试与用户可见的"没启动"对得上。
            _log.Error(
           "消息窗口创建失败，无法驱动调度定时器，本次退出"
          + "（这不是托盘图标的问题，是进程连一个窗口都建不出来）。");
            return 1;
        }

        if (!_tray.HasIcon)
        {
   // 图标没出来但窗口在 ⇒ 定时器照常驱动，到点照常启动，失败与结果照常落盘。
            // 代价只是用户看不见图标、点不到右键菜单（也就没有「立即启动 / 跳过剩余」
            // 这两个手动入口）—— 那个代价远小于"全部不启动"。
    _log.Warn("托盘图标注册失败，本次按无托盘运行（不显示图标，但调度照常进行）。");
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
            // 🔴 配置不可用时**整体不调度**，而不是拿空配置凑合：配置里存着"哪些系统项正被我们
            // 软禁用"，读不到就等于不知道该启动什么、也不知道该不该纠正回去。半懂不懂地跑
            // 比明确地不跑危险得多（硬约束 7：失败必须可见）。
            _log.Error(ex, "读取配置失败，本次不调度（保持全部接管状态不变）。");
            return false;
        }

        _settings = config.Settings;

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
                // 照旧走 FR-5.10 静默退出的话，调度日志里连一行都不会有，
                // 用户看到的是一个没动静的早晨，而答案在延时页的一条徽标上。
                RecordNoLaunchRun([], outcome.SkippedToday);
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

        // 🔴 S1.1 目标存活预筛，在**建计划之后、起停表之前**。
        //
        // 为什么在这：SchedulePlan 已经按"启用 + 今天在周期内"过滤过了，
        // 剩下的就是"本该启动的"。而在这一步再问一次"目标程序还在不在"，
        // 答"不在"的那些是**必然失败**的项 —— 让它们进计划就等于排一个注定失败的队列，
        // 用户要盯着进度条等完整个延时，最后收到一条失败通知。
        //
        // 筛掉的条目**仍然全部进调度日志**（记 Failed + 原因），不能静默丢弃：
        // 否则用户看到"我配了 5 个，只启动了 2 个"，而日志里只有 2 行，
        // 那 3 个去哪了就成了无解的问题。
        //
        // 🔴 兜底已够宽：TargetFileProbe.IsMissing 对无路径、UWP 解析名、裸命令名、
        // 相对路径一律返回 false（按"在"处理）。四个来源的 IsMissing 早就复用它，
        // 所以这里不会误杀任何一类。方向只能是"少启动"，不能是"多判失败"。
        var (launchable, missingTargets) = SplitByTargetExistence(plan);

        if (launchable.Count == 0)
        {
            // 🔴 筛掉的条目必须**进调度日志**（P1-4）：哪怕一项都启动不了，
            // 也要留下这份归档。否则用户看到"我配了 5 个，一条都没启动"，
            // 而调度日志页上什么都没有 —— "那 5 个去哪了"就成了无解的问题。
            //
            // 两种"空"给**不同的**通知文案：「今天不在周期内」是设置问题，
            // 「目标程序不存在」是软件被卸载了。都发"0 项成功"的话，
            // 用户会以为设置生效了，而真正的原因要翻日志才看得见。
            RecordNoLaunchRun(missingTargets, outcome.SkippedToday);
            SendNoLaunchableTargetsNotification(missingTargets);
            return false;
        }

        if (missingTargets.Count > 0)
        {
            _log.Warn(
                $"本轮跳过 {missingTargets.Count} 项目标程序已不存在的条目："
                + $"{DescribeNames(missingTargets)}（已记入调度日志，不静默丢弃）。");
        }

        plan = launchable;

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

        // 🔴 目标已不存在的条目同样进本次日志（审计 P1-4），记为 `Failed` + 原因。
        // 它们**不进 `_items`**：没有到点时刻，也不该被起停表管。
        //
        // 为什么要记 Failed 而不是 Skipped：Failed 是"试过且没成"，
        // 用户会去装回软件或删条目；Skipped 是"没轮到"，用户不该为它做任何事。
        // 混起来的话，最该被看见的那一批会伪装成"正常跳过"。
        AppendMissingTargetItems(missingTargets, _record.Items);

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
        _log.Info($"调度开始：{_record.RunId}，共 {_record.PlannedCount} 项"
            + (outcome.SkippedToday.Count > 0
                ? $"，另有 {outcome.SkippedToday.Count} 项今天不在周期内（记入日志，不启动）。"
                : "。"));
        return true;
    }

    /// <summary>
    /// 把"今天不在周期内"的条目追加进调度日志（FR-15.26）。
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

    /// <summary>目标程序已不存在的条目的固定原因文案。</summary>
    private const string MissingTargetReason = "目标程序已不存在（可能被卸载），本次未启动";

    /// <summary>
    /// 把"目标程序已不存在"的条目追加进调度日志（审计 P1-4）。
    /// </summary>
    /// <param name="entries">被预筛掉的目标已缺失条目。</param>
    /// <param name="target">要追加到的运行记录条目表。</param>
    /// <remarks>
    /// 🔴 记 <see cref="RunItemState.Failed"/> 而不是 <see cref="RunItemState.Skipped"/>：
    /// Failed = "试过且没成"，用户会去装回软件或删条目；Skipped = "没轮到"，
    /// 用户不该为它做任何事。混起来的话，最该被看见的那一批会伪装成"正常跳过"。
    /// <para>
    /// <see cref="RunItemResult.LaunchedAt"/> 留空 —— 它没有被发起过，
    /// 而"没有发起时刻"正是该字段的既有语义。
    /// </para>
    /// </remarks>
    private static void AppendMissingTargetItems(List<ScheduleEntry> entries, List<RunItemResult> target)
    {
        foreach (var entry in entries)
        {
            target.Add(new RunItemResult
            {
                Id = entry.Item.Id,
                Name = entry.Item.Name,
                Delay = entry.Item.DelaySeconds,
                State = RunItemState.Failed,
                Reason = MissingTargetReason,
            });
        }
    }

    /// <summary>
    /// 本轮**一个条目都没执行**时的运行记录（FR-15.26 / 审计 P1-4）。
    /// </summary>
    /// <param name="missingTargets">目标程序已不存在的条目；无则传空表。</param>
    /// <param name="notInCycle">今天不在周期内的启用条目；无则传空表。</param>
    /// <remarks>
    /// <para>
    /// 🔴 这份记录的**全部意义就是回答一个问题**："今天为什么什么都没启动？"
    /// 它没有任何条目被执行过，因此不进消息循环、不建托盘 —— 写完就与 FR-5.10 一样退出
    /// （通知照旧按策略发）。
    /// </para>
    /// <para>
    /// 🔴 两类跳过都要记进去（审计 P1-4）：用户看到"配了 8 项，今天启动 0 项"，
    /// 需要分别知道"哪几项的目标没了"与"哪几项今天本来就不轮"。只给一类，
    /// 另一类就成了无解的问题。
    /// </para>
    /// <para>
    /// 两类都为空时不写空归档 —— 那是"配置里一条启用项都没有"，
    /// 写一份零条目归档只会让调度日志页多一行没有信息量的记录。
    /// </para>
    /// </remarks>
    private void RecordNoLaunchRun(List<ScheduleEntry> missingTargets, IReadOnlyList<DelayedItem> notInCycle)
    {
        if (missingTargets.Count == 0 && notInCycle.Count == 0)
        {
            _log.Info("本轮没有可启动的条目，且没有任何需要记录的跳过项。");
            return;
        }

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

        AppendMissingTargetItems(missingTargets, record.Items);
        AppendNotInCycleItems(notInCycle, record.Items);

        try
        {
            _runState.Archive(record);

            // 有目标缺失时走 Warn：那不是"正常的今天不轮"，是软件被卸载了。
            if (missingTargets.Count > 0)
            {
                _log.Warn(
                    $"本轮无一可启动，已写入归档 {record.RunId}："
                    + $"{missingTargets.Count} 项目标程序不存在、{notInCycle.Count} 项今天不在周期内。");
            }
            else
            {
                _log.Info(
                    $"今天没有条目在启动周期内：{notInCycle.Count} 个启用条目全部跳过，已写入调度日志。");
            }
        }
        catch (Exception ex)
        {
            // 写不进去也不该让登录路径上的这个进程卡住或报错 —— 与 FR-5.10 的静默退出等价。
            _log.Warn(ex, "写入「本轮无一可启动」的运行记录失败（用户已从通知得到结论，不阻断收尾）。");
        }
    }

    /// <summary>
    /// 按"目标程序还在不在"把计划分成两拨（S1.1）。
    /// </summary>
    /// <param name="plan">SchedulePlan 产出的本轮计划。</param>
    /// <returns>（可启动项，目标已不存在的项）。</returns>
    /// <remarks>
    /// 🔴 <b>直接委托给 Core 的 <see cref="TargetPrefilter"/>，不自己实现一遍。</b>
    /// 审计 P1-1：原先这里是逐行相同的一份拷贝，于是 Core 那份成了**死代码** ——
    /// 14 个单测测的是一个没有任何生产代码调用的函数，而真正跑的那份无人覆盖。
    /// 两份可以静默漂移，而漂移的方向是"少启动还是多判失败"，判错代价很大。
    /// <para>
    /// 判据（<see cref="TargetFileProbe.IsMissing"/>）对无路径、UWP 解析名、裸命令名、
    /// 相对路径**一律返回 false**（按"在"处理）。所以这一筛**只会少启动，
    /// 绝不会多判失败** —— 方向与 D87/D90 一致：兜底只能更宽松。
    /// 而"少启动"的那些项仍全部进调度日志（记 Failed + 原因），不是静默丢弃。
    /// </para>
    /// </remarks>
    private static (List<ScheduleEntry> Launchable, List<ScheduleEntry> MissingTargets)
        SplitByTargetExistence(IReadOnlyList<ScheduleEntry> plan)
    {
        var split = TargetPrefilter.Split(plan);
        return (split.Launchable, split.MissingTargets);
    }

    /// <summary>列出若干条目的名字（超过三个时截断并给"等 N 项"）。</summary>
    private static string DescribeNames(List<ScheduleEntry> entries)
    {
        const int limit = 3;
        var names = string.Join("、", entries.Take(limit).Select(static entry => entry.Item.Name));
        return entries.Count > limit ? $"{names} 等 {entries.Count} 项" : names;
    }

    /// <summary>
    /// 目标程序全部不存在时的收尾通知（S1.3）。
    /// </summary>
    /// <param name="missingTargets">被预筛掉的条目。</param>
    /// <remarks>
    /// 🔴 文案必须写明是**"目标程序没了"**而不是"你没配置" —— 这两件事的处置完全相反：
    /// 前者要去装回软件或清理条目，后者要去检查"启用"开关。
    /// 而通用收尾通知写的是"0 项成功"，用户读到它会以为设置生效了。
    /// <para>
    /// 🔴 这一步**不排任何延时**（S1.2）：没有可启动的项却还要等 10 分钟才收到通知，
    /// 是纯粹的浪费；而且用户在这 10 分钟里完全可以先去把软件装回来。
    /// </para>
    /// </remarks>
    private void SendNoLaunchableTargetsNotification(List<ScheduleEntry> missingTargets)
    {
        var detail = DescribeNames(missingTargets);
        _log.Warn(
            $"本轮无一可启动：{missingTargets.Count} 项目标程序已不存在（{detail}）。"
            + "已记入调度日志；装回软件后会自动恢复。");

        SendCompletionNotification(
            failedCount: missingTargets.Count,
            summaryOverride: $"0 项成功 · {missingTargets.Count} 项目标程序已不存在（{detail}）。"
                + "这些条目已保留在延时启动页，装回软件后会自动恢复；也可在那里删除。");
    }


    /// <summary>归档里因"目标程序已不存在"而记为失败的条目数（审计 P1-4）。</summary>
    /// <returns>计数。</returns>
    /// <remarks>
    /// 按 <see cref="RunItemResult.Reason"/> 判而不是靠一个字段：归档里没有"这是预筛掉的"
    /// 这样的标记，而 <see cref="MissingTargetReason"/> 是本类唯一的产地。
    /// </remarks>
    private int CountMissingTargetsInRecord()
        => _record.Items.Count(static result =>
            result.State == RunItemState.Failed && result.Reason == MissingTargetReason);

    private TrayIconHost? CreateTrayHost(IconResources? icons)
    {
     // 托盘只承载**展示**（图标、悬停提示、右键菜单）；启动目标的能力在别处。
        // 生命周期 = 调度期间显示 → 收尾延迟结束后退出（托盘随之消失）。
        // 🔴 返回 null 的唯一含义是"消息窗口建不起来" —— 定时器挂在那个窗口上，
        // 没有它就没有 Tick 的驱动源。图标注册失败**不**返回 null（见 TrayIconHost.TryCreate）。
        var host = new TrayIconHost(
            icons,
            OpenRunLog,
            OpenManager,
            LaunchRemainingNow,
            SkipRemaining,
            RequestQuit,
            HasWaitingItems,
            () => _finishing,
            BuildMenuStatusText);
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

        foreach (var runtime in _items)
        {
            if (runtime.Result.State == RunItemState.Waiting
                && elapsed >= runtime.LaunchAt) // LaunchAt 初值=配置延时；「立即启动」把它拨到当下
            {
                Launch(runtime);
            }
            else if (runtime.Result.State == RunItemState.Launching && elapsed >= runtime.RecheckAt)
            {
                Evaluate(runtime);
            }
        }


        if (!_finishing && _items.All(static runtime => runtime.Result.State is RunItemState.Done or RunItemState.Failed or RunItemState.Skipped))
        {
            Finish();
        }

        // 收尾延迟时间到 → 退。原先这里还有 `&& !_awaitingPanelClose`：
        // 面板在时退出时机交给面板（倒计时归零或失焦关闭，N9-D3），面板取消后没有第二个进程了。
        if (_finishing && _stopwatch.Elapsed >= _quitAt)
        {
            Quit();
            return;
        }

        _tray?.UpdateTip(BuildTooltip());
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
            // 🔴 标 Skipped 而不是 Failed（D1 A）："进程已经在跑"根本不是失败 ——
            // 标成 Failed 会让这条记录在调度日志里显示为一次启动失败，白白制造假警报。
            // Skipped 语义准确，用户看到的是"已跳过（进程已在运行）"。
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

                if (RetryPolicy.Decide(runtime.Result.Attempts, Settings.RetryCount) == RetryDecision.Fail)
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
            && RetryPolicy.Decide(runtime.Result.Attempts, Settings.RetryCount) == RetryDecision.Retry)
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
    /// 通知与"收尾怎么退"互不读取对方的输入（D83 的拆分之一；另一半是 CompletionPolicy，已删）。
    /// 收尾**不再**自动弹面板（N4：面板仅手动弹出）。
    /// </remarks>
    private void Finish(bool quitImmediately = false)
    {
        _finishing = true;
        _record.FinishedAt = DateTimeOffset.Now;
        _record.CompletedNormally = true;

        try
        {
            _runState.Archive(_record);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "运行归档写入失败（实时状态仍是完整的）。");
        }

        _log.Info($"调度结束：{_record.RunId}。");

        // 🔴 失败计数要**同时**包含"起停表里启动失败的"与"预筛时目标就没了���"
        // （P1-4）：后者写进了归档但不在 `_items` 里，只数前者的话，
        // 5 项配了 3 项启动成功、2 项目标没了，通知却说"3 项全部成功" ——
        // 用户被告知了一个与归档不一致的结论。
        var failedCount = _items.Count(static runtime => runtime.Result.State == RunItemState.Failed)
            + CountMissingTargetsInRecord();
        _tray?.SetIcon(IconFor(failedCount > 0));

        // N5：通知是通知，面板是面板 —— 面板已于 v0.6.1 取消，这里只剩"发不发系统通知"一件事。
        SendCompletionNotification(failedCount);

        // 🔴 S1.5：本轮收尾后再跑一次守卫（fire-and-forget）。
        //
        // 覆盖一个真实场景：被接管的程序启动后可能把 StartupApproved 软禁用标记翻回启用，
        // 导致下次登录被系统重复启动。守卫在本轮**最终退出前**纠正一次，
        // 落在下次登录之前 —— 效果与"每个条目启动后立刻纠正"等价，却只扫一轮。
        //
        // 位置在「归档 → 通知」之后、「退不退」之前：此时"最新 RunId"归档里已经有了，
        // 守卫读到的系统状态是**最终状态**。
        RequestGuardRun();

        // 🔴 收尾**只有**一个决定：什么时候退。
        //
        // 原先这里还有一个"等不等面板关闭"的分叉，由 Core 的 CompletionPolicy 判定
        // （判定表可单测；D71–D73 三个缺陷都出在那一处）。面板取消后那个问题本身不存在了 ——
        // 没有第二个进程需要等，调度端可以直接决定自己的死期，所以连同
        // CompletionPolicy 与它的测试一并删除。
        //
        // 两种速度：菜单「跳过剩余任务并退出」要立刻离开（不等 Launching 条目的
        // 1.5 秒复查窗口，也不等下面的通知停留）；其余情况把 _quitAt 拨到
        // 「当下 + 通知停留时长」，让用户有时间读完系统通知。
        if (quitImmediately)
        {
            Quit();
            return;
        }

        _quitAt = _stopwatch.Elapsed + CompletionStaySeconds;
    }

    /// <summary>
    /// 发完系统通知后、退出前停留的秒数（v0.6.1 新增，F7.3）。
    /// </summary>
    /// <remarks>
    /// 🔴 面板取消后调度端退出即托盘消失，那条系统通知就成了用户在这次登录里
    /// **最后**一条线索 —— 立刻退出会让它来不及被读。所以收尾要在这里停一下。
    /// <para>
    /// 取 <see cref="CompletionStaySeconds"/> 这个经验值而不是去检测通知是否消失：
    /// 横幅停留时长由系统与用户的辅助功能设置决定、不归我们管，而且用户手动划走通知时
    /// 反而"检测不到"，那种情况下等满固定时长只是白等几秒。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan CompletionStaySeconds = TimeSpan.FromSeconds(8);

    /// <summary>请求守卫在收尾后跑一次巡检（S1.5–S1.7，fire-and-forget）。</summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>fire-and-forget</b>：启动即返回，不等守卫退出、不读回执。巡检实测中位 528 ms，
    /// 但那是它<em>独立跑</em>时；这里它与"退出"只差 8 秒，等它没有收益而收尾会被拖住。
    /// </para>
    /// <para>
    /// 🔴 <b>失败只 Warn</b>（S1.7）：exe 缺失 / 拉起失败只记"没能请求巡检"的值。
    /// 此时归档已经写好、通知已经发过，用户能拿到的结果一条都不少；而"读不懂
    /// 完整 exe 路径""守护没起来"这类排查线索，**已经在调度日志里留着了**。
    /// </para>
    /// <para>
    /// 🔴 走 <see cref="DeElevatedProcessLauncher.LaunchElevatedAuxiliary"/>：守卫要写 HKLM
    /// 并要提权注册/修改计划任务，<b>不能</b>走 <c>LaunchAuxiliary</c> —— 后者的存在意义
    /// 就是降权（拿 explorer 令牌降级到 Medium）。
    /// </para>
    /// <para>
    /// 守卫自己也有单实例互斥体（<c>Local\DelayStart.Guard</c>），所以与计划任务、
    /// 管理端内联那几路并发时只有一轮会真跑，其余秒退 —— 这一点不需要这里额外处理。
    /// </para>
    /// </remarks>
    private void RequestGuardRun()
    {
        // F7.2：设置开关（默认开）。🔴 只管调度收尾后这一次，不管管理端加载数据时的内联处理。
        if (!Settings.RunGuardAfterSchedule)
        {
            _log.Info("设置里关闭了「调度退出前运行一次守卫」，本次收尾未请求巡检。");
            return;
        }

        try
        {
            var guard = _paths.GuardExecutablePath;
            if (!File.Exists(guard))
            {
                _log.Warn($"守卫程序缺失，本轮收尾未请求巡检：{guard}（安装不完整或文件被删）");
                return;
            }


            var launcher = _auxiliaryLauncher ??= new DeElevatedProcessLauncher(_log);
            var outcome = launcher.LaunchElevatedAuxiliary("守卫巡检", guard, arguments: null);
            if (!outcome.Created)
            {
                _log.Warn($"守卫巡检拉起失败：{outcome.FailureMessage}（不影响收尾与退出码）");
            }
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "请求守卫巡检时异常（不影响收尾与退出码）。");
        }
    }

    /// <summary>
    /// 按 <see cref="NotifyDecision"/> 经通知中转器发"调度完成"系统通知（N1/N2，2026-09-22 批复）。
    /// </summary>
    /// <param name="failedCount">本批次失败条目数（供策略判定与文案）。</param>
    /// <param name="summaryOverride">
    /// 覆盖正文；<see langword="null"/> 时按常规口径拼（成功 / 失败 / 跳过）。
    /// </param>
    /// <remarks>
    /// <para>
    /// 🔴 best-effort（N12）：策略判"不发"只记一行日志；中转器缺失 / 拉起失败 / 任何异常
    /// 都只记日志 —— 通知绝不阻塞、绝不拖垮调度退出。fire-and-forget：不等中转器退出、不读回执。
    /// </para>
    /// <para>
    /// 🔴 必须**降权**拉起中转器：调度端提权运行，Win10/11 抑制提权进程的系统通知，
    /// 通知必须以中完整性发出（见 <c>design.md</c> FR-14）。
    /// </para>
    /// <para>
    /// 🔴 <paramref name="summaryOverride"/> 存在只为 S1.3 那条"目标程序全没了"的路径：
    /// 常规口径写的是"0 项成功"，用户读到会以为设置生效了，而真正的原因是软件被卸载了。
    /// 两种"空"必须给不同的文案 —— 处置方向完全相反（装回软件 vs 检查启用开关）。
    /// <para>
    /// 🔴 覆盖正文时 <paramref name="failedCount"/> 仍要如实给：它决定
    /// <see cref="NotifyDecision.Decide"/> 是发还是不发 —— "目标全没了"属于失败，
    /// 必须走"有失败就通知"的那一档，否则用户在"从不通知"档位下什么也看不到。
    /// </para>
    /// </para>
    /// </remarks>
    private void SendCompletionNotification(int failedCount, string? summaryOverride = null)
    {
        if (!NotifyDecision.Decide(Settings.NotifyMode, failedCount))
        {
            _log.Info($"通知策略为 {Settings.NotifyMode}，跳过完成通知。");
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
                // 值 = delaystart://runs-log（D82：点击直达调度日志页）。
                Launch = NotifyToastJob.ScheduleDoneLaunch,
                Title = ScheduleToastComposer.Title,
                Message = summaryOverride
                    ?? ScheduleToastComposer.ComposeMessage(doneCount, failedCount, skippedCount, failedNames),
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

            var launcher = _auxiliaryLauncher ??= new DeElevatedProcessLauncher(_log);
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
        var done = _items.Count(static runtime =>
            runtime.Result.State is RunItemState.Done or RunItemState.Failed);
        var failed = _items.Count(static runtime => runtime.Result.State == RunItemState.Failed);
        var total = _record.PlannedCount;

        if (_finishing)
        {
            return SchedulerTip.Finished(total, failed);
        }

        var next = _items
            .Where(static runtime => runtime.Result.State == RunItemState.Waiting)
            .OrderBy(static runtime => runtime.LaunchAt)
            .ToList();

        if (next.Count == 0)
        {
            return SchedulerTip.Building(done, total, nextName: null, remainingSeconds: 0);
        }

        var first = next[0];
        var remaining = first.LaunchAt - _stopwatch.Elapsed;

        // 🔴 文案合成在 Core 的 SchedulerTip 里（调度端没有测试工程，纯逻辑放这儿就等于没测试），
        // 三行的分工与 127 字符预算都在那边写清楚了。
        return SchedulerTip.Building(
            done,
            total,
            first.Result.Name,
            (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds)));
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

    /// <summary>打开管理端调度日志（D18）。失败只记日志，不影响调度。</summary>
    private void OpenRunLog()
    {
        try
        {
            var manager = _paths.ManagerExecutablePath;
            if (!File.Exists(manager))
            {
                _log.Warn($"管理端不存在，无法打开调度日志：{manager}");
                return;
            }

            // 诊断日志（2026-09-20 气泡点击不唤起管理端问题）：接线正确但管理端未出现，
            // 需要留下"点没点到、启动是否抛错、起了哪个进程"的痕迹来定位真实走向。
            _log.Info($"正在启动管理端查看调度日志：{manager}");
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

    /// <summary>立即启动全部剩余条目 —— 托盘右键菜单入口（2026-09-21 批复）。
    /// N4 后：完成后不弹任何窗口，通知照按策略发。</summary>
    private void LaunchRemainingNow()
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

        _log.Info($"用户请求立即启动全部剩余条目（{count} 项，来源：托盘菜单）。");
    }

    /// <summary>跳过剩余条目并结束调度 —— 托盘右键菜单入口（2026-09-21 批复 D2=不启动）。
    /// 等待条目标记 <see cref="RunItemState.Skipped"/> 落盘归档，收尾后直接退出。</summary>
    private void SkipRemaining()
    {
        if (MarkRemainingSkipped() == 0)
        {
            return;
        }

        // 菜单语义 =「跳过并走人」：**不等** 1.5 秒复查窗口 —— 正在 Launching 的条目
        // 一并标记 Skipped（原因文案与未执行的等待项一致），落盘 + 归档后立刻退出。
        Finish(quitImmediately: true);
    }

    /// <summary>把当前仍可跳过的条目标记为 <see cref="RunItemState.Skipped"/>。</summary>
    /// <returns>被标记的条目数；0 = 没有可跳过的条目。</returns>
    /// <remarks>
    /// 🔴 可跳判定下沉到 Core 的 <see cref="SkipPolicy"/>；原因文案留在引擎层
    /// （策略层不产出面向用户的文案）。面板已于 v0.6.1 取消，判定不再按入口分流，
    /// 所以 <c>SkipEntry</c> 枚举已随之删除，这里也只剩托盘菜单一个入口。
    /// </remarks>
    private int MarkRemainingSkipped()
    {
        const string reason = "用户跳过（托盘右键菜单）";

        var count = 0;
        foreach (var runtime in _items)
        {
            if (!SkipPolicy.Decide(runtime.Result.State).CanSkip)
            {
                continue;
            }

            runtime.Result.State = RunItemState.Skipped;
            runtime.Result.Reason = reason;
            count++;
        }

        if (count > 0)
        {
            _log.Info($"用户跳过剩余 {count} 个条目，本次调度收尾（来源：托盘菜单）。");
        }

        return count;
    }
}
