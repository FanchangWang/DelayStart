using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 新增自启动项检测（D74）：本次扫描结果里"上次没见过"的条目。
/// </summary>
/// <remarks>
/// <para>
/// 基线是上一次守卫运行的全量扫描结果（按稳定主键记 Id）。差集判定带来两个必要约束：
/// </para>
/// <list type="bullet">
/// <item><description>
/// **首次运行（无基线）不提示**：此时"上次"不存在，任何一个差集都会把当前全部条目
/// 报成"新增"，那是噪声而不是情报。
/// </description></item>
/// <item><description>
/// **来源整体失败时不参与差集**：失败意味着该来源这次根本没扫出来，把它当成"条目全没了"
/// 会在下一次运行反过来把一整批老条目报成"新增"。不完整的快照不能当"新出现"。
/// </description></item>
/// </list>
/// <para>
/// 被守卫纠正过的项不会误报：它们在基线里已经存在（基线在每轮结束时按**纠正后**的
/// 扫描结果更新），差集自然为空。
/// </para>
/// </remarks>
public static class GuardNewItemPolicy
{
    /// <summary>
    /// 选出本次新出现的条目。
    /// </summary>
    /// <param name="scannedEntries">本次全量扫描结果。</param>
    /// <param name="baselineIds">
    /// 上一次运行的条目主键集合；<see langword="null"/> 表示尚无基线（首次运行）。
    /// </param>
    /// <param name="failedScopes">本次整体扫描失败的来源实例。</param>
    /// <returns>新出现的条目；首次运行或没有新增时为空列表。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scannedEntries"/> 或 <paramref name="failedScopes"/> 为 <see langword="null"/>。
    /// </exception>
    public static IReadOnlyList<StartupEntry> SelectNewItems(
        IReadOnlyList<StartupEntry> scannedEntries,
        IReadOnlySet<string>? baselineIds,
        IReadOnlyCollection<ScanScope> failedScopes)
    {
        ArgumentNullException.ThrowIfNull(scannedEntries);
        ArgumentNullException.ThrowIfNull(failedScopes);

        if (baselineIds is null)
        {
            return [];
        }

        var failures = new HashSet<ScanScope>(failedScopes);
        var newItems = new List<StartupEntry>();

        foreach (var entry in scannedEntries)
        {
            if (failures.Contains(new ScanScope(entry.Source, entry.Scope)))
            {
                continue;
            }

            if (!baselineIds.Contains(entry.Id))
            {
                newItems.Add(entry);
            }
        }

        return newItems;
    }
}
