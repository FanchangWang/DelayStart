using DelayStart.Core.Models;
using DelayStart.Management.Services;
using DelayStart.Management.Sources;

namespace DelayStart.Core.Tests;

/// <summary>
/// 应用自有计划任务的扫描过滤与历史条目识别测试（B3）。
/// </summary>
/// <remarks>
/// 过滤必须只认两个精确的根路径：调度端 <c>\DelayStartScheduler</c> 与守卫端
/// <c>\DelayStartGuard</c>。名字相似、位于子文件夹或带 Microsoft 前缀的第三方任务都不能误伤。
/// </remarks>
public sealed class ScheduledTaskOwnedFilterTests
{
    [Theory]
    [InlineData(@"\DelayStartScheduler")]
    [InlineData(@"\DelayStartGuard")]
    [InlineData(@"\delaystartscheduler")]
    [InlineData(@"\DELAYSTARTGUARD")]
    public void IsOwnedTask_ExactApplicationRootPath_True(string path)
    {
        Assert.True(ScheduledTaskSource.IsOwnedTask(path));
        Assert.True(OwnedScheduledTaskPolicy.IsOwnedPath(path));
    }

    [Theory]
    [InlineData(@"\DelayStartGuardExtra")]
    [InlineData(@"\DelayStartSchedulerBackup")]
    [InlineData(@"\MyFolder\DelayStartGuard")]
    [InlineData(@"\Microsoft\DelayStartGuard")]
    [InlineData(@"\MicrosoftEdgeUpdateTaskMachineCore")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsOwnedTask_NonExactPath_False(string? path)
    {
        Assert.False(ScheduledTaskSource.IsOwnedTask(path));
        Assert.False(OwnedScheduledTaskPolicy.IsOwnedPath(path));
    }

    [Fact]
    public void IsHistoricalOwnedItem_ExactGuardTask_True()
    {
        var item = new DelayedItem
        {
            Id = "scheduledtask:none:\\delaystartguard",
            Source = StartupSource.ScheduledTask,
            Scope = StartupScope.None,
            SourceKey = @"\DelayStartGuard",
        };

        Assert.True(OwnedScheduledTaskPolicy.IsHistoricalOwnedItem(item));
    }

    [Fact]
    public void IsHistoricalOwnedItem_ExactSchedulerTask_True()
    {
        var item = new DelayedItem
        {
            Id = "scheduledtask:none:\\delaystartscheduler",
            Source = StartupSource.ScheduledTask,
            Scope = StartupScope.None,
            SourceKey = @"\DelayStartScheduler",
        };

        Assert.True(OwnedScheduledTaskPolicy.IsHistoricalOwnedItem(item));
    }

    [Theory]
    [InlineData(StartupSource.Registry, StartupScope.None, @"\DelayStartGuard")]
    [InlineData(StartupSource.ScheduledTask, StartupScope.Hkcu, @"\DelayStartGuard")]
    [InlineData(StartupSource.ScheduledTask, StartupScope.None, @"\MyFolder\DelayStartGuard")]
    [InlineData(StartupSource.ScheduledTask, StartupScope.None, @"\DelayStartGuardExtra")]
    public void IsHistoricalOwnedItem_WrongSourceScopeOrKey_False(
        StartupSource source,
        StartupScope scope,
        string sourceKey)
    {
        var item = new DelayedItem
        {
            Source = source,
            Scope = scope,
            SourceKey = sourceKey,
        };

        Assert.False(OwnedScheduledTaskPolicy.IsHistoricalOwnedItem(item));
    }
}
