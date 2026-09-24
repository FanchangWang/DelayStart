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

    /// <summary>
    /// 配置不可用导致"是否已接管"无法判定，本次结果**整体不可信**（<see cref="ScanResult.ConfigUnavailable"/>）。
    /// </summary>
    /// <remarks>
    /// 🔴 这不是"某个来源失败"那种可以部分使用的情况：<see cref="Entries"/> 里每一项的
    /// <c>IsTakenOver</c> 都是错的。此时若照常展示，用户会看到一堆"未接管"的可点条目，
    /// 点下去就是**双重接管**（系统项被再次接管，而它其实早就被接管了）。
    /// 宁可整份列表都不给。
    /// </remarks>
    public bool ConfigUnavailable { get; init; }

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
