namespace DelayStart.Core.Services;

/// <summary>一次尝试失败后的去向。</summary>
public enum RetryDecision
{
    /// <summary>还有重试额度，再试一次。</summary>
    Retry,

    /// <summary>额度用尽（或输入不可信），本条以失败为终态。</summary>
    Fail,
}

/// <summary>
/// 同一次运行内的重试判定（FR-9.6）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **计数口径**：<c>attempts</c> 是**已经完成的尝试次数，包含当前这次失败**；
/// <c>retryCount</c> 是允许的**重试**次数，**不是总尝试次数**。
/// 把它当成总次数会让实际尝试数少一次或多一次。
/// </para>
/// <para>
/// 创建失败与复查失败共用这一条判定，因此策略不区分失败类型 ——
/// 两种调用场景（<c>Launch</c> 的创建失败、<c>Evaluate</c> 的复查失败）在调用侧各自验证。
/// </para>
/// </remarks>
public static class RetryPolicy
{
    /// <summary>判断是否还应当重试。</summary>
    /// <param name="attempts">已完成的尝试次数（含本次失败）。</param>
    /// <param name="retryCount">允许的重试次数。</param>
    /// <returns>重试或终态失败。</returns>
    public static RetryDecision Decide(int attempts, int retryCount)
    {
        // 防御性定义：不明输入不重试，落在安全侧。
        // 现有两个调用点都不可能产生 ≤0 —— Attempts 初值 0 且每次尝试前自增 ——
        // 这里锁定的是"绝不因脏数据陷入无限重试"这一保证。
        if (attempts <= 0)
        {
            return RetryDecision.Fail;
        }

        return attempts <= retryCount ? RetryDecision.Retry : RetryDecision.Fail;
    }
}
