using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="GuardNewItemPolicy"/> 的单元测试（D74）：本次扫描 − 基线 = 新增，以及两条排除规则。
/// </summary>
public sealed class GuardNewItemPolicyTests
{
    [Fact]
    public void Select_NoBaseline_ReturnsEmpty()
    {
        // 首次运行只负责建立基线：此时"上次"不存在，任何差集都会把当前全部条目报成"新增"。
        var newItems = GuardNewItemPolicy.SelectNewItems(
            [Entry("registry:hkcu:a"), Entry("registry:hkcu:b")],
            baselineIds: null,
            failedScopes: []);

        Assert.Empty(newItems);
    }

    [Fact]
    public void Select_IdMissingFromBaseline_IsNew()
    {
        var newItems = GuardNewItemPolicy.SelectNewItems(
            [Entry("registry:hkcu:a"), Entry("registry:hkcu:new")],
            new HashSet<string>(["registry:hkcu:a"], StringComparer.Ordinal),
            []);

        var entry = Assert.Single(newItems);
        Assert.Equal("registry:hkcu:new", entry.Id);
    }

    [Fact]
    public void Select_AllKnown_ReturnsEmpty()
    {
        var newItems = GuardNewItemPolicy.SelectNewItems(
            [Entry("registry:hkcu:a")],
            new HashSet<string>(["registry:hkcu:a"], StringComparer.Ordinal),
            []);

        Assert.Empty(newItems);
    }

    [Fact]
    public void Select_FailedScope_IsExcludedFromDiff()
    {
        // 来源整体失败 ⇒ 这次根本没扫出来。把它当"条目全没了"会在下一次运行反过来
        // 把一整批老条目报成"新增"—— 不完整的快照不能当"新出现"。
        var newItems = GuardNewItemPolicy.SelectNewItems(
            [
                Entry("scheduledtask:none:a", source: StartupSource.ScheduledTask, scope: StartupScope.None),
                Entry("registry:hkcu:b"),
            ],
            new HashSet<string>(["registry:hkcu:b"], StringComparer.Ordinal),
            [new ScanScope(StartupSource.ScheduledTask, StartupScope.None)]);

        Assert.Empty(newItems);
    }

    [Fact]
    public void Select_FailureOfOtherScope_DoesNotSuppress()
    {
        var newItems = GuardNewItemPolicy.SelectNewItems(
            [Entry("registry:hkcu:new")],
            new HashSet<string>(StringComparer.Ordinal),
            [new ScanScope(StartupSource.Registry, StartupScope.HklmWow)]);

        Assert.Single(newItems);
    }

    [Fact]
    public void Select_EmptyBaseline_ReportsEverything()
    {
        // 基线存在但为空 = 上次一台自启动项都没有。这不是"首次运行"，如实报新增。
        var newItems = GuardNewItemPolicy.SelectNewItems(
            [Entry("registry:hkcu:a"), Entry("registry:hkcu:b")],
            new HashSet<string>(StringComparer.Ordinal),
            []);

        Assert.Equal(2, newItems.Count);
    }

    [Fact]
    public void Select_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => GuardNewItemPolicy.SelectNewItems(null!, null, []));
        Assert.Throws<ArgumentNullException>(
            () => GuardNewItemPolicy.SelectNewItems([], null, null!));
    }

    private static StartupEntry Entry(
        string id,
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu)
        => new()
        {
            Id = id,
            Name = id,
            Source = source,
            Scope = scope,
            SourceKey = id,
        };
}
