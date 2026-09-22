using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="NotifyDecision"/> 的单元测试（N2，2026-09-22 批复）：
/// 通知策略三档 × 有/无失败。
/// </summary>
public sealed class NotifyDecisionTests
{
    [Theory]
    [InlineData(NotifyMode.FailuresOnly, 0, false)]
    [InlineData(NotifyMode.FailuresOnly, 1, true)]
    [InlineData(NotifyMode.FailuresOnly, 5, true)]
    [InlineData(NotifyMode.Always, 0, true)]
    [InlineData(NotifyMode.Always, 3, true)]
    [InlineData(NotifyMode.Never, 0, false)]
    [InlineData(NotifyMode.Never, 2, false)]
    public void Decide_CoversAllPolicyRows(NotifyMode mode, int failedCount, bool expected)
    {
        Assert.Equal(expected, NotifyDecision.Decide(mode, failedCount));
    }

    [Fact]
    public void Decide_EmptyPlan_FollowsPolicyWithoutSpecialCase()
    {
        // D4 批复 A：空计划无特判 —— Always 照样发，Never 照样不发。
        Assert.True(NotifyDecision.Decide(NotifyMode.Always, failedCount: 0));
        Assert.False(NotifyDecision.Decide(NotifyMode.Never, failedCount: 0));
    }
}
