using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="DuplicateLaunchPolicy"/> 的单元测试（D76）：目标进程已在运行 → 放弃启动。
/// </summary>
/// <remarks>
/// 覆盖重点是**两个方向的不对称**：漏判只是多启动一次，误判却是条目永不启动。
/// 因此"名字相同但路径不同"、"路径读不到"这两种情形都必须落在"不跳过"。
/// </remarks>
public sealed class DuplicateLaunchPolicyTests
{
    private const string Target = @"C:\Apps\Foo\foo.exe";

    [Fact]
    public void Decide_SameModulePath_Skips()
    {
        var decision = DuplicateLaunchPolicy.Decide(
            new LaunchTarget(Target, "foo"),
            [new RunningProcessInfo("foo", Target)]);

        Assert.True(decision.Skip);
    }

    [Fact]
    public void Decide_SameModulePathDifferentCase_Skips()
    {
        var decision = DuplicateLaunchPolicy.Decide(
            new LaunchTarget(Target, "foo"),
            [new RunningProcessInfo("foo", @"c:\apps\foo\FOO.EXE")]);

        Assert.True(decision.Skip);
    }

    [Fact]
    public void Decide_SameProcessNameDifferentPath_DoesNotSkip()
    {
        // 两个不同产品都叫 updater.exe 是真实存在的；只比名字会让其中一个永不启动。
        var decision = DuplicateLaunchPolicy.Decide(
            new LaunchTarget(Target, "foo"),
            [new RunningProcessInfo("foo", @"C:\Other\Bar\foo.exe")]);

        Assert.False(decision.Skip);
    }

    [Fact]
    public void Decide_ModulePathUnreadable_DoesNotSkip()
    {
        // 读不到 MainModule 的进程（权限不足 / 受保护 / 刚退出）绝不能退化成按名字匹配。
        var decision = DuplicateLaunchPolicy.Decide(
            new LaunchTarget(Target, "foo"),
            [new RunningProcessInfo("foo", null)]);

        Assert.False(decision.Skip);
    }

    [Fact]
    public void Decide_NoProcesses_DoesNotSkip()
    {
        var decision = DuplicateLaunchPolicy.Decide(new LaunchTarget(Target, "foo"), []);

        Assert.False(decision.Skip);
    }

    [Fact]
    public void Decide_UnresolvableTarget_DoesNotSkip()
    {
        // 推导不出目标（.lnk / 脚本 / AUMID）= 不做这项检查，照常启动。
        var decision = DuplicateLaunchPolicy.Decide(
            null,
            [new RunningProcessInfo("foo", Target)]);

        Assert.False(decision.Skip);
    }

    [Fact]
    public void Decide_OneOfManyMatches_Skips()
    {
        var decision = DuplicateLaunchPolicy.Decide(
            new LaunchTarget(Target, "foo"),
            [
                new RunningProcessInfo("foo", @"C:\Other\bar.exe"),
                new RunningProcessInfo("foo", null),
                new RunningProcessInfo("foo", Target),
            ]);

        Assert.True(decision.Skip);
    }

    [Fact]
    public void Decide_NullProcessList_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => DuplicateLaunchPolicy.Decide(new LaunchTarget(Target, "foo"), null!));
    }
}
