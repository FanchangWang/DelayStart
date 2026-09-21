namespace DelayStart.Core.Models;

/// <summary>
/// 一次扫描得到的自启动项（**内存模型，不入配置文件**）。
/// </summary>
/// <remarks>
/// <para>
/// 与持久化的 <see cref="DelayedItem"/> 刻意分开：扫描结果每次登录都可能变化
/// （程序被卸载、任务被删除、用户手动改回启用状态），而 <see cref="DelayedItem"/>
/// 记录的是"用户希望延时启动什么"。把两者混成一个类型会让"该项已失效"和
/// "用户配置了它"这两种状态无处安放。
/// </para>
/// <para>
/// 设计为不可变（<c>init</c>）：扫描器一次性构造出完整快照，后续只读消费。
/// </para>
/// </remarks>
public sealed class StartupEntry
{
    /// <summary>稳定主键，由 <c>ItemKeyBuilder</c> 生成（见 docs/design.md 7.3 机制 1）。</summary>
    public required string Id { get; init; }

    /// <summary>显示名：注册表值名 / 文件名 / 任务名 / 应用显示名。</summary>
    public required string Name { get; init; }

    /// <summary>目标路径。启动文件夹的 <c>.lnk</c> 已解析为真实目标（FR-1.7）；UWP 为 AUMID。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// 实际承载图标的应用程序路径；空串表示回退用 <see cref="Path"/>。
    /// </summary>
    /// <remarks>
    /// 计划任务的动作经常是 <c>cmd.exe /c start "" "C:\app\x.exe" …</c> 这类包装形态
    /// —— <see cref="Path"/> 指向的是包装器，图标会是 cmd 的方框。来源扫描时把真正
    /// 的 exe 解析出来放这里（2026-09-19 用户批复 9），只用于**展示**；
    /// 接管 / 启动语义仍以 <see cref="Path"/> + <see cref="Arguments"/> 为准。
    /// </remarks>
    public string ExecutablePath { get; init; } = string.Empty;

    /// <summary>启动参数。</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>来源类型。</summary>
    public required StartupSource Source { get; init; }

    /// <summary>作用域。🔴 显式记录，禁止后续从字符串推断（坑 5）。</summary>
    public required StartupScope Scope { get; init; }

    /// <summary>来源内的原始键：值名 / 文件名 / 任务完整路径 / TaskId。</summary>
    public required string SourceKey { get; init; }

    /// <summary>面向用户的位置描述（展示用，**不参与任何逻辑判断**）。</summary>
    public string SourceDetail { get; init; } = string.Empty;

    /// <summary>当前是否处于启用状态。已考虑 StartupApproved 键名三级回退（FR-1.5 / 机制 2）。</summary>
    public bool IsEnabled { get; init; }

    /// <summary>目标文件不存在，判为"已失效"，不参与延时配置（E3 / FR-1.10）。</summary>
    public bool IsMissing { get; init; }

    /// <summary>受保护项：<c>\Microsoft\*</c> 下的计划任务、GPO 下发项 → 只读（FR-1.9 / E16）。</summary>
    public bool IsProtected { get; init; }

    /// <summary>已被本软件接管。按稳定主键匹配，**禁止**用 (Name, Source) 二元组（坑 6 / FR-1.6）。</summary>
    public bool IsTakenOver { get; init; }

    /// <summary>该项当前是否可被接管。受保护或已失效时不可操作。</summary>
    public bool CanTakeOver => !IsProtected && !IsMissing && !IsTakenOver;
}
