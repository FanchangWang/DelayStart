using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="LaunchTargetResolver"/> 的单元测试（D76）：各来源的路径 / 参数形态 → 启动目标。
/// </summary>
/// <remarks>
/// 这层推导是"防双启动"的入口，**推导错方向的代价不对称** ——
/// 推导不出来只是不做检查（多启动一次），而推导错了会让条目永不启动。
/// 因此这里覆盖的重点是"哪些形态必须推导不出来"。
/// </remarks>
public sealed class LaunchTargetResolverTests
{
    [Fact]
    public void Resolve_PlainExePath_ReturnsPathAndProcessName()
    {
        var target = LaunchTargetResolver.Resolve(Item(@"C:\Apps\Foo\foo.exe"));

        Assert.NotNull(target);
        Assert.Equal(@"C:\Apps\Foo\foo.exe", target.ExecutablePath);
        Assert.Equal("foo", target.ProcessName);
    }

    [Fact]
    public void Resolve_EnvironmentVariable_IsExpanded()
    {
        var expanded = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Foo\foo.exe");

        var target = LaunchTargetResolver.Resolve(Item(@"%ProgramFiles%\Foo\foo.exe"));

        Assert.NotNull(target);
        Assert.Equal(expanded, target.ExecutablePath);
    }

    [Fact]
    public void Resolve_QuotedExePath_Unquotes()
    {
        var target = LaunchTargetResolver.Resolve(Item(@"""C:\Apps\Foo\foo.exe"""));

        Assert.NotNull(target);
        Assert.Equal(@"C:\Apps\Foo\foo.exe", target.ExecutablePath);
    }

    [Fact]
    public void Resolve_ScheduledTaskWrapper_TakesNestedTargetFromArguments()
    {
        // 计划任务最典型的形态：动作是 cmd.exe /c start "" "真目标"，真目标只在参数里。
        var target = LaunchTargetResolver.Resolve(Item(
            @"C:\Windows\System32\cmd.exe",
            "/c start \"\" \"C:\\Apps\\Foo\\foo.exe\" --minimized",
            StartupSource.ScheduledTask,
            StartupScope.None));

        Assert.NotNull(target);
        Assert.Equal(@"C:\Apps\Foo\foo.exe", target.ExecutablePath);
        Assert.Equal("foo", target.ProcessName);
    }

    [Fact]
    public void Resolve_BareWrapperNameWithoutPath_StillLooksIntoArguments()
    {
        // 动作路径也可能就写 cmd.exe（无全限定）—— 包装器判定必须按文件名，不能按"是否全限定"。
        var target = LaunchTargetResolver.Resolve(Item(
            "cmd.exe",
            "/c \"C:\\Apps\\Foo\\foo.exe\"",
            StartupSource.ScheduledTask,
            StartupScope.None));

        Assert.NotNull(target);
        Assert.Equal(@"C:\Apps\Foo\foo.exe", target.ExecutablePath);
    }

    [Fact]
    public void Resolve_WrapperWithoutUsableArguments_ReturnsNull()
    {
        // 🔴 关键防线：这里如果返回 cmd.exe，那条被接管的计划任务会每轮判"已经跑着了"、
        // 永不启动（cmd 几乎总是有实例存活）。
        Assert.Null(LaunchTargetResolver.Resolve(Item(
            @"C:\Windows\System32\cmd.exe",
            "/c echo hello",
            StartupSource.ScheduledTask,
            StartupScope.None)));
    }

    [Fact]
    public void Resolve_UwpAumid_ReturnsNull()
    {
        // UWP 的 Path 是裸 AUMID，不是文件路径。
        Assert.Null(LaunchTargetResolver.Resolve(Item(
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            source: StartupSource.Uwp)));
    }

    [Fact]
    public void Resolve_ShortcutFile_ReturnsNull()
    {
        // 能进配置的 .lnk 都是管理端 COM 解析失败后回落的 —— 调度端是 AOT、零 COM，
        // 在这里另写一份解析器既违约束也无胜算。
        Assert.Null(LaunchTargetResolver.Resolve(Item(
            @"C:\Users\me\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\app.lnk",
            source: StartupSource.StartupFolder,
            scope: StartupScope.UserFolder)));
    }

    [Theory]
    [InlineData(@"C:\Scripts\run.ps1")]
    [InlineData(@"C:\Scripts\run.cmd")]
    [InlineData(@"C:\Scripts\run.bat")]
    public void Resolve_ScriptTargets_ReturnsNull(string path)
    {
        // 用户批复 D2：不做脚本级判定 —— 真正跑起来的是 pwsh.exe / cmd.exe，不是脚本本身。
        Assert.Null(LaunchTargetResolver.Resolve(Item(path)));
    }

    [Fact]
    public void Resolve_BareExecutableName_ReturnsNull()
    {
        // 裸程序名要经 PATH / App Paths 才能解析；猜错路径 = 误判"已存在" = 永不启动。
        Assert.Null(LaunchTargetResolver.Resolve(Item("chrome.exe")));
    }

    [Fact]
    public void Resolve_EmptyPath_ReturnsNull()
    {
        Assert.Null(LaunchTargetResolver.Resolve(Item(string.Empty)));
    }

    [Fact]
    public void Resolve_NullItem_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LaunchTargetResolver.Resolve(null!));
    }

    private static DelayedItem Item(
        string path,
        string arguments = "",
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu)
        => new()
        {
            Id = "registry:hkcu:test",
            Name = "test",
            Path = path,
            Arguments = arguments,
            Source = source,
            Scope = scope,
        };
}
