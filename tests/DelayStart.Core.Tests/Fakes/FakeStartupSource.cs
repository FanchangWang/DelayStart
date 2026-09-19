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
    public IReadOnlyList<StartupEntry> Entries { get; init; } = [];

    /// <summary>非空时 <see cref="Scan"/> 抛出它，用于模拟来源整体失败。</summary>
    public Exception? ScanException { get; init; }

    /// <summary>非空时 <see cref="Disable"/> 抛出它，用于模拟软禁用被 ACL 拒绝。</summary>
    public Exception? DisableException { get; init; }

    /// <summary>非空时 <see cref="Enable"/> 抛出它，用于模拟回滚本身也失败。</summary>
    public Exception? EnableException { get; init; }

    /// <summary><see cref="Scan"/> 被调用的次数。</summary>
    public int ScanCount { get; private set; }

    /// <summary><see cref="Disable"/> 被调用的次数。</summary>
    public int DisableCount { get; private set; }

    /// <summary><see cref="Enable"/> 被调用的次数。回滚断言全靠它。</summary>
    public int EnableCount { get; private set; }

    /// <summary>最后一次 <see cref="Scan"/> 收到的已接管主键集合。</summary>
    public IReadOnlySet<string>? LastTakenOverKeys { get; private set; }

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
        return [.. Entries.Select(entry => Rebuild(entry, takenOverKeys.Contains(entry.Id)))];
    }

    /// <inheritdoc />
    public void Disable(StartupEntry entry)
    {
        DisableCount++;
        if (DisableException is not null)
        {
            throw DisableException;
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

    private static StartupEntry Rebuild(StartupEntry entry, bool takenOver) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        Path = entry.Path,
        Arguments = entry.Arguments,
        Source = entry.Source,
        Scope = entry.Scope,
        SourceKey = entry.SourceKey,
        SourceDetail = entry.SourceDetail,
        IsEnabled = entry.IsEnabled,
        IsMissing = entry.IsMissing,
        IsProtected = entry.IsProtected,
        IsTakenOver = takenOver,
    };
}
