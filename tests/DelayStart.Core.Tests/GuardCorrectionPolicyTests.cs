using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="GuardCorrectionPolicy"/> 的单元测试（D74）：哪些被接管的项需要再次软禁用。
/// </summary>
public sealed class GuardCorrectionPolicyTests
{
    [Fact]
    public void Select_ManagedAndEnabled_IsSelected()
    {
        var corrections = GuardCorrectionPolicy.SelectCorrections(
            [Item("registry:hkcu:alpha")],
            [Entry("registry:hkcu:alpha", enabled: true)],
            []);

        var entry = Assert.Single(corrections);
        Assert.Equal("registry:hkcu:alpha", entry.Id);
    }

    [Fact]
    public void Select_ManagedButStillDisabled_IsNotSelected()
    {
        // 没被写回 = 不需要纠正。这是"每一轮都把全清单当成要纠正"这类 bug 的防线：
        // 假纠正记录会把日志淹掉，让"纠正过什么"彻底失去意义。
        var corrections = GuardCorrectionPolicy.SelectCorrections(
            [Item("registry:hkcu:alpha")],
            [Entry("registry:hkcu:alpha", enabled: false)],
            []);

        Assert.Empty(corrections);
    }

    [Fact]
    public void Select_NotManaged_IsNotSelected()
    {
        // 用户没接管过的项，启不启用都是用户自己的事，守卫不插手。
        var corrections = GuardCorrectionPolicy.SelectCorrections(
            [Item("registry:hkcu:alpha")],
            [Entry("registry:hkcu:alpha", enabled: false), Entry("registry:hkcu:beta", enabled: true)],
            []);

        Assert.Empty(corrections);
    }

    [Fact]
    public void Select_ManualItem_IsExcluded()
    {
        // 手动条目在系统里没有对应物，永远匹配不到扫描结果 —— 不能因此出问题。
        var corrections = GuardCorrectionPolicy.SelectCorrections(
            [Item("manual:none:guid", source: StartupSource.Manual, scope: StartupScope.None)],
            [Entry("manual:none:guid", enabled: true)],
            []);

        Assert.Empty(corrections);
    }

    [Fact]
    public void Select_OrphanEntry_IsNotSelected()
    {
        // 系统项已被删除 —— 没有对象可禁用，归 GuardStalePolicy 通报。
        var corrections = GuardCorrectionPolicy.SelectCorrections(
            [Item("registry:hkcu:gone")],
            [],
            []);

        Assert.Empty(corrections);
    }

    [Fact]
    public void Select_FailedSource_IsNotJudged()
    {
        var corrections = GuardCorrectionPolicy.SelectCorrections(
            [Item("registry:hkcu:alpha")],
            [Entry("registry:hkcu:alpha", enabled: true)],
            [new ScanScope(StartupSource.Registry, StartupScope.Hkcu)]);

        Assert.Empty(corrections);
    }

    [Fact]
    public void Select_FailureOfOtherScope_DoesNotSuppress()
    {
        // HKLM-Wow 失败不该让 HKLM 的纠正也停掉 —— 来源实例是逐个独立的。
        var corrections = GuardCorrectionPolicy.SelectCorrections(
            [Item("registry:hkcu:alpha")],
            [Entry("registry:hkcu:alpha", enabled: true)],
            [new ScanScope(StartupSource.Registry, StartupScope.HklmWow)]);

        Assert.Single(corrections);
    }

    [Fact]
    public void Select_MultipleManagedItems_ReturnsOnlyWrittenBackOnes()
    {
        var corrections = GuardCorrectionPolicy.SelectCorrections(
            [Item("registry:hkcu:a"), Item("registry:hkcu:b"), Item("scheduledtask:none:c", StartupSource.ScheduledTask, StartupScope.None)],
            [
                Entry("registry:hkcu:a", enabled: true),
                Entry("registry:hkcu:b", enabled: false),
                Entry("scheduledtask:none:c", enabled: true, source: StartupSource.ScheduledTask, scope: StartupScope.None),
            ],
            []);

        Assert.Equal(2, corrections.Count);
        Assert.Contains(corrections, entry => entry.Id == "registry:hkcu:a");
        Assert.Contains(corrections, entry => entry.Id == "scheduledtask:none:c");
    }

    [Fact]
    public void Select_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => GuardCorrectionPolicy.SelectCorrections(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => GuardCorrectionPolicy.SelectCorrections([], null!, []));
        Assert.Throws<ArgumentNullException>(() => GuardCorrectionPolicy.SelectCorrections([], [], null!));
    }

    private static DelayedItem Item(
        string id,
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu)
        => new() { Id = id, Name = id, Source = source, Scope = scope };

    private static StartupEntry Entry(
        string id,
        bool enabled,
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu)
        => new()
        {
            Id = id,
            Name = id,
            Source = source,
            Scope = scope,
            SourceKey = id,
            IsEnabled = enabled,
        };
}
