using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="CompletionPolicy"/> 的单元测试。
/// </summary>
/// <remarks>
/// 🔴 判定表是**需求变更**后重写的（N2–N5，2026-09-22 批复，<c>design.md</c> FR-14.1）：
/// <see cref="NotifyMode"/> 与失败数已从输入移除（通知归 <see cref="NotifyDecision"/> 管），
/// 判定表退化为三行：QuitImmediately → 面板发起 → 面板已显示 → 其余。
/// 旧表（D71–D73 六行）的测试随旧表一起作废；D71–D73 的历史引用保留在实现注释里。
/// </remarks>
public sealed class CompletionPolicyTests
{
    private static CompletionDecision Decide(
        bool quitImmediately = false,
        bool panelRequested = false,
        bool panelVisible = false)
        => CompletionPolicy.Decide(new CompletionPolicyInput(
            quitImmediately, panelRequested, panelVisible));

    // ---- 第 1 行：显式立即退出压过一切 ----

    [Fact]
    public void Decide_QuitImmediately_OutranksEveryOtherCondition()
    {
        var decision = Decide(
            quitImmediately: true,
            panelRequested: true,
            panelVisible: true);

        Assert.Equal(CompletionExitMode.QuitImmediately, decision.ExitMode);
    }

    // ---- 第 2 行：面板发起（收尾动作由面板按钮触发） ----

    [Fact]
    public void Decide_PanelRequestedCompletion_WaitsForPanelClose()
    {
        var decision = Decide(panelRequested: true);

        Assert.Equal(CompletionExitMode.WaitForPanelClose, decision.ExitMode);
    }

    // ---- 第 3 行：面板已显示（D73 的核心：用户在看就必须给结果） ----

    [Fact]
    public void Decide_PanelVisible_WaitsForPanel()
    {
        var decision = Decide(panelVisible: true);

        Assert.Equal(CompletionExitMode.WaitForPanelClose, decision.ExitMode);
    }

    // ---- 第 4 行：其余情况直接退（面板与通知分离后的新默认） ----

    [Fact]
    public void Decide_NoPanelInvolved_QuitsImmediately()
    {
        // 无论成败多少：通知策略已不参与收尾判定，没面板就发完通知直接退。
        var decision = Decide();

        Assert.Equal(CompletionExitMode.QuitImmediately, decision.ExitMode);
    }

    [Fact]
    public void Decide_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CompletionPolicy.Decide(null!));
    }
}
