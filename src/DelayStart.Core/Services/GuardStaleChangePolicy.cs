namespace DelayStart.Core.Services;

/// <summary>
/// 失效条目的通报差集判定（D148）：本轮失效列表里"**还没通知过**"的那几条。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GuardStalePolicy"/> 给出的失效列表是**全量现状** —— 一条被卸载的程序
/// 会**每一轮**都被列在里面。日志页 / 总览卡 / <c>guard.log</c> 汇总三处共用的正是这份全量
/// 口径（用户要能随时看到"哪些条目还在失效"），但**系统通知**必须走差集：
/// 否则用户卸载一个程序，接下来每个周期都会收到一条一模一样的通知，
/// 于是通知中心被塞满，他会开始无视守卫的全部消息 —— 那等于把唯一的变化通道变成噪声。
/// </para>
/// <para>
/// 🔴 <b>判据只吃一件事：这一条之前有没有被通报过。</b>
/// 入参是"已通报集合"，不是任何扫描派生量（上一轮的失效列表 / 基线 / 上轮是否失效）。
/// 这一点是刻意的：
/// </para>
/// <list type="bullet">
/// <item><description>因此<b>手动条目一视同仁</b>：它的主键同样进已通报集合，同样只通知一次。
/// 早期版本拿"上一轮基线的失效标志"当判据，而手动条目根本不在扫描基线里（七个来源实例
/// 没有 Manual），于是它会被<b>永久静默</b> —— 用户卸载了手动添加的 exe，一条通知都不会来。</description></item>
/// <item><description>因此<b>不需要给失效分档</b>：<see cref="StaleKind.SourceLost"/> 与
/// <see cref="StaleKind.TargetLost"/> 走同一条路。早期的"源丢失一律算新的"是拿基线当
/// 通知状态时逼出来的特判（源丢失的条目下一轮就不在基线里了），载体换掉后它自然消失。</description></item>
/// </list>
/// <para>
/// 🔴 <b>方向是"没报过就报"，不是"上轮还好着"。</b>判据必须是"上一次通报里没有它"，
/// 否则"恢复之后再次失效"永远不会通报 —— 而那恰恰是用户最需要知道的一次
/// （程序装回去又被清掉）。所以已通报集合每轮**整体替换**为本轮的全量失效主键，
/// 而不是只增不减。
/// </para>
/// <para>
/// 🔴 <b>降级方向 fail-open</b>：状态写失败 ⇒ 状态保持旧值 ⇒ 下一轮重新命中 ⇒ <b>多报一次</b>。
/// 假阳性（多一条通知）远好于静默漏报（用户永远不知道自己有个程序已经失效）——
/// 与 D87 / D90 同源：兜底只能更宽松，绝不更严格。
/// </para>
/// <para>
/// <b>入参里故意没有 <c>failedScopes</c></b>，不要"补上它"：
/// 来源整体失败的排除已经由 <see cref="GuardStalePolicy"/> 做过了，本策略是它的纯后置过滤器。
/// 再排一次就是同一条排除规则有两份真相 —— 将来谁改了其中一份，另一份会默默失效。
/// </para>
/// <para>
/// 否掉的几种形态：加成 <see cref="GuardStalePolicy"/> 的重载（两个消费方语义不同，迟早拿错）；
/// 写进守卫编排（判据会被整轮夹具掩盖，而那正是假绿潜伏的地方）；
/// 新增持久化字段到<b>基线</b>里（基线的定义是"上一次扫描快照"，塞进通知状态会让它不再是快照）。
/// </para>
/// </remarks>
public static class GuardStaleChangePolicy
{
    /// <summary>
    /// 从本轮失效列表里选出"还没通报过"的条目。
    /// </summary>
    /// <param name="staleItems">本轮的全量失效列表（<see cref="GuardStalePolicy"/> 的输出）。</param>
    /// <param name="notifiedIds">
    /// 上一轮结束时已通报的失效条目主键；<see langword="null"/> 表示状态尚不存在
    /// （首次运行 / 状态文件损坏 / 状态写失败过）。🔴 <see langword="null"/> 取"静默"这一支，
    /// 与 <c>GuardNewItemPolicy</c> 的首扫静默同向：把"上一次"不存在时的任何差集当成噪声。
    /// </param>
    /// <returns>本轮该通报的失效条目；顺序与入参一致，没有则为空列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="staleItems"/> 为 <see langword="null"/>。</exception>
    public static IReadOnlyList<StaleEntry> SelectUnnotified(
        IReadOnlyList<StaleEntry> staleItems,
        IReadOnlySet<string>? notifiedIds)
    {
        ArgumentNullException.ThrowIfNull(staleItems);

        // 没有"上一次" ⇒ 任何差集都是噪声。
        if (notifiedIds is null)
        {
            return [];
        }

        var pending = new List<StaleEntry>();

        foreach (var stale in staleItems)
        {
            if (!notifiedIds.Contains(stale.Item.Id))
            {
                pending.Add(stale);
            }
        }

        return pending;
    }

    /// <summary>
    /// 取本轮失效列表的主键集合，作为下一轮的"已通报集合"。
    /// </summary>
    /// <param name="staleItems">本轮的全量失效列表。</param>
    /// <returns>主键集合（不可变快照）；入参为空时返回空集合。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="staleItems"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 🔴 **整体替换，不是只增不减**：条目的失效状态恢复了（用户装回程序）就必须从集合里
    /// 出去，否则"恢复之后再失效"永远不会被通报。
    /// </remarks>
    public static IReadOnlySet<string> ToNotifiedIds(IReadOnlyList<StaleEntry> staleItems)
    {
        ArgumentNullException.ThrowIfNull(staleItems);

        var ids = new HashSet<string>(staleItems.Count, StringComparer.Ordinal);
        foreach (var stale in staleItems)
        {
            _ = ids.Add(stale.Item.Id);
        }

        return ids;
    }
}