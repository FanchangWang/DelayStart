namespace DelayStart.Management.Models;

/// <summary>
/// 「还原全部接管项」的结果（FR-11.4 / D22 的卸载路径）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这个类型的 <see cref="ExitCode"/> 直接决定卸载能否继续。
/// </para>
/// <para>
/// Inno Setup 的 <c>[UninstallRun]</c> **读不到被调程序的退出码**，所以卸载脚本必须写在
/// <c>[Code] InitializeUninstall()</c> 里用 <c>ewWaitUntilTerminated</c> 调用本程序并检查返回码，
/// 非 0 就**中止卸载**。违反的后果是用户卸载后所有程序永久不自启且毫不知情 ——
/// 本项目最严重的潜在缺陷（<c>design.md</c> NFR-6.4）。
/// </para>
/// </remarks>
public sealed class RestoreOutcome
{
    /// <summary>成功还原的条目数。</summary>
    public int RestoredCount { get; init; }

    /// <summary>还原失败的条目数。</summary>
    public int FailedCount { get; init; }

    /// <summary>
    /// 因"系统里的源已不存在"而跳过系统恢复、**仅删掉配置**的条目数（FR-12.4）。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意<b>不计入 <see cref="FailedCount"/></b>。那类条目的启动项早被软件卸载或
    /// 用户清理掉了，系统里**本来就没有可恢复的对象** —— 把它们算成失败会让卸载器
    /// 弹"仍要强行卸载吗"，而那次弹窗的理由是假的；更糟的是用户为了绕过它点了"强行卸载"，
    /// 于是那些**真正**还原失败的项被一起放过去。
    /// </remarks>
    public int SkippedCount { get; init; }

    /// <summary>失败项的「名称：原因」描述，用于日志与卸载中止时的提示。</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>计划任务是否已删除。</summary>
    public bool TaskDeleted { get; init; }

    /// <summary>
    /// 进程退出码：全部成功（含"本来就没有接管项"与"源已丢失因而跳过恢复"）为 <c>0</c>，
    /// 任一项**真正**失败为 <c>1</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 判据里刻意不含 <see cref="SkippedCount"/>：跳过恢复的条目没有留下任何未还原的东西，
    /// 为它们中止卸载是假警报（详见 <see cref="SkippedCount"/>）。
    /// </remarks>
    public int ExitCode => FailedCount == 0 ? 0 : 1;

    /// <summary>是否全部成功。</summary>
    public bool Succeeded => FailedCount == 0;
}
