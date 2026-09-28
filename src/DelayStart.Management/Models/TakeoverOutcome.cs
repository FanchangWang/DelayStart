using DelayStart.Core.Models;

namespace DelayStart.Management.Models;

/// <summary>
/// 接管或释放单个条目的结果。
/// </summary>
/// <remarks>
/// 接管是**复合操作**（FR-3.1：软禁用 + 写配置 + 注册计划任务，三步都成功才算成功），
/// 所以结果里必须能回答"失败时系统被回滚成什么样了"——
/// <see cref="RolledBack"/> 就是这个答案。它为 <see langword="false"/> 时意味着
/// 存在半完成状态，调用方**必须**把这个信息呈现给用户，不能只显示一句"失败"。
/// </remarks>
public sealed class TakeoverOutcome
{
    /// <summary>整体是否成功。</summary>
    public bool Succeeded { get; init; }

    /// <summary>涉及条目的稳定主键。</summary>
    public string ItemId { get; init; } = string.Empty;

    /// <summary>失败分类；成功时为 <see cref="StartupFailureReason.None"/>。</summary>
    public StartupFailureReason FailureReason { get; init; } = StartupFailureReason.None;

    /// <summary>面向用户的失败原因；成功时为 <see langword="null"/>。</summary>
    public string? Message { get; init; }

    /// <summary>
    /// 失败时，已完成的步骤是否被成功撤销。
    /// </summary>
    /// <remarks>
    /// 成功路径下恒为 <see langword="true"/>（没有需要回滚的东西）。
    /// 失败且本值为 <see langword="false"/> 时属于**需要人工介入**的状态，日志中会有 Error 级记录。
    /// </remarks>
    public bool RolledBack { get; init; } = true;

    /// <summary>
    /// 本次释放是否因为"系统里的源已经不存在"而**跳过了系统恢复**（FR-12.4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 这是成功（<see cref="Succeeded"/> 为 <see langword="true"/>）的一个**子分类**，
    /// 不是第三种结局 —— 条目确实被释放了、配置确实被删掉了，只是没什么可恢复的对象。
    /// </para>
    /// <para>
    /// 之所以要单独标出来：<c>TakeoverService.RestoreAll</c> 需要据此把它计入
    /// <see cref="RestoreOutcome.SkippedCount"/> 而<b>不是</b> <c>FailedCount</c>。
    /// 混进失败会让卸载器弹"仍要强行卸载吗"，而那次弹窗的理由是假的 ——
    /// 系统里那个启动项早就被软件自己卸载掉了，本来就没什么可还原的。
    /// </para>
    /// </remarks>
    public bool SourceWasMissing { get; init; }

    /// <summary>构造成功结果。</summary>
    /// <param name="itemId">条目主键。</param>
    /// <returns>成功结果。</returns>
    public static TakeoverOutcome Success(string itemId)
        => new() { Succeeded = true, ItemId = itemId };

    /// <summary>构造成功结果，但注明"源已丢失、系统恢复被跳过"。</summary>
    /// <param name="itemId">条目主键。</param>
    /// <param name="message">说明性文案（记日志与卸载汇总用；不是错误）。</param>
    /// <returns>成功且标记了 <see cref="SourceWasMissing"/> 的结果。</returns>
    public static TakeoverOutcome Skipped(string itemId, string message)
        => new() { Succeeded = true, ItemId = itemId, SourceWasMissing = true, Message = message };

    /// <summary>构造失败结果。</summary>
    /// <param name="itemId">条目主键。</param>
    /// <param name="reason">失败分类。</param>
    /// <param name="message">面向用户的原因。</param>
    /// <param name="rolledBack">已完成的步骤是否被成功撤销。</param>
    /// <returns>失败结果。</returns>
    public static TakeoverOutcome Failure(
        string itemId,
        StartupFailureReason reason,
        string message,
        bool rolledBack = true)
        => new()
        {
            Succeeded = false,
            ItemId = itemId,
            FailureReason = reason,
            Message = message,
            RolledBack = rolledBack,
        };
}
