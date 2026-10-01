using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="SkipPolicy"/> 的单元测试：按状态判可跳（v0.6.1 起不再按入口分流）。
/// </summary>
public sealed class SkipPolicyTests
{
    [Theory]
    [InlineData(RunItemState.Waiting, true)]
    [InlineData(RunItemState.Launching, true)]
    [InlineData(RunItemState.Done, false)]
    [InlineData(RunItemState.Failed, false)]
    [InlineData(RunItemState.Skipped, false)]
    public void Decide_State_ReturnsExpected(RunItemState state, bool expected)
    {
        var decision = SkipPolicy.Decide(state);

        Assert.Equal(expected, decision.CanSkip);
    }

    [Fact]
    public void Decide_Launching_IsSkippable_BecauseMenuMeansSkipAndLeave()
    {
        // 菜单语义是"跳过并走人"：用户不想等那 1.5 秒复查窗口。
        // 代价是这些条目拿不到真实成败判定 —— 那是这条策略明确接受的取舍。
        //
        // 🔴 这条断言原先是"面板下不可跳 / 菜单下可跳"的对比（面板要等复查出结果给用户看）。
        // 面板取消后对比消失，只剩菜单这一侧，结论与 SkipPolicy 的 remarks 一致。
        Assert.True(SkipPolicy.Decide(RunItemState.Launching).CanSkip);
    }

    [Fact]
    public void Decide_FinishedStates_AreNeverSkippable()
    {
        // 已完成/已失败/已跳过都不是"剩余条目"，重复跳过会污染运行归档
        foreach (var state in new[] { RunItemState.Done, RunItemState.Failed, RunItemState.Skipped })
        {
            Assert.False(SkipPolicy.Decide(state).CanSkip);
        }
    }
}
