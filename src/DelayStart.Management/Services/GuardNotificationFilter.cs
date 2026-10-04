using DelayStart.Core.Services;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 守卫「本轮该通报什么」的编排（D148）：读通知状态 → 取差集 → 写回状态 → 给出通报视图。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是<b>测试覆盖</b>：判据（<see cref="GuardStaleChangePolicy"/>）与状态读写
/// （<see cref="GuardNotifyStateStore"/>）各自都是可测的，但把它们缝起来的这一步
/// 一旦写进 <c>DelayStart.Guard</c>，就成了整个仓库里唯一无法单测的地方 ——
/// 而"同一条失效别重复通知"恰好是一条<b>错了就静默</b>的规则。
/// 所以这一步收在这里（有 1000+ 条用例护着），守卫进程只负责"调它、发通知"。
/// </para>
/// <para>
/// 🔴 <b>单写者：只有守卫进程</b>。管理端「自启动项」页 /「延时启动」页也跑
/// <see cref="GuardService.RunOnce"/>，但它们不发通知，因此<b>不构造本类</b>——
/// 所有权由"谁构造"保证，而不是靠调用点记得传对一个参数（D148 早期版本用
/// <c>RunOnce(advanceBaseline: false)</c> 表达同一件事，那是把结构约束降级成了约定）。
/// </para>
/// <para>
/// 🔴 <b>状态在发通知之前就写</b>，不是之后：守卫发完通知即退出，没有"之后"。
/// 代价是"通知发送失败但状态已写" ⇒ 该条这一轮不再重报。取舍依据是
/// <c>GuardToast.TryShow</c> 失败时已经记了 Warn（通知是尽力而为的旁路），
/// 而"多报一次"与"少报一次"在两个方向上不可兼得，这里选<b>不发噪声</b>：
/// 通知发不出去的原因是用户关了通知权限 / 专注助手开着，重报一万次也没用。
/// 真正的信息出口是日志页与总览卡，那两处走的是全量口径，不受本状态影响。
/// </para>
/// </remarks>
public sealed class GuardNotificationFilter
{
    private readonly GuardNotifyStateStore _stateStore;

    /// <summary>构造通报过滤器。</summary>
    /// <param name="stateStore">通知状态存储（守卫进程独占）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="stateStore"/> 为 <see langword="null"/>。</exception>
    public GuardNotificationFilter(GuardNotifyStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        _stateStore = stateStore;
    }

    /// <summary>
    /// 算出本轮该通报的失效条目，并把本轮的全量失效主键写回状态。
    /// </summary>
    /// <param name="report">本轮巡检结果（原样使用，不修改）。</param>
    /// <returns>通报口径的报告副本：失效列表已替换为"还没通知过"的那几条。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 🔴 <b>失效列表为空时也要写状态</b>：那意味着"上一轮的失效都已恢复"，
    /// 必须把已通报集合清空，否则用户把程序装回去、又把它清掉时（也就是最需要
    /// 通知的那一次）会判成"已经报过"而静默。
    /// </remarks>
    public GuardRunReport Apply(GuardRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var notifiedIds = _stateStore.Read();
        var pending = GuardStaleChangePolicy.SelectUnnotified(report.StaleItems, notifiedIds);

        // 🔴 无论 pending 是否为空都要写：状态表达的是"本轮全量失效"，
        // 不是"本轮通报了什么"。写成后者会让"报过但已恢复"的条目永远留在集合里。
        _stateStore.Write(GuardStaleChangePolicy.ToNotifiedIds(report.StaleItems));

        return report.AsNotificationView(pending);
    }
}