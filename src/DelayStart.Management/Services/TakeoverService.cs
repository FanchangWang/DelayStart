using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 接管（加入延时启动）与释放（移出延时启动）的复合操作（FR-3 / <c>architecture.md</c> 3.3）。
/// </summary>
/// <remarks>
/// <para>
/// 接管是**有副作用的三步事务**，任一步失败都必须回滚，不能留下半成品（FR-3.1）：
/// </para>
/// <list type="number">
/// <item><description>记录接管前的原始状态。</description></item>
/// <item><description>写入配置（新增 <see cref="DelayedItem"/>）。</description></item>
/// <item><description>软禁用原自启动项。</description></item>
/// <item><description>确保调度用的计划任务存在。</description></item>
/// </list>
/// <para>
/// 🔴 **第 2 步刻意排在第 3 步之前**。反过来的话，若配置写入失败，用户的程序已经被禁用了
/// 却没有任何记录能说明"是谁禁的、该怎么恢复"—— 那是最坏的结果。现在的顺序保证：
/// 只要系统项被动过，配置里就一定有对应的还原依据。
/// </para>
/// </remarks>
public sealed class TakeoverService
{
    private readonly IAppConfigStore _configStore;
    private readonly ISchedulerTaskRegistrar _taskRegistrar;
    private readonly Dictionary<(StartupSource Kind, StartupScope Scope), IStartupSource> _sourceIndex;
    private readonly ILogSink _log;

    /// <summary>构造接管服务。</summary>
    /// <param name="configStore">配置读写端。</param>
    /// <param name="taskRegistrar">调度计划任务注册端。</param>
    /// <param name="sources">全部来源实例。同一个 (<c>Kind</c>, <c>Scope</c>) 只能出现一次。</param>
    /// <param name="log">日志接收端。</param>
    public TakeoverService(
        IAppConfigStore configStore,
        ISchedulerTaskRegistrar taskRegistrar,
        IEnumerable<IStartupSource> sources,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(taskRegistrar);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _taskRegistrar = taskRegistrar;
        _log = log;

        var index = new Dictionary<(StartupSource, StartupScope), IStartupSource>();
        foreach (var source in sources)
        {
            if (!index.TryAdd((source.Kind, source.Scope), source))
            {
                // 重复注册是装配错误，早失败比运行时改错对象好得多。
                throw new ArgumentException(
                    $"来源 ({source.Kind}, {source.Scope}) 被注册了多次，无法确定该操作哪一个。",
                    nameof(sources));
            }
        }

        _sourceIndex = index;
    }

    /// <summary>
    /// 接管一个自启动项。
    /// </summary>
    /// <param name="entry">扫描得到的条目。</param>
    /// <param name="options">用户选择的延时与身份等参数。</param>
    /// <returns>操作结果；失败时 <see cref="TakeoverOutcome.RolledBack"/> 说明是否已回滚干净。</returns>
    public TakeoverOutcome Takeover(StartupEntry entry, TakeoverOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        if (entry.IsTakenOver)
        {
            return TakeoverOutcome.Failure(entry.Id, StartupFailureReason.Unknown, "该项已在延时启动列表中。");
        }

        if (entry.IsProtected)
        {
            return TakeoverOutcome.Failure(
                entry.Id,
                StartupFailureReason.AccessDenied,
                $"『{entry.Name}』受系统保护，不能接管。");
        }

        if (entry.IsMissing)
        {
            return TakeoverOutcome.Failure(
                entry.Id,
                StartupFailureReason.TargetMissing,
                $"『{entry.Name}』的目标文件已不存在，请先修复或从列表中忽略它。");
        }

        if (!TryResolveSource(entry.Source, entry.Scope, out var source))
        {
            return TakeoverOutcome.Failure(
                entry.Id,
                StartupFailureReason.Unknown,
                $"找不到『{entry.Name}』所属的来源（{entry.Source} / {entry.Scope}），无法接管。");
        }

        var config = _configStore.Load();
        if (config.Items.Exists(item => string.Equals(item.Id, entry.Id, StringComparison.Ordinal)))
        {
            return TakeoverOutcome.Failure(entry.Id, StartupFailureReason.Unknown, "该项已被接管，无需重复操作。");
        }

        // ── 步骤 1：记录接管前的原始状态（FR-3.2）──────────────────────────────
        var originalState = new OriginalState { WasEnabled = entry.IsEnabled };

        // ── 步骤 2：写配置 ────────────────────────────────────────────────────
        var item = BuildDelayedItem(entry, options, originalState);
        config.Items.Add(item);

        try
        {
            _configStore.Save(config);
        }
        catch (Exception ex)
        {
            // 配置没写进去 = 系统尚未被改动，无需回滚。
            _log.Error(ex, $"接管『{entry.Name}』失败：配置写入失败，系统未被改动");
            return TakeoverOutcome.Failure(
                entry.Id,
                StartupFailureReason.Unknown,
                $"写入配置失败，系统未被改动：{ex.Message}");
        }

        // ── 步骤 3：软禁用原自启动项 ──────────────────────────────────────────
        var disabled = false;
        if (source is not null)
        {
            try
            {
                source.Disable(entry);
                disabled = true;
            }
            catch (StartupOperationException ex)
            {
                var rolledBack = Rollback(entry, source, disableApplied: false, entry.Id);
                return TakeoverOutcome.Failure(entry.Id, ex.Reason, ex.Message, rolledBack);
            }
        }

        // ── 步骤 4：确保调度计划任务存在（FR-3.3）──────────────────────────────
        try
        {
            _taskRegistrar.RegisterOrUpdate();
        }
        catch (StartupOperationException ex)
        {
            // 计划任务失败也要回滚前两步：否则用户会看到一个"已接管"的条目，
            // 但它永远不会被启动 —— 比单纯失败更糟糕。
            var rolledBack = Rollback(entry, source, disabled, entry.Id);
            return TakeoverOutcome.Failure(entry.Id, ex.Reason, ex.Message, rolledBack);
        }

        _log.Info($"已接管『{entry.Name}』，延时 {options.DelaySeconds} 秒（{entry.Id}）");
        return TakeoverOutcome.Success(entry.Id);
    }

    /// <summary>
    /// 释放一个条目（移出延时启动），把系统恢复成接管前的样子（FR-3.4 的反向）。
    /// </summary>
    /// <param name="item">配置中的条目。</param>
    /// <returns>操作结果。</returns>
    /// <remarks>
    /// 顺序与接管**相反**，且第 1 步优先：
    /// <list type="number">
    /// <item><description>恢复系统启动项。</description></item>
    /// <item><description>从配置里删除条目。</description></item>
    /// <item><description>若已无任何条目，删除计划任务。</description></item>
    /// </list>
    /// 🔴 第 1 步失败时**保持配置不动**并直接返回失败。配置是唯一的还原依据，
    /// 在系统项还没恢复成功时删掉它，就等于永久丢失了"该怎么恢复"的答案。
    /// 用户重试即可 —— 恢复动作本身是幂等的（删标记，值不存在时静默跳过）。
    /// </remarks>
    public TakeoverOutcome Release(DelayedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // 找不到来源不算错误：手动条目本就没有来源，其余情况也只需跳过系统动作。
        _ = TryResolveSource(item.Source, item.Scope, out var source);

        // ── 步骤 1：恢复系统启动项 ────────────────────────────────────────────
        // 手动条目在系统里没有任何对应物，直接跳过（FR-3.4）。
        if (!item.IsManual && source is not null)
        {
            try
            {
                RestoreToOriginalState(item, source);
            }
            catch (StartupOperationException ex)
            {
                _log.Error(ex, $"释放『{item.Name}』失败：无法恢复系统启动项，已保留延时配置");
                return TakeoverOutcome.Failure(
                    item.Id,
                    ex.Reason,
                    $"恢复系统启动项失败，已保留延时配置以免丢失还原依据，可稍后重试：{ex.Message}");
            }
        }

        // ── 步骤 2：从配置中删除 ──────────────────────────────────────────────
        var config = _configStore.Load();
        if (config.Items.RemoveAll(candidate => string.Equals(candidate.Id, item.Id, StringComparison.Ordinal)) > 0)
        {
            try
            {
                _configStore.Save(config);
            }
            catch (Exception ex)
            {
                // 系统项已恢复但配置没删掉 —— 状态不一致，但**用户重试即可**：
                // Enable 是幂等的，本方法会再走一遍并删掉配置。
                _log.Error(ex, $"释放『{item.Name}』失败：系统项已恢复但配置未更新，请重试");
                return TakeoverOutcome.Failure(
                    item.Id,
                    StartupFailureReason.Unknown,
                    $"系统启动项已恢复，但配置未能更新（{ex.Message}）。请在列表中重试「移出延时启动」。");
            }
        }

        // ── 步骤 3：没有条目了就删掉计划任务 ──────────────────────────────────
        if (config.Items.Count == 0)
        {
            try
            {
                _taskRegistrar.Delete();
            }
            catch (StartupOperationException ex)
            {
                // 不算失败：条目已释放完毕，残留的任务下次启动管理端会被幂等清理。
                _log.Warn(ex, "已无延时条目，但删除计划任务失败（不影响本次释放）");
            }
        }

        _log.Info($"已释放『{item.Name}』（{item.Id}）");
        return TakeoverOutcome.Success(item.Id);
    }

    /// <summary>
    /// 把系统启动项恢复成**接管前**的样子（FR-2.7 / FR-3.4 与 <c>requirements.md</c> 9.3 第 4 条）。
    /// </summary>
    /// <param name="item">配置中的条目，靠 <see cref="DelayedItem.OriginalState"/> 回答"接管前是什么样"。</param>
    /// <param name="source">该条目所属来源。</param>
    /// <remarks>
    /// 🔴 <b>不能无条件调 <see cref="IStartupSource.Enable"/></b>。接管前该项可能**已被用户
    /// 在任务管理器 / MSCONFIG 里禁用过**（<see cref="OriginalState.WasEnabled"/> 为
    /// <see langword="false"/>，标记字节 <c>0x03</c>）。此时无条件 <c>Enable</c> 会删掉那个标记，
    /// 于是用户明明禁过的程序在"移出延时启动"之后开始自启动 —— 用户没有任何办法知道是谁改的。
    /// 这是"全部可逆"承诺上的一个破口，而不是可以接受的偏差。
    /// <para>
    /// 两种动作都是幂等的（写标记 / 删标记，重复执行结果相同），所以释放失败后重试是安全的。
    /// </para>
    /// </remarks>
    private static void RestoreToOriginalState(DelayedItem item, IStartupSource source)
    {
        var entry = ToEntry(item);

        if (item.OriginalState.WasEnabled)
        {
            // 原本会自启动 → 删掉我们写的禁用标记，让它照原样启动。
            source.Enable(entry);
        }
        else
        {
            // 原本就是禁用的 → 重新写回禁用标记，保持"不启动"。
            source.Disable(entry);
        }
    }

    /// <summary>
    /// 还原全部接管项并删除计划任务（FR-11.4 / D22 的卸载路径）。
    /// </summary>
    /// <returns>
    /// 汇总结果。<see cref="RestoreOutcome.ExitCode"/> 非 0 时卸载脚本**必须中止卸载** ——
    /// 否则被接管的程序会在用户毫不知情的情况下永久失去自启动。
    /// </returns>
    /// <remarks>
    /// 逐项隔离：某一项恢复失败不影响其余项继续恢复。计划任务**无论成败都会尝试删除** ——
    /// 程序即将被卸载，残留的任务只会指向一个不存在的 exe。
    /// </remarks>
    public RestoreOutcome RestoreAll()
    {
        var config = _configStore.Load();
        var failures = new List<string>();
        var restored = 0;

        foreach (var item in config.Items.ToList())
        {
            var outcome = Release(item);
            if (outcome.Succeeded)
            {
                restored++;
            }
            else
            {
                failures.Add($"{item.Name}：{outcome.Message}");
            }
        }

        var taskDeleted = false;
        try
        {
            _taskRegistrar.Delete();
            taskDeleted = true;
        }
        catch (StartupOperationException ex)
        {
            failures.Add($"计划任务：{ex.Message}");
            _log.Error(ex, "还原全部时删除计划任务失败");
        }

        _log.Info($"还原完成：成功 {restored} 项，失败 {failures.Count} 项");
        return new RestoreOutcome
        {
            RestoredCount = restored,
            FailedCount = failures.Count,
            Failures = failures,
            TaskDeleted = taskDeleted,
        };
    }

    /// <summary>
    /// 回滚已完成的步骤（**逆序**）。
    /// </summary>
    /// <param name="entry">正在接管的条目。</param>
    /// <param name="source">该条目所属来源；手动条目为 <see langword="null"/>。</param>
    /// <param name="disableApplied">步骤 3 是否已生效（只有生效了才需要撤销）。</param>
    /// <param name="itemId">要移除的配置条目主键。</param>
    /// <returns>全部回滚动作都成功时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 回滚本身**不抛异常**：它已经在异常路径上，再抛会盖掉原始错误。
    /// 单项失败只记 Error 日志，并通过返回值告诉调用方"存在需要人工介入的半完成状态"。
    /// </remarks>
    private bool Rollback(StartupEntry entry, IStartupSource? source, bool disableApplied, string itemId)
    {
        var succeeded = true;

        // 先撤销步骤 3：把系统项恢复到**接管前**的状态。
        // 🔴 接管前该项若已被用户禁用（entry.IsEnabled == false），"撤销"就是让它继续禁用 ——
        // 无条件 Enable 会把它变成启用，等于回滚本身又制造了一次数据损坏。
        if (disableApplied && source is not null)
        {
            try
            {
                if (entry.IsEnabled)
                {
                    source.Enable(entry);
                }
                else
                {
                    source.Disable(entry);
                }
            }
            catch (Exception ex)
            {
                succeeded = false;
                _log.Error(ex, $"回滚失败：无法恢复『{entry.Name}』的系统启动项");
            }
        }

        // 再撤销步骤 2：把配置条目移除。
        try
        {
            var config = _configStore.Load();
            if (config.Items.RemoveAll(candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal)) > 0)
            {
                _configStore.Save(config);
            }
        }
        catch (Exception ex)
        {
            succeeded = false;
            _log.Error(ex, $"回滚失败：无法从配置中移除条目 {itemId}，下次扫描会显示为「已接管」");
        }

        return succeeded;
    }

    /// <summary>
    /// 按 (<paramref name="kind"/>, <paramref name="scope"/>) 找出对应来源。
    /// </summary>
    /// <param name="kind">来源类型。</param>
    /// <param name="scope">作用域。</param>
    /// <param name="source">找到的来源；无需系统动作时为 <see langword="null"/>。</param>
    /// <returns>该组合可用（含"合法地无需来源"）时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 返回值与 <paramref name="source"/> 为 <see langword="null"/> 是**两件不同的事**：
    /// 前者 <see langword="false"/> 表示装配缺了来源，是错误；
    /// 后者为 <see langword="null"/> 表示手动条目这种"本来就没有系统对应物"的合法情况（FR-3.4）。
    /// 把两者混成一个可空返回值会让调用方无法区分。
    /// </remarks>
    private bool TryResolveSource(StartupSource kind, StartupScope scope, out IStartupSource? source)
    {
        if (kind == StartupSource.Manual)
        {
            source = null;
            return true;
        }

        return _sourceIndex.TryGetValue((kind, scope), out source);
    }

    private static DelayedItem BuildDelayedItem(StartupEntry entry, TakeoverOptions options, OriginalState originalState)
        => new()
        {
            Id = entry.Id,
            Name = entry.Name,
            Path = entry.Path,
            // 用户在接管对话框里没填参数时，沿用原自启动项自带的参数。
            Arguments = options.Arguments ?? entry.Arguments,
            DelaySeconds = options.DelaySeconds,
            SortOrder = options.SortOrder,
            RunAsAdmin = options.RunAsAdmin,
            Enabled = true,
            Source = entry.Source,
            Scope = entry.Scope,
            SourceKey = entry.SourceKey,
            SourceDetail = entry.SourceDetail,
            OriginalState = originalState,
        };

    /// <summary>
    /// 把配置条目还原成一个最小的 <see cref="StartupEntry"/>，供来源执行恢复动作。
    /// </summary>
    /// <remarks>
    /// 恢复动作只用到 <c>Source</c> / <c>Scope</c> / <c>SourceKey</c> / <c>Id</c> 四项，
    /// 其余字段是为了让类型成立而填的。这比给 <c>IStartupSource</c> 加一个
    /// "按配置项恢复"的重载更简单 —— 后者会让接口出现两套形状相似但语义不同的入参。
    /// </remarks>
    private static StartupEntry ToEntry(DelayedItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Path = item.Path,
        Arguments = item.Arguments,
        Source = item.Source,
        Scope = item.Scope,
        SourceKey = item.SourceKey,
        SourceDetail = item.SourceDetail,
        IsEnabled = false,
    };
}
