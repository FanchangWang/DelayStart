using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// 管理端启动时清理应用自有计划任务配置的单元测试（B3）。
/// </summary>
/// <remarks>
/// 清理器只改配置，不调用计划任务来源或注册端；自有任务的当前状态由管理端启动自检负责。
/// 所有测试使用内存配置和假日志，不触碰真实计划任务库。
/// </remarks>
public sealed class OwnedScheduledTaskCleanupTests
{
    [Fact]
    public void RemoveOwnedTasks_RemovesBothOwnedEntriesAndPreservesOthers()
    {
        var store = new InMemoryConfigStore();
        store.Seed(new AppConfig
        {
            Items =
            [
                OwnedItem(@"\DelayStartScheduler", "调度"),
                new DelayedItem
                {
                    Id = "registry:hkcu:other",
                    Source = StartupSource.Registry,
                    Scope = StartupScope.Hkcu,
                    SourceKey = "other",
                },
                OwnedItem(@"\DelayStartGuard", "守卫"),
            ],
        });
        var log = new FakeLogSink();
        var service = new OwnedScheduledTaskConfigCleanupService(store, log);

        var report = service.RemoveOwnedTasks();

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.RemovedCount);
        Assert.Equal(1, store.SaveCount);
        var remaining = Assert.Single(store.Snapshot().Items);
        Assert.Equal("registry:hkcu:other", remaining.Id);
        Assert.True(log.Contains(LogLevel.Info, "已从延时配置移除 2 条"));
    }

    [Fact]
    public void RemoveOwnedTasks_NoOwnedEntries_DoesNotSave()
    {
        var store = new InMemoryConfigStore();
        store.Seed(new AppConfig
        {
            Items =
            [
                new DelayedItem
                {
                    Id = "registry:hkcu:other",
                    Source = StartupSource.Registry,
                    Scope = StartupScope.Hkcu,
                    SourceKey = "other",
                },
            ],
        });
        var service = new OwnedScheduledTaskConfigCleanupService(store, new FakeLogSink());

        var report = service.RemoveOwnedTasks();

        Assert.True(report.Succeeded);
        Assert.Equal(0, report.RemovedCount);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void RemoveOwnedTasks_ConfigLoadFails_DoesNotSave()
    {
        var store = new InMemoryConfigStore
        {
            LoadException = new InvalidOperationException("模拟配置损坏"),
        };
        var log = new FakeLogSink();
        var service = new OwnedScheduledTaskConfigCleanupService(store, log);

        var report = service.RemoveOwnedTasks();

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.RemovedCount);
        Assert.Equal(0, store.SaveCount);
        Assert.True(log.HasExceptionAt(LogLevel.Error));
    }

    [Fact]
    public void RemoveOwnedTasks_SaveFails_PreservesPersistedConfig()
    {
        var store = new InMemoryConfigStore
        {
            SaveException = new IOException("模拟配置保存失败"),
        };
        store.Seed(new AppConfig { Items = [OwnedItem(@"\DelayStartGuard", "守卫")] });
        var log = new FakeLogSink();
        var service = new OwnedScheduledTaskConfigCleanupService(store, log);

        var report = service.RemoveOwnedTasks();

        Assert.False(report.Succeeded);
        Assert.Equal(0, report.RemovedCount);
        Assert.Single(store.Snapshot().Items);
        Assert.True(log.HasExceptionAt(LogLevel.Error));
    }

    private static DelayedItem OwnedItem(string sourceKey, string name) => new()
    {
        Id = $"scheduledtask:none:{sourceKey}",
        Name = name,
        Source = StartupSource.ScheduledTask,
        Scope = StartupScope.None,
        SourceKey = sourceKey,
    };
}
