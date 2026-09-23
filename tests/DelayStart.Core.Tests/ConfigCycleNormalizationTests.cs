using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// 周期数据进 <c>config.json</c> 的规范化（FR-15.7 / FR-15.20）。
/// </summary>
/// <remarks>
/// <para>
/// 配置文件是给人手改的。配置一旦被人手改，就可能出现：周期表为 <c>null</c>、id 重复、
/// 星期掩码带着 <c>0x80</c> 这种脏位、条目上的引用写成空白。这些都必须**收敛成合法值**，
/// 而不是让管理端在渲染时才崩。
/// </para>
/// <para>
/// 反向的取舍同样重要：规范化**不删**任何一条周期定义，哪怕是空的。
/// 删掉等于替用户做决定；无效定义的兜底交给 <see cref="ScheduleCycleResolver"/>。
/// </para>
/// </remarks>
public sealed class ConfigCycleNormalizationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    private ConfigService Service =>
        new(new PathService(_temp.Combine("local"), _temp.Combine("config")), new FakeLogSink(), new FakeClock());

    [Fact]
    public void SaveThenLoad_RoundTripsCycles()
    {
        var service = Service;
        var config = new AppConfig();
        config.Cycles.Add(new ScheduleCycle
        {
            Id = "c-a1",
            Name = "上一休一",
            Days = WeekdaySet.Monday | WeekdaySet.Wednesday | WeekdaySet.Friday,
        });

        service.Save(config);

        var loaded = service.Load();
        var cycle = Assert.Single(loaded.Cycles);
        Assert.Equal("c-a1", cycle.Id);
        Assert.Equal("上一休一", cycle.Name);
        Assert.Equal(WeekdaySet.Monday | WeekdaySet.Wednesday | WeekdaySet.Friday, cycle.Days);
    }

    [Fact]
    public void Load_MissingCyclesNode_GivesEmptyList()
    {
        var config = Service.Load();

        Assert.NotNull(config.Cycles);
        Assert.Empty(config.Cycles);
    }

    [Fact]
    public void Save_NormalizesCycleFields()
    {
        var service = Service;
        var config = new AppConfig();
        config.Cycles.Add(new ScheduleCycle
        {
            Id = "  c-dirty  ",
            Name = "脏数据",
            Days = (WeekdaySet)((1 << 7) | (int)WeekdaySet.Friday),   // 越界位 + 周五
        });

        service.Save(config);

        var cycle = Assert.Single(service.Load().Cycles);
        Assert.Equal("c-dirty", cycle.Id);          // 两端空白被去掉
        Assert.Equal(WeekdaySet.Friday, cycle.Days); // 越界位清零
    }

    [Fact]
    public void Save_GeneratesIdWhenMissing()
    {
        var service = Service;
        var config = new AppConfig();
        config.Cycles.Add(new ScheduleCycle { Name = "没 id 的周期" });
        config.Cycles.Add(new ScheduleCycle { Id = "   ", Name = "空白 id" });

        service.Save(config);

        var ids = service.Load().Cycles.Select(static cycle => cycle.Id).ToArray();
        Assert.All(ids, static id => Assert.StartsWith(BuiltinCycleIds.CustomPrefix, id, StringComparison.Ordinal));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Save_DeDuplicatesIdenticalIds()
    {
        // 同 id 只有第一条能被解析到，第二条等于被悄悄屏蔽 —— 重发 id 比留着好。
        var service = Service;
        var config = new AppConfig();
        config.Cycles.Add(new ScheduleCycle { Id = "c-same", Name = "甲", Days = WeekdaySet.Monday });
        config.Cycles.Add(new ScheduleCycle { Id = "c-same", Name = "乙", Days = WeekdaySet.Tuesday });

        service.Save(config);

        var loaded = service.Load().Cycles;
        Assert.Equal(2, loaded.Count);
        Assert.Single(loaded, static cycle => cycle.Id == "c-same");
    }

    [Fact]
    public void Save_KeepsEmptyCycleRatherThanDroppingIt()
    {
        // Days = 0 在 UI 上拦不住（手改配置可以），但不该在加载时消失：
        // 消失会让"这条引用到底指向过什么"再也查不出来。
        var service = Service;
        var config = new AppConfig();
        config.Cycles.Add(new ScheduleCycle { Id = "c-empty", Name = "空周期", Days = WeekdaySet.None });

        service.Save(config);

        var cycle = Assert.Single(service.Load().Cycles);
        Assert.Equal(WeekdaySet.None, cycle.Days);
        Assert.False(ScheduleCycleResolver.Resolve("c-empty", service.Load().Cycles).Found);
    }

    [Fact]
    public void Save_BlankCycleReferenceBecomesEveryday()
    {
        var service = Service;
        var config = new AppConfig();
        config.Items.Add(new DelayedItem { Id = "orphan", ScheduleCycleId = "   " });

        service.Save(config);

        Assert.Equal(BuiltinCycleIds.Everyday, service.Load().Items[0].ScheduleCycleId);
    }

    [Fact]
    public void NewItems_DefaultToEveryday()
    {
        var config = new AppConfig();
        config.Items.Add(new DelayedItem { Id = "fresh" });

        Assert.Equal(BuiltinCycleIds.Everyday, config.Items[0].ScheduleCycleId);
    }
}
