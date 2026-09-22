using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="GuardSchedulePlan"/> 的单元测试（D74）：档位 → 触发规则，以及非法档位的收拢。
/// </summary>
/// <remarks>
/// 这段逻辑"算错了也不报错，只会静默不跑"（守卫根本不启动），所以必须逐档断言。
/// </remarks>
public sealed class GuardSchedulePlanTests
{
    [Fact]
    public void Build_Disabled_ReturnsNull()
    {
        // null 的语义是"该删除守卫计划任务"。
        Assert.Null(GuardSchedulePlan.Build(GuardMode.Disabled, 30));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public void Build_OnceAfterLogin_DelaysAndNeverRepeats(int minutes)
    {
        var trigger = GuardSchedulePlan.Build(GuardMode.OnceAfterLogin, minutes);

        Assert.NotNull(trigger);
        Assert.Equal(TimeSpan.FromMinutes(minutes), trigger.InitialDelay);
        Assert.Null(trigger.RepeatInterval);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public void Build_Periodic_DelaysFirstThenRepeatsAtSameInterval(int minutes)
    {
        var trigger = GuardSchedulePlan.Build(GuardMode.Periodic, minutes);

        Assert.NotNull(trigger);
        // 第一次也延迟：登录瞬间是磁盘与 CPU 最拥挤的窗口，守卫不该去挤那一段。
        Assert.Equal(TimeSpan.FromMinutes(minutes), trigger.InitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(minutes), trigger.RepeatInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(120)]
    [InlineData(-30)]
    public void Build_IllegalMinutes_FallsBackToDefault(int minutes)
    {
        // 越界值会直接变成计划任务的重复周期 —— 0 或负数让任务行为不可预测（可能反复触发）。
        var trigger = GuardSchedulePlan.Build(GuardMode.Periodic, minutes);

        Assert.NotNull(trigger);
        Assert.Equal(TimeSpan.FromMinutes(GuardPresets.DefaultMinutes), trigger.InitialDelay);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    public void NormalizeMinutes_LegalValues_AreKept(int input, int expected)
    {
        Assert.Equal(expected, GuardSchedulePlan.NormalizeMinutes(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(int.MaxValue)]
    public void NormalizeMinutes_IllegalValues_ReturnDefault(int input)
    {
        Assert.Equal(GuardPresets.DefaultMinutes, GuardSchedulePlan.NormalizeMinutes(input));
    }

    [Fact]
    public void Defaults_MatchUserDecision()
    {
        // 2026-09-22 用户批复：默认 OnceAfterLogin / 30 分钟。
        var settings = new Settings();

        Assert.Equal(GuardMode.OnceAfterLogin, settings.GuardMode);
        Assert.Equal(30, settings.GuardMinutes);
        Assert.Equal(GuardPresets.DefaultMinutes, settings.GuardMinutes);
    }
}
