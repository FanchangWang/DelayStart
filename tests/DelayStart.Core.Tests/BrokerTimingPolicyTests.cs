using DelayStart.Core.Launch;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="BrokerTimingPolicy"/>：UIAccess 降权链的时间参数与轮询截止判定（D70）。
/// </summary>
/// <remarks>
/// <para>
/// 三段时间原先是启动器里的私有字段、零覆盖。它们的取值直接决定两件用户看得见的事：
/// "目标多久没退出就算秒退"（E4：4 秒）与"中转器多久不回写结果就算超时"
/// （超时 ⇒ 本条目判失败，**不提权回退**，D70）。漂移了没有任何编译期信号。
/// </para>
/// <para>
/// 真正等待目标退出的 <c>WaitForSingleObject</c> 在中转器进程里、真正读结果文件的轮询要
/// 真文件与真进程，单元测试禁止触碰进程 —— 因此这里只钉住<b>时间判定</b>与<b>取值</b>。
/// </para>
/// </remarks>
public sealed class BrokerTimingPolicyTests
{
    /// <summary>轮询起点的固定基准时刻（避免测试依赖真时钟）。</summary>
    private static readonly DateTime Start = new(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ExitWaitTimeout_秒退等待窗口为四秒_取值不得漂移()
    {
        // Arrange / Act / Assert
        // 该值经 job.WaitTimeoutMs 传给中转器，中转器拿它做 WaitForSingleObject。
        // 改短了会把"启动稍慢的程序"误判成秒退（假失败），改长了每次启动都要多等。
        Assert.Equal(TimeSpan.FromSeconds(4), BrokerTimingPolicy.ExitWaitTimeout);
    }

    [Fact]
    public void ExitWaitTimeout_与作业契约的默认等待窗口一致_两端不得各改各的()
    {
        // Arrange：调度端显式写 job.WaitTimeoutMs，中转器另有自己的默认值兜底。
        var jobDefault = new BrokerLaunchJob().WaitTimeoutMs;

        // Act / Assert：两个"4 秒"是同一个契约的两端，漂移其一即行为不一致。
        Assert.Equal((int)BrokerTimingPolicy.ExitWaitTimeout.TotalMilliseconds, jobDefault);
    }

    [Fact]
    public void ResultPollTimeout_轮询超时为二十秒_取值不得漂移()
    {
        // Arrange / Act / Assert
        // 该值同时出现在超时告警文案里（用户据此判断"等了多久"），所以它是对外可见的。
        Assert.Equal(TimeSpan.FromSeconds(20), BrokerTimingPolicy.ResultPollTimeout);
    }

    [Fact]
    public void PollInterval_轮询间隔为两百毫秒_取值不得漂移()
    {
        // Arrange / Act / Assert
        Assert.Equal(TimeSpan.FromMilliseconds(200), BrokerTimingPolicy.PollInterval);
    }

    [Fact]
    public void ResultPollTimeout_必须明显大于秒退等待窗口_否则会把秒退误判成超时()
    {
        // Arrange / Act / Assert
        // 轮询超时要覆盖：中转器启动 + ShellExecute + 秒退等待 + 回写全过程。
        // 一旦两者相等，秒退窗口一满就同时判超时 —— 一个正常启动会被报成失败。
        Assert.True(
            BrokerTimingPolicy.ResultPollTimeout > BrokerTimingPolicy.ExitWaitTimeout,
            $"轮询超时（{BrokerTimingPolicy.ResultPollTimeout.TotalSeconds} 秒）"
            + $"必须大于秒退等待窗口（{BrokerTimingPolicy.ExitWaitTimeout.TotalSeconds} 秒）。");
    }

    [Fact]
    public void ResultPollTimeout_必须是轮询间隔的整数倍_否则实际超时点会前移()
    {
        // Arrange / Act / Assert
        // 轮询循环是"睡满一个间隔再判一次"，所以截止时刻只会落在某个 sleep 之后。
        // 20 秒 = 100 × 200 毫秒：第 100 次醒来恰好到点。若间隔改成 300 毫秒，
        // 实际停止时刻会变成 19.8 秒，超时文案却还写着 20 秒 —— 文案与行为对不上。
        Assert.Equal(0, BrokerTimingPolicy.ResultPollTimeout.Ticks % BrokerTimingPolicy.PollInterval.Ticks);
    }

    [Fact]
    public void GetResultPollDeadline_由轮询起点加超时算出_取开始时刻之后二十秒()
    {
        // Arrange / Act
        var deadline = BrokerTimingPolicy.GetResultPollDeadline(Start);

        // Assert：截止时刻完全由入参决定（轮询内部显式传 DateTime.UtcNow），不读真时钟。
        Assert.Equal(Start + TimeSpan.FromSeconds(20), deadline);
    }

    [Fact]
    public void ShouldKeepPolling_尚未到截止时刻_继续轮询()
    {
        // Arrange / Act / Assert
        Assert.True(BrokerTimingPolicy.ShouldKeepPolling(Start, Start + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void ShouldKeepPolling_恰好到达截止时刻_停止轮询并判超时()
    {
        // Arrange / Act / Assert
        // 🔴 这条边界决定"失败"还是"再等一轮"：到达即停，PollBrokerResult 返回 null，
        // 调用方判"超时未回写结果 = 本条目失败"且不提权回退（D70）。写成 <= 会多等一轮，
        // 判据就漂了。
        Assert.False(BrokerTimingPolicy.ShouldKeepPolling(
            Start + TimeSpan.FromSeconds(20),
            Start + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void ShouldKeepPolling_已越过截止时刻_停止轮询并判超时()
    {
        // Arrange / Act / Assert
        Assert.False(BrokerTimingPolicy.ShouldKeepPolling(
            Start + TimeSpan.FromSeconds(20) + TimeSpan.FromTicks(1),
            Start + TimeSpan.FromSeconds(20)));
    }
}
