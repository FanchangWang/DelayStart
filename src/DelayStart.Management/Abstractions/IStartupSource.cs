using DelayStart.Core.Models;

namespace DelayStart.Management.Abstractions;

/// <summary>
/// 一类自启动来源的扫描与软禁用（<c>docs/design.md</c> 7.4）。
/// </summary>
/// <remarks>
/// <para>
/// 四个实现（注册表 / 启动文件夹 / 计划任务 / UWP）覆盖 <c>design.md</c> 二 的全部来源。
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

    /// <summary>
    /// 该源条目当前是否**仍然存在**于系统里（FR-12.4 / G2）。
    /// </summary>
    /// <param name="entry">要探测的条目，其 <see cref="StartupEntry.SourceKey"/> 是唯一判据。</param>
    /// <returns>确认还在时为 <see langword="true"/>；确认已被删除时为 <see langword="false"/>。</returns>
    /// <exception cref="StartupOperationException">
    /// 🔴 **看不真切时必须抛异常，绝不能返回 <see langword="false"/>。** 典型情形是
    /// ACL 拒绝、任务计划服务没起来、UWP 包在另一个用户下。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 🔴 <b><see langword="false"/> 与"抛异常"是两种完全不同的结论，调用方必须严格区分：</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description><b><c>false</c></b> = 源真的没了 ⇒ 系统里**没有可恢复的对象**，
    /// 继续调 <see cref="Enable"/> 只会抛"已不存在"，而配置必须照常删掉（否则它永远删不掉）。
    /// 这正是 G2 要修的缺陷：计划任务 / UWP 来源在源已消失时 <c>Enable</c> 抛错 ⇒
    /// <c>Release</c> 失败 ⇒ 配置保留 ⇒ <c>RestoreAll</c> 非 0 ⇒ 卸载器弹"仍要强行卸载吗"。</description></item>
    /// <item><description><b>抛异常</b> = 看不真切 ⇒ 必须按"保留配置 + 报失败"处理。
    /// 把它当成"源没了"就会删掉配置、而系统的软禁用标记留在原地 ——
    /// 用户的那个程序从此再也回不到原状，而且我们**已经忘了自己动过它**。
    /// 这是比"删不掉"坏得多的方向：可逆性断在这里。</description></item>
    /// </list>
    /// <para>
    /// 🔴 由此得出实现上的**偏保守**方向：宁可答 <see langword="true"/> 也不要错答
    /// <see langword="false"/>。答 <c>true</c> 的最坏后果是一次无副作用的空操作恢复
    /// （<c>Enable</c> 本来就只是删标记，标记不在就是空操作）；
    /// 错答 <c>false</c> 的后果是永久丢失还原依据。
    /// </para>
    /// <para>
    /// 各来源的判据：注册表 / 启动文件夹查 <see cref="StartupEntry.SourceKey"/> 的三级候选名
    /// （<c>StartupApprovedStore.GetCandidateNames</c>，坑 1 的同一份）；
    /// 计划任务取该路径的任务对象；UWP 查 <c>State</c> 子键。
    /// </para>
    /// </remarks>
    bool Exists(StartupEntry entry);
}
