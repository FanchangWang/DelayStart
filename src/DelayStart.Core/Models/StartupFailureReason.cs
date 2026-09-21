namespace DelayStart.Core.Models;

/// <summary>
/// 启动或系统操作失败的分类原因。
/// </summary>
/// <remarks>
/// <para>
/// 这是给**程序**判断用的结构化原因；面向用户的文案另有一层映射
/// （见 <c>docs/design.md</c> 7.3）。两者分离是为了让 UI 文案可改而不动逻辑。
/// </para>
/// <para>
/// ⚠️ <see cref="AccessDenied"/> 的语义在 D20 之后**已经变了**：管理端全程提权，
/// 所以它不再表示「需要提权」，而是「该项受 ACL / 组策略保护」。
/// UI 不得据此提示用户「请以管理员身份运行」—— 他已经在管理员身份下了。
/// 见 <c>docs/design.md</c> 9.3、E2。
/// </para>
/// </remarks>
public enum StartupFailureReason
{
    /// <summary>无失败。</summary>
    None,

    /// <summary>访问被 ACL / 组策略拒绝。消息必须带具体条目标识（E2 / FR-2.6）。</summary>
    AccessDenied,

    /// <summary>目标文件不存在（E3 / FR-1.10）。</summary>
    TargetMissing,

    /// <summary>读取该项时失败，已跳过并记日志（E1 / FR-1.4）。</summary>
    ReadFailed,

    /// <summary>创建进程失败（含路径为空、从 PATH 也解析不到）。</summary>
    LaunchFailed,

    /// <summary>启动后立即以非零退出码退出（E5 / FR-5.9）。</summary>
    ExitedNonZero,

    /// <summary>配置文件损坏，已保留副本并重建默认配置（E10 / FR-12.3）。</summary>
    ConfigCorrupted,

    /// <summary>配置文件版本高于本程序支持的版本，拒绝加载以免写坏（前向兼容保护）。</summary>
    ConfigVersionUnsupported,

    /// <summary>计划任务操作失败（E12 / FR-11）。</summary>
    ScheduledTaskFailed,

    /// <summary>未分类的失败。出现它意味着有代码路径没被归类，应当在评审时消灭。</summary>
    Unknown,
}
