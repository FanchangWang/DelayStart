namespace DelayStart.Core.Models;

/// <summary>
/// 界面主题偏好（FR-9 主题设置）。
/// </summary>
public enum ThemePreference
{
    /// <summary>跟随系统主题。</summary>
    FollowSystem,

    /// <summary>浅色主题。</summary>
    Light,

    /// <summary>深色主题。</summary>
    Dark,
}

/// <summary>
/// 全局设置，持久化在 <c>config.json</c> 的 <c>settings</c> 节点下（FR-9）。
/// </summary>
/// <remarks>
/// 每一项都对应设置页的一个控件，字段名与 <c>docs/design-spec.md</c> 的设置项一一对应，
/// 增删字段时必须同步改设置页文案。
/// <para>
/// 2026-09-19 用户批复精简：
/// ① 移除单条目延时上限（<c>MaxDelaySeconds</c>）；
/// ② 移除降权两开关 —— 普通条目一律降权启动，失败记日志，绝不回退提权启动；
/// ③ 移除托盘两设置 —— 托盘只属调度端，生命周期跟随通知。
/// </para>
/// </remarks>
public sealed class Settings
{
    /// <summary>延时预设值，驱动延时编辑器的快选按钮（FR-4.2 / FR-9.2）。升序去重，至少保留一项。</summary>
    public int[] DelayPresets { get; set; } = [0, 10, 30, 60, 120];

    /// <summary>
    /// 默认预设（秒）：预设列表中处于「选中」状态的项，接管条目时编辑器预选它。
    /// 必须是 <see cref="DelayPresets"/> 的成员，加载时由规范化兜底修正。
    /// </summary>
    public int DefaultPreset { get; set; } = 30;

    /// <summary>完成时的通知策略（FR-6.3 / FR-9.7）。</summary>
    public NotifyMode NotifyMode { get; set; } = NotifyMode.FailuresOnly;

    /// <summary>同一次运行内的重试次数（FR-9.6）。</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>管理端界面主题（FR-9 主题设置）。</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.FollowSystem;

    /// <summary>最近一次运行的 <c>runId</c>，供管理端总览页横幅使用（FR-6.7）。从未运行时为 <see langword="null"/>。</summary>
    public string? LastRunId { get; set; }
}
