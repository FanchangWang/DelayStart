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
/// 每一项都对应设置页的一个控件，字段名与 <c>docs/design.md</c> FR-9 的设置项一一对应，
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
    /// <remarks>
    /// 默认列表按用户 2026-09-20 批复改为 <c>0 / 5 / 10 / 15 / 20 / 30 / 60</c> 秒
    /// （原为 <c>0 / 10 / 30 / 60 / 120</c>）：登录后的前 30 秒才是真正拥挤的区间，
    /// 用 5 秒一档的细粒度比 120 秒这样的大间隔有用。只影响**新装 / 未改过该项**的配置 ——
    /// 已经落盘的列表原样保留，配置加载不会拿默认值覆盖用户改过的值。
    /// </remarks>
    public int[] DelayPresets { get; set; } = [0, 5, 10, 15, 20, 30, 60];

    /// <summary>
    /// 默认预设（秒）：预设列表中处于「选中」状态的项，接管条目时编辑器预选它。
    /// 必须是 <see cref="DelayPresets"/> 的成员，加载时由规范化兜底修正。
    /// </summary>
    /// <remarks>
    /// 默认值 2026-09-21 由 30 改为 **10**（用户要求「设置-延时 默认选中 10 秒」）。
    /// ⚠️ 与 <see cref="DelayPresets"/> 同理：只影响新装 / 未改过该项的配置 ——
    /// 已落盘的 <c>config.json</c> 里存的是用户当时的值，加载不会拿新默认值覆盖它。
    /// </remarks>
    public int DefaultPreset { get; set; } = 10;

    /// <summary>完成时的通知策略（FR-6.3 / FR-9.7）。</summary>
    public NotifyMode NotifyMode { get; set; } = NotifyMode.FailuresOnly;

    /// <summary>同一次运行内的重试次数（FR-9.6）。</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>管理端界面主题（FR-9 主题设置）。</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.FollowSystem;

    /// <summary>最近一次运行的 <c>runId</c>，供管理端总览页横幅使用（FR-6.7）。从未运行时为 <see langword="null"/>。</summary>
    public string? LastRunId { get; set; }
}
