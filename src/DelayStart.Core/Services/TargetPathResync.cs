using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 一条"配置里的目标路径已过期、应同步为系统现值"的判定结果。
/// </summary>
/// <param name="ItemId">条目稳定主键。</param>
/// <param name="Name">条目显示名。</param>
/// <param name="CurrentPath">配置里那个**已过期**的路径。</param>
/// <param name="FreshPath">系统里**当前**的路径。</param>
public sealed record PathResync(
    string ItemId,
    string Name,
    string CurrentPath,
    string FreshPath);

/// <summary>
/// 选出"源还在、但配置里记的目标路径已经过期"的条目（D137）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么必须有这个修正</b>：<c>config\app.json</c> 里的 <c>path</c> 是<strong>接管那一刻
/// 的快照</strong>，之后没有任何地方刷新它。而程序自更新/重装会换掉可执行文件名、
/// 换掉安装目录 —— 注册表项指向了新 exe，配置还指着旧 exe。
/// </para>
/// <para>
/// 后果是<strong>静默且永久</strong>的，而且两边说两套话：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>页面 / 守卫</b>：<see cref="GuardStalePolicy"/> 在源还扫得到时用
/// <c>entry.IsMissing</c>（<strong>注册表现值</strong>）判定，所以显示"一切正常"；
/// </description></item>
/// <item><description>
/// <b>调度端</b>：<see cref="TargetPrefilter"/> 用的是 <c>item.Path</c>（<strong>过期快照</strong>），
/// 于是每次登录都判它"目标程序已不存在"、跳过不启动，并在调度日志里记一条失败。
/// </description></item>
/// </list>
/// <para>
/// 实测（2026-10-02，用户机器）：FluxDown 在 2026-10-01 自更新，注册表项已改指
/// <c>fluxdown-agent.exe</c>，配置里仍是 <c>flux_down.exe</c> ——
/// 页面看不出任何异常，而那个程序从那天起再也没被启动过，日志每天记一条"目标已不存在"。
/// 用户没有任何线索去查。
/// </para>
/// <para>
/// 🔴 <b>判据方向：只认"源还在"</b>。源没了是 <see cref="StaleKind.SourceLost"/> 那一档
/// （另一件事：接管关系断了，要去来源页重新接管），那时候没有"现值"可同步。
/// 用户要的就是这一条：<b>注册表或计划任务还存在、只是内部路径变了 ⇒ 同步为最新的</b>。
/// </para>
/// <para>
/// 🔴 兜底方向仍然是 D87/D90：<b>只在能确定的情况下动手</b>。
/// 来源整体失败时一律不同步（不完整的扫描不能用来覆盖配置）；
/// 系统报上来的新路径不是绝对路径时也不动（那多半是 UWP 解析名或协议名，
/// 写进 <c>path</c> 没有意义，还会让 <see cref="TargetPrefilter"/> 判错）。
/// </para>
/// </remarks>
public static class TargetPathResync
{
    /// <summary>选出需要同步路径的条目。</summary>
    /// <param name="managedItems">接管清单。</param>
    /// <param name="scannedEntries">本次全量扫描结果。</param>
    /// <param name="failedScopes">本次整体扫描失败的来源实例。</param>
    /// <returns>需要同步的条目；没有则为空列表。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public static IReadOnlyList<PathResync> SelectResyncs(
        IReadOnlyList<DelayedItem> managedItems,
        IReadOnlyList<StartupEntry> scannedEntries,
        IReadOnlyCollection<ScanScope> failedScopes)
    {
        ArgumentNullException.ThrowIfNull(managedItems);
        ArgumentNullException.ThrowIfNull(scannedEntries);
        ArgumentNullException.ThrowIfNull(failedScopes);

        var byId = new Dictionary<string, StartupEntry>(scannedEntries.Count, StringComparer.Ordinal);
        foreach (var entry in scannedEntries)
        {
            _ = byId.TryAdd(entry.Id, entry);
        }

        var failures = new HashSet<ScanScope>(failedScopes);
        var resyncs = new List<PathResync>();

        foreach (var item in managedItems)
        {
            // 来源不可用 ≠ 路径变了：一次整体失败绝不能拿来覆盖配置。
            if (failures.Contains(new ScanScope(item.Source, item.Scope)))
            {
                continue;
            }

            if (!byId.TryGetValue(item.Id, out var entry))
            {
                // 源没了 —— 那是"接管关系断了"（SourceLost），没有"现值"可同步。
                continue;
            }

            // 🔴 UWP 的 path 是解析名（shell:AppsFolder\...），不是文件路径。
            // 它不参与"程序自更新换 exe"这类漂移，而把它写进 path 只会污染 TargetPrefilter。
            if (entry.Source is StartupSource.Uwp or StartupSource.Manual)
            {
                continue;
            }

            var fresh = entry.Path?.Trim() ?? string.Empty;
            var current = item.Path?.Trim() ?? string.Empty;

            if (string.Equals(fresh, current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 🔴 只接受**绝对路径**。系统报上来的若是协议名 / 相对路径 / 命令名，
            // 写进配置会让调度端按它去启动一个不存在的东西 —— 比不同步糟得多。
            if (!Path.IsPathFullyQualified(fresh))
            {
                continue;
            }

            resyncs.Add(new PathResync(item.Id, item.Name, current, fresh));
        }

        return resyncs;
    }
}
