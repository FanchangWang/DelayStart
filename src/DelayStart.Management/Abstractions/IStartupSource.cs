using DelayStart.Core.Models;

namespace DelayStart.Management.Abstractions;

/// <summary>
/// 一类自启动来源的扫描与软禁用（<c>docs/architecture.md</c> 3.2）。
/// </summary>
/// <remarks>
/// <para>
/// 四个实现（注册表 / 启动文件夹 / 计划任务 / UWP）覆盖 <c>requirements.md</c> 4.1 的全部来源。
/// 注册表与启动文件夹各需要**两个或三个实例**（不同 scope），所以实现类的数量少于实例数 ——
/// <c>ScanService</c> 拿到的是实例列表，不是类型列表。
/// </para>
/// <para>
/// 🔴 <see cref="Disable"/> / <see cref="Enable"/> 必须是**可逆软禁用**：
/// 只写标记、不删原值、不移动文件（FR-2.1 / FR-2.2）。这是全项目最硬的一条约束。
/// </para>
/// </remarks>
public interface IStartupSource
{
    /// <summary>来源类型。</summary>
    StartupSource Kind { get; }

    /// <summary>本实例负责的作用域。</summary>
    StartupScope Scope { get; }

    /// <summary>面向用户的来源名（如「当前用户」「所有用户（32 位）」），用于列表分组与错误消息。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 是否必须提权才能读写本来源。
    /// </summary>
    /// <remarks>
    /// 仅用于**诊断展示**：管理端按 D20 全程提权（<c>app.manifest requireAdministrator</c>），
    /// 所以正常路径下永远为真也不会触发"需要提权"的提示（NFR-3.1）。
    /// </remarks>
    bool RequiresElevation { get; }

    /// <summary>
    /// 扫描本来源的全部自启动项。
    /// </summary>
    /// <param name="takenOverKeys">
    /// 已接管条目的稳定主键集合（机制 1）。用于填充 <see cref="StartupEntry.IsTakenOver"/>。
    /// 由调用方从配置里读出后传入 —— 来源本身不读配置，保持职责单一。
    /// </param>
    /// <returns>扫描结果；单个条目读取失败时**跳过该项**而不是中断整次扫描（FR-1.4）。</returns>
    IReadOnlyList<StartupEntry> Scan(IReadOnlySet<string> takenOverKeys);

    /// <summary>软禁用该项：写标记，原始数据保持不变（FR-2.1 / 机制 4）。</summary>
    /// <param name="entry">要禁用的条目，必须来自本实例的 <see cref="Scan"/>。</param>
    /// <exception cref="StartupOperationException">写入被 ACL / 组策略拒绝时抛出，消息须带具体条目（FR-2.6）。</exception>
    void Disable(StartupEntry entry);

    /// <summary>恢复该项为系统默认状态：删除标记，原值从未被改动（FR-2.2）。</summary>
    /// <param name="entry">要恢复的条目。</param>
    /// <exception cref="StartupOperationException">删除被拒绝时抛出。</exception>
    void Enable(StartupEntry entry);
}
