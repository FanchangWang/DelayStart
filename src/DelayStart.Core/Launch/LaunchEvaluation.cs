using DelayStart.Core.Models;

namespace DelayStart.Core.Launch;

/// <summary>
/// 「启动成功 / 失败」的最终判定结果（机制 7 / FR-5.9）。
/// </summary>
/// <param name="State">判定出的条目状态：<see cref="RunItemState.Done"/> 或 <see cref="RunItemState.Failed"/>。</param>
/// <param name="Reason">失败分类；成功时为 <see cref="StartupFailureReason.None"/>。</param>
/// <param name="Message">面向用户的失败说明；成功时为 <see langword="null"/>。</param>
public readonly record struct LaunchEvaluation(
    RunItemState State,
    StartupFailureReason Reason,
    string? Message)
{
    /// <summary>是否判定为启动成功。</summary>
    public bool IsSuccess => State == RunItemState.Done;
}
