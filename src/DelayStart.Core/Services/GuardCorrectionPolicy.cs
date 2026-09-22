using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 写回纠正判定（D74）：哪些已接管的项被应用写回了启用状态，需要再次软禁用。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **判据只有一个：本次扫描结果里该条目的 <see cref="StartupEntry.IsEnabled"/>。**
/// 刻意不在这里另写一套"是否被写回"的判据 —— 四个来源各自的实现（注册表要读
/// <c>StartupApproved</c> 三级回退、计划任务要判"任务开关 **且** 至少一个自启动触发器启用"、
/// UWP 看 <c>State</c>）已经把这件事算准了，再写第二份必然与它们漂移。
/// </para>
/// <para>
/// 最典型的漂移后果在计划任务上：接管多触发器任务时关掉的是**触发器**而不是任务开关
/// （D67），若这里简化成"任务 <c>Enabled == true</c> 即被写回"，那每个被接管的多触发器任务
/// **每一轮都会被误判为"被写回"** —— 日志被假纠正记录淹没，"纠正过什么"这件事从此不可信。
/// </para>
/// <para>
/// 不计数、不设阈值、不做对抗升级：接管是用户明确的期望，被写回就纠回来，与次数无关。
/// </para>
/// </remarks>
public static class GuardCorrectionPolicy
{
    /// <summary>
    /// 选出需要重新软禁用的条目。
    /// </summary>
    /// <param name="managedItems">接管清单（即 <c>config.json</c> 的条目）。</param>
    /// <param name="scannedEntries">本次全量扫描结果。</param>
    /// <param name="failedScopes">本次整体扫描失败的来源实例。</param>
    /// <returns>需要重新软禁用的扫描条目；没有则为空列表。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<StartupEntry> SelectCorrections(
        IReadOnlyList<DelayedItem> managedItems,
        IReadOnlyList<StartupEntry> scannedEntries,
        IReadOnlyCollection<ScanScope> failedScopes)
    {
        ArgumentNullException.ThrowIfNull(managedItems);
        ArgumentNullException.ThrowIfNull(scannedEntries);
        ArgumentNullException.ThrowIfNull(failedScopes);

        var byId = IndexById(scannedEntries);
        var failures = new HashSet<ScanScope>(failedScopes);
        var corrections = new List<StartupEntry>();

        foreach (var item in managedItems)
        {
            // 手动条目在系统里没有任何对应物，不参与纠正。
            if (item.IsManual)
            {
                continue;
            }

            // 来源本次没扫出来（整体失败）→ 状态未知 → 不判。
            if (failures.Contains(new ScanScope(item.Source, item.Scope)))
            {
                continue;
            }

            // 扫描结果里没有它 = 系统项已被删除（孤儿）。孤儿不是"被写回"，
            // 归 GuardStalePolicy 通报给用户，这里不动作 —— 没有对象可禁用。
            if (!byId.TryGetValue(item.Id, out var entry))
            {
                continue;
            }

            if (entry.IsEnabled)
            {
                corrections.Add(entry);
            }
        }

        return corrections;
    }

    /// <summary>按稳定主键建索引；同键重复时保留首个（正常不会发生，防御外部注入的畸形结果）。</summary>
    private static Dictionary<string, StartupEntry> IndexById(IReadOnlyList<StartupEntry> entries)
    {
        var map = new Dictionary<string, StartupEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            _ = map.TryAdd(entry.Id, entry);
        }

        return map;
    }
}
