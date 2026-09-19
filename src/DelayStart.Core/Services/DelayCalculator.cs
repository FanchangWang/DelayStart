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
    /// 全部条目启动完成后、托盘图标保留之外的**初始化开销估值**。
    /// </summary>
    /// <remarks>
    /// 仅用于设置页"调度器驻留时长"的展示计算（FR-9.4），不参与任何调度决策。
    /// 取 2 秒是经验值：进程创建 + 1.5 秒复查窗口 + 归档写入。
    /// </remarks>
    public static readonly TimeSpan StartupOverhead = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 低于这个驻留时长的调度不显示托盘图标（FR-6.1）。
    /// </summary>
    /// <remarks>
    /// 图标出现又立刻消失的闪烁比"没有图标"更让人困惑，因此短任务干脆不显示。
    /// </remarks>
    public static readonly TimeSpan TrayIconMinimumResidency = TimeSpan.FromSeconds(15);

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

    /// <summary>
    /// 估算调度端从启动到退出的总驻留时长，用于设置页的只读展示（FR-9.4）。
    /// </summary>
    /// <param name="delaySeconds">全部**已启用**条目的延时秒数。</param>
    /// <param name="trayKeepSeconds">全部启动完成后托盘图标保留的秒数。</param>
    /// <returns>估算的驻留时长；没有任何条目时返回 <see cref="TimeSpan.Zero"/>（FR-5.10 静默退出）。</returns>
    public static TimeSpan EstimateResidency(IEnumerable<int> delaySeconds, int trayKeepSeconds)
    {
        ArgumentNullException.ThrowIfNull(delaySeconds);

        // 用 -1 作哨兵以区分「空集合」与「全是 0 延时」——后者仍然要驻留（保留托盘 + 开销）。
        var maxDelaySeconds = -1;
        foreach (var value in delaySeconds)
        {
            var normalized = Math.Max(value, 0);
            if (normalized > maxDelaySeconds)
            {
                maxDelaySeconds = normalized;
            }
        }

        if (maxDelaySeconds < 0)
        {
            return TimeSpan.Zero;
        }

        var keepSeconds = Math.Max(trayKeepSeconds, 0);
        return TimeSpan.FromSeconds(maxDelaySeconds)
            + TimeSpan.FromSeconds(keepSeconds)
            + StartupOverhead;
    }

    /// <summary>
    /// 判断本次调度是否值得显示托盘图标（FR-6.1）。
    /// </summary>
    /// <param name="delaySeconds">全部**已启用**条目的延时秒数。</param>
    /// <param name="trayKeepSeconds">全部启动完成后托盘图标保留的秒数。</param>
    /// <returns>预计驻留达到阈值才返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 调用方仍须与设置里的「显示托盘进度图标」开关（FR-9.8）取与。
    /// </remarks>
    public static bool ShouldShowTrayIcon(IEnumerable<int> delaySeconds, int trayKeepSeconds)
        => EstimateResidency(delaySeconds, trayKeepSeconds) >= TrayIconMinimumResidency;
}
