using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>失效条目的类型。</summary>
public enum StaleKind
{
    /// <summary>接管清单里有它，但系统启动项已被整个删除 —— 无处可还原，也无人可启动。</summary>
    Orphan,

    /// <summary>
    /// 目标程序已不存在（<see cref="StartupEntry.IsMissing"/>，FR-1.10）。
    /// </summary>
    /// <remarks>
    /// 两个来源：已接管条目由**扫描结果**给出（<see cref="StartupEntry.IsMissing"/>），
    /// 手动条目则由 <see cref="TargetFileProbe"/> 直接查文件系统得出 ——
    /// 前者有对应的 <see cref="StartupEntry"/>，后者没有（<see cref="StaleEntry.Entry"/> 为 null）。
    /// </remarks>
    Missing,
}

/// <summary>一条待通报的失效条目。</summary>
/// <param name="Item">接管清单里的原始条目。</param>
/// <param name="Kind">失效类型。</param>
/// <param name="Entry">
/// 扫描到的对应项；孤儿条目与"手动条目目标文件消失"时为 <see langword="null"/>。
/// </param>
public sealed record StaleEntry(DelayedItem Item, StaleKind Kind, StartupEntry? Entry);

/// <summary>
/// 失效条目检测（D77）：接管清单里"已经没意义了"的条目。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：调度端会照着接管清单**无条件尝试启动**每一项。清单里留下已被删除的启动项
/// （软件卸载、用户手动清掉自启动），结果就是每次登录都失败一次 —— 而管理端此前只显示
/// 扫描结果（按主键与配置关联），孤儿条目用户根本看不见，也就无从清理。
/// </para>
/// <para>
/// 两类归并成一份通报：<see cref="StaleKind.Orphan"/>（启动项没了）与
/// <see cref="StaleKind.Missing"/>（程序文件没了）。对用户而言"这一条已经没用了"是同一件事。
/// </para>
/// <para>
/// 🔴 <b>手动条目也参与判定，但只参与"目标文件没了"这一半</b>：它们在系统里没有锚点，
/// 所以"扫描结果里找不到"不是孤儿；而目标文件是否存在与来源无关 ——
/// 手动条目同样会让调度端每次登录都失败一次（2026-09-22 用户回报的漏判）。
/// </para>
/// <para>
/// 判定动作出口在用户手上：守卫只通报，清理（删除 / 转为手动）由管理端「延时启动」页
/// 里那些被标成"已失效"的行确认后执行（D81 —— 失效条目已不再是独立页面）。
/// </para>
/// </remarks>
public static class GuardStalePolicy
{
    /// <summary>
    /// 选出失效条目。
    /// </summary>
    /// <param name="managedItems">接管清单。</param>
    /// <param name="scannedEntries">本次全量扫描结果。</param>
    /// <param name="failedScopes">本次整体扫描失败的来源实例。</param>
    /// <returns>失效条目（孤儿 + 已失效）；没有则为空列表。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<StaleEntry> SelectStaleItems(
        IReadOnlyList<DelayedItem> managedItems,
        IReadOnlyList<StartupEntry> scannedEntries,
        IReadOnlyCollection<ScanScope> failedScopes)
    {
        ArgumentNullException.ThrowIfNull(managedItems);
        ArgumentNullException.ThrowIfNull(scannedEntries);
        ArgumentNullException.ThrowIfNull(failedScopes);

        // 按稳定主键建索引；同键重复时保留首个（正常不会发生，防御外部注入的畸形结果）。
        var byId = new Dictionary<string, StartupEntry>(scannedEntries.Count, StringComparer.Ordinal);
        foreach (var entry in scannedEntries)
        {
            _ = byId.TryAdd(entry.Id, entry);
        }

        var failures = new HashSet<ScanScope>(failedScopes);
        var stale = new List<StaleEntry>();

        foreach (var item in managedItems)
        {
            if (item.IsManual)
            {
                // 手动条目在系统中没有锚点，"扫描结果里找不到"是它的正常状态，不是孤儿 ——
                // 所以**只跳过孤儿判定**，目标文件还在不在与来源无关（它连扫描都不需要），
                // 必须照判：2026-09-22 用户回报"手动添加的 exe 等文件不存在之后，守卫不会检测"。
                if (TargetFileProbe.IsMissing(item.Path))
                {
                    stale.Add(new StaleEntry(item, StaleKind.Missing, Entry: null));
                }

                continue;
            }

            // 来源不可用 ≠ 条目消失：一次整体失败（服务未启动 / 键被 ACL 拒绝）绝不能把
            // 整份清单报成"已失效"，那会诱导用户把好好的条目清理掉。
            if (failures.Contains(new ScanScope(item.Source, item.Scope)))
            {
                continue;
            }

            if (!byId.TryGetValue(item.Id, out var entry))
            {
                stale.Add(new StaleEntry(item, StaleKind.Orphan, Entry: null));
                continue;
            }

            if (entry.IsMissing)
            {
                stale.Add(new StaleEntry(item, StaleKind.Missing, entry));
            }
        }

        return stale;
    }
}
