namespace DelayStart.Core.Launch;

/// <summary>
/// UIAccess 降权链的时间参数与轮询截止判定（D70）。**纯逻辑**，便于单测。
/// </summary>
/// <remarks>
/// <para>
/// 这三段时间原本是 <see cref="DeElevatedProcessLauncher"/> 里的三个私有
/// <c>static readonly</c> 字段，取值只有一处用途（作业的秒退等待窗口 + 结果轮询循环），
/// 零测试覆盖；而它们的取值直接决定"目标秒退多久算秒退"（E4）与"轮询多久判超时
/// （超时即判本条目失败，不提权回退）"，漂移了没有任何编译期信号。
/// </para>
/// <para>
/// 下沉出来的只有<b>时间判定</b>：真正等待目标退出的
/// <c>WaitForSingleObject</c> 在中转器进程里、真正读结果文件的轮询要真文件与真进程，
/// 单元测试禁止触碰进程，所以那些仍然留在原处。这里的两个方法覆盖的是"何时该停"，
/// 以及三段时长之间的相互约束。
/// </para>
/// </remarks>
internal static class BrokerTimingPolicy
{
    /// <summary>
    /// 中转器为识别"目标秒退"而等待目标退出的时长；超时即认为目标在正常运行并回写结果。
    /// </summary>
    internal static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromMilliseconds(4_000);

    /// <summary>
    /// 调度端轮询中转器结果文件的超时（须明显大于秒退等待窗口：覆盖中转器启动、
    /// ShellExecute、等待与回写的全过程）。
    /// </summary>
    internal static readonly TimeSpan ResultPollTimeout = TimeSpan.FromSeconds(20);

    /// <summary>两次轮询之间的间隔。</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>算出本轮结果轮询的截止时刻。</summary>
    /// <param name="utcNow">轮询开始时刻（UTC）。</param>
    /// <returns>截止时刻 = 开始时刻 + <see cref="ResultPollTimeout"/>。</returns>
    internal static DateTime GetResultPollDeadline(DateTime utcNow) => utcNow + ResultPollTimeout;

    /// <summary>是否还要继续轮询。</summary>
    /// <param name="utcNow">当前时刻（UTC）。</param>
    /// <param name="deadline">截止时刻。</param>
    /// <returns>尚未到达截止时刻为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 边界用 <c>&lt;</c> 而非 <c>&lt;=</c>：到达截止时刻即停止，返回
    /// <see langword="null"/>，由调用方判"超时未回写结果 = 本条目失败"（D20 不提权回退）。
    /// 这条边界是唯一决定"失败 vs 再等一轮"的地方，因此单独可测。
    /// </remarks>
    internal static bool ShouldKeepPolling(DateTime utcNow, DateTime deadline)
        => utcNow < deadline;
}
