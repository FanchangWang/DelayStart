using DelayStart.Core.Models;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// 周期名唯一性（2026-09-23 批复 14）—— 判据本身 + 落盘前的兜底校验。
/// </summary>
/// <remarks>
/// <para>
/// 两层各测一遍的理由：判据（<see cref="CycleNames.IsTaken"/>）被界面与管理端**共用**，
/// 它错了会同时错两处；而落盘点（<c>ConfigEditService</c>）必须**真的拦得住** ——
/// 配置是外部可改的文件，只在界面上画红字等于没校验。
/// </para>
/// <para>
/// 🔴 与内置五档同名也必须拦：列表徽标里两枚一模一样的「每天」，没人分得清哪个是自己的。
/// </para>
/// <para>
/// 配置走 <see cref="InMemoryConfigStore"/>，不碰文件系统；计划任务注册器走
/// <see cref="FakeSchedulerTaskRegistrar"/>（本功能根本不碰计划任务，但它要求构造函数注入）。
/// </para>
/// </remarks>
public sealed class CycleNameUniquenessTests
{
    // ── 判据（纯函数）────────────────────────────────────────────

    [Fact]
    public void Same_IgnoresSurroundingWhitespaceAndCase()
    {
        Assert.True(CycleNames.Same("上一休一", "  上一休一  "));
        Assert.True(CycleNames.Same("Work", "work"));
        Assert.False(CycleNames.Same("上一休一", "上二休二"));
        Assert.False(CycleNames.Same("", ""));
        Assert.False(CycleNames.Same(null, CycleNames.Everyday));
    }

    [Fact]
    public void IsTaken_ReservesBuiltinNames()
    {
        foreach (var builtin in CycleNames.Builtin)
        {
            Assert.True(CycleNames.IsTaken(builtin), $"内置档「{builtin}」应被视为已占用");
        }
    }

    [Fact]
    public void IsTaken_ComparesAgainstOtherCustomNames()
    {
        string[] others = ["上一休一", "周末班"];

        Assert.True(CycleNames.IsTaken(" 周末班 ", others));
        Assert.False(CycleNames.IsTaken("夜班", others));
        Assert.False(CycleNames.IsTaken("夜班"));
    }

    [Fact]
    public void IsTaken_BlankNameIsNotTaken()
    {
        // "必须填名字"是另一条规则（FR-15.20），文案也不同 —— 这里必须放行，
        // 否则一次点保存会同时报两条互相矛盾的错。
        Assert.False(CycleNames.IsTaken("   ", ["上一休一"]));
    }

    [Fact]
    public void Of_MapsBuiltinIdsAndNothingElse()
    {
        Assert.Equal(CycleNames.Everyday, CycleNames.Of(BuiltinCycleIds.Everyday));
        Assert.Equal(CycleNames.LegalWorkday, CycleNames.Of(BuiltinCycleIds.LegalWorkday));
        Assert.Null(CycleNames.Of("c-1234abcd"));
        Assert.Null(CycleNames.Of(null));
    }

    [Fact]
    public void BuiltinNames_AlignWithBuiltinIdOrder()
    {
        // 顺序由 BuiltinCycleIds.Ordered 定义、名称在 CycleNames 里翻译，
        // 两者必须一一对应 —— 对不上会让编辑弹窗的胶囊按错的顺序排。
        Assert.Equal(BuiltinCycleIds.Ordered.Length, CycleNames.Builtin.Length);
        for (var i = 0; i < BuiltinCycleIds.Ordered.Length; i++)
        {
            Assert.Equal(CycleNames.Builtin[i], CycleNames.Of(BuiltinCycleIds.Ordered[i]));
        }
    }

    // ── 落盘点（ConfigEditService）────────────────────────────────

    [Fact]
    public void AddCycle_DuplicateCustomName_ThrowsAndDoesNotPersist()
    {
        var harness = new Harness();
        harness.SeedCycle("c-1", "上一休一");

        var ex = Assert.Throws<ArgumentException>(
            () => harness.Service.AddCycle("上一休一", WeekdaySet.Monday));

        Assert.Contains("同名", ex.Message, StringComparison.Ordinal);
        Assert.Single(harness.Store.Snapshot().Cycles);
    }

    [Fact]
    public void AddCycle_DuplicateAfterTrimming_Throws()
    {
        var harness = new Harness();
        harness.SeedCycle("c-1", "上一休一");

        Assert.Throws<ArgumentException>(
            () => harness.Service.AddCycle("  上一休一 ", WeekdaySet.Monday));
    }

    [Fact]
    public void AddCycle_BuiltinName_Throws()
    {
        var harness = new Harness();

        Assert.Throws<ArgumentException>(
            () => harness.Service.AddCycle(CycleNames.Everyday, WeekdaySet.All));
        Assert.Empty(harness.Store.Snapshot().Cycles);
    }

    [Fact]
    public void AddCycle_FreshName_Persists()
    {
        var harness = new Harness();
        harness.SeedCycle("c-1", "上一休一");

        var created = harness.Service.AddCycle("夜班", WeekdaySet.Monday | WeekdaySet.Thursday);

        Assert.Equal("夜班", created.Name);
        Assert.Equal(2, harness.Store.Snapshot().Cycles.Count);
    }

    [Fact]
    public void UpdateCycle_KeepingOwnName_IsAllowed()
    {
        // 🔴 最容易写坏的一条：改星期时"名字照旧"不该被自己挡住。
        var harness = new Harness();
        harness.SeedCycle("c-1", "上一休一");

        var updated = harness.Service.UpdateCycle("c-1", "上一休一", WeekdaySet.Sunday);

        Assert.True(updated);
        Assert.Equal(WeekdaySet.Sunday, harness.Store.Snapshot().Cycles[0].Days);
    }

    [Fact]
    public void UpdateCycle_RenamingToAnotherCycle_ThrowsAndKeepsOldName()
    {
        var harness = new Harness();
        harness.SeedCycle("c-1", "上一休一");
        harness.SeedCycle("c-2", "夜班");

        Assert.Throws<ArgumentException>(
            () => harness.Service.UpdateCycle("c-2", "上一休一", WeekdaySet.All));

        Assert.Equal("夜班", harness.Store.Snapshot().Cycles[1].Name);
    }

    /// <summary>最小装配：只到"周期能落盘"为止。</summary>
    private sealed class Harness
    {
        public Harness()
        {
            Store = new InMemoryConfigStore();
            Service = new ConfigEditService(Store, new FakeSchedulerTaskRegistrar(), new FakeLogSink());
        }

        public InMemoryConfigStore Store { get; }

        public ConfigEditService Service { get; }

        /// <summary>往配置里加一个自定义周期（累加，不覆盖已有的）。</summary>
        public void SeedCycle(string id, string name) =>
            Store.Seed(new AppConfig
            {
                Cycles =
                [
                    .. Store.Snapshot().Cycles,
                    new ScheduleCycle { Id = id, Name = name, Days = WeekdaySet.Monday },
                ],
            });
    }
}
