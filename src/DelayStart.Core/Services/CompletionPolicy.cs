using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 调度收尾时的退出模式（UI v2 批复 2026-09-21 / D71–D73）。
/// </summary>
/// <remarks>
/// N4/N5（2026-09-22 批复，<c>design.md</c> FR-14.1）后语义收窄：
/// <see cref="WaitForPanelClose"/> 只剩"面板是用户手动打开的（或收尾动作由面板发起）"这一种来源 ——
/// 收尾**不再**按通知策略自动弹面板（面板与通知彻底分离）。
/// </remarks>
public enum CompletionExitMode
{
    /// <summary>把完成面板留在屏幕上，等倒计时归零或失焦关闭之后再退出。</summary>
    WaitForPanelClose,

    /// <summary>不碰面板，直接退出（通知照按策略发，见 <see cref="NotifyDecision"/>）。</summary>
    QuitImmediately,
}

/// <summary><see cref="CompletionPolicy.Decide"/> 的输入。</summary>
/// <param name="QuitImmediately">调用方显式要求立即退出（菜单「跳过剩余任务并退出」）。</param>
/// <param name="PanelRequestedCompletion">本次收尾动作由面板上的按钮发起。</param>
/// <param name="PanelVisible">面板此刻正显示在屏幕上。</param>
/// <remarks>
/// 🔴 N2–N5（2026-09-22 批复）把 <see cref="NotifyMode"/> 与 <c>FailedCount</c>
/// 从输入里**移除**了：通知（发不发系统通知）由 <see cref="NotifyDecision"/> 单独判定，
/// 收尾（等不等面板）不再读通知策略 —— "面板是面板，通知是通知"。
/// D71–D73 时代"面板可见性优先于通知策略"的规则随之自然消亡：
/// 策略已经管不到面板了。
/// </remarks>
public sealed record CompletionPolicyInput(
    bool QuitImmediately,
    bool PanelRequestedCompletion,
    bool PanelVisible);

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
/// 通知策略三个来源拼成，D71–D73 连续三个缺陷都出在这一处，
/// 而调度端是 NativeAOT 的 WinExe、没有单元测试工程。移到 Core 后判定表可逐行覆盖。
/// </para>
/// <para>
/// 🔴 **核心语义（N4，2026-09-22 批复）：面板只跟随用户的手 —— 手动打开过
/// （或收尾动作由面板发起）就给完成态 + 倒计时；否则调度端发完通知直接退出。**
/// 通知策略已不再参与本判定（历史版本里它决定"要不要主动弹面板"，
/// 那条规则随"通知与面板分离"一起废除，见 <c>design.md</c> FR-14.1）。
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
    /// <c>QuitImmediately</c> → <c>PanelRequestedCompletion</c> → <c>PanelVisible</c> → 其余。
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
            // 通知仍按策略发（D2 批复 A：结果已归档，与"调度结束按策略通知"一致）。
            return new CompletionDecision(CompletionExitMode.QuitImmediately);
        }

        if (input.PanelRequestedCompletion || input.PanelVisible)
        {
            // 面板发起，或面板已经在屏幕上 —— 用户在看，必须给结果（切完成态 + 倒计时）。
            return new CompletionDecision(CompletionExitMode.WaitForPanelClose);
        }

        // 其余：不碰面板，直接退出。要不要发系统通知由 NotifyDecision 另行判定。
        return new CompletionDecision(CompletionExitMode.QuitImmediately);
    }
}
