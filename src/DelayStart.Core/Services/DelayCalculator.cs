namespace DelayStart.Core.Services;

/// <summary>
/// 调度时序计算（机制 5 / FR-5.3 / FR-5.4）。**纯计算，无副作用**，是本项目重点测试对象之一。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 核心语义：延时是**相对登录时刻的绝对时间点**，不是"上一个程序启动后再等 N 秒"。
/// 第 3 个条目的 60 秒意味着"登录后第 60 秒"，而不是"前两个跑完后再过 60 秒"。
/// 把它实现成累加会让整个时间轴随前序条目的耗时漂移。
/// </para>
/// <para>
/// ⚠️ 调用方的 <c>elapsed</c> 必须来自 <see cref="System.Diagnostics.Stopwatch"/>（单调时钟），
/// **不得**用 <c>DateTime.Now</c> 相减 —— 系统时间或时区被改动会让已排定的延时错乱（E15）。
/// </para>
/// </remarks>
public static class DelayCalculator
{
    /// <summary>
    /// 计算某条目距离应当启动还有多久。
    /// </summary>
    /// <param name="delaySeconds">该条目配置的延时秒数（相对登录时刻）。</param>
    /// <param name="elapsedSinceRunStart">自本次调度开始已过去的时长，取自单调时钟。</param>
    /// <returns>剩余时长；<c>&lt;= TimeSpan.Zero</c> 表示已到点，应立即启动（E7）。</returns>
    public static TimeSpan Remaining(int delaySeconds, TimeSpan elapsedSinceRunStart)
        => TimeSpan.FromSeconds(delaySeconds) - elapsedSinceRunStart;

    /// <summary>判断某条目是否已到应当启动的时刻。</summary>
    /// <param name="delaySeconds">该条目配置的延时秒数。</param>
    /// <param name="elapsedSinceRunStart">自本次调度开始已过去的时长。</param>
    /// <returns>已到点返回 <see langword="true"/>。</returns>
    public static bool IsDue(int delaySeconds, TimeSpan elapsedSinceRunStart)
        => Remaining(delaySeconds, elapsedSinceRunStart) <= TimeSpan.Zero;
}
