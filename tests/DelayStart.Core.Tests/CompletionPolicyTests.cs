using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="CompletionPolicy"/> 的单元测试：收尾判定表六行逐行覆盖（D71–D73）。
/// </summary>
public sealed class CompletionPolicyTests
{
    private static CompletionDecision Decide(
        bool quitImmediately = false,
        bool panelRequested = false,
        bool panelVisible = false,
        int failedCount = 0,
        NotifyMode notifyMode = NotifyMode.FailuresOnly)
        => CompletionPolicy.Decide(new CompletionPolicyInput(
            quitImmediately, panelRequested, panelVisible, failedCount, notifyMode));

    // ---- 第 1 行：显式立即退出压过一切 ----

    [Fact]
    public void Decide_QuitImmediately_OutranksEveryOtherCondition()
    {
        var decision = Decide(
            quitImmediately: true,
            panelRequested: true,
            panelVisible: true,
            failedCount: 3,
            notifyMode: NotifyMode.Always);

        Assert.Equal(CompletionExitMode.QuitImmediately, decision.ExitMode);
    }

    // ---- 第 2 行：面板发起 ----

    [Fact]
    public void Decide_PanelRequestedCompletion_WaitsForPanelClose()
    {
        var decision = Decide(panelRequested: true);

        Assert.Equal(CompletionExitMode.WaitForPanelClose, decision.ExitMode);
    }

    // ---- 第 3 行：面板已显示（D73 的核心） ----

    [Fact]
    public void Decide_PanelVisible_WaitsForPanelEvenWhenPolicyWouldNotShowOne()
    {
        // FailuresOnly + 无失败：策略判定"不弹"，但面板已经开着就不能关掉它
        var decision = Decide(panelVisible: true, failedCount: 0, notifyMode: NotifyMode.FailuresOnly);

        Assert.Equal(CompletionExitMode.WaitForPanelClose, decision.ExitMode);
    }

    [Fact]
    public void Decide_PanelVisible_WaitsForPanelEvenWhenNotifyModeIsNever()
    {
        var decision = Decide(panelVisible: true, notifyMode: NotifyMode.Never);

        Assert.Equal(CompletionExitMode.WaitForPanelClose, decision.ExitMode);
    }

    // ---- 第 4 行：Always ----

    [Fact]
    public void Decide_NotifyModeAlways_WaitsForPanelEvenWithoutFailures()
    {
        var decision = Decide(failedCount: 0, notifyMode: NotifyMode.Always);

        Assert.Equal(CompletionExitMode.WaitForPanelClose, decision.ExitMode);
    }

    // ---- 第 5 行：FailuresOnly 且有失败 ----

    [Fact]
    public void Decide_FailuresOnlyWithFailures_WaitsForPanelClose()
    {
        var decision = Decide(failedCount: 1);

        Assert.Equal(CompletionExitMode.WaitForPanelClose, decision.ExitMode);
    }

    // ---- 第 6 行：其余情况直接退 ----

    [Fact]
    public void Decide_FailuresOnlyWithoutFailures_QuitsImmediately()
    {
        // 也是"全部条件都不成立"的默认路径
        var decision = Decide();

        Assert.Equal(CompletionExitMode.QuitImmediately, decision.ExitMode);
    }

    [Fact]
    public void Decide_NotifyModeNever_QuitsImmediatelyEvenWithFailures()
    {
        var decision = Decide(failedCount: 2, notifyMode: NotifyMode.Never);

        Assert.Equal(CompletionExitMode.QuitImmediately, decision.ExitMode);
    }

    [Fact]
    public void Decide_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CompletionPolicy.Decide(null!));
    }
}
