using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="GuardStalePolicy"/> 的单元测试（D77）：孤儿 / 已失效条目的判定与排除规则。
/// </summary>
/// <remarks>
/// 三类条目各有一条路：已接管条目（靠扫描结果判孤儿与失效）、手动条目（只判目标文件，
/// 没有锚点所以不判孤儿，2026-09-22 用户回报的漏判）、来源整体失败的条目（一律不判）。
/// </remarks>
public sealed class GuardStalePolicyTests
{
    [Fact]
    public void Select_ManagedItemMissingFromScan_IsOrphan()
    {
        var stale = GuardStalePolicy.SelectStaleItems([Item("registry:hkcu:gone")], [], []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.Orphan, entry.Kind);
        Assert.Null(entry.Entry);
        Assert.Equal("registry:hkcu:gone", entry.Item.Id);
    }

    [Fact]
    public void Select_ManagedItemWithMissingTarget_IsMissing()
    {
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("registry:hkcu:alpha")],
            [Entry("registry:hkcu:alpha", missing: true)],
            []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.Missing, entry.Kind);
        Assert.NotNull(entry.Entry);
    }

    [Fact]
    public void Select_ManagedItemHealthy_IsNotStale()
    {
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("registry:hkcu:alpha")],
            [Entry("registry:hkcu:alpha", missing: false)],
            []);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_ManualItemWithoutScanAnchor_IsExcluded()
    {
        // 手动条目在系统里没有锚点，"扫描结果里找不到"是它的正常状态。
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("manual:none:guid", source: StartupSource.Manual, scope: StartupScope.None)],
            [],
            []);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_ManualItemWithMissingTarget_IsMissing()
    {
        // 2026-09-22 用户回报：手动添加的 exe 被删掉之后守卫不检测。
        // 目标文件是否存在与"有没有系统锚点"无关，手动条目照样要判。
        var stale = GuardStalePolicy.SelectStaleItems(
            [ManualItem(DeletedFile)],
            [],
            []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.Missing, entry.Kind);
        // 手动条目没有扫描结果，Entry 为 null —— 通报只用到 Item.Name / Item.Path。
        Assert.Null(entry.Entry);
        Assert.Equal("manual:none:gone", entry.Item.Id);
    }

    [Fact]
    public void Select_ManualItemWithExistingTarget_IsNotStale()
    {
        var stale = GuardStalePolicy.SelectStaleItems([ManualItem(ExistingFile)], [], []);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_ManualUwpItem_IsNotJudgedMissing()
    {
        // 手动添加的 UWP 应用存的是外壳解析名（D41），不是文件路径 ——
        // 对它调 File.Exists 必然为 false，判"目标没了"就是误报。
        var stale = GuardStalePolicy.SelectStaleItems(
            [ManualItem("shell:AppsFolder\\Package_abc!App")],
            [],
            []);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_ManualItemWithBareCommandName_IsNotJudgedMissing()
    {
        // 与三个扫描来源同一条排除规则：裸命令名（相对路径 / PATH 解析）判不了存在性。
        var stale = GuardStalePolicy.SelectStaleItems(
            [ManualItem("chrome.exe"), ManualItem("relative\\tool.exe")],
            [],
            []);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_ManualItemMissingTarget_IsNotSuppressedByFailedSource()
    {
        // 手动条目与来源失败无关（它根本不属于任何来源），这条用来钉住"失败来源排除"
        // 不会连手动条目一起跳过。
        var stale = GuardStalePolicy.SelectStaleItems(
            [ManualItem(DeletedFile)],
            [],
            [new ScanScope(StartupSource.Registry, StartupScope.Hkcu)]);

        Assert.Single(stale);
    }

    [Fact]
    public void Select_FailedSource_IsNotJudged()
    {
        // 🔴 一次整体失败（任务服务没起来 / 键被 ACL 拒绝）若被判成"全部失效"，
        // 会诱导用户把好好的条目清理掉 —— 这是本策略最危险的方向。
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("scheduledtask:none:a", source: StartupSource.ScheduledTask, scope: StartupScope.None)],
            [],
            [new ScanScope(StartupSource.ScheduledTask, StartupScope.None)]);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_OrphanAndMissingAreReportedTogether()
    {
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("registry:hkcu:gone"), Item("registry:hkcu:broken"), Item("registry:hkcu:ok")],
            [Entry("registry:hkcu:broken", missing: true), Entry("registry:hkcu:ok", missing: false)],
            []);

        Assert.Equal(2, stale.Count);
        Assert.Contains(stale, entry => entry.Kind == StaleKind.Orphan);
        Assert.Contains(stale, entry => entry.Kind == StaleKind.Missing);
    }

    [Fact]
    public void Select_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => GuardStalePolicy.SelectStaleItems(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => GuardStalePolicy.SelectStaleItems([], null!, []));
        Assert.Throws<ArgumentNullException>(() => GuardStalePolicy.SelectStaleItems([], [], null!));
    }

    private static DelayedItem Item(
        string id,
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu,
        string path = "")
        => new() { Id = id, Name = id, Source = source, Scope = scope, Path = path };

    /// <summary>构造一个手动条目：主键形如 <c>manual:none:&lt;guid&gt;</c>，待测目标是它的 <c>Path</c>。</summary>
    private static DelayedItem ManualItem(string path)
        => Item("manual:none:gone", source: StartupSource.Manual, scope: StartupScope.None, path: path);

    /// <summary>确定不存在的目标文件（用 GUID 保证不会被别的东西撞上）。</summary>
    private static string DeletedFile => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"delaystart-tests-deleted-{Guid.NewGuid():N}.exe");

    /// <summary>确定存在的文件：就用本测试程序集自己。</summary>
    private static string ExistingFile => typeof(GuardStalePolicyTests).Assembly.Location;

    private static StartupEntry Entry(
        string id,
        bool missing,
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu)
        => new()
        {
            Id = id,
            Name = id,
            Source = source,
            Scope = scope,
            SourceKey = id,
            IsMissing = missing,
        };
}
