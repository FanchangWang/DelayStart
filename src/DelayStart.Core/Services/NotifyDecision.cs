using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 调度完成通知的判定（N2 / N5，2026-09-22 批复）：
/// <see cref="NotifyMode"/> 从此**只管发不发系统通知**，与面板再无关系。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="CompletionPolicy"/> 拆成两个纯函数是本次需求的核心
/// （"面板是面板，通知是通知"）：<see cref="CompletionPolicy"/> 回答"等不等面板"，
/// 本类回答"发不发通知"，两者互不读取对方的输入。
/// </para>
/// <para>
/// 判定表：<see cref="NotifyMode.Always"/> → 发；<see cref="NotifyMode.FailuresOnly"/>
/// → 有失败才发；<see cref="NotifyMode.Never"/> → 不发（调用方记一行"已跳过通知"日志）。
/// 空计划（0 条条目）**无特判**（D4 批复 A：简单一致）。
/// </para>
/// </remarks>
public static class NotifyDecision
{
    /// <summary>按通知策略判定是否发送调度完成通知。</summary>
    /// <param name="mode">管理端设置的通知策略。</param>
    /// <param name="failedCount">本批次失败条目数。</param>
    /// <returns>应发送通知时为 <see langword="true"/>。</returns>
    public static bool Decide(NotifyMode mode, int failedCount) => mode switch
    {
        NotifyMode.Always => true,
        NotifyMode.FailuresOnly => failedCount > 0,
        _ => false,
    };
}
