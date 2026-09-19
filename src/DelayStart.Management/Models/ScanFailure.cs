using DelayStart.Core.Models;

namespace DelayStart.Management.Models;

/// <summary>
/// 一次扫描中某个来源**整体**失败（FR-1.4 的另一半）。
/// </summary>
/// <remarks>
/// 与"单个条目读取失败"要区分开：后者由来源自己在循环里 try/catch 掉、只记日志、不进这个列表；
/// 前者是整个来源打不开（例如注册表键被 ACL 拒绝），此时该来源一条结果都拿不到，
/// 必须让用户知道"你看到的列表是不完整的"。
/// </remarks>
public sealed class ScanFailure
{
    /// <summary>失败来源的类型。</summary>
    public StartupSource Source { get; init; }

    /// <summary>失败来源的作用域。</summary>
    public StartupScope Scope { get; init; }

    /// <summary>来源的展示名，用于拼提示文案。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>失败原因（面向用户）。</summary>
    public string Message { get; init; } = string.Empty;
}
