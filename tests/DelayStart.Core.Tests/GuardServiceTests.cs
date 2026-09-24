using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>GuardService</c> 的巡检编排测试（D74）：扫描 → 写回纠正 → 新增 / 失效检测 → 基线更新。
/// </summary>
/// <remarks>
/// <para>
/// 单条判据已在 <c>GuardCorrectionPolicyTests</c> / <c>GuardNewItemPolicyTests</c> /
/// <c>GuardStalePolicyTests</c> 里逐条覆盖；本类只回答编排层的三个问题：
/// </para>
/// <list type="bullet">
/// <item><description>顺序对不对（关闭时不扫、基线**最后**更新）；</description></item>
/// <item><description>纠正的"复读确认"结果有没有如实反映到报告里；</description></item>
/// <item><description>某一轮来源整体失败时，会不会在下一轮制造假"新增"。</description></item>
/// </list>
/// <para>
/// 基线走真实的 <c>GuardBaselineStore</c>，但落在临时目录里 —— 这样"跨两轮"的行为
/// （基线读写）也一并被验证，而不是被一个内存替身抹平。
/// </para>
/// </remarks>
public sealed class GuardServiceTests
{
    // ── 关闭 / 首次运行 ──────────────────────────────────────────────────────

    [Fact]
    public void RunOnce_GuardDisabled_ReturnsDisabledReportWithoutScanning()
    {
        var source = new FakeStartupSource { Entries = [Entry("registry:hkcu:a", "甲")] };
        using var harness = new Harness(GuardMode.Disabled, source);

        var report = harness.Service.RunOnce();

        Assert.True(report.GuardDisabled);
        Assert.False(report.HasNotifications);
        // 关闭时**不能**扫：这不只是省事，扫描会读注册表/任务库，
        // 用户明确关掉了守卫却还有后台读盘行为，属于越权。
        Assert.Equal(0, source.ScanCount);
    }

    [Fact]
    public void RunOnce_FirstRun_ReportsNothingAsNew()
    {
        var source = new FakeStartupSource { Entries = [Entry("registry:hkcu:a", "甲")] };
        using var harness = new Harness(GuardMode.Periodic, source);

        var report = harness.Service.RunOnce();

        // 没有基线 ⇒ "上次"不存在 ⇒ 报"全部新增"是噪声。首次运行必须安静。
        Assert.Empty(report.NewItems);
        Assert.Equal(1, report.ScannedCount);
    }

    [Fact]
    public void RunOnce_SetsCompletedAtFromClock()
    {
        var source = new FakeStartupSource { Entries = [Entry("registry:hkcu:a", "甲")] };
        using var harness = new Harness(GuardMode.Periodic, source);

        var report = harness.Service.RunOnce();

        // D116：完成时间戳取自时钟 —— 巡检归档的文件名与界面组标题都依赖它。
        Assert.Equal(harness.Clock.Now, report.CompletedAt);
    }

    // ── 新增检测 ────────────────────────────────────────────────────────────

    [Fact]
    public void RunOnce_NewEntryAfterBaseline_IsReported()
    {
        var source = new FakeStartupSource
        {
            Entries = [Entry("registry:hkcu:a", "甲")],
        };
        using var harness = new Harness(GuardMode.Periodic, source);

        _ = harness.Service.RunOnce(); // 第一轮：建立基线

        // 第二轮：多了一个条目（模拟某应用安装后自启）。
        source.SetEntries([Entry("registry:hkcu:a", "甲"), Entry("registry:hkcu:b", "乙")]);

        var report = harness.Service.RunOnce();

        var added = Assert.Single(report.NewItems);
        Assert.Equal("registry:hkcu:b", added.Id);
        Assert.True(report.HasNotifications);
    }

    [Fact]
    public void RunOnce_SourceFailsForOneRound_DoesNotReportOldItemsAsNewNextRound()
    {
        var registry = new FakeStartupSource
        {
            Entries = [Entry("registry:hkcu:a", "甲"), Entry("registry:hkcu:b", "乙")],
        };
        using var harness = new Harness(GuardMode.Periodic, registry);

        _ = harness.Service.RunOnce(); // 基线里有甲、乙

        // 第二轮：来源整体失败（注册表被 ACL 拒绝）。此时扫描结果为空。
        registry.FailWith(new InvalidOperationException("注册表打不开"));
        var failedRound = harness.Service.RunOnce();

        Assert.Single(failedRound.Failures);
        Assert.Empty(failedRound.NewItems);

        // 第三轮：来源恢复。若第二轮把空快照写进了基线，这里甲乙都会被报成"新增"。
        registry.FailWith(exception: null);
        var recovered = harness.Service.RunOnce();

        Assert.Empty(recovered.NewItems);
    }

    [Fact]
    public void RunOnce_FirstRoundSourceFails_DoesNotPersistPartialBaseline()
    {
        // 🔴 B6：首扫（无旧基线）+ 来源整体失败 ⇒ 那份残缺结果**不能**成为正式基线。
        // 否则失败来源里的条目永久从基线消失：既不在基线里，也永远等不到被扫到，
        // 守卫从此对它们完全失明。宁可不写 —— 没有基线只是下一轮重扫。
        var registry = new FakeStartupSource
        {
            Entries = [Entry("registry:hkcu:a", "甲")],
        };
        var healthy = new FakeStartupSource
        {
            Kind = StartupSource.ScheduledTask,
            Scope = StartupScope.None,
            Entries = [Entry("scheduled_task:none:x", "丙")],
        };
        using var harness = new Harness(GuardMode.Periodic, registry, healthy);

        // 第一轮：注册表来源整体失败 ⇒ 不落盘。
        registry.FailWith(new InvalidOperationException("注册表打不开"));
        var first = harness.Service.RunOnce();

        Assert.Single(first.Failures);

        // 第二轮：注册表恢复。因为首轮没写基线，这一轮等同"首次运行" ——
        // 而 `GuardNewItemPolicy` 刻意在无基线时不报新增（否则会把全部条目报成新增，那是噪声）。
        // 代价是"少一轮新增通知"，换来的是"不会永久失明"。这一条就是那个代价的记录。
        registry.FailWith(exception: null);
        var second = harness.Service.RunOnce();

        Assert.Empty(second.Failures);
        Assert.Empty(second.NewItems);

        // 第三轮：基线已由第二轮的完整扫描建立 ⇒ 新增检测恢复正常，新条目能被报出来。
        registry.SetEntries([Entry("registry:hkcu:a", "甲"), Entry("registry:hkcu:b", "乙")]);
        var third = harness.Service.RunOnce();

        var added = Assert.Single(third.NewItems);
        Assert.Equal("registry:hkcu:b", added.Id);
    }

    // ── 写回纠正 ────────────────────────────────────────────────────────────

    [Fact]
    public void RunOnce_TakenOverItemWrittenBack_IsCorrectedAndConfirmed()
    {
        var source = new FakeStartupSource
        {
            Entries = [Entry("registry:hkcu:a", "甲")],
            DisableTakesEffect = true,
        };
        using var harness = new Harness(GuardMode.Periodic, source);
        harness.SeedManagedItem("registry:hkcu:a", "甲");

        var report = harness.Service.RunOnce();

        var outcome = Assert.Single(report.Corrections);
        Assert.True(outcome.Succeeded);
        Assert.Equal(0, report.CorrectionFailureCount);
        Assert.Equal(1, source.DisableCount);
        Assert.True(harness.Log.Contains(LogLevel.Info, "已重新禁用"));
    }

    [Fact]
    public void RunOnce_CorrectionDoesNotStick_IsReportedAsFailure()
    {
        // 写成功了但状态没变 —— 例如另一进程同时把它改回启用。
        // 这类"静默无效"的纠正必须可见，否则用户会以为守卫在保护他。
        var source = new FakeStartupSource
        {
            Entries = [Entry("registry:hkcu:a", "甲")],
            DisableTakesEffect = false,
        };
        using var harness = new Harness(GuardMode.Periodic, source);
        harness.SeedManagedItem("registry:hkcu:a", "甲");

        var report = harness.Service.RunOnce();

        var outcome = Assert.Single(report.Corrections);
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, report.CorrectionFailureCount);
        Assert.True(harness.Log.Contains(LogLevel.Warn, "纠正失败"));
    }

    [Fact]
    public void RunOnce_DisableThrows_IsReportedAsFailureWithoutAbortingRun()
    {
        var source = new FakeStartupSource
        {
            Entries = [Entry("registry:hkcu:a", "甲")],
            DisableException = new UnauthorizedAccessException("ACL 拒绝"),
        };
        using var harness = new Harness(GuardMode.Periodic, source);
        harness.SeedManagedItem("registry:hkcu:a", "甲");

        var report = harness.Service.RunOnce();

        var outcome = Assert.Single(report.Corrections);
        Assert.False(outcome.Succeeded);
        Assert.Contains("ACL 拒绝", outcome.Detail, StringComparison.Ordinal);
        Assert.True(harness.Log.HasExceptionAt(LogLevel.Error));
    }

    [Fact]
    public void RunOnce_UntrackedEnabledItem_IsNotCorrected()
    {
        // 没被接管过的项即便处于启用态也不该被守卫碰 —— 接管才是"允许干预"的凭据。
        var source = new FakeStartupSource
        {
            Entries = [Entry("registry:hkcu:a", "甲")],
            DisableTakesEffect = true,
        };
        using var harness = new Harness(GuardMode.Periodic, source);

        var report = harness.Service.RunOnce();

        Assert.Empty(report.Corrections);
        Assert.Equal(0, source.DisableCount);
    }

    // ── 失效检测 ────────────────────────────────────────────────────────────

    [Fact]
    public void RunOnce_ManagedItemGoneFromSystem_IsReportedAsStale()
    {
        var source = new FakeStartupSource { Entries = [] };
        using var harness = new Harness(GuardMode.Periodic, source);
        harness.SeedManagedItem("registry:hkcu:a", "甲");

        var report = harness.Service.RunOnce();

        var stale = Assert.Single(report.StaleItems);
        Assert.Equal("registry:hkcu:a", stale.Item.Id);
    }

    // ── 通知策略（D80）────────────────────────────────────────────────────────

    [Fact]
    public void RunOnce_DefaultNotifyMode_IsOnChange()
    {
        var source = new FakeStartupSource { Entries = [] };
        using var harness = new Harness(GuardMode.Periodic, source);

        // 默认必须是"有变化时通知"：守卫的全部价值就在于告诉用户系统里多了 / 少了什么，
        // 默认静默等于让这个功能白跑（Settings.GuardNotifyMode 的备注）。
        Assert.Equal(GuardNotifyMode.OnChange, harness.Service.RunOnce().NotifyMode);
    }

    [Fact]
    public void RunOnce_CarriesConfiguredNotifyMode()
    {
        var source = new FakeStartupSource { Entries = [Entry("registry:hkcu:a", "甲")] };
        using var harness = new Harness(GuardMode.Periodic, source);
        harness.SeedNotifyMode(GuardNotifyMode.Never);

        // 🔴 「从不通知」只影响**报不报**，不影响巡检本身 —— 扫描照跑、报告照出，
        // 否则就成了"关掉通知顺带把守卫也关了"。
        var report = harness.Service.RunOnce();

        Assert.Equal(GuardNotifyMode.Never, report.NotifyMode);
        Assert.Equal(1, source.ScanCount);
        Assert.Equal(1, report.ScannedCount);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static StartupEntry Entry(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Path = @"C:\Program Files\Demo\demo.exe",
        Source = StartupSource.Registry,
        Scope = StartupScope.Hkcu,
        SourceKey = id,
        IsEnabled = true,
    };

    /// <summary>把待测服务、内存配置与落临时目录的基线捆在一起。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public Harness(GuardMode mode, params IStartupSource[] sources)
        {
            Log = new FakeLogSink();
            Store = new InMemoryConfigStore();
            Store.Seed(new AppConfig
            {
                Settings = new Settings { GuardMode = mode, GuardMinutes = 30 },
            });

            var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);
            var baseline = new GuardBaselineStore(paths, Log, new FakeClock());

            Service = new GuardService(
                new ScanService(sources, Store, Log),
                Store,
                baseline,
                sources,
                Log,
                Clock);
        }

        /// <summary>时间源（D116 起 <c>GuardRunReport.CompletedAt</c> 取它）。</summary>
        public FakeClock Clock { get; } = new();

        public FakeLogSink Log { get; }

        public InMemoryConfigStore Store { get; }

        public GuardService Service { get; }

        /// <summary>给配置里加一条"已接管"的条目（纠正 / 失效判定都要它）。</summary>
        public void SeedManagedItem(string id, string name)
        {
            var config = Store.Snapshot();
            config.Items = [.. config.Items, new DelayedItem
            {
                Id = id,
                Name = name,
                Source = StartupSource.Registry,
                Scope = StartupScope.Hkcu,
            }];
            Store.Save(config);
        }

        /// <summary>把守卫通知策略写进配置（D80）。</summary>
        public void SeedNotifyMode(GuardNotifyMode mode)
        {
            var config = Store.Snapshot();
            config.Settings.GuardNotifyMode = mode;
            Store.Save(config);
        }

        public void Dispose() => _temp.Dispose();
    }
}
