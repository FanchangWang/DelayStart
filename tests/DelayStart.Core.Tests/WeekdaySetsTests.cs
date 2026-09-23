using DelayStart.Core.Models;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="WeekdaySets"/> 的逐位测试。
/// </summary>
/// <remarks>
/// <para>
/// 这组断言存在的全部理由就是一个坑：**.NET 的 <c>DayOfWeek.Sunday == 0</c>**，
/// 而本项目的掩码位序从周一起头（bit0）。两种顺序混用会产生"每个星期都成立"的错位，
/// 既不抛异常也不出现在日志里。所以在三个方向上各锁一遍：
/// ① 掩码 → DayOfWeek；② DayOfWeek → 掩码；③ 两者的包含关系。
/// </para>
/// </remarks>
public sealed class WeekdaySetsTests
{
    [Theory]
    [InlineData(DayOfWeek.Monday, WeekdaySet.Monday)]
    [InlineData(DayOfWeek.Tuesday, WeekdaySet.Tuesday)]
    [InlineData(DayOfWeek.Wednesday, WeekdaySet.Wednesday)]
    [InlineData(DayOfWeek.Thursday, WeekdaySet.Thursday)]
    [InlineData(DayOfWeek.Friday, WeekdaySet.Friday)]
    [InlineData(DayOfWeek.Saturday, WeekdaySet.Saturday)]
    [InlineData(DayOfWeek.Sunday, WeekdaySet.Sunday)]
    public void ToFlag_MapsEveryDayWithoutOffset(DayOfWeek day, WeekdaySet expected)
    {
        Assert.Equal(expected, WeekdaySets.ToFlag(day));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, WeekdaySet.Monday)]
    [InlineData(DayOfWeek.Tuesday, WeekdaySet.Tuesday)]
    [InlineData(DayOfWeek.Wednesday, WeekdaySet.Wednesday)]
    [InlineData(DayOfWeek.Thursday, WeekdaySet.Thursday)]
    [InlineData(DayOfWeek.Friday, WeekdaySet.Friday)]
    [InlineData(DayOfWeek.Saturday, WeekdaySet.Saturday)]
    [InlineData(DayOfWeek.Sunday, WeekdaySet.Sunday)]
    public void Contains_IsTrueOnlyForIncludedDay(DayOfWeek day, WeekdaySet expectedFlag)
    {
        var set = WeekdaySet.Monday | WeekdaySet.Wednesday | WeekdaySet.Friday;

        Assert.Equal((set & expectedFlag) != 0, WeekdaySets.Contains(set, day));
    }

    [Fact]
    public void ToFlag_UnknownDayOfWeek_ReturnsNoneNotThrow()
    {
        Assert.Equal(WeekdaySet.None, WeekdaySets.ToFlag((DayOfWeek)99));
    }

    [Fact]
    public void Sanitize_ClearsBitsOutsideSevenDays()
    {
        // 手改配置写出 0xFFFFFFFF 这种脏值时，越界位必须清掉而不是参与判定。
        Assert.Equal(WeekdaySet.All, WeekdaySets.Sanitize((WeekdaySet)unchecked((int)0xFFFFFFFF)));
        Assert.Equal(WeekdaySet.None, WeekdaySets.Sanitize((WeekdaySet)(1 << 7)));
    }

    [Theory]
    [InlineData(WeekdaySet.None, 0)]
    [InlineData(WeekdaySet.Monday, 1)]
    [InlineData(WeekdaySet.Monday | WeekdaySet.Friday, 2)]
    [InlineData(WeekdaySet.All, 7)]
    public void Count_ReturnsPopulatedBitCount(WeekdaySet set, int expected)
    {
        Assert.Equal(expected, WeekdaySets.Count(set));
    }

    [Fact]
    public void All_HasExactlySevenBits()
    {
        Assert.Equal(0x7F, (int)WeekdaySet.All);
    }
}
