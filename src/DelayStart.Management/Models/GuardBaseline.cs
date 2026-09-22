using DelayStart.Core.Models;

namespace DelayStart.Management.Models;

/// <summary>
/// 守卫的基线快照（D74）：上一次巡检结束时"系统里有哪些自启动项"。
/// </summary>
/// <remarks>
/// <para>
/// 只用于一件事：与本次扫描做差集，找出**新出现**的条目。因此它必须记录<b>稳定主键</b>
/// （<see cref="GuardBaselineEntry.Id"/>，由 <c>ItemKeyBuilder</c> 生成）而不是名称 ——
/// 用户给某个自启动项改个显示名，不该被报成"新增了一项"。
/// </para>
/// <para>
/// 其余字段（名称 / 来源 / 启用态）只为排查时能看懂这份文件，不参与任何判定。
/// </para>
/// </remarks>
public sealed class GuardBaseline
{
    /// <summary>快照格式版本，便于将来演进。</summary>
    public int Version { get; set; } = 1;

    /// <summary>快照时间（ISO 8601），便于排查"这次提示为什么不对"。</summary>
    public string? CapturedAt { get; set; }

    /// <summary>快照里的条目。</summary>
    public List<GuardBaselineEntry> Entries { get; set; } = [];
}

/// <summary>基线里的一条自启动项。</summary>
public sealed class GuardBaselineEntry
{
    /// <summary>稳定主键（差集判定唯一依据）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名（仅供排查）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>来源类型。</summary>
    public StartupSource Source { get; set; }

    /// <summary>作用域。</summary>
    public StartupScope Scope { get; set; }

    /// <summary>快照时刻是否启用（仅供排查）。</summary>
    public bool IsEnabled { get; set; }

    /// <summary>快照时刻是否已失效（仅供排查）。</summary>
    public bool IsMissing { get; set; }
}
