using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;

namespace DelayStart.Core.Tests.Fakes;

/// <summary>
/// <see cref="IStartupSource"/> 的测试替身。
/// </summary>
/// <remarks>
/// 它能做的两件事正对应 <c>D33</c> 要覆盖的两条需求：
/// 让 <c>Scan</c> 抛异常（FR-1.4「单条/单源失败不影响其余」），
/// 以及记录 <c>Disable</c> / <c>Enable</c> 的调用次数（FR-3.1「失败要逆序回滚」）。
/// 真实实现要碰注册表与磁盘，这里**不产生任何系统副作用**。
/// </remarks>
internal sealed class FakeStartupSource : IStartupSource
{
    /// <inheritdoc />
    public StartupSource Kind { get; init; } = StartupSource.Registry;

    /// <inheritdoc />
    public StartupScope Scope { get; init; } = StartupScope.Hkcu;

    /// <inheritdoc />
    public string DisplayName { get; init; } = "假来源";

    /// <inheritdoc />
    public bool RequiresElevation => false;

    /// <summary>扫描要返回的条目（<c>IsTakenOver</c> 会被按传入的主键集合重算）。</summary>
    /// <remarks>
    /// 支持对象初始化器与 <see cref="SetEntries"/> 两种写法：后者用于"跨轮次状态变化"的用例
    /// （第二轮多出一个条目、来源先失败后恢复）。
    /// </remarks>
    public IReadOnlyList<StartupEntry> Entries
    {
        get => _entries;
        init => _entries = value;
    }

    /// <summary>非空时 <see cref="Scan"/> 抛出它，用于模拟来源整体失败。</summary>
    public Exception? ScanException
    {
        get => _scanException;
        init => _scanException = value;
    }

    /// <summary>非空时 <see cref="Disable"/> 抛出它，用于模拟软禁用被 ACL 拒绝。</summary>
    public Exception? DisableException { get; init; }

    /// <summary>非空时 <see cref="Enable"/> 抛出它，用于模拟回滚本身也失败。</summary>
    public Exception? EnableException { get; init; }

    /// <summary>
    /// <see langword="true"/> 时 <see cref="Disable"/> 会真正改变后续 <see cref="Scan"/> 的结果
    /// （该条目被报成禁用态）。
    /// </summary>
    /// <remarks>
    /// 默认 <see langword="false"/>：只记调用次数，扫描结果永远不变 —— 这既保持了既有用例的语义，
    /// 也让"写成功了但状态没变（被别的进程改回）"这一失败场景可以被**直接**测出来。
    /// 守卫的复读确认两条分支（确认通过 / 仍为启用）都靠这个开关区分。
    /// </remarks>
    public bool DisableTakesEffect { get; init; }

    private readonly HashSet<string> _disabledIds = new(StringComparer.Ordinal);

    private IReadOnlyList<StartupEntry> _entries = [];
    private Exception? _scanException;

    /// <summary><see cref="Scan"/> 被调用的次数。</summary>
    public int ScanCount { get; private set; }

    /// <summary><see cref="Disable"/> 被调用的次数。</summary>
    public int DisableCount { get; private set; }

    /// <summary><see cref="Enable"/> 被调用的次数。回滚断言全靠它。</summary>
    public int EnableCount { get; private set; }

    /// <summary>最后一次 <see cref="Scan"/> 收到的已接管主键集合。</summary>
    public IReadOnlySet<string>? LastTakenOverKeys { get; private set; }

    /// <summary>换掉扫描结果，用于"第二轮多出一个条目"这类跨轮次用例。</summary>
    /// <param name="entries">新的条目集合。</param>
    public void SetEntries(IReadOnlyList<StartupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries;
    }

    /// <summary>切换"来源整体失败"状态；传 <see langword="null"/> 表示恢复健康。</summary>
    /// <param name="exception">要抛出的异常，或 <see langword="null"/>。</param>
    public void FailWith(Exception? exception) => _scanException = exception;

    /// <inheritdoc />
    public IReadOnlyList<StartupEntry> Scan(IReadOnlySet<string> takenOverKeys)
    {
        ScanCount++;
        LastTakenOverKeys = takenOverKeys;

        if (ScanException is not null)
        {
            throw ScanException;
        }

        // 真实来源就是这样填 IsTakenOver 的（FR-1.6），替身保持一致，
        // 否则 ScanService 传参是否正确就测不出来。
        return
        [
            .. Entries.Select(entry => Rebuild(
                entry,
                takenOverKeys.Contains(entry.Id),
                DisableTakesEffect && _disabledIds.Contains(entry.Id) ? false : entry.IsEnabled)),
        ];
    }

    /// <inheritdoc />
    public void Disable(StartupEntry entry)
    {
        DisableCount++;
        if (DisableException is not null)
        {
            throw DisableException;
        }

        if (DisableTakesEffect)
        {
            _disabledIds.Add(entry.Id);
        }
    }

    /// <inheritdoc />
    public void Enable(StartupEntry entry)
    {
        EnableCount++;
        if (EnableException is not null)
        {
            throw EnableException;
        }
    }

    private static StartupEntry Rebuild(StartupEntry entry, bool takenOver, bool isEnabled) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        Path = entry.Path,
        Arguments = entry.Arguments,
        Source = entry.Source,
        Scope = entry.Scope,
        SourceKey = entry.SourceKey,
        SourceDetail = entry.SourceDetail,
        IsEnabled = isEnabled,
        IsMissing = entry.IsMissing,
        IsProtected = entry.IsProtected,
        IsTakenOver = takenOver,
    };
}
