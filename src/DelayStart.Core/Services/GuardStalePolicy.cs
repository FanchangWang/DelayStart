using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>失效条目的类型。</summary>
/// <remarks>
/// 🔴 两档是**互斥且穷尽**的，判定顺序是**先目标、后源**（见 <see cref="GuardStalePolicy"/>）。
/// 顺序不能反：先判源的话，"源没了 + 目标也没了"会被归成"源丢失"，于是继续尝试启动一个
/// 不存在的程序 —— 那正是这个判定要消除的行为。
/// </remarks>
public enum StaleKind
{
    /// <summary>
    /// 源丢失：系统启动项已被删除，但**目标程序还在**（FR-13.3）。
    /// </summary>
    /// <remarks>
    /// 🔴 **继续启动**。理由：用户要的是"这个程序被延时启动"，而"它在哪个启动项里被登记"
    /// 只是实现手段。启动项被清理工具/优化软件删掉不等于用户不要它了 ——
    /// 此时把它一起停掉，是在替用户做决定。
    /// 标记出来是为了让用户知道"接管关系已经断了"，可以在来源页重新接管。
    /// </remarks>
    SourceLost,

    /// <summary>
    /// 目标丢失：目标程序已不存在（<see cref="StartupEntry.IsMissing"/> / <see cref="TargetFileProbe"/>，FR-1.10）。
    /// </summary>
    /// <remarks>
    /// **不再启动**。源在不在都算这一档：源在也只是"还留着一个指向已删除程序的启动项"，
    /// 那同样启动不了，标成"源丢失"会诱导用户去重新接管一个永远跑不起来的东西。
    /// </remarks>
    TargetLost,
}

/// <summary>一条待通报的失效条目。</summary>
/// <param name="Item">接管清单里的原始条目。</param>
/// <param name="Kind">失效类型。</param>
/// <param name="Entry">
/// 扫描到的对应项。源丢失时必为 <see langword="null"/>（源已经不在了，没有可返回的对象）；
/// 目标丢失时可能为 <see langword="null"/> —— 手动条目与"源和目标都没了"的情形。
/// </param>
/// 🔴 本列表是**全量现状**：一条被卸载的程序每轮都在里面，日志页 / 总览卡 / 汇总行按它算；
/// **系统通知**用"已通报集合"的差集（<c>GuardStaleChangePolicy</c>，D148）。
public sealed record StaleEntry(DelayedItem Item, StaleKind Kind, StartupEntry? Entry);

/// <summary>
/// 失效条目检测（D77 / FR-13.3）：接管清单里"已经没意义了"的条目。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：调度端会照着接管清单**尝试启动**每一项。清单里留下已被删除的程序，
/// 结果就是每次登录都失败一次 —— 而管理端此前只显示扫描结果（按主键与配置关联），
/// 这类条目用户根本看不见，也就无从清理。
/// </para>
/// <para>
/// 🔴 <b>判定顺序：先目标、后源</b>。这是本策略唯一容易写错的地方：
/// </para>
/// <list type="bullet">
/// <item><description>目标不在 ⇒ <see cref="StaleKind.TargetLost"/>，<b>无论源在不在</b>。源在也只是"留着一个指向已删除程序的启动项"，照样启动不了；标成"源丢失"会诱导用户去重新接管一个永远跑不起来的东西。</description></item>
/// <item><description>目标在 + 源不在 ⇒ <see cref="StaleKind.SourceLost"/>，<b>继续启动</b>。启动项被清理工具删掉不等于用户不要这个程序了。</description></item>
/// </list>
/// <para>
/// 🔴 源不在时没有 <see cref="StartupEntry"/> 可用，<c>IsMissing</c> 拿不到 ⇒ 改用
/// <see cref="TargetFileProbe"/> 直接查文件系统。这正是"先判目标"必须重排的原因：
/// 旧顺序（先判源）根本走不到这一步，直接把条目判成孤儿，就再也没人问过"目标还在不在"。
/// </para>
/// <para>
/// 🔴 <b>手动条目只参与"目标"这一半</b>：它们在系统里没有锚点，
/// "扫描结果里找不到"是正常状态而非"源丢失"；而目标文件是否存在与来源无关 ——
/// 手动条目同样会让调度端每次登录都失败一次（2026-09-22 用户回报的漏判）。
/// </para>
/// <para>
/// 🔴 <b>来源整体失败 ⇒ 一律不判</b>：一次整体失败（服务未启动 / 键被 ACL 拒绝）
/// 绝不能把整份清单报成失效，那会诱导用户把好好的条目清理掉。
/// </para>
/// <para>
/// 判定动作出口在用户手上：守卫只通报，清理（删除 / 转为手动）由管理端「延时启动」页
/// 里那些被标成失效的行确认后执行（D81 —— 失效条目已不再是独立页面）。
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
    /// <returns>失效条目（源丢失 + 目标丢失）；没有则为空列表。</returns>
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
                // 手动条目在系统中没有锚点，"扫描结果里找不到"是它的正常状态而非源丢失 ——
                // 所以**只判目标**，且它连扫描都不需要。
                if (TargetFileProbe.IsMissing(item.Path))
                {
                    stale.Add(new StaleEntry(item, StaleKind.TargetLost, Entry: null));
                }

                continue;
            }

            // 来源不可用 ≠ 条目消失：一次整体失败绝不能把整份清单报成失效。
            if (failures.Contains(new ScanScope(item.Source, item.Scope)))
            {
                continue;
            }

            var found = byId.TryGetValue(item.Id, out var entry);

            // ① 先判目标。源不在时没有 StartupEntry 可问 IsMissing，直接查文件系统 ——
            //    TargetFileProbe 对 UWP / 协议 / 相对路径一律返回 false（"在"），
            //    兜底方向与 D87/D90 一致：只能更宽松，绝不更严格。
            var targetLost = found
                ? entry!.IsMissing
                : TargetFileProbe.IsMissing(item.Path);

            if (targetLost)
            {
                stale.Add(new StaleEntry(item, StaleKind.TargetLost, entry));
                continue;
            }

            // ② 目标还在、源没了 ⇒ 继续启动，只做标记。
            if (!found)
            {
                stale.Add(new StaleEntry(item, StaleKind.SourceLost, Entry: null));
            }
        }

        return stale;
    }
}
