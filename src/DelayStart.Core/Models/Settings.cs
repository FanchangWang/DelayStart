namespace DelayStart.Core.Models;

/// <summary>
/// 全局设置，持久化在 <c>config.json</c> 的 <c>settings</c> 节点下（FR-9）。
/// </summary>
/// <remarks>
/// 每一项都对应设置页的一个控件，字段名与 <c>docs/design-spec.md</c> 的设置项一一对应，
/// 增删字段时必须同步改设置页文案。
/// </remarks>
public sealed class Settings
{
    /// <summary><see cref="MaxDelaySeconds"/> 的默认值：24 小时（FR-4.3 / FR-9.3）。</summary>
    public const int DefaultMaxDelaySeconds = 86400;

    /// <summary>延时预设值，驱动延时编辑器的快选按钮（FR-4.2 / FR-9.2）。逗号分隔编辑，解析后升序去重。</summary>
    public int[] DelayPresets { get; set; } = [0, 10, 30, 60, 120];

    /// <summary>
    /// 单条目延时上限，**仅做输入校验**，不影响调度能力（FR-4.3）。
    /// <c>0</c> 表示不限制。
    /// </summary>
    public int MaxDelaySeconds { get; set; } = DefaultMaxDelaySeconds;

    /// <summary>完成时的通知策略（FR-6.3 / FR-9.7）。</summary>
    public NotifyMode NotifyMode { get; set; } = NotifyMode.FailuresOnly;

    /// <summary>同一次运行内的重试次数（FR-9.6）。</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>全部启动完成后托盘图标保留的秒数（FR-9.5）。</summary>
    public int TrayKeepSeconds { get; set; } = 3;

    /// <summary>是否显示托盘进度图标。关闭后调度端完全无形（FR-9.8）。</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>普通用户条目优先降权启动（NFR-3.3 / FR-9.9）。</summary>
    public bool PreferDeElevatedLaunch { get; set; } = true;

    /// <summary>降权启动失败时回退为直接启动（FR-5.7 / FR-9.9）。关闭则判为失败。</summary>
    public bool FallbackOnDeElevationFailure { get; set; } = true;

    /// <summary>最近一次运行的 <c>runId</c>，供管理端总览页横幅使用（FR-6.7）。从未运行时为 <see langword="null"/>。</summary>
    public string? LastRunId { get; set; }
}
