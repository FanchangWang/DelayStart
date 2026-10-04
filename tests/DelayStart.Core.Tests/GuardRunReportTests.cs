using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Models;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="GuardRunReport"/> 的通报口径测试（D148）。
/// </summary>
/// <remarks>
/// 全量失效（<see cref="GuardRunReport.StaleItems"/>）与"本轮该通报"（差集）是<b>两件事</b>：
/// 日志页 / 总览卡 / 汇总行要全量，系统通知要差集。折叠成一个字段就会让日志页每轮显示「失效 0」。
/// 两者靠 <see cref="GuardRunReport.AsNotificationView"/> 分开，差集由
/// <c>GuardNotificationFilter</c> 算好后作为参数传入。
/// </remarks>
public sealed class GuardRunReportTests
{
    [Fact]
    public void HasNotifications_只有持续失效时仍为真()
    {
        // 🔴 这一条刻意与"通报口径"相反：原报告说的是**现状**，不是"该不该打扰"。
        // 守卫进程必须先拿通报视图再问 HasNotifications，否则每轮都会通知。
        var report = new GuardRunReport { StaleItems = [Stale("a")] };

        Assert.True(report.HasNotifications);
    }

    [Fact]
    public void AsNotificationView_失效列表替换为传入的差集()
    {
        var report = new GuardRunReport { StaleItems = [Stale("a"), Stale("b")] };

        var view = report.AsNotificationView([Stale("a")]);

        Assert.Equal(["a"], [.. view.StaleItems.Select(static stale => stale.Item.Id)]);
        Assert.True(view.HasNotifications);
    }

    [Fact]
    public void AsNotificationView_差集为空时不该通报()
    {
        // 持续失效但都已通报过 ⇒ 视图上无内容 ⇒ 守卫静默退出。
        var report = new GuardRunReport { StaleItems = [Stale("a"), Stale("b")] };

        var view = report.AsNotificationView([]);

        Assert.Empty(view.StaleItems);
        Assert.False(view.HasNotifications);
    }

    [Fact]
    public void AsNotificationView_不改动原对象()
    {
        // 🔴 归档 / 日志页 / 汇总拿的是原对象：就地改掉会让日志页不再列全量失效条目。
        var report = new GuardRunReport { StaleItems = [Stale("a"), Stale("b")] };

        _ = report.AsNotificationView([Stale("a")]);

        Assert.Equal(["a", "b"], [.. report.StaleItems.Select(static stale => stale.Item.Id)]);
    }

    [Fact]
    public void AsNotificationView_入参null_抛()
        => Assert.Throws<ArgumentNullException>(
            () => new GuardRunReport().AsNotificationView(null!));

    private static StaleEntry Stale(string id) => new(
        new DelayedItem { Id = id, Name = id, Source = StartupSource.Registry, Scope = StartupScope.Hkcu },
        StaleKind.TargetLost,
        Entry: null);
}