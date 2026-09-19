namespace DelayStart.Core.Models;

/// <summary>
/// 单次运行中一个条目的执行结果（<c>runs/&lt;runId&gt;.json</c> 的 <c>items[]</c> 元素，§4.5）。
/// </summary>
public sealed class RunItemResult
{
    /// <summary>条目的稳定主键。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>条目显示名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本次使用的延时秒数。</summary>
    public int Delay { get; set; }

    /// <summary>最终状态。</summary>
    public RunItemState State { get; set; } = RunItemState.Waiting;

    /// <summary>实际发起时刻。未发起（等待中被终止）时为 <see langword="null"/>。</summary>
    public DateTimeOffset? LaunchedAt { get; set; }

    /// <summary>失败原因的结构化分类，供程序判断（E13 的连续失败统计等）。</summary>
    public StartupFailureReason FailureReason { get; set; } = StartupFailureReason.None;

    /// <summary>面向用户的失败原因描述。成功时为 <see langword="null"/>。</summary>
    public string? Reason { get; set; }

    /// <summary>实际尝试次数（含重试）。</summary>
    public int Attempts { get; set; }
}
