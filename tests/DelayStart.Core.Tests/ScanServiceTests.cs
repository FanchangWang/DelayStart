using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>ScanService</c> 的编排逻辑测试（重点：FR-1.4「单条/单源失败不影响其余」）。
/// </summary>
/// <remarks>
/// 真机上很难**按需**造出"某一个来源整体打不开"的场景（例如让注册表键突然被 ACL 拒绝），
/// 所以这条需求只能靠假实现来获得确定性证据 —— 这正是 <c>D33</c> 的核心论点。
/// </remarks>
public sealed class ScanServiceTests
{
    [Fact]
    public void Scan_OneSourceThrows_OtherSourcesStillContribute()
    {
        var broken = new FakeStartupSource
        {
            DisplayName = "坏来源",
            ScanException = new StartupOperationException(
                StartupFailureReason.ReadFailed,
                entryId: string.Empty,
                message: "模拟打不开"),
        };

        var healthy = new FakeStartupSource
        {
            Kind = StartupSource.StartupFolder,
            Scope = StartupScope.UserFolder,
            DisplayName = "好来源",
            Entries = [Entry("startupfolder:userfolder:a", "甲")],
        };

        var result = Create(broken, healthy).Scan();

        Assert.Single(result.Entries);
        Assert.Equal("甲", result.Entries[0].Name);
        Assert.True(broken.ScanCount > 0);
    }

    [Fact]
    public void Scan_OneSourceThrows_RecordsFailureWithDisplayNameAndMessage()
    {
        var broken = new FakeStartupSource
        {
            DisplayName = "计划任务",
            ScanException = new StartupOperationException(
                StartupFailureReason.ReadFailed,
                entryId: string.Empty,
                message: "任务服务不可用"),
        };

        var result = Create(broken).Scan();

        var failure = Assert.Single(result.Failures);
        Assert.Equal("计划任务", failure.DisplayName);
        Assert.Contains("任务服务不可用", failure.Message, StringComparison.Ordinal);
        Assert.True(result.HasFailures);
    }

    [Fact]
    public void Scan_OneSourceThrows_LogsError()
    {
        var log = new FakeLogSink();
        var broken = new FakeStartupSource { ScanException = new InvalidOperationException("炸了") };

        _ = new ScanService([broken], new InMemoryConfigStore(), log).Scan();

        Assert.True(log.Contains(LogLevel.Error, "扫描失败"));
        Assert.True(log.HasExceptionAt(LogLevel.Error));
    }

    [Fact]
    public void Scan_AllSourcesHealthy_ReportsNoFailures()
    {
        var result = Create(new FakeStartupSource()).Scan();

        Assert.False(result.HasFailures);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Scan_PassesTakenOverKeysToEverySource()
    {
        var store = new InMemoryConfigStore();
        store.Seed(new AppConfig
        {
            Items = [new DelayedItem { Id = "registry:hkcu:weixin", Name = "微信" }],
        });

        var source = new FakeStartupSource
        {
            Entries = [Entry("registry:hkcu:weixin", "微信")],
        };

        var result = new ScanService([source], store, new FakeLogSink()).Scan();

        Assert.NotNull(source.LastTakenOverKeys);
        Assert.Contains("registry:hkcu:weixin", source.LastTakenOverKeys);
        // FR-1.6：靠稳定主键匹配，所以扫描结果里的该项必须被标记为已接管。
        Assert.True(result.Entries[0].IsTakenOver);
    }

    [Fact]
    public void Scan_EntryNotInConfig_IsNotMarkedTakenOver()
    {
        var source = new FakeStartupSource { Entries = [Entry("registry:hkcu:other", "别的")] };

        var result = Create(source).Scan();

        Assert.False(result.Entries[0].IsTakenOver);
    }

    [Fact]
    public void Scan_SortsBySourceThenScopeThenName()
    {
        // 名称刻意用 ASCII：排序用的是 OrdinalIgnoreCase，中文会按码位排
        // （"乙" U+4E59 在 "甲" U+7532 之前），拿中文写断言反而看不出意图。
        var folderSource = new FakeStartupSource
        {
            Kind = StartupSource.StartupFolder,
            Scope = StartupScope.UserFolder,
            Entries = [Entry("startupfolder:userfolder:z", "Bravo", StartupSource.StartupFolder, StartupScope.UserFolder)],
        };

        var registrySource = new FakeStartupSource
        {
            Kind = StartupSource.Registry,
            Scope = StartupScope.Hkcu,
            // 故意让输入的 id 次序（z 在前）与排序结果（Alpha 在前）相反。
            Entries =
            [
                Entry("registry:hkcu:z", "Zulu"),
                Entry("registry:hkcu:a", "Alpha"),
            ],
        };

        var result = Create(folderSource, registrySource).Scan();

        // 先按来源（Registry=0 排在 StartupFolder=1 前），同来源内按**显示名**排序。
        Assert.Equal(
            ["registry:hkcu:a", "registry:hkcu:z", "startupfolder:userfolder:z"],
            result.Entries.Select(static entry => entry.Id));
    }

    [Fact]
    public void Scan_ConfigUnsupportedVersion_TreatsEverythingAsNotTakenOver()
    {
        // 前向兼容保护会抛 ConfigVersionUnsupported；扫描本身不该因此不可用。
        var store = new ThrowingConfigStore(
            new StartupOperationException(
                StartupFailureReason.ConfigVersionUnsupported,
                entryId: string.Empty,
                message: "配置版本过高"));

        var source = new FakeStartupSource { Entries = [Entry("registry:hkcu:a", "甲")] };

        var result = new ScanService([source], store, new FakeLogSink()).Scan();

        Assert.Single(result.Entries);
        Assert.False(result.Entries[0].IsTakenOver);
    }

    [Fact]
    public void ActiveCount_ExcludesTakenOverDisabledAndMissing()
    {
        var source = new FakeStartupSource
        {
            Entries =
            [
                Entry("registry:hkcu:a", "正常"),
                Entry("registry:hkcu:b", "被禁用", isEnabled: false),
                Entry("registry:hkcu:c", "已失效", isMissing: true),
                Entry("registry:hkcu:d", "已接管"),
            ],
        };

        var store = new InMemoryConfigStore();
        store.Seed(new AppConfig { Items = [new DelayedItem { Id = "registry:hkcu:d", Name = "已接管" }] });

        var result = new ScanService([source], store, new FakeLogSink()).Scan();

        Assert.Equal(4, result.TotalCount);
        Assert.Equal(1, result.ActiveCount);
        Assert.Equal(1, result.TakenOverCount);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ScanService Create(params IStartupSource[] sources)
        => new(sources, new InMemoryConfigStore(), new FakeLogSink());

    private static StartupEntry Entry(
        string id,
        string name,
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu,
        bool isEnabled = true,
        bool isMissing = false)
        => new()
        {
            Id = id,
            Name = name,
            Path = @"C:\Program Files\Demo\demo.exe",
            Source = source,
            Scope = scope,
            SourceKey = id,
            IsEnabled = isEnabled,
            IsMissing = isMissing,
        };

    /// <summary>只用于模拟"配置读取必定失败"的替身。</summary>
    private sealed class ThrowingConfigStore : IAppConfigStore
    {
        private readonly StartupOperationException _exception;

        public ThrowingConfigStore(StartupOperationException exception) => _exception = exception;

        public string ConfigFilePath => "(抛异常)";

        public AppConfig Load() => throw _exception;

        public void Save(AppConfig config) => throw _exception;
    }
}
