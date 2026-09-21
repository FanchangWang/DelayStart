using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="SkipPolicy"/> 的单元测试：状态 × 入口的可跳判定（D72 按入口分流）。
/// </summary>
public sealed class SkipPolicyTests
{
    [Theory]
    [InlineData(RunItemState.Waiting, SkipEntry.Panel, true)]
    [InlineData(RunItemState.Waiting, SkipEntry.TrayMenu, true)]
    [InlineData(RunItemState.Launching, SkipEntry.Panel, false)]
    [InlineData(RunItemState.Launching, SkipEntry.TrayMenu, true)]
    [InlineData(RunItemState.Done, SkipEntry.Panel, false)]
    [InlineData(RunItemState.Done, SkipEntry.TrayMenu, false)]
    [InlineData(RunItemState.Failed, SkipEntry.Panel, false)]
    [InlineData(RunItemState.Failed, SkipEntry.TrayMenu, false)]
    [InlineData(RunItemState.Skipped, SkipEntry.Panel, false)]
    [InlineData(RunItemState.Skipped, SkipEntry.TrayMenu, false)]
    public void Decide_StateAndEntry_ReturnsExpected(RunItemState state, SkipEntry entry, bool expected)
    {
        var decision = SkipPolicy.Decide(state, entry);

        Assert.Equal(expected, decision.CanSkip);
    }

    [Fact]
    public void Decide_Launching_DiffersBetweenEntries()
    {
        // 唯一在两个入口下结论不同的状态 —— 菜单要"跳过并走人"（不等复查窗口），
        // 面板要等复查出结果给用户看。
        Assert.False(SkipPolicy.Decide(RunItemState.Launching, SkipEntry.Panel).CanSkip);
        Assert.True(SkipPolicy.Decide(RunItemState.Launching, SkipEntry.TrayMenu).CanSkip);
    }

    [Fact]
    public void Decide_FinishedStates_AreNeverSkippable()
    {
        // 已完成/已失败/已跳过都不是"剩余条目"，重复跳过会污染运行归档
        foreach (var state in new[] { RunItemState.Done, RunItemState.Failed, RunItemState.Skipped })
        {
            foreach (var entry in new[] { SkipEntry.Panel, SkipEntry.TrayMenu })
            {
                Assert.False(SkipPolicy.Decide(state, entry).CanSkip);
            }
        }
    }
}
