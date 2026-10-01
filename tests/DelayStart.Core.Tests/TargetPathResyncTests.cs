using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="TargetPathResync"/> 的单元测试（D137）。
/// </summary>
/// <remarks>
/// 🔴 这条修的是一个**静默且永久**的故障：程序自更新换了可执行文件名后，
/// 注册表项指向了新 exe，配置里还指着旧 exe —— 页面显示一切正常（守卫按注册表现值判定），
/// 而调度端按配置的过期路径判定，于是那个程序从那天起再也没被启动过，
/// 日志每天记一条"目标程序已不存在"，用户没有任何线索去查。
/// </remarks>
public sealed class TargetPathResyncTests
{
    private const string OldExe = @"C:\Users\guyue\AppData\Local\Programs\FluxDown\flux_down.exe";
    private const string NewExe = @"C:\Users\guyue\AppData\Local\Programs\FluxDown\fluxdown-agent.exe";

    private static DelayedItem Item(
        string id = "registry:hkcu:fluxdown",
        string path = OldExe,
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu)
        => new() { Id = id, Name = "FluxDown", Path = path, Source = source, Scope = scope };

    private static StartupEntry Entry(
        string id = "registry:hkcu:fluxdown",
        string path = NewExe,
        StartupSource source = StartupSource.Registry,
        StartupScope scope = StartupScope.Hkcu)
        => new()
        {
            Id = id,
            Name = "FluxDown",
            Path = path,
            Source = source,
            Scope = scope,
            SourceKey = "FluxDown",
        };

    [Fact]
    public void SourceExists_ButPathChanged_SelectsResync()
    {
        // 用户机器上的真实形态：FluxDown 2026-10-01 自更新，注册表项改指新 exe。
        var resync = Assert.Single(TargetPathResync.SelectResyncs([Item()], [Entry()], []));

        Assert.Equal("registry:hkcu:fluxdown", resync.ItemId);
        Assert.Equal(OldExe, resync.CurrentPath);
        Assert.Equal(NewExe, resync.FreshPath);
    }

    [Fact]
    public void PathUnchanged_SelectsNothing()
    {
        // 🔴 反向：绝大多数条目是**没有**漂移的。这一条钉住"不误伤"——
        // 一个"永远返回第一条"的实现也能让上面那条变绿。
        Assert.Empty(TargetPathResync.SelectResyncs([Item(path: NewExe)], [Entry()], []));
    }

    [Fact]
    public void PathDiffersOnlyByCaseOrTrailingSpace_SelectsNothing()
    {
        // Windows 路径不区分大小写，且注册表值常带尾随空格（`pot` 那条实测就有）。
        // 逐字符比较会把这两者当成漂移，于是每次巡检都白写一次配置。
        Assert.Empty(TargetPathResync.SelectResyncs(
            [Item(path: NewExe.ToUpperInvariant())], [Entry()], []));
        Assert.Empty(TargetPathResync.SelectResyncs(
            [Item(path: NewExe + " ")], [Entry()], []));
    }

    [Fact]
    public void SourceGone_SelectsNothing()
    {
        // 源没了是"接管关系断了"（SourceLost），**没有"现值"可同步** ——
        // 那件事要去来源页重新接管，不是改 path 能解决的。
        Assert.Empty(TargetPathResync.SelectResyncs([Item()], [], []));
    }

    [Fact]
    public void ScanFailedForThatScope_SelectsNothing()
    {
        // 🔴 来源整体失败（服务没起 / 键被 ACL 拒绝）时**一律不动**：
        // 用不完整的扫描结果去覆盖配置，可能把一个本来正确的路径改成空的。
        var failures = new[] { new ScanScope(StartupSource.Registry, StartupScope.Hkcu) };

        Assert.Empty(TargetPathResync.SelectResyncs([Item()], [Entry()], failures));
    }

    [Fact]
    public void FreshPathIsNotFullyQualified_SelectsNothing()
    {
        // 🔴 只接受**绝对路径**。来源报上来的若是协议名 / 命令名 / 相对路径，
        // 写进配置会让调度端按它去启动一个不存在的东西 —— 比不同步糟得多。
        Assert.Empty(TargetPathResync.SelectResyncs(
            [Item()], [Entry(path: "notepad.exe")], []));
    }

    [Fact]
    public void UwpItem_SelectsNothing()
    {
        // UWP 的 path 是解析名（shell:AppsFolder\...），不是文件路径。
        // 它不参与"自更新换 exe"这类漂移，而把它写进 path 只会污染 TargetPrefilter。
        Assert.Empty(TargetPathResync.SelectResyncs(
            [Item(source: StartupSource.Uwp, scope: StartupScope.None)],
            [Entry(source: StartupSource.Uwp, scope: StartupScope.None)],
            []));
    }

    [Fact]
    public void ScheduledTask_PathChanged_SelectsResync()
    {
        // 用户明确点名了计划任务：注册表**或计划任务**还存在、内部路径变了就同步。
        var item = Item(id: "scheduledtask:task:a", source: StartupSource.ScheduledTask);
        var entry = Entry(id: "scheduledtask:task:a", source: StartupSource.ScheduledTask);

        Assert.Single(TargetPathResync.SelectResyncs([item], [entry], []));
    }

    [Fact]
    public void EmptyConfiguredPath_SelectsResync()
    {
        // 配置里 path 为空、而来源报了一个绝对路径 —— 那本身就是错的配置，
        // 同步过去是对的（而且比"什么都不做"安全）。
        Assert.Single(TargetPathResync.SelectResyncs([Item(path: "")], [Entry()], []));
    }
}
