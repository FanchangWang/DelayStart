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
}
