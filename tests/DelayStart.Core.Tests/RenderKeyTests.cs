using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="RenderKey"/> 的行为测试（D139）。
/// </summary>
public sealed class RenderKeyTests
{
    private const string SameFingerprint = "fp-abc";

    [Fact]
    public void For_SameFilterSameFingerprint_SameKey()
    {
        // 正向：不换筛选、不换数据 ⇒ 键相同，"这一屏没变"的判断才成立。
        Assert.Equal(
            RenderKey.For(StartupSource.Registry, SameFingerprint),
            RenderKey.For(StartupSource.Registry, SameFingerprint));
    }

    [Fact]
    public void For_SameFingerprintButDifferentFilter_DifferentKey()
    {
        // 🔴 反向的关键：这条是本组的意义所在。
        // 快照指纹覆盖**整份快照**，而列表显示的是「快照 ∩ 来源筛选」。
        // 若两个不同筛选算出同一个键，切子页面就会被判成"没变化"而跳过重画，
        // 列表停在上一个来源 —— 四个子页面看上去像混在一起（2026-10-02 用户实测）。
        Assert.NotEqual(
            RenderKey.For(StartupSource.Registry, SameFingerprint),
            RenderKey.For(StartupSource.ScheduledTask, SameFingerprint));
    }

    [Fact]
    public void For_AllSourcesFilter_DiffersFromEverySingleSource()
    {
        // 「全部」那一屏是七个来源的合集，与任何单一来源都必须是不同的键。
        var all = RenderKey.For(filter: null, SameFingerprint);

        Assert.NotEqual(all, RenderKey.For(StartupSource.Registry, SameFingerprint));
        Assert.NotEqual(all, RenderKey.For(StartupSource.StartupFolder, SameFingerprint));
        Assert.NotEqual(all, RenderKey.For(StartupSource.ScheduledTask, SameFingerprint));
        Assert.NotEqual(all, RenderKey.For(StartupSource.Uwp, SameFingerprint));
    }

    [Fact]
    public void For_DifferentFingerprint_DifferentKey()
    {
        // 另一维也不能丢：换了筛选但数据没变，与数据变了而筛选没变，是两回事。
        Assert.NotEqual(
            RenderKey.For(StartupSource.Registry, "fp-old"),
            RenderKey.For(StartupSource.Registry, SameFingerprint));
    }

    [Fact]
    public void For_AllEnumeratedSources_PairwiseDistinct()
    {
        // 逐一枚举：七个来源（含 None）两两不同键。
        // 少枚举一个来源就可能漏掉一对撞车的 —— 而撞车等于漏刷，只能真机看出来。
        var kinds = Enum.GetValues<StartupSource>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var kind in kinds)
        {
            Assert.True(keys.Add(RenderKey.For(kind, SameFingerprint)), $"来源 {kind} 的键与别的撞车了。");
        }

        Assert.True(keys.Add(RenderKey.For(filter: null, SameFingerprint)), "「全部」的键与某个来源撞车了。");
        Assert.Equal(kinds.Length + 1, keys.Count);
    }

    [Fact]
    public void For_NullFingerprint_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => RenderKey.For(StartupSource.Registry, fingerprint: null!));
    }

    [Fact]
    public void For_FingerprintWithSeparatorInside_StillDistinguishesSources()
    {
        // 指纹本身是拼接出来的，里面带冒号。若前缀靠"截断到第一个冒号"来解析，
        // 就能构造出撞车；这里只是确认实现没有做那种解析（直接前缀拼接，天然安全）。
        const string Awkward = "a:b:c";

        Assert.NotEqual(
            RenderKey.For(StartupSource.Registry, Awkward),
            RenderKey.For(StartupSource.StartupFolder, Awkward));
    }
}