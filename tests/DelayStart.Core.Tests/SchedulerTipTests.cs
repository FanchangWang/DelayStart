using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="SchedulerTip"/> 的单元测试：托盘悬停提示的三行分工与 127 字符预算。
/// </summary>
public sealed class SchedulerTipTests
{
    [Fact]
    public void Building_FirstLine_IsAlwaysTheBrand()
    {
        // 🔴 通知中心与任务栏都可能把 tooltip 裁成一行，第一行必须是名字 ——
        // 否则用户看到的是一段没头没尾的状态文字。
        var tip = SchedulerTip.Building(3, 8, "微信", 12);

        Assert.StartsWith(SchedulerTip.BrandLine, tip, StringComparison.Ordinal);
        Assert.Equal(SchedulerTip.BrandLine, tip.Split("\r\n")[0]);
    }

    [Fact]
    public void Building_HasExactlyThreeLines()
    {
        var tip = SchedulerTip.Building(3, 8, "微信", 12);

        Assert.Equal(3, tip.Split("\r\n").Length);
    }

    [Fact]
    public void Building_SecondLine_CarriesProgress()
    {
        var tip = SchedulerTip.Building(3, 8, "微信", 12);

        Assert.Contains("已启动 3/8", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Building_ThirdLine_CarriesNextItemNameAndCountdown()
    {
        // 这两件事原先挤在同一行里，结果"下一项 12 秒"永远只有一个数字而没有名字 ——
        // 用户知道在等 12 秒，却不知道在等谁。
        var tip = SchedulerTip.Building(3, 8, "微信", 12);

        Assert.Contains("微信", tip, StringComparison.Ordinal);
        Assert.Contains("12", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Building_ImminentLaunch_SaysStartingRatherThanCountdown()
    {
        // 倒计时 ≤ 2 秒时说"12 秒"是骗人的：它到不了 12 秒就该启动了。
        var tip = SchedulerTip.Building(3, 8, "微信", 1);

        Assert.Contains("正在启动", tip, StringComparison.Ordinal);
        Assert.DoesNotContain("秒后", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Building_NoNextItem_SaysFinishingUp()
    {
        var tip = SchedulerTip.Building(8, 8, nextName: null, remainingSeconds: 0);

        Assert.Contains("正在完成", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Finished_WithFailures_LinesTwoAndThreeExplainWhereToLook()
    {
        // 失败那一档第三行必须是"去哪儿看"，而不是又一个数字。
        var tip = SchedulerTip.Finished(8, 2);

        var lines = tip.Split("\r\n");
        Assert.Equal(SchedulerTip.BrandLine, lines[0]);
        Assert.Contains("2 项失败", lines[1], StringComparison.Ordinal);
        Assert.Contains("调度日志", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Finished_AllSucceeded_SaysExiting()
    {
        var tip = SchedulerTip.Finished(8, failedCount: 0);

        Assert.Contains("全部启动", tip, StringComparison.Ordinal);
        Assert.Contains("即将退出", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void LongItemName_IsTruncated_AndNeverExceedsTheBudget()
    {
        // 程序名可以任意长（"Microsoft Visual Studio Professional 2022 Preview"），
        // 而 szTip 超长是**静默截断** —— 截掉一半的句子比短句糟糕得多，
        // 所以必须在合成时就把全文压进预算。
        var tip = SchedulerTip.Building(3, 8, new string('超', 200), 12);

        Assert.True(
            tip.Length <= SchedulerTip.MaxLength,
            $"提示 {tip.Length} 字，超出 {SchedulerTip.MaxLength} 字预算");
        Assert.EndsWith("…", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_KeepsTheFirstTwoLinesIntact()
    {
        // 截断只该发生在第三行：前两行是被反复读到的主信息，
        // 砍它们等于把最该看到的部分让给一个超长的程序名。
        var tip = SchedulerTip.Building(3, 8, new string('名', 200), 12);

        var lines = tip.Split("\r\n");
        Assert.Equal(SchedulerTip.BrandLine, lines[0]);
        Assert.Equal("已启动 3/8", lines[1]);
    }

    [Fact]
    public void EveryShape_StaysWithinBudget()
    {
        var names = new[] { "微信", string.Empty, new string('长', 500) };
        var counts = new[] { (0, 0), (1, 1), (12, 34), (999, 1000) };
        var seconds = new[] { 0, 1, 2, 3, 9999 };

        foreach (var name in names)
        {
            foreach (var (done, total) in counts)
            {
                foreach (var second in seconds)
                {
                    var tip = name.Length == 0
                        ? SchedulerTip.Building(done, total, null, second)
                        : SchedulerTip.Building(done, total, name, second);

                    Assert.True(
                        tip.Length <= SchedulerTip.MaxLength,
                        $"done={done} total={total} name={name.Length}字 sec={second} → {tip.Length} 字");

                    var finished = SchedulerTip.Finished(total, done % 3);
                    Assert.True(finished.Length <= SchedulerTip.MaxLength, finished);
                }
            }
        }
    }

    [Fact]
    public void CountdownBoundary_UsesTwoSecondsNotThree()
    {
        Assert.Contains("正在启动", SchedulerTip.Building(1, 4, "A", 2), StringComparison.Ordinal);
        Assert.Contains("秒后", SchedulerTip.Building(1, 4, "A", 3), StringComparison.Ordinal);
    }
}