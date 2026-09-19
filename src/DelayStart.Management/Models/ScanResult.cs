using DelayStart.Core.Models;

namespace DelayStart.Management.Models;

/// <summary>
/// 一次完整扫描的结果（FR-1.1–FR-1.3）。
/// </summary>
public sealed class ScanResult
{
    /// <summary>全部来源的条目，按「来源 → 名称」排序后拼在一起。</summary>
    public IReadOnlyList<StartupEntry> Entries { get; init; } = [];

    /// <summary>整体失败的来源（FR-1.4）。非空表示 <see cref="Entries"/> 是不完整的。</summary>
    public IReadOnlyList<ScanFailure> Failures { get; init; } = [];

    /// <summary>是否有来源整体失败。</summary>
    public bool HasFailures => Failures.Count > 0;

    /// <summary>条目总数。</summary>
    public int TotalCount => Entries.Count;

    /// <summary>已被本软件接管的条目数。</summary>
    public int TakenOverCount => Entries.Count(static entry => entry.IsTakenOver);

    /// <summary>当前处于启用状态且未被接管的条目数。</summary>
    public int ActiveCount => Entries.Count(
        static entry => entry.IsEnabled && !entry.IsTakenOver && !entry.IsMissing);
}
