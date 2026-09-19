using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="DelayCalculator"/> 的单元测试：时序计算、排序前置条件、驻留估算（FR-5.3 / FR-5.4 / FR-9.4）。
/// </summary>
public sealed class DelayCalculatorTests
{
    [Fact]
    public void Remaining_DelayNotElapsed_ReturnsPositiveRemainder()
    {
        // Arrange
        const int delaySeconds = 30;
        var elapsed = TimeSpan.FromSeconds(10);

        // Act
        var remaining = DelayCalculator.Remaining(delaySeconds, elapsed);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(20), remaining);
    }

    [Fact]
    public void Remaining_DelayExactlyElapsed_ReturnsZero()
    {
        // Arrange / Act
        var remaining = DelayCalculator.Remaining(30, TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Fact]
    public void Remaining_DelayAlreadyElapsed_ReturnsNegative()
    {
        // Arrange：调度端启动时某项延时已过期（E7）
        var remaining = DelayCalculator.Remaining(30, TimeSpan.FromSeconds(45));

        // Act / Assert
        Assert.Equal(TimeSpan.FromSeconds(-15), remaining);
    }

    [Theory]
    [InlineData(30, 30.0)]
    [InlineData(30, 45.0)]
    [InlineData(0, 0.0)]
    [InlineData(0, 0.001)]
    [InlineData(86400, 86400.0)]
    public void IsDue_AtOrAfterDeadline_ReturnsTrue(int delaySeconds, double elapsedSeconds)
    {
        // Arrange / Act
        var isDue = DelayCalculator.IsDue(delaySeconds, TimeSpan.FromSeconds(elapsedSeconds));

        // Assert
        Assert.True(isDue);
    }

    [Theory]
    [InlineData(30, 29.999)]
    [InlineData(1, 0.0)]
    public void IsDue_BeforeDeadline_ReturnsFalse(int delaySeconds, double elapsedSeconds)
    {
        // Arrange / Act
        var isDue = DelayCalculator.IsDue(delaySeconds, TimeSpan.FromSeconds(elapsedSeconds));

        // Assert
        Assert.False(isDue);
    }

    [Fact]
    public void Remaining_LateItem_IsAbsoluteFromRunStartNotCumulative()
    {
        // 机制 5 的核心：延时是相对**登录时刻**的绝对时间点。
        // 若实现成"前一个跑完再等 N 秒"，已过 55 秒时第三个条目（60 秒）就不该只剩 5 秒。
        // 这条断言就是用来钉死这个语义的 —— 它一旦失败，说明有人把时序改成了累加。
        var remaining = DelayCalculator.Remaining(60, TimeSpan.FromSeconds(55));

        Assert.Equal(TimeSpan.FromSeconds(5), remaining);
    }

    [Fact]
    public void EstimateResidency_NoItems_ReturnsZero()
    {
        // 没有任何有效条目时调度端静默退出，不驻留（FR-5.10 / E14）
        var residency = DelayCalculator.EstimateResidency([], trayKeepSeconds: 3);

        Assert.Equal(TimeSpan.Zero, residency);
    }

    [Fact]
    public void EstimateResidency_UsesMaxDelayNotSum()
    {
        // Arrange
        int[] delays = [10, 30, 120, 60];

        // Act
        var residency = DelayCalculator.EstimateResidency(delays, trayKeepSeconds: 3);

        // Assert
        var expected = TimeSpan.FromSeconds(120)
            + TimeSpan.FromSeconds(3)
            + DelayCalculator.StartupOverhead;

        Assert.Equal(expected, residency);
        Assert.True(
            residency < TimeSpan.FromSeconds(10 + 30 + 120 + 60),
            "驻留时长取最大延时，不应是各延时之和");
    }

    [Fact]
    public void EstimateResidency_AllZeroDelays_StillIncludesTrayKeepAndOverhead()
    {
        // 全部条目延时为 0 时仍然要驻留：要显示托盘、要等 1.5 秒复查、要写归档
        var residency = DelayCalculator.EstimateResidency([0, 0], trayKeepSeconds: 3);

        Assert.Equal(TimeSpan.FromSeconds(3) + DelayCalculator.StartupOverhead, residency);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public void EstimateResidency_NegativeTrayKeep_TreatedAsZero(int trayKeepSeconds)
    {
        // Arrange / Act
        var residency = DelayCalculator.EstimateResidency([10], trayKeepSeconds);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(10) + DelayCalculator.StartupOverhead, residency);
    }

    [Fact]
    public void EstimateResidency_NegativeDelay_ClampedToZero()
    {
        // 非法延时值不应该把展示用的驻留时长算成负数
        var residency = DelayCalculator.EstimateResidency([-100], trayKeepSeconds: 3);

        Assert.Equal(TimeSpan.FromSeconds(3) + DelayCalculator.StartupOverhead, residency);
    }

    [Fact]
    public void EstimateResidency_NullDelays_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => DelayCalculator.EstimateResidency(null!, trayKeepSeconds: 3));
    }

    [Fact]
    public void ShouldShowTrayIcon_ResidencyBelow15Seconds_ReturnsFalse()
    {
        // FR-6.1：预计驻留 < 15 秒不显示托盘图标（5 + 3 + 2 = 10 秒）
        var shouldShow = DelayCalculator.ShouldShowTrayIcon([5], trayKeepSeconds: 3);

        Assert.False(shouldShow);
    }

    [Fact]
    public void ShouldShowTrayIcon_ResidencyExactly15Seconds_ReturnsTrue()
    {
        // 边界：10 + 3 + 2 = 15，达到阈值即显示
        var shouldShow = DelayCalculator.ShouldShowTrayIcon([10], trayKeepSeconds: 3);

        Assert.True(shouldShow);
    }

    [Fact]
    public void ShouldShowTrayIcon_NoItems_ReturnsFalse()
    {
        var shouldShow = DelayCalculator.ShouldShowTrayIcon([], trayKeepSeconds: 3);

        Assert.False(shouldShow);
    }
}
