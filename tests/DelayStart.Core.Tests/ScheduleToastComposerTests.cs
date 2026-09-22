using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ScheduleToastComposer"/> 的单元测试（N2，2026-09-22 批复）：
/// 通知正文组装 —— 计数分段、失败列表截断、总长截断。
/// </summary>
public sealed class ScheduleToastComposerTests
{
    [Fact]
    public void ComposeMessage_AllSuccess_OmitsFailureAndSkipSegments()
    {
        var message = ScheduleToastComposer.ComposeMessage(5, 0, 0, []);

        Assert.Equal("启动完成：5 项成功", message);
    }

    [Fact]
    public void ComposeMessage_WithFailuresAndSkips_ListsAllSegments()
    {
        var message = ScheduleToastComposer.ComposeMessage(3, 2, 1, ["甲", "乙"]);

        Assert.Equal($"启动完成：3 项成功 · 2 项失败 · 跳过 1{System.Environment.NewLine}甲、乙", message);
    }

    [Fact]
    public void ComposeMessage_ZeroDone_KeepsZeroSegment()
    {
        var message = ScheduleToastComposer.ComposeMessage(0, 0, 2, []);

        Assert.Equal("启动完成：0 项成功 · 跳过 2", message);
    }

    [Fact]
    public void ComposeMessage_MoreThanThreeFailures_FoldsRemainder()
    {
        var names = new[] { "一", "二", "三", "四", "五" };
        var message = ScheduleToastComposer.ComposeMessage(0, 5, 0, names);

        Assert.Contains("一、二、三 等 5 项", message);
        Assert.StartsWith("启动完成：0 项成功 · 5 项失败", message);
    }

    [Fact]
    public void ComposeMessage_OverlongMessage_TruncatesWithEllipsis()
    {
        // 折叠后仍超长：单条名称就要 100+ 字符，即使只列 3 条也会撞上总长上限。
        var longName = new string('长', 100);
        var names = Enumerable.Range(1, 5).Select(i => $"{longName}{i}").ToList();
        var message = ScheduleToastComposer.ComposeMessage(0, 5, 0, names);

        Assert.True(message.Length <= ScheduleToastComposer.MaxMessageLength);
        Assert.EndsWith("…", message);
    }
}
