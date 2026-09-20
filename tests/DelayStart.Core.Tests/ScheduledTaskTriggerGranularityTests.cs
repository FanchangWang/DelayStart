using DelayStart.Management.Sources;

using Microsoft.Win32.TaskScheduler;

namespace DelayStart.Core.Tests;

/// <summary>
/// 计划任务「禁用粒度」的单元测试（D67，2026-09-21）。
/// </summary>
/// <remarks>
/// 出这组用例的原因是一次真机缺陷：接管计划任务时一律写 <c>task.Enabled = false</c>，
/// 而那是**任务级**开关，会把任务里的所有触发器一起关掉。第三方任务常见形态是
/// 「登录触发 + 每日定时」共存（本机实测 <c>\QuarkCloudDriveUpdaterUser\…</c>、
/// <c>\GoogleUser\GoogleUpdater\…</c> 都正是这个形状）—— 用户只想接管登录那一个，
/// 结果每日定时也跟着被禁用了。
/// <para>
/// 本组用例把两条边界钉死：<b>单触发器 → 切任务级开关</b>；<b>多触发器 → 只切登录 / 启动触发器</b>。
/// </para>
/// </remarks>
public sealed class ScheduledTaskTriggerGranularityTests
{
    // ── 触发器分类：只有登录 / 启动算「自启动触发器」 ────────────────────────

    [Fact]
    public void IsAutostartTrigger_LogonAndBoot_True()
    {
        Assert.True(ScheduledTaskSource.IsAutostartTrigger(new LogonTrigger()));
        Assert.True(ScheduledTaskSource.IsAutostartTrigger(new BootTrigger()));
    }

    [Fact]
    public void IsAutostartTrigger_NonAutostartTypes_False()
    {
        // 🔴 SessionStateChangeTrigger（会话解锁 / 远程连接）最容易被误当成登录触发 ——
        // 它是独立类型、**不是** LogonTrigger 的子类，这条断言就是防止有人"顺手"改坏判据。
        Assert.False(ScheduledTaskSource.IsAutostartTrigger(new DailyTrigger(1)));
        Assert.False(ScheduledTaskSource.IsAutostartTrigger(new WeeklyTrigger(DaysOfTheWeek.Monday, 1)));
        Assert.False(ScheduledTaskSource.IsAutostartTrigger(new TimeTrigger()));
        Assert.False(ScheduledTaskSource.IsAutostartTrigger(new IdleTrigger()));
        Assert.False(ScheduledTaskSource.IsAutostartTrigger(new RegistrationTrigger()));
        Assert.False(ScheduledTaskSource.IsAutostartTrigger(new EventTrigger()));
        Assert.False(ScheduledTaskSource.IsAutostartTrigger(new SessionStateChangeTrigger()));
    }

    [Fact]
    public void IsAutostartTrigger_Null_False()
    {
        Assert.False(ScheduledTaskSource.IsAutostartTrigger(null));
    }

    // ── 禁用粒度：单触发器切任务，多触发器只切自启动触发器 ──────────────────

    [Theory]
    [InlineData(1, 1)] // 只挂一个登录触发（绝大多数自启动项）
    [InlineData(1, 0)] // 防呆：只有一个触发器、且它不是自启动类
    [InlineData(0, 0)] // 防呆：任务没有任何触发器
    [InlineData(2, 0)] // 有多个触发器、但没有一个自启动类 → 退回任务级
    public void ShouldToggleWholeTask_WholeTaskCases_True(int triggerCount, int autostartTriggerCount)
    {
        Assert.True(ScheduledTaskSource.ShouldToggleWholeTask(triggerCount, autostartTriggerCount));
    }

    [Theory]
    [InlineData(2, 1)] // 登录 + 每日定时 —— 本次缺陷的样本形状
    [InlineData(2, 2)] // 两个登录触发 → 两个都得切，否则登录时启动两次
    [InlineData(3, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 2)]
    public void ShouldToggleWholeTask_MultiTriggerCases_False(int triggerCount, int autostartTriggerCount)
    {
        Assert.False(ScheduledTaskSource.ShouldToggleWholeTask(triggerCount, autostartTriggerCount));
    }

    // ── 位置描述：多触发器必须写清「另有几个」 ──────────────────────────────

    [Theory]
    [InlineData(1, "计划任务（登录时）")]
    [InlineData(2, "计划任务（登录时；另有 1 个触发器）")]
    [InlineData(3, "计划任务（登录时；另有 2 个触发器）")]
    public void BuildSourceDetail_ReportsOtherTriggerCount(int triggerCount, string expected)
    {
        Assert.Equal(expected, ScheduledTaskSource.BuildSourceDetail("登录时", triggerCount));
    }
}
