using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ScheduleCycleResolver"/> 的单元测试（§4.2 / FR-15.15）。
/// </summary>
/// <remarks>
/// 这里守的是一条单行规则：**解析不出来就往"每天"兜，绝不往"不启动"兜。**
/// 引用失效（手改配置、备份还原不一致）之后让条目静默地永远不启动，是本项目最难排查的那一类失败。
/// </remarks>
public sealed class ScheduleCycleResolverTests
{
    private static readonly List<ScheduleCycle> Cycles =
    [
        new() { Id = "c-a1", Name = "上一休一", Days = WeekdaySet.Monday | WeekdaySet.Wednesday | WeekdaySet.Friday },
        new() { Id = "c-b2", Name = "周末班", Days = WeekdaySet.Saturday | WeekdaySet.Sunday },
    ];

    [Theory]
    [InlineData(BuiltinCycleIds.Everyday, ScheduleRuleKind.Everyday)]
    [InlineData(BuiltinCycleIds.Weekdays, ScheduleRuleKind.Weekdays)]
    [InlineData(BuiltinCycleIds.Weekends, ScheduleRuleKind.Weekends)]
    [InlineData(BuiltinCycleIds.LegalWorkday, ScheduleRuleKind.LegalWorkday)]
    [InlineData(BuiltinCycleIds.LegalHoliday, ScheduleRuleKind.LegalHoliday)]
    public void Resolve_BuiltinIds_MapToKind(string cycleId, ScheduleRuleKind expected)
    {
        var resolved = ScheduleCycleResolver.Resolve(cycleId, Cycles);

        Assert.Equal(expected, resolved.Kind);
        Assert.True(resolved.Found);
    }

    [Fact]
    public void Resolve_CustomId_MapsToCustomWithDays()
    {
        var resolved = ScheduleCycleResolver.Resolve("c-a1", Cycles);

        Assert.Equal(ScheduleRuleKind.Custom, resolved.Kind);
        Assert.Equal(WeekdaySet.Monday | WeekdaySet.Wednesday | WeekdaySet.Friday, resolved.Days);
        Assert.True(resolved.Found);
    }

    [Fact]
    public void Resolve_UnknownId_FallsBackToEverydayAndReportsNotFound()
    {
        var resolved = ScheduleCycleResolver.Resolve("c-does-not-exist", Cycles);

        Assert.Equal(ScheduleRuleKind.Everyday, resolved.Kind);
        Assert.False(resolved.Found);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyId_TreatedAsEveryday(string? cycleId)
    {
        var resolved = ScheduleCycleResolver.Resolve(cycleId, Cycles);

        // 没配 = 默认每天，且**不算引用失效**（老配置升级就是这种形态，不值得报警）。
        Assert.Equal(ScheduleRuleKind.Everyday, resolved.Kind);
        Assert.True(resolved.Found);
    }

    [Fact]
    public void Resolve_NullCycleTable_StillResolvesBuiltin()
    {
        var resolved = ScheduleCycleResolver.Resolve(BuiltinCycleIds.Weekdays, null);

        Assert.Equal(ScheduleRuleKind.Weekdays, resolved.Kind);
        Assert.True(resolved.Found);
    }

    [Fact]
    public void Resolve_HandWrittenEmptyCycle_TreatedAsMissing()
    {
        // 手改配置把 Days 写成 0：这不是"永不启动"的意思，等于"这条定义坏了" ——
        // 走与引用失效相同的兜底，避免引入第三种语义。
        var cycles = new List<ScheduleCycle> { new() { Id = "c-empty", Days = WeekdaySet.None } };

        var resolved = ScheduleCycleResolver.Resolve("c-empty", cycles);

        Assert.Equal(ScheduleRuleKind.Everyday, resolved.Kind);
        Assert.False(resolved.Found);
    }

    [Fact]
    public void Resolve_CycleWithOutOfRangeBits_Sanitizes()
    {
        var cycles = new List<ScheduleCycle>
        {
            new() { Id = "c-dirty", Days = (WeekdaySet)((1 << 7) | (int)WeekdaySet.Monday) },
        };

        var resolved = ScheduleCycleResolver.Resolve("c-dirty", cycles);

        Assert.Equal(WeekdaySet.Monday, resolved.Days);
    }

    [Fact]
    public void Resolve_DuplicateIds_FirstOneWins()
    {
        var cycles = new List<ScheduleCycle>
        {
            new() { Id = "c-dup", Days = WeekdaySet.Monday },
            new() { Id = "c-dup", Days = WeekdaySet.Tuesday },
        };

        var resolved = ScheduleCycleResolver.Resolve("c-dup", cycles);

        Assert.Equal(WeekdaySet.Monday, resolved.Days);
    }

    [Fact]
    public void IsBuiltin_OnlyRecognizesFiveLiterals()
    {
        Assert.True(ScheduleCycleResolver.IsBuiltin(BuiltinCycleIds.Everyday));
        Assert.True(ScheduleCycleResolver.IsBuiltin(BuiltinCycleIds.LegalHoliday));
        Assert.False(ScheduleCycleResolver.IsBuiltin("c-a1"));
        Assert.False(ScheduleCycleResolver.IsBuiltin(null));
        Assert.False(ScheduleCycleResolver.IsBuiltin("b:custom"));
    }

    [Fact]
    public void CountReferences_CountsItemsPointingAtCycle()
    {
        var items = new List<DelayedItem>
        {
            new() { Id = "1", ScheduleCycleId = "c-a1" },
            new() { Id = "2", ScheduleCycleId = "c-a1" },
            new() { Id = "3", ScheduleCycleId = BuiltinCycleIds.Everyday },
        };

        Assert.Equal(2, ScheduleCycleResolver.CountReferences("c-a1", items));
        Assert.Equal(0, ScheduleCycleResolver.CountReferences("c-b2", items));
        Assert.Equal(0, ScheduleCycleResolver.CountReferences(null, items));
        Assert.Equal(0, ScheduleCycleResolver.CountReferences("c-a1", null));
    }
}
