using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 守卫一次巡检的编排（D74）：扫描 → 写回纠正 → 新增 / 失效检测 → 更新基线。
/// </summary>
/// <remarks>
/// <para>
/// 本类只负责"按顺序做、把结果收集起来"，判定规则全部委托给 Core 的三份策略
/// （<see cref="GuardCorrectionPolicy"/> / <see cref="GuardNewItemPolicy"/> /
/// <see cref="GuardStalePolicy"/>），因此"判据"可以逐条单测，而这里的编排可以对着假来源测。
/// </para>
/// <para>
/// 🔴 **不抛异常**：一次来源失败已经由 <see cref="ScanService"/> 收集成
/// <see cref="ScanResult.Failures"/>；纠正失败也只记进结果。守卫是周期任务，
/// 让一次巡检半途崩掉会让"上一轮到底做了什么"无从得知。
/// </para>
/// <para>
/// 🔴 <b>单实例保护在 <see cref="RunOnce"/> 内部</b>（D74 / G3），不放在调用方。
/// 三个入口（守卫进程、管理端启动时后台跑一次、管理端「真跑守卫」按钮）都会调它，
/// 保护放在调用方就意味着要复制三遍 —— 漏一处就等于没有保护。
/// </para>
/// </remarks>
public sealed class GuardService
{
    /// <summary>单实例互斥名（原先只在守卫进程里，现下沉到本类）。</summary>
    private const string SingleInstanceMutexName = @"Local\DelayStart.Guard";

    private readonly ScanService _scanner;
    private readonly IAppConfigStore _configStore;
    private readonly GuardBaselineStore _baselineStore;
    private readonly IReadOnlyList<IStartupSource> _sources;
    private readonly ILogSink _log;
    private readonly IClock _clock;

    /// <summary>
    /// 进程内重入标志：0 = 空闲，1 = 正在巡检。
    /// </summary>
    /// <remarks>
    /// 🔴 这一层**不是多余的**：内核互斥体挡不住进程内重入。同一个进程里两次
    /// <c>new Mutex(initiallyOwned: true, 同名, out isFirst)</c> 拿到的是**同一个内核对象**，
    /// 两次的 <c>isFirst</c> **都会是 <see langword="true"/>** —— 内核只管跨进程，
    /// 同进程重入对它完全透明。用一次 <c>Mutex</c> 直接测这个现象是测不出来的：
    /// 两次调用都返回 <c>isFirst=true</c>，单测会绿，而真机上就是并发跑了两轮。
    /// <para>
    /// 而管理端会在启动时后台跑一次、用户又能点「真跑守卫」按钮，两条路都进
    /// <see cref="RunOnce"/>。没有这层标志，用户在启动后几秒内点一下按钮就会**同时**
    /// 跑两轮巡检：两轮都写基线、都写归档、可能都发通知 ——
    /// 表现为"守卫日志里同一时刻出现两条巡检记录"和"用户莫名收到两条一样的通知"。
    /// </para>
    /// <para>
    /// 用 <see cref="Interlocked"/> 而不是 <see cref="SemaphoreSlim"/>：我们要的是
    /// <b>立刻跳过</b>而不是排队等待（第二次调用本来就该什么也不做），
    /// 标志位既够用又不引入一个需要释放的资源。
    /// </para>
    /// </remarks>
    private int _runningInProcess;

    /// <summary>构造守卫服务。</summary>
    /// <param name="scanner">全量扫描服务。</param>
    /// <param name="configStore">配置端（接管清单 + 守卫档位）。</param>
    /// <param name="baselineStore">基线快照存储。</param>
    /// <param name="sources">全部来源实例（纠正动作要按来源定位）。</param>
    /// <param name="log">日志接收端。</param>
    /// <param name="clock">时间源（给巡检结果打完成时间戳，D116）。</param>
    public GuardService(
        ScanService scanner,
        IAppConfigStore configStore,
        GuardBaselineStore baselineStore,
        IReadOnlyList<IStartupSource> sources,
        ILogSink log,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(baselineStore);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _scanner = scanner;
        _configStore = configStore;
        _baselineStore = baselineStore;
        _sources = sources;
        _log = log;
        _clock = clock;
    }

    /// <summary>执行一次巡检。</summary>
    /// <param name="reusedScan">
    /// 调用方**已经扫好的**结果（管理端加载自启动项数据时用）；为 <see langword="null"/> 时
    /// 本方法自己调 <see cref="ScanService.Scan"/>。见方法说明里的"复用扫描结果"。
    /// </param>
    /// <param name="processedScopes">
    /// 本轮**真的重新扫过**的来源作用域；为 <see langword="null"/> 表示"全部都扫了"
    /// （守卫进程走的就是这一路）。见方法说明里的"只处理刷到的"。
    /// </param>
    /// <returns>
    /// 巡检结果；守卫关闭时返回 <see cref="GuardRunReport.Disabled"/>；
    /// 已有巡检在进行时返回 <see cref="GuardRunReport.AlreadyRunningReport"/>。
    /// </returns>
    /// <remarks>
    /// 🔴 单实例保护在这里，而不在任何调用方：两个入口（守卫进程 / 管理端加载自启动项数据时）
    /// 都调它，保护放在调用方就要复制两遍，漏一处等于没有保护。
    /// 两层锁各管一段：<c>_runningInProcess</c> 管同进程重入（内核互斥体对此完全透明），
    /// 命名互斥体管跨进程。
    /// </remarks>
    public GuardRunReport RunOnce(
        Management.Models.ScanResult? reusedScan = null,
        IReadOnlySet<ScanScope>? processedScopes = null)
    {
        // 先抢进程内标志再抢内核互斥体：反过来会出现"本进程已经有一轮在跑，却先在内核上
        // 占了个位再白等一轮"的顺序问题。
        if (Interlocked.CompareExchange(ref _runningInProcess, 1, 0) != 0)
        {
            _log.Info("本进程内已有守卫巡检在进行，本次跳过（进程内重入保护）。");
            return GuardRunReport.AlreadyRunningReport();
        }

        try
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var acquired);
                if (!acquired)
                {
                    // 🔴 这里直接 return，**不能**落到下面的 finally 去 ReleaseMutex：
                    // 没拥有就释放会抛 ApplicationException，把"有人在跑"变成"这次崩了"。
                    _log.Info("已有守卫实例在运行（单实例互斥命中），本次跳过。");
                    return GuardRunReport.AlreadyRunningReport();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 🔴 拿不到互斥体 ≠ 有人在跑。可能是权限、也可能互斥名被别的东西占着。
                // 这里**不能**抛：一次巡检的机会比一个"完美"的并发保护更值钱，
                // 而真出并发时的后果只是多跑一轮（幂等，不会损坏状态）。
                _log.Error(ex, "获取守卫单实例互斥体失败，本次巡检照常执行（并发保护不可用）");
                return RunInspection(reusedScan, processedScopes);
            }

            try
            {
                return RunInspection(reusedScan, processedScopes);
            }
            finally
            {
                // 释放与 Dispose 的顺序：先 ReleaseMutex 再 Dispose。
                // 反过来会让内核对象在仍被当前线程拥有时被 Dispose，
                // 留下一个"已放弃但仍存在"的锁 —— 同名 Mutex 在本进程内后续构造
                // 会一直拿到 isFirst=true（内核对象还在）。
                mutex!.ReleaseMutex();
                mutex.Dispose();
            }
        }
        finally
        {
            // 无论走到哪条分支都要放掉标志，否则一次异常就让本进程**永远**跑不了守卫 ——
            // 而守卫是周期任务，这种"从此静默停摆"是最坏的一类失败。
            Interlocked.Exchange(ref _runningInProcess, 0);
        }
    }

    /// <summary>真正干活的巡检（<see cref="RunOnce"/> 已确保单实例之后调用）。</summary>
    private GuardRunReport RunInspection(Management.Models.ScanResult? reusedScan, IReadOnlySet<ScanScope>? processedScopes)
    {
        AppConfig config;
        try
        {
            config = _configStore.Load();
        }
        catch (Exception ex)
        {
            // 🔴 配置不可用时**跳过本次巡检**，而不是拿空配置继续：配置里存着"哪些项正被我们
            // 软禁用"，读不到就无从判断"有没有人把接管项改回启用"，也纠不回去。
            // 降级成空配置的后果是守卫**安静地什么都不做** —— 用户看不到任何迹象，
            // 而系统实际已偏离预期状态。
            _log.Error(ex, "读取配置失败，本次巡检已跳过（未纠正任何项目）。");
            return GuardRunReport.ConfigUnavailableReport();
        }

        if (config.Settings.GuardMode is GuardMode.Disabled)
        {
            // 关卡放在这里而不是只放在入口：计划任务可能还残留着（用户刚把档位调成"不启动"、
            // 或任务被外部工具恢复），任务拉起的进程必须自己认得"我现在是关闭的"。
            return GuardRunReport.Disabled();
        }

        // 🔴 复用调用方已经扫好的结果（v0.6.1 / F11.1）：管理端为了显示自启动项列表本来就要扫一遍，
        // 守卫再扫一遍是纯浪费 —— 而且两次扫描之间系统状态可能变，守卫拿到的与用户看到的不一致，
        // 会出现"列表里显示已启用、守卫却按已软禁用处理"这种自相矛盾。
        var scan = reusedScan ?? _scanner.Scan();

        // 🔴 "只处理刷到的"（v0.6.1 / F11.3）：管理端「刷新本页」只重扫了一个来源，
        // 其余来源的数据是缓存里的**旧值**。若照单全收，守卫就会拿着陈旧数据去判断
        // "有没有人把接管项写回启用" —— 那时它看到的是几轮之前的世界。
        //
        // 处理办法不是"跳过不处理的来源"，而是**把它们算进失败集合**：`failures` 在下面
        // 一路贯穿纠正 / 新增 / 失效 / 基线四个策略，每个策略遇到失败作用域都跳过。
        // 于是"没重扫过"与"重扫失败"走了**同一条**已经验证过的路径，不用新增分支。
        var failures = new HashSet<ScanScope>(
            scan.Failures.Select(static failure => new ScanScope(failure.Source, failure.Scope)));

        if (processedScopes is { } processed)
        {
            AddUnprocessedScopes(failures, processed);
        }

        var failureArray = failures.ToArray();

        var takenOverKeys = new HashSet<string>(
            config.Items.Select(static item => item.Id),
            StringComparer.Ordinal);

        var corrections = CorrectWrittenBackItems(config.Items, scan.Entries, failureArray, takenOverKeys);

        var baseline = _baselineStore.Read();
        var newItems = GuardNewItemPolicy.SelectNewItems(
            scan.Entries,
            GuardBaselineStore.ToIdSet(baseline),
            failureArray);
        var staleItems = GuardStalePolicy.SelectStaleItems(config.Items, scan.Entries, failureArray);

        // 基线必须最后更新（用纠正后的状态），否则"刚被纠正回来的项"会在下一轮又被看成没变。
        // 返回 null = 本次不落盘（首扫不完整，B6）：宁可下一轮重扫，也不要把残缺结果写成永久事实。
        if (MergeBaselineForNext(baseline, scan.Entries, failureArray) is { } nextBaseline)
        {
            _baselineStore.Write(nextBaseline);
        }
        else
        {
            _log.Warn(
                $"本轮有 {failureArray.Length} 个来源不可用（真失败或未重扫），本次不更新基线"
                + "（否则这些来源里的条目会永久从基线消失、守卫再也不会看见它们）。");
        }

        return new GuardRunReport
        {
            CompletedAt = _clock.Now,
            ScannedCount = scan.TotalCount,
            Corrections = corrections,
            NewItems = newItems,
            StaleItems = staleItems,

            // 🔴 报**真的**失败，不报"未重扫"合成的那批：用户看到"3 个来源扫描失败"而实际上
            // 一个都没失败，那是凭空造出来的问题。合成的失败只参与上面的策略判定。
            Failures = scan.Failures,

            // 一起读出来而不是让守卫入口再读一次配置：配置在本次巡检里已经加载过一次，
            // 再读一遍既多一次 IO，也可能与上面判定失效时用的那份不是同一版本。
            NotifyMode = config.Settings.GuardNotifyMode,
        };
    }

    /// <summary>把"本轮没重新扫过"的来源作用域并进失败集合。</summary>
    /// <param name="failures">就地扩充的失败集合。</param>
    /// <param name="processed">本轮真的重新扫过的作用域。</param>
    /// <remarks>
    /// 🔴 遍历的是 <c>_sources</c>（依赖注入的七个来源实例）而不是扫描结果里的条目：
    /// 一个来源本轮一条条目都没有，它同样属于"没重新扫过"—— 而按条目反推会漏掉它，
    /// 漏掉的后果是这个来源的旧条目被当成"确认还在"，基线因此被错误地改写。
    /// </remarks>
    private void AddUnprocessedScopes(HashSet<ScanScope> failures, IReadOnlySet<ScanScope> processed)
    {
        var added = 0;

        foreach (var source in _sources)
        {
            var scope = new ScanScope(source.Kind, source.Scope);

            // 已经有真失败的不要重复加：那一条要按"真失败"上报给用户，不能被合成的顶掉。
            if (processed.Contains(scope) || failures.Contains(scope))
            {
                continue;
            }

            failures.Add(scope);
            added++;
        }

        if (added > 0)
        {
            _log.Info(
                $"本轮只重扫了 {processed.Count} 个作用域，另 {added} 个沿用缓存数据"
                + "（守卫对它们按\"未重扫\"处理：既不纠正、也不改基线）。");
        }
    }

    /// <summary>对每一个"被写回启用"的已接管项重新软禁用，并复读确认。</summary>
    private List<GuardCorrectionOutcome> CorrectWrittenBackItems(
        IReadOnlyList<DelayedItem> managedItems,
        IReadOnlyList<StartupEntry> scannedEntries,
        IReadOnlyCollection<ScanScope> failures,
        IReadOnlySet<string> takenOverKeys)
    {
        var outcomes = new List<GuardCorrectionOutcome>();

        foreach (var entry in GuardCorrectionPolicy.SelectCorrections(managedItems, scannedEntries, failures))
        {
            if (!TryResolveSource(entry.Source, entry.Scope, out var source))
            {
                outcomes.Add(new GuardCorrectionOutcome(entry.Id, entry.Name, false, "找不到对应的来源实现"));
                _log.Warn($"纠正写回失败：『{entry.Name}』({entry.Source}/{entry.Scope}) → 找不到对应的来源实现");
                continue;
            }

            try
            {
                source.Disable(entry);
            }
            catch (Exception ex)
            {
                outcomes.Add(new GuardCorrectionOutcome(entry.Id, entry.Name, false, ex.Message));
                _log.Error(ex, $"纠正写回失败：『{entry.Name}』({entry.Source}/{entry.Scope}) → {ex.Message}");
                continue;
            }

            // 复读确认：写成功不等于写对了（可能被别的进程同时改回）。
            var confirmed = ReReadDisabled(source, entry.Id, takenOverKeys);
            outcomes.Add(new GuardCorrectionOutcome(
                entry.Id,
                entry.Name,
                confirmed,
                confirmed ? "已重新禁用" : "重新禁用后仍为启用"));

            if (confirmed)
            {
                _log.Info($"纠正写回：『{entry.Name}』({entry.Source}/{entry.Scope}) → 已重新禁用");
            }
            else
            {
                // 纠正失败必须可见：静默吞掉会让用户以为"守卫在保护我"，而实际上没有。
                _log.Warn($"纠正失败：『{entry.Name}』({entry.Source}/{entry.Scope}) → 重新禁用后仍为启用（见来源日志）");
            }
        }

        return outcomes;
    }

    /// <summary>复读该来源，确认同一主键的条目已经处于禁用态。</summary>
    private bool ReReadDisabled(IStartupSource source, string itemId, IReadOnlySet<string> takenOverKeys)
    {
        try
        {
            var entry = source.Scan(takenOverKeys)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal));

            return entry is not null && !entry.IsEnabled;
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "纠正后复读确认失败，按未确认处理");
            return false;
        }
    }

    /// <summary>
    /// 算出下一次巡检要用的基线；返回 <see langword="null"/> 表示**本次不落盘**。
    /// </summary>
    /// <remarks>
    /// 🔴 **本次整体失败的来源，其条目要沿用旧基线**。否则它们的条目会从基线里消失，
    /// 等来源恢复正常时，整整一批老条目会被报成"新增"—— 用户看到的是几十条假情报。
    /// <para>
    /// 🔴 **首扫（没有旧基线）且有来源失败时不落盘**（B6）。旧基线不存在，"沿用旧条目"
    /// 这条保护无从谈起：那份残缺结果一旦成为正式基线，失败来源里的条目就**永久消失** ——
    /// 既不在基线里，也永远等不到被扫到，守卫从此对它们完全失明。
    /// 宁可不写：没有基线只是下一轮重新扫一遍，写错的基线却是永久的假事实。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<StartupEntry>? MergeBaselineForNext(
        GuardBaseline? previous,
        IReadOnlyList<StartupEntry> currentEntries,
        ScanScope[] failures)
    {
        if (failures.Length == 0)
        {
            return currentEntries;
        }

        if (previous is null)
        {
            // 首扫 + 不完整 = 不知道全貌，不落盘。
            return null;
        }

        var failed = new HashSet<ScanScope>(failures);
        var merged = new Dictionary<string, StartupEntry>(StringComparer.Ordinal);

        foreach (var entry in previous.Entries)
        {
            if (failed.Contains(new ScanScope(entry.Source, entry.Scope)))
            {
                merged[entry.Id] = ToStartupEntry(entry);
            }
        }

        // 本次真正扫到的条目覆盖旧记录（状态可能是最新的）。
        foreach (var entry in currentEntries)
        {
            merged[entry.Id] = entry;
        }

        return [.. merged.Values];
    }

    /// <summary>把基线条目还原成扫描条目的形状，仅供下一轮差集使用。</summary>
    private static StartupEntry ToStartupEntry(GuardBaselineEntry entry) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        Source = entry.Source,
        Scope = entry.Scope,
        SourceKey = entry.Id,
        IsEnabled = entry.IsEnabled,
        IsMissing = entry.IsMissing,
    };

    /// <summary>按「来源类型 + 作用域」定位来源实例。</summary>
    private bool TryResolveSource(StartupSource kind, StartupScope scope, out IStartupSource source)
    {
        foreach (var candidate in _sources)
        {
            if (candidate.Kind == kind && candidate.Scope == scope)
            {
                source = candidate;
                return true;
            }
        }

        source = null!;
        return false;
    }
}
