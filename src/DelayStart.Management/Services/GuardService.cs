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
/// </remarks>
public sealed class GuardService
{
    private readonly ScanService _scanner;
    private readonly IAppConfigStore _configStore;
    private readonly GuardBaselineStore _baselineStore;
    private readonly IReadOnlyList<IStartupSource> _sources;
    private readonly ILogSink _log;
    private readonly IClock _clock;

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

    /// <summary>执行一次完整巡检。</summary>
    /// <returns>巡检结果；守卫关闭时返回 <see cref="GuardRunReport.Disabled"/>。</returns>
    public GuardRunReport RunOnce()
    {
        var config = _configStore.Load();

        if (config.Settings.GuardMode is GuardMode.Disabled)
        {
            // 关卡放在这里而不是只放在入口：计划任务可能还残留着（用户刚把档位调成"不启动"、
            // 或任务被外部工具恢复），任务拉起的进程必须自己认得"我现在是关闭的"。
            return GuardRunReport.Disabled();
        }

        var scan = _scanner.Scan();
        var failures = scan.Failures
            .Select(static failure => new ScanScope(failure.Source, failure.Scope))
            .ToArray();

        var takenOverKeys = new HashSet<string>(
            config.Items.Select(static item => item.Id),
            StringComparer.Ordinal);

        var corrections = CorrectWrittenBackItems(config.Items, scan.Entries, failures, takenOverKeys);

        var baseline = _baselineStore.Read();
        var newItems = GuardNewItemPolicy.SelectNewItems(
            scan.Entries,
            GuardBaselineStore.ToIdSet(baseline),
            failures);
        var staleItems = GuardStalePolicy.SelectStaleItems(config.Items, scan.Entries, failures);

        // 基线必须最后更新（用纠正后的状态），否则"刚被纠正回来的项"会在下一轮又被看成没变。
        _baselineStore.Write(MergeBaselineForNext(baseline, scan.Entries, failures));

        return new GuardRunReport
        {
            CompletedAt = _clock.Now,
            ScannedCount = scan.TotalCount,
            Corrections = corrections,
            NewItems = newItems,
            StaleItems = staleItems,
            Failures = scan.Failures,

            // 一起读出来而不是让守卫入口再读一次配置：配置在本次巡检里已经加载过一次，
            // 再读一遍既多一次 IO，也可能与上面判定失效时用的那份不是同一版本。
            NotifyMode = config.Settings.GuardNotifyMode,
        };
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
    /// 算出下一次巡检要用的基线。
    /// </summary>
    /// <remarks>
    /// 🔴 **本次整体失败的来源，其条目要沿用旧基线**。否则它们的条目会从基线里消失，
    /// 等来源恢复正常时，整整一批老条目会被报成"新增"—— 用户看到的是几十条假情报。
    /// </remarks>
    private static IReadOnlyList<StartupEntry> MergeBaselineForNext(
        GuardBaseline? previous,
        IReadOnlyList<StartupEntry> currentEntries,
        ScanScope[] failures)
    {
        if (previous is null || failures.Length == 0)
        {
            return currentEntries;
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
