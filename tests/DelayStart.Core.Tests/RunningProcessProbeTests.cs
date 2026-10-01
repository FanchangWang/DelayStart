using DelayStart.Core.Launch;
using DelayStart.Core.Models;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="RunningProcessProbe"/> 的行为测试（D138）。
/// </summary>
/// <remarks>
/// 🔴 这一组**不测**"某个进程现在跑着没有"（那依赖真实进程，是环境相关的），
/// 只测那条可以被静态钉住的不变量：**空进程名不得被当成"全都在跑"**。
/// </remarks>
public sealed class RunningProcessProbeTests
{
    [Fact]
    public void Collect_EmptyProcessName_ReturnsEmpty_AndNeverReportsRunning()
    {
        // 🔴 反向的关键：空名字会让 GetProcessesByName 抛或返回一堆无关进程。
        // 若这里返回非空，IsAlreadyRunning 就会把一个**空目标**判成"已在跑"，
        // 而那会让一个正常程序永远启动不了 —— 方向与 D87/D90 正好相反。
        Assert.Empty(RunningProcessProbe.Collect(string.Empty));
        Assert.Empty(RunningProcessProbe.Collect("   "));
    }

    [Fact]
    public void Collect_KnownAbsentName_ReturnsEmpty()
    {
        // 用一个几乎不可能存在的进程名，避免结果依赖真实进程表。
        Assert.Empty(RunningProcessProbe.Collect(
            "delaystart-no-such-process-" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Collect_ReturnsEntriesCarryingTheirProcessName()
    {
        // 自指：测试宿主自己一定在跑。验证"取到了就带得上进程名"这条基本契约，
        // 以及**取不到模块路径也不会抛**（权限不足是常态，不是异常情况）。
        var snapshot = RunningProcessProbe.Collect("dotnet");

        Assert.NotNull(snapshot);
        foreach (var process in snapshot)
        {
            Assert.Equal("dotnet", process.ProcessName);
        }
    }

    [Fact]
    public void IsAlreadyRunning_TargetCannotBeResolved_DoesNotSkip()
    {
        // 解析不出目标 ⇒ 无从判断"是不是同一个 exe"，而误判成"已在跑"更糟。
        var item = new DelayedItem
        {
            Id = "manual:none:x",
            Name = "手动项",
            Path = string.Empty,
            Source = StartupSource.Manual,
            Scope = StartupScope.None,
        };

        Assert.False(RunningProcessProbe.IsAlreadyRunning(item, log: null, out var reason));
        Assert.Empty(reason);
    }
}
