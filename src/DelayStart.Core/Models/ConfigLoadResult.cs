namespace DelayStart.Core.Models;

/// <summary>配置加载结果的状态。</summary>
public enum ConfigLoadStatus
{
    /// <summary>配置文件不存在；这是首次运行的正常状态。</summary>
    Missing,

    /// <summary>配置文件已成功读取并完成规范化。</summary>
    Loaded,

    /// <summary>配置文件存在，但内容损坏或为空；已尽力保留副本，未覆盖原文件。</summary>
    Corrupt,

    /// <summary>配置文件暂时无法读取（权限、锁定或其他 I/O 故障）。</summary>
    AccessDenied,
}

/// <summary>
/// 一次配置加载的结果。配置对象始终非空，但只有 <see cref="IsSafeForMutation"/>
/// 为真时才允许基于它执行写入、删除或恢复操作。
/// </summary>
/// <param name="Config">规范化后的配置快照；损坏 / 不可用时为安全的空快照。</param>
/// <param name="Status">加载状态。</param>
/// <param name="Message">面向日志和用户的状态说明；正常加载时为空。</param>
public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadStatus Status, string? Message)
{
    /// <summary>是否允许把这次加载结果用于配置写入、删除或系统恢复。</summary>
    public bool IsSafeForMutation => Status is ConfigLoadStatus.Missing or ConfigLoadStatus.Loaded;
}
