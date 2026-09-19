namespace DelayStart.Core.Models;

/// <summary>
/// 调度完成时的通知策略（FR-9.7 / FR-6.3）。
/// </summary>
/// <remarks>
/// 显式赋值（而非按声明顺序）是为了让配置文件里的数字含义长期稳定 ——
/// 将来若在中间插入新取值，旧配置的语义不会被悄悄改写。
/// </remarks>
public enum NotifyMode
{
    /// <summary>仅在有失败时通知。默认值。</summary>
    FailuresOnly = 0,

    /// <summary>无论成败都通知。</summary>
    Always = 1,

    /// <summary>从不通知。</summary>
    Never = 2,
}
