using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>GuardInspectionStore</c> 的读写测试（D116）：巡检归档的原子落盘、滚动清理与"坏归档必须跳过"。
/// </summary>
/// <remarks>
/// 与 <see cref="GuardBaselineStoreTests"/> 同一套做法：落点在临时目录（真实的
/// <c>GuardInspectionsRoot</c> 由 <see cref="PathService"/> 决定），读写都真实走一遍文件系统。
/// </remarks>
public sealed class GuardInspectionStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadRecent_NoFilesYet_ReturnsEmpty()
    {
        var harness = Create();

        var reports = harness.Store.ReadRecent(10);

        Assert.Empty(reports);
    }

    [Fact]
    public void WriteThenRead_RoundTripsReport()
    {
        var harness = Create();
        var completedAt = harness.Clock.Now;

        harness.Store.Write(new GuardRunReport
        {
            CompletedAt = completedAt,
            ScannedCount = 12,
            Corrections = [new GuardCorrectionOutcome("registry:hkcu:a", "甲", false, "拒绝访问")],
            NewItems = [Entry("registry:hkcu:b", "乙")],
            StaleItems = [new StaleEntry(MakeDelayedItem("registry:hkcu:c", "丙"), StaleKind.Orphan, null)],
        });

        var reports = harness.Store.ReadRecent(10);

        var report = Assert.Single(reports);
        Assert.Equal(completedAt, report.CompletedAt);
        Assert.Equal(12, report.ScannedCount);

        var correction = Assert.Single(report.Corrections);
        Assert.Equal("甲", correction.Name);
        Assert.False(correction.Succeeded);

        var added = Assert.Single(report.NewItems);
        Assert.Equal("乙", added.Name);

        var stale = Assert.Single(report.StaleItems);
        Assert.Equal("丙", stale.Item.Name);
        Assert.Equal(StaleKind.Orphan, stale.Kind);

        // 文件名 = 完成时刻（yyyyMMdd-HHmmss），调度归档的 runId 同一语义。
        Assert.True(
            File.Exists(Path.Combine(harness.Paths.GuardInspectionsRoot, $"{completedAt:yyyyMMdd-HHmmss}.json")),
            "归档文件名必须取 CompletedAt");
    }

    [Fact]
    public void Write_DisabledReport_IsNotArchived()
    {
        var harness = Create();

        harness.Store.Write(GuardRunReport.Disabled());

        Assert.Empty(harness.Store.ReadRecent(10));
        Assert.False(Directory.Exists(harness.Paths.GuardInspectionsRoot));
    }

    [Fact]
    public void ReadRecent_CorruptJson_SkipsFileAndWarns()
    {
        var harness = Create();
        harness.Store.Write(Report("甲"));
        File.WriteAllText(
            Path.Combine(harness.Paths.GuardInspectionsRoot, "20260923-120000.json"),
            "{ \"CompletedAt\": ");

        var reports = harness.Store.ReadRecent(10);

        // 坏归档只跳过自己，不能拖空整个列表（FR-1.4 的同一条原则）。
        var report = Assert.Single(reports);
        Assert.Equal("甲", Assert.Single(report.NewItems).Name);
        Assert.True(harness.Log.Contains(LogLevel.Warn, "守卫巡检归档无法读取"));
    }

    [Fact]
    public void ReadRecent_OrdersNewestFirst()
    {
        var harness = Create();
        harness.Store.Write(Report("旧", at: 10));
        harness.Store.Write(Report("新", at: 20));

        var reports = harness.Store.ReadRecent(10);

        Assert.Equal("新", Assert.Single(reports[0].NewItems).Name);
        Assert.Equal("旧", Assert.Single(reports[1].NewItems).Name);
    }

    [Fact]
    public void Write_TrimsBeyondMaxRetained()
    {
        var harness = Create();

        // 写 31 份（时间各差一分钟）→ 只留最近 30 份，最旧的那份被清掉。
        for (var minute = 0; minute <= GuardInspectionStore.MaxRetainedInspections; minute++)
        {
            harness.Store.Write(Report($"条目-{minute}", at: minute));
        }

        var reports = harness.Store.ReadRecent(GuardInspectionStore.MaxRetainedInspections + 10);
        Assert.Equal(GuardInspectionStore.MaxRetainedInspections, reports.Count);
        Assert.DoesNotContain(reports, static report => report.NewItems.Any(static item => item.Name == "条目-0"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static GuardRunReport Report(string newItemName, int at = 0)
        => new()
        {
            CompletedAt = new DateTimeOffset(2026, 9, 24, 12, 0 + at, 0, TimeSpan.Zero),
            ScannedCount = 3,
            NewItems = [Entry("registry:hkcu:a", newItemName)],
        };

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

    private static DelayedItem MakeDelayedItem(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Source = StartupSource.Registry,
        Scope = StartupScope.Hkcu,
    };

    private HarnessState Create()
    {
        var log = new FakeLogSink();
        var clock = new FakeClock();
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);
        var store = new GuardInspectionStore(paths, log);
        return new HarnessState(paths, log, clock, store);
    }

    private sealed record HarnessState(PathService Paths, FakeLogSink Log, FakeClock Clock, GuardInspectionStore Store);
}
