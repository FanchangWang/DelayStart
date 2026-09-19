using DelayStart.Core.Models;

namespace DelayStart.Management.Models;

/// <summary>
/// 接管一个自启动项时用户可选的参数（FR-4.1 / FR-4.4 / FR-4.5）。
/// </summary>
/// <remarks>
/// 与 <see cref="DelayedItem"/> 分开是刻意的：本类型只承载"用户在这一次接管时做的选择"，
/// 而条目主键、来源、原始状态这些**由系统推导**的字段不在这里 —— 它们不该由调用方提供，
/// 否则就给了调用方拼错的机会。
/// </remarks>
public sealed class TakeoverOptions
{
    /// <summary>延时秒数，相对登录时刻的绝对时间点（机制 5）。</summary>
    public int DelaySeconds { get; init; } = DelayedItem.DefaultDelaySeconds;

    /// <summary>启动身份：<see langword="false"/> 走降权启动（NFR-3.3）。</summary>
    public bool RunAsAdmin { get; init; }

    /// <summary>自定义命令行参数，留空则用原自启动项自带的参数（FR-4.5）。</summary>
    public string? Arguments { get; init; }

    /// <summary>相同延时内的发起顺序（FR-4.7）。</summary>
    public int SortOrder { get; init; }
}
