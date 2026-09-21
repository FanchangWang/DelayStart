namespace DelayStart.Core.Models;

/// <summary>
/// 失败提醒的升级级别（<c>docs/design.md</c> 八 的三级表）。
/// </summary>
/// <remarks>
/// <para>
/// 分级的**唯一**依据是「同一项连续失败的次数」，不是"总共失败过几次" ——
/// 中间成功过一次就重新从 0 开始数。这样用户的直觉是对的：修好了就不再吵他。
/// </para>
/// <para>
/// 阈值见 <see cref="FailureAlertPolicy.EscalationThreshold"/>。
/// </para>
/// </remarks>
public enum FailureStreakLevel
{
    /// <summary>没有失败。</summary>
    None,

    /// <summary>连续 1–2 次：提示可被用户「忽略」。</summary>
    Warning,

    /// <summary>
    /// 连续 3 次及以上：常驻告警条、**不提供「忽略」**。
    /// </summary>
    /// <remarks>
    /// 刻意不给「忽略」：走到这一步说明该程序已连续 3 次登录都没起来，
    /// 用户需要的是**做决定**（修好它 / 移出延时启动），而不是把提示关掉。
    /// 但告警条只出现在用户主动打开的管理端内，**不构成额外打扰**（D17）。
    /// </remarks>
    Escalated,
}
