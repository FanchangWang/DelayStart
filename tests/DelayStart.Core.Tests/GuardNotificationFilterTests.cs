using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>GuardNotificationFilter</c> 的测试（D148）：跨轮次的"同一条失效只通报一次"。
/// </summary>
/// <remarks>
/// <para>
/// 这一层是守卫进程唯一调用的通报入口（<c>Guard/Program.cs</c>），而守卫工程
/// <b>零测试覆盖</b> —— 所以"每轮该报什么"的行为必须在这里被钉住，
/// 否则它只能靠真机验收发现。
/// </para>
/// <para>
/// 用真实文件系统（临时目录）而不是内存替身：本类的全部价值就在<b>跨轮次的状态延续</b>，
/// 而"第二轮读到的必须是第一轮写下的那份"正是最容易被替身掩盖的地方。
/// </para>
/// </remarks>
public sealed class GuardNotificationFilterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Apply_首次运行_有失效也不通报()
    {
        // 无状态 ⇒ "没有上一次" ⇒ 静默一轮，方向与 GuardNewItemPolicy 的首扫静默一致。
        var harness = Create();

        var view = harness.Filter.Apply(Report([Stale("a")]));

        Assert.Empty(view.StaleItems);
        Assert.False(view.HasNotifications);
    }

    [Fact]
    public void Apply_第二轮_报出上一轮之后才失效的条目()
    {
        var harness = Create();

        // 第一轮：一条失效也没有 ⇒ 状态写成空集合（不是 null）。
        _ = harness.Filter.Apply(Report([]));

        // 第二轮：它失效了 ⇒ 该报。
        var view = harness.Filter.Apply(Report([Stale("a")]));

        Assert.Equal(["a"], Ids(view));
        Assert.True(view.HasNotifications);
    }

    [Fact]
    public void Apply_持续失效_只通报一次()
    {
        // 🔴 D148 的核心：用户卸载一个程序，之后每个周期都收到一模一样的通知，
        // 通知中心被塞满后他会开始无视守卫的全部消息。
        var harness = Create();

        _ = harness.Filter.Apply(Report([]));
        var first = harness.Filter.Apply(Report([Stale("a")]));
        var second = harness.Filter.Apply(Report([Stale("a")]));
        var third = harness.Filter.Apply(Report([Stale("a")]));

        Assert.Equal(["a"], Ids(first));
        Assert.Empty(second.StaleItems);
        Assert.Empty(third.StaleItems);
        Assert.False(third.HasNotifications);
    }

    [Fact]
    public void Apply_全量现状始终保留_差集只影响通报()
    {
        // 🔴 差集不能顺手把"现在还有哪些条目失效"这个视图也毁掉：
        // 原报告每轮都要列全量（日志页 / 总览卡 / 汇总行都靠它）。
        var harness = Create();
        var original = Report([Stale("a"), Stale("b")]);

        _ = harness.Filter.Apply(Report([]));
        var view = harness.Filter.Apply(original);

        Assert.Equal(["a", "b"], Ids(view));
        Assert.Equal(["a", "b"], Ids(original));
    }

    [Fact]
    public void Apply_失效恢复后再失效_再次通报()
    {
        // 🔴 "曾经报过一次"不能当成永久豁免 —— 程序装回去又被清掉，
        // 恰恰是用户最需要知道的一次。
        var harness = Create();

        _ = harness.Filter.Apply(Report([]));
        _ = harness.Filter.Apply(Report([Stale("a")]));

        // 中间一轮恢复正常：状态必须被清空。
        var recovered = harness.Filter.Apply(Report([]));
        Assert.Empty(recovered.StaleItems);

        var again = harness.Filter.Apply(Report([Stale("a")]));

        Assert.Equal(["a"], Ids(again));
    }

    [Fact]
    public void Apply_手动条目失效_同样通报一次()
    {
        // 🔴 这条是本方案存在的理由。早期版本拿扫描基线当通报判据，
        // 而手动添加的条目永远不在基线里（七个来源实例没有 Manual），
        // 于是用户卸载手动添加的 exe 后**一条通知都不会来**。
        var harness = Create();
        var manual = Stale("manual:none:abc", manual: true);

        _ = harness.Filter.Apply(Report([]));

        var first = harness.Filter.Apply(Report([manual]));
        var second = harness.Filter.Apply(Report([manual]));

        Assert.True(manual.Item.IsManual);
        Assert.Equal(["manual:none:abc"], Ids(first));
        Assert.Empty(second.StaleItems);
    }

    [Fact]
    public void Apply_两档StaleKind_都能通报且都只报一次()
    {
        var harness = Create();
        var sourceLost = Stale("registry:hkcu:a", kind: StaleKind.SourceLost);
        var targetLost = Stale("registry:hkcu:b", kind: StaleKind.TargetLost);

        _ = harness.Filter.Apply(Report([]));
        var first = harness.Filter.Apply(Report([sourceLost, targetLost]));
        var second = harness.Filter.Apply(Report([sourceLost, targetLost]));

        Assert.Equal(["registry:hkcu:a", "registry:hkcu:b"], Ids(first));
        Assert.Empty(second.StaleItems);
    }

    [Fact]
    public void Apply_多条目_只报新出现的那几条()
    {
        var harness = Create();

        _ = harness.Filter.Apply(Report([]));
        _ = harness.Filter.Apply(Report([Stale("a")]));

        var view = harness.Filter.Apply(Report([Stale("a"), Stale("b")]));

        Assert.Equal(["b"], Ids(view));
    }

    [Fact]
    public void Apply_新增条目不受失效差集影响()
    {
        // 新增检测走基线（不是通知状态），两套差集互不干涉。
        var harness = Create();
        var report = Report([Stale("a")]) with
        {
            NewItems = [Entry("registry:hkcu:new")],
        };

        _ = harness.Filter.Apply(Report([]));
        var view = harness.Filter.Apply(report);

        Assert.True(view.HasNotifications);
        Assert.Single(view.NewItems);
    }

    [Fact]
    public void Apply_状态写失败_下一轮重复通报而不是静默漏报()
    {
        // 🔴 降级方向 fail-open：状态<b>写</b>不进去而<b>读</b>仍然正常（磁盘满 / 配额用尽时
        // 上一轮那份文件还在）⇒ 下一轮重新命中 ⇒ 多报一次。
        // 反过来（把失败读成"没有状态"）会静默漏报，用户永远不知道自己有个程序已经失效。
        var harness = Create();

        // 先正常建立一份状态（此刻无失效 ⇒ 空集合）。
        _ = harness.Filter.Apply(Report([]));

        harness.MakeWritesFail();

        // 这一轮该报，也确实报了；只是状态没能写下去。
        var first = harness.Filter.Apply(Report([Stale("a")]));
        Assert.Equal(["a"], Ids(first));

        // 下一轮状态仍是那份空集合 ⇒ 同一条再次命中 ⇒ 重复通报（而不是静默）。
        var again = harness.Filter.Apply(Report([Stale("a")]));
        Assert.Equal(["a"], Ids(again));

        // 写失败只记 Warn，绝不抛 —— 守卫是周期任务，不能为一次落盘失败整体失败。
    Assert.True(harness.Log.Contains(LogLevel.Warn, "通知状态"));
    }

    [Fact]
    public void Apply_状态与通知策略无关_过滤器照常推进()
    {
        // 🔴 D148 ⑥：过滤器在守卫进程的策略检查**之前**执行，所以 Never 期间的失效
        // 照样记入"已通报" ⇒ 切回 OnChange 后不补报。这是刻意的：Never 是用户明确
        // 表达的"别打扰我"，补报会在他刚切回来那一刻集中弹一轮。
        // 本用例钉住"过滤器不读 NotifyMode" —— 将来若有人把策略判断挪进过滤器、
        // 或让过滤器在 Never 时跳过写状态，这条就会红。
        var harness = Create();

        // 第一轮走 Never（守卫进程会跳过 Notify，但过滤器已跑过）。
        var never = Report([Stale("a")]) with { NotifyMode = GuardNotifyMode.Never };
        _ = harness.Filter.Apply(never);

        // 切回 OnChange：这一轮不该再报"a"（Never 期间已记入）。
        var back = Report([Stale("a")]) with { NotifyMode = GuardNotifyMode.OnChange };
        var view = harness.Filter.Apply(back);

        Assert.Empty(view.StaleItems);
    }

    [Fact]
    public void Apply_入参null_抛()
        => Assert.Throws<ArgumentNullException>(() => Create().Filter.Apply(null!));

    // ── helpers ─────────────────────────────────────────────────────────────

    private Harness Create()
    {
        var log = new FakeLogSink();
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);
        var store = new GuardNotifyStateStore(paths, log, new FakeClock());
        return new Harness(paths, log, new GuardNotificationFilter(store));
    }

    private sealed record Harness(PathService Paths, FakeLogSink Log, GuardNotificationFilter Filter)
    {
        /// <summary>
        /// 让<b>写</b>失败而<b>读</b>仍成功（模拟磁盘满 / 配额用尽：上一轮那份文件还在，
        /// 新的写不进去）。
        /// </summary>
        /// <remarks>
        /// 🔴 手法是占住 <c>AtomicFileWriter</c> 的临时路径（<c>&lt;name&gt;.tmp</c>）：
        /// 写必失败，而正式文件原封不动、照常读得出来。
        /// 别用"把状态文件本身变成目录"—— 那会让<b>读</b>也失败，
        /// 走到的是"没有状态 ⇒ 静默"那一支，测到的就不是 fail-open 了。
        /// </remarks>
        public void MakeWritesFail()
        {
            var temporaryPath = Paths.GuardNotifyStatePath + ".tmp";
            if (Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }

            _ = Directory.CreateDirectory(temporaryPath);
        }
    }

    private static GuardRunReport Report(IReadOnlyList<StaleEntry> staleItems) => new()
    {
        CompletedAt = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero),
        ScannedCount = 3,
        StaleItems = staleItems,
    };

    private static StaleEntry Stale(string id, StaleKind kind = StaleKind.TargetLost, bool manual = false)
    {
        var isManual = manual || id.StartsWith("manual:", StringComparison.Ordinal);

        return new StaleEntry(
            new DelayedItem
            {
                Id = id,
                Name = id,
                Source = isManual ? StartupSource.Manual : StartupSource.Registry,
                Scope = isManual ? StartupScope.None : StartupScope.Hkcu,
            },
            kind,
            Entry: null);
    }

    private static StartupEntry Entry(string id) => new()
    {
        Id = id,
        Name = id,
        Path = @"C:\Program Files\Demo\demo.exe",
        Source = StartupSource.Registry,
        Scope = StartupScope.Hkcu,
        SourceKey = id,
        IsEnabled = true,
    };

    private static string[] Ids(GuardRunReport report)
        => [.. report.StaleItems.Select(static stale => stale.Item.Id)];
}