using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="SourceFilterPolicy"/> 的行为测试（D140）。
/// </summary>
/// <remarks>
/// 🔴 这一组是为了钉死一个**曾经反了**的谓词。它反掉的时候构建绿、单测全绿，
/// 而四个子页面的条目混在一起 —— 只有真机点标签才看得见。所以用例必须
/// **逐一枚举每个来源**并断言"留下的正好是它自己"，不能只测一两个组合。
/// </remarks>
public sealed class SourceFilterPolicyTests
{
    /// <summary>界面上真实存在的四个子页面来源（与 <c>NavigationService</c> 的四个标签一一对应）。</summary>
    private static readonly StartupSource[] SubPageSources =
    [
        StartupSource.Registry,
        StartupSource.StartupFolder,
        StartupSource.ScheduledTask,
        StartupSource.Uwp,
    ];

    [Fact]
    public void IsVisible_NullFilter_KeepsEverything()
    {
        // 左侧父菜单「自启动项」（tag = "items"）走这一支：要的是七个来源的合集。
        foreach (var source in Enum.GetValues<StartupSource>())
        {
            Assert.True(SourceFilterPolicy.IsVisible(source, filter: null), $"来源 {source} 被 null 筛选漏掉了。");
        }
    }

    [Fact]
    public void IsVisible_MatchingSource_IsKept()
    {
        // 🔴 正向：这条以前是反的。谓词为真时该**加入**，而我当时写成了 continue。
        foreach (var filter in SubPageSources)
        {
            Assert.True(SourceFilterPolicy.IsVisible(filter, filter), $"来源 {filter} 把自己筛掉了。");
        }
    }

    [Fact]
    public void IsVisible_NonMatchingSource_IsDropped()
    {
        // 🔴 反向：这一条以前是错的另一半 —— 不该显示的全被摆上了屏幕，
        // 四个子页面看上去"混在一起"。
        foreach (var filter in SubPageSources)
        {
            foreach (var source in SubPageSources)
            {
                var expected = source == filter;
                Assert.Equal(
                    expected,
                    SourceFilterPolicy.IsVisible(source, filter));
            }
        }
    }

    [Fact]
    public void IsVisible_EachSubPageFilter_KeepsExactlyItself()
    {
        // 🔴 本组的意义所在：把"每个子页面只留自己"这件事对**枚举里的每一个来源**都验一遍。
        // 只测两三个组合正是当初漏掉这个 bug 的原因。
        var all = Enum.GetValues<StartupSource>();

        foreach (var filter in all)
        {
            var kept = all.Where(source => SourceFilterPolicy.IsVisible(source, filter)).ToArray();

            Assert.Equal([filter], kept);
        }
    }

    [Fact]
    public void IsVisible_IsSymmetricBetweenKeptAndDropped()
    {
        // 每种来源被恰好一个筛选保留、恰好被其余全部丢弃。
        var all = Enum.GetValues<StartupSource>();

        foreach (var source in all)
        {
            var keepers = all.Count(filter => SourceFilterPolicy.IsVisible(source, filter));
            Assert.Equal(1, keepers);
        }
    }

    [Fact]
    public void Visible_FiltersRealEntriesAndPreservesOrder()
    {
        var entries = new[]
        {
            Entry("a", StartupSource.Registry),
            Entry("b", StartupSource.StartupFolder),
            Entry("c", StartupSource.Registry),
            Entry("d", StartupSource.ScheduledTask),
        };

        var registry = SourceFilterPolicy.Visible(entries, StartupSource.Registry).ToArray();

        Assert.Equal(["a", "c"], registry.Select(static entry => entry.Name));
    }

    [Fact]
    public void Visible_NullFilter_ReturnsAllInOrder()
    {
        var entries = new[]
        {
            Entry("a", StartupSource.Registry),
            Entry("b", StartupSource.Uwp),
        };

        Assert.Equal(["a", "b"], SourceFilterPolicy.Visible(entries, null).Select(static entry => entry.Name));
    }

    [Fact]
    public void Visible_FilterMatchingNothing_ReturnsEmpty()
    {
        // 「UWP Apps」子页面在这台机器上一个 UWP 应用都没有是常态 ——
        // 该显示空列表，而不是退化成显示全部。
        var entries = new[] { Entry("a", StartupSource.Registry) };

        Assert.Empty(SourceFilterPolicy.Visible(entries, StartupSource.Uwp));
    }

    [Fact]
    public void Visible_NullEntries_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SourceFilterPolicy.Visible(entries: null!, StartupSource.Registry));
    }

    private static StartupEntry Entry(string name, StartupSource source) => new()
    {
        Id = $"{source}:scope:{name}",
        Name = name,
        Path = $@"C:\{name}.exe",
        Source = source,
        Scope = StartupScope.None,
        SourceKey = name,
    };
}