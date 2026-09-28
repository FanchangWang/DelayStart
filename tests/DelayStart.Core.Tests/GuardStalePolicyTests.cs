using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="GuardStalePolicy"/> 的单元测试（D77 / FR-12.4）：源丢失 / 目标丢失的判定与排除规则。
/// </summary>
/// <remarks>
/// <para>
/// 判定顺序是**先目标、后源**，所以四种组合各有归属：
/// </para>
/// <list type="bullet">
/// <item><description>源在 + 目标在 → 正常，不报</description></item>
/// <item><description>源在 + 目标不在 → <see cref="StaleKind.TargetLost"/>，不再启动</description></item>
/// <item><description>源不在 + 目标在 → <see cref="StaleKind.SourceLost"/>，**继续启动**，只做标记</description></item>
/// <item><description>源不在 + 目标不在 → <see cref="StaleKind.TargetLost"/>（归到目标那档）</description></item>
/// </list>
/// <para>
/// 另有三类一律不判：手动条目的"源"（它没有锚点）、来源整体失败的条目、
/// 以及判不出存在性的目标（UWP 解析名 / 裸命令名 / 相对路径 —— 兜底方向只能是"在"）。
/// </para>
/// </remarks>
public sealed class GuardStalePolicyTests
{
    // ── 四种组合 ────────────────────────────────────────────────────────────

    [Fact]
    public void Select_SourcePresentTargetPresent_IsNotStale()
    {
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("registry:hkcu:ok", path: ExistingFile)],
            [Entry("registry:hkcu:ok", missing: false)],
            []);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_SourcePresentTargetLost_IsTargetLost()
    {
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("registry:hkcu:alpha", path: DeletedFile)],
            [Entry("registry:hkcu:alpha", missing: true)],
            []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.TargetLost, entry.Kind);
        Assert.NotNull(entry.Entry);
    }

    [Fact]
    public void Select_SourceLostTargetPresent_IsSourceLost_AndCarriesNoEntry()
    {
        // 🔴 本策略存在的核心理由：源没了但目标还在时**继续启动**。
        // 旧实现先判源，这类条目被直接判成"孤儿"，于是从此再也没人问过目标还在不在 ——
        // 而它其实每天都在被正常启动。判成 SourceLost 是"标记"，不是"停掉"。
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("registry:hkcu:gone", path: ExistingFile)],
            [],
            []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.SourceLost, entry.Kind);

        // 源已经不在了，没有 StartupEntry 可返回。
        Assert.Null(entry.Entry);
        Assert.Equal("registry:hkcu:gone", entry.Item.Id);
    }

    [Fact]
    public void Select_SourceLostTargetLost_IsTargetLost_NotSourceLost()
    {
        // 顺序反了的话这条会落进 SourceLost，于是继续尝试启动一个已经删掉的程序 ——
        // 正是这个判定要消除的行为。源在不在不改变结论：只要目标没了就不启动。
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("registry:hkcu:both", path: DeletedFile)],
            [],
            []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.TargetLost, entry.Kind);
        Assert.Null(entry.Entry);
    }

    [Fact]
    public void Select_SourceLostWithUnjudgeablePath_IsSourceLost_NotTargetLost()
    {
        // 🔴 判不出存在性时必须按"在"处理（D87/D90：兜底只能更宽松）。
        // UWP 存的是外壳解析名、计划任务 action 可能是裸命令名 ——
        // 对它们 File.Exists 必然为 false，判"目标没了"就是**静默停摆**。
        foreach (var path in new[]
                 {
                     "shell:AppsFolder\\Package_abc!App",
                     "chrome.exe",
                     "relative\\tool.exe",
                     string.Empty,
                 })
        {
            var stale = GuardStalePolicy.SelectStaleItems([Item($"registry:hkcu:{path.GetHashCode()}", path: path)], [], []);

            var entry = Assert.Single(stale);
            Assert.Equal(StaleKind.SourceLost, entry.Kind);
        }
    }

    // ── 手动条目 ────────────────────────────────────────────────────────────

    [Fact]
    public void Select_ManualItemWithoutScanAnchor_IsExcluded()
    {
        // 手动条目在系统里没有锚点，"扫描结果里找不到"是它的正常状态 ——
        // 不是"源丢失"，所以不报。
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("manual:none:guid", source: StartupSource.Manual, scope: StartupScope.None)],
            [],
            []);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_ManualItemWithMissingTarget_IsTargetLost()
    {
        // 2026-09-22 用户回报：手动添加的 exe 被删掉之后守卫不检测。
        // 目标文件是否存在与"有没有系统锚点"无关，手动条目照样要判。
        var stale = GuardStalePolicy.SelectStaleItems([ManualItem(DeletedFile)], [], []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.TargetLost, entry.Kind);

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

    // ── 排除规则 ────────────────────────────────────────────────────────────

    [Fact]
    public void Select_FailedSource_IsNotJudged()
    {
        // 🔴 一次整体失败（任务服务没起来 / 键被 ACL 拒绝）若被判成"全部失效"，
        // 会诱导用户把好好的条目清理掉 —— 这是本策略最危险的方向。
        var stale = GuardStalePolicy.SelectStaleItems(
            [Item("scheduledtask:none:a", source: StartupSource.ScheduledTask, scope: StartupScope.None, path: DeletedFile)],
            [],
            [new ScanScope(StartupSource.ScheduledTask, StartupScope.None)]);

        Assert.Empty(stale);
    }

    [Fact]
    public void Select_BothKindsAreReportedTogether()
    {
        var stale = GuardStalePolicy.SelectStaleItems(
            [
                Item("registry:hkcu:gone", path: ExistingFile),
                Item("registry:hkcu:broken", path: DeletedFile),
                Item("registry:hkcu:ok", path: ExistingFile),
            ],
            [Entry("registry:hkcu:ok", missing: false)],
            []);

        Assert.Equal(2, stale.Count);
        Assert.Contains(stale, entry => entry.Kind == StaleKind.SourceLost);
        Assert.Contains(stale, entry => entry.Kind == StaleKind.TargetLost);
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
