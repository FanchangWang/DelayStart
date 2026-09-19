namespace DelayStart.Core.Models;

/// <summary>
/// 系统操作（注册表 / 启动文件夹 / 计划任务）失败时抛出的语义异常。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 构造时**强制传入条目标识**，并自动拼进消息。这不是形式主义：
/// 管理端全程提权（D20）之后，还能遇到 <c>AccessDenied</c> 属于异常情况，
/// 排查时必须一眼看出是**哪一项**被 ACL 或组策略挡住了（E2 / FR-2.6）。
/// </para>
/// <para>
/// ⚠️ UI 拿到 <see cref="StartupFailureReason.AccessDenied"/> 时，
/// 提示应为「该项受系统策略保护，已跳过」，**不得**提示「请以管理员身份运行」。
/// </para>
/// </remarks>
public sealed class StartupOperationException : Exception
{
    /// <summary>失败的结构化分类。</summary>
    public StartupFailureReason Reason { get; }

    /// <summary>出错的条目稳定主键。无条目上下文时为空串。</summary>
    public string EntryId { get; }

    /// <summary>
    /// 用结构化原因、条目标识与消息构造异常。
    /// </summary>
    /// <param name="reason">失败分类。</param>
    /// <param name="entryId">出错条目的稳定主键。会拼进异常消息。</param>
    /// <param name="message">面向人的原因描述，不含条目标识（由本构造函数追加）。</param>
    /// <param name="innerException">底层异常，通常是 <see cref="UnauthorizedAccessException"/>。</param>
    public StartupOperationException(
        StartupFailureReason reason,
        string entryId,
        string message,
        Exception? innerException = null)
        : base(ComposeMessage(entryId, message), innerException)
    {
        Reason = reason;
        EntryId = entryId;
    }

    private static string ComposeMessage(string entryId, string message)
        => string.IsNullOrWhiteSpace(entryId) ? message : $"{message}（条目：{entryId}）";
}
