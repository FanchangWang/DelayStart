namespace DelayStart.Core.Models;

/// <summary>
/// 接管前该项在系统中的原始状态，用于精确还原（FR-2.7 / FR-3.2）。
/// </summary>
/// <remarks>
/// 这个类型存在的唯一理由是「全部可逆」原则：没有它就无法回答
/// "移除接管时该把系统恢复成什么样"。字段只增不改语义。
/// </remarks>
public sealed class OriginalState
{
    /// <summary>接管前该项在系统中是否处于启用状态。</summary>
    public bool WasEnabled { get; set; }

    /// <summary>
    /// 接管前的附加身份标记，用于精确还原。例如计划任务的 <c>RunLevel</c>。
    /// 无附加信息时为 <see langword="null"/>。
    /// </summary>
    public string? Extra { get; set; }
}
