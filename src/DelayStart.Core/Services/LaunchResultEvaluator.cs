using DelayStart.Core.Launch;
using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 「启动成功 / 失败」的判定（机制 7 / FR-5.9）。**纯逻辑**，进程状态由调用方探测后传进来。
/// </summary>
/// <remarks>
/// <para>
/// 判定规则：
/// </para>
/// <list type="number">
/// <item><description>进程没被创建出来 → 失败。</description></item>
/// <item><description>延时复查时仍存活 → 成功。</description></item>
/// <item><description>已退出且退出码为 0 → **成功**。</description></item>
/// <item><description>已退出且退出码非 0 → 失败，记录退出码。</description></item>
/// </list>
/// <para>
/// 第 3 条是**必要的宽容**：<c>msedge.exe</c> 等大量程序在拉起已有实例后会立刻退出，
/// 退出码为 0。缺了这条会产生大量假失败，用户会很快不再相信这个列表（E4）。
/// </para>
/// <para>
/// 「拿到不到进程句柄」也按成功计：UWP 经 shell 激活拿不到目标 PID（D28 / R12），
/// 这种情况下能确认的只有"shell 接受了这次激活"，判定降级为乐观。
/// </para>
/// </remarks>
public static class LaunchResultEvaluator
{
    /// <summary>发起后复查进程状态的等待毫秒数（机制 7）。</summary>
    public const int RecheckDelayMilliseconds = 1500;

    /// <summary>依据创建结果与复查快照判定条目的最终状态。</summary>
    /// <param name="outcome">进程创建结果。</param>
    /// <param name="snapshotAfterDelay">
    /// 等待 <see cref="RecheckDelayMilliseconds"/> 毫秒后探测到的进程状态；
    /// 进程句柄不可得（UWP 激活）或调用方未探测时传 <see langword="null"/>。
    /// </param>
    /// <returns>判定结果。</returns>
    /// <remarks>
    /// 本方法**不处理**降权回退（E6）——回退不算失败，提示信息由调用方读
    /// <see cref="LaunchOutcome.DeElevationFellBack"/> 后追加，判定结果保持成功。
    /// </remarks>
    public static LaunchEvaluation Evaluate(LaunchOutcome outcome, ProcessSnapshot? snapshotAfterDelay)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (!outcome.Created)
        {
            var message = string.IsNullOrWhiteSpace(outcome.FailureMessage)
                ? "创建进程失败。"
                : outcome.FailureMessage;

            return new LaunchEvaluation(RunItemState.Failed, StartupFailureReason.LaunchFailed, message);
        }

        if (snapshotAfterDelay is not { } snapshot || !snapshot.HasExited)
        {
            return new LaunchEvaluation(RunItemState.Done, StartupFailureReason.None, null);
        }

        if (snapshot.ExitCode == 0)
        {
            return new LaunchEvaluation(RunItemState.Done, StartupFailureReason.None, null);
        }

        return new LaunchEvaluation(
            RunItemState.Failed,
            StartupFailureReason.ExitedNonZero,
            $"启动后立即退出（退出码 {snapshot.ExitCode}）。");
    }
}
