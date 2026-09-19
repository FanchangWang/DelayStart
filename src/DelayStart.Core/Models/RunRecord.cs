namespace DelayStart.Core.Models;

/// <summary>
/// 一次完整调度运行的记录，归档到 <c>runs/&lt;runId&gt;.json</c>（§4.5 / FR-5.13）。
/// </summary>
/// <remarks>
/// <para>
/// 同一个对象也用于实时状态文件 <c>state/current-run.json</c>（FR-5.12）——
/// 调度端每有状态变化就原子重写一次，因此管理端**不需要任何 IPC** 就能读到进度（D19）。
/// </para>
/// <para>
/// 连续失败次数**不存在这里**，由 Management 层的 <c>FailureStreakService</c> 扫最近若干份
/// 归档现算。存计数会在"用户手动移出条目"和"归档被清理"时产生不一致。
/// </para>
/// </remarks>
public sealed class RunRecord
{
    /// <summary>运行标识，格式 <c>yyyyMMdd-HHmmss</c>（本地时间）。同时用作归档文件名。</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>本次运行开始时刻（本地时间偏移）。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>本次运行结束时刻。仍在运行或被强杀时为 <see langword="null"/>。</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// 是否正常跑完。为 <see langword="false"/> 意味着上次调度**没走完**（被强杀或崩溃），
    /// 管理端据此显示「上次调度未正常完成」（E9 / NFR-2.3）。
    /// </summary>
    public bool CompletedNormally { get; set; }

    /// <summary>计划处理的条目数（不含已关闭的条目）。</summary>
    public int PlannedCount { get; set; }

    /// <summary>各条目结果。</summary>
    public List<RunItemResult> Items { get; set; } = [];
}
