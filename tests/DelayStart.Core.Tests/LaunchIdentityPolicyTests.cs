using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="LaunchIdentityPolicy"/> 的单元测试（D147：来源项默认以管理员身份启动）。
/// </summary>
/// <remarks>
/// <para>
/// 这一段此前只以一个表达式的形式活在 <c>DelayEditorDialog.RunAsAdmin</c> 里（零覆盖），
/// 判错不会报错、构建照样绿。加上判据之后最可能的退化是"被顺手简化成只读
/// <c>RequiresAdminRun</c>"—— 那会让 UWP 条目走进管理员分支。所以两条判据各自都要钉死。
/// </para>
/// <para>
/// 全部是纯数据入参，不碰注册表 / 任务计划 / 文件系统（硬约束 10）。
/// </para>
/// </remarks>
public sealed class LaunchIdentityPolicyTests
{
    [Fact]
    public void DefaultRunAsAdmin_来源项按原定义需要管理员_默认选中管理员()
    {
        // Arrange：🔴 本次功能的核心回归 —— 计划任务 RunLevel=Highest 的那一类。
        var entry = Entry(StartupSource.ScheduledTask, requiresAdminRun: true);

        // Act / Assert
        Assert.True(LaunchIdentityPolicy.DefaultRunAsAdmin(entry));
    }

    [Fact]
    public void DefaultRunAsAdmin_来源项不需要管理员_默认普通身份()
    {
        // Arrange：RunLevel=LUA 的计划任务 —— 与上面只差 RequiresAdminRun 一个字段，
        // 这条确保判据确实读了它，而不是"计划任务一律管理员"。
        var entry = Entry(StartupSource.ScheduledTask, requiresAdminRun: false);

        // Act / Assert
        Assert.False(LaunchIdentityPolicy.DefaultRunAsAdmin(entry));
    }

    [Fact]
    public void DefaultRunAsAdmin_来源项需要管理员但目标是Uwp_仍按普通身份()
    {
        // Arrange：🔴 D45 的不变量 —— UWP 进程恒为普通用户身份，中转外壳用谁的令牌都不改结果。
        // 这里是**矛盾输入**（RequiresAdminRun 为真却要普通），它证明 UWP 排除分支是显式写出来的，
        // 而不是"恰好因为 RequiresAdminRun 为假才通过"。删掉排除分支，这条立刻红。
        var entry = Entry(StartupSource.Uwp, requiresAdminRun: true);

        // Act / Assert
        Assert.False(LaunchIdentityPolicy.DefaultRunAsAdmin(entry));
    }

    [Fact]
    public void DefaultRunAsAdmin_Uwp来源但路径是解析名_按普通身份()
    {
        // Arrange：D41 的兜底 —— 路径已是 shell:AppsFolder\… 时，即便来源字段没写成 Uwp 也算 UWP。
        // 这条挡住"来源判错就走进管理员分支"：判错的代价是 UWP 条目根本启动不了。
        var entry = Entry(
            StartupSource.Manual,
            requiresAdminRun: true,
            path: @"shell:AppsFolder\Microsoft.X_8wekyb3d8bbwe!App");

        // Act / Assert
        Assert.False(LaunchIdentityPolicy.DefaultRunAsAdmin(entry));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void DefaultRunAsAdmin_带有操作性标记_不改变默认值(
        bool isMissing,
        bool isProtected,
        bool isTakenOver)
    {
        // Arrange：🔴 IsMissing / IsProtected / IsTakenOver 说的是「这一项现在还能不能被接管」，
        // 与「它启动时要什么身份」正交，所以三者都**不得**影响默认身份。
        // 依据有两条：① 语义正交；② 失效项与受保护项走不到这个编辑器
        // （CanTakeOver 已挡掉），把它们接进身份默认值只会造一条永远走不到的分支。
        // 唯一还能走到的是命令行 --takeover，那里同样由调用方按 CanTakeOver 把关。
        var entry = Entry(StartupSource.ScheduledTask, requiresAdminRun: true);
        entry = new StartupEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            Path = entry.Path,
            Source = entry.Source,
            Scope = entry.Scope,
            SourceKey = entry.SourceKey,
            RequiresAdminRun = entry.RequiresAdminRun,
            IsMissing = isMissing,
            IsProtected = isProtected,
            IsTakenOver = isTakenOver,
        };

        // Act / Assert
        Assert.True(LaunchIdentityPolicy.DefaultRunAsAdmin(entry));
    }

    [Fact]
    public void DefaultRunAsAdmin_条目为null_抛ArgumentNullException()
    {
        // 判据是纯函数，不做隐式的 null 兜底 —— null 属于编程错误，必须当场炸出来，
        // 不能退化成"什么来源都按普通身份"这种静默正确。
        Assert.Throws<ArgumentNullException>(() => LaunchIdentityPolicy.DefaultRunAsAdmin(null!));
    }

    private static StartupEntry Entry(
        StartupSource source,
        bool requiresAdminRun,
        string path = @"C:\app\x.exe")
        => new()
        {
            Id = "scheduled-task:none:\\Demo",
            Name = "Demo",
            Path = path,
            Source = source,
            Scope = StartupScope.None,
            SourceKey = @"\Demo",
            RequiresAdminRun = requiresAdminRun,
        };
}
