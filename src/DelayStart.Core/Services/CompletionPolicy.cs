using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 调度收尾时的退出模式（UI v2 批复 2026-09-21 / D71–D73）。
/// </summary>
public enum CompletionExitMode
{
    /// <summary>把完成面板留在屏幕上，等倒计时归零或用户点 ✕ 之后再退出。</summary>
    WaitForPanelClose,

    /// <summary>不弹面板，直接退出。</summary>
    QuitImmediately,
}

/// <summary><see cref="CompletionPolicy.Decide"/> 的输入。</summary>
/// <param name="QuitImmediately">调用方显式要求立即退出（菜单「跳过剩余任务并退出」）。</param>
/// <param name="PanelRequestedCompletion">本次收尾动作由面板上的按钮发起。</param>
/// <param name="PanelVisible">面板此刻正显示在屏幕上。</param>
/// <param name="FailedCount">本批次失败条目数。</param>
/// <param name="NotifyMode">管理端设置的通知策略。</param>
public sealed record CompletionPolicyInput(
    bool QuitImmediately,
    bool PanelRequestedCompletion,
    bool PanelVisible,
    int FailedCount,
    NotifyMode NotifyMode);

/// <summary>收尾决策。</summary>
/// <param name="ExitMode">
/// 退出模式。**只保留这一个字段** —— "要不要显示面板"可由它完全推导
/// （<see cref="CompletionExitMode.WaitForPanelClose"/> 必然显示），
/// 单字段即不存在非法组合。
/// </param>
public sealed record CompletionDecision(CompletionExitMode ExitMode);

/// <summary>
/// 调度收尾判据：决定「要不要把完成面板留在屏幕上等用户关闭」。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来的理由：原判据由调度端的 <c>_panelRequestedCompletion</c>、面板可见性与
/// <see cref="NotifyMode"/> 三个来源拼成，D71–D73 连续三个缺陷都出在这一处，
/// 而调度端是 NativeAOT 的 WinExe、没有单元测试工程。移到 Core 后判定表可逐行覆盖。
/// </para>
/// <para>
/// 🔴 **核心语义：<see cref="NotifyMode"/> 只决定"要不要主动弹面板"，不决定"已显示的面板要不要留"。**
/// 用户手动打开过面板就该看到结果，否则面板会在全部成功后毫无理由地自己消失（D73 实测）。
/// 这正是输入中的 <c>PanelVisible</c> 优先级高于通知策略的原因。
/// </para>
/// <para>
/// 不含倒计时时长（成功 10 秒 / 失败 60 秒）、hover 暂停、面板文案与托盘图标 ——
/// 那些是 <c>PanelWindow</c> 的职责。策略只回答"等不等面板"，不回答"等多久"。
/// </para>
/// </remarks>
public static class CompletionPolicy
{
    /// <summary>
    /// 按判定表给出收尾决策，首个命中的条件生效：
    /// <c>QuitImmediately</c> → <c>PanelRequestedCompletion</c> → <c>PanelVisible</c>
    /// → <c>Always</c> → <c>FailuresOnly 且有失败</c> → 其余。
    /// </summary>
    /// <param name="input">收尾判据的全部输入。</param>
    /// <returns>退出模式决策。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <see langword="null"/>。</exception>
    public static CompletionDecision Decide(CompletionPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.QuitImmediately)
        {
            // 菜单「跳过剩余任务并退出」：用户明确表示不看结果，压过下面所有条件。
            return new CompletionDecision(CompletionExitMode.QuitImmediately);
        }

        if (input.PanelRequestedCompletion || input.PanelVisible)
        {
            // 面板发起，或面板已经在屏幕上 —— 通知策略管不到已弹出的面板。
            return new CompletionDecision(CompletionExitMode.WaitForPanelClose);
        }

        var policyWantsPanel = input.NotifyMode switch
        {
            NotifyMode.Always => true,
            NotifyMode.FailuresOnly => input.FailedCount > 0,
            _ => false,
        };

        return new CompletionDecision(
            policyWantsPanel ? CompletionExitMode.WaitForPanelClose : CompletionExitMode.QuitImmediately);
    }
}
