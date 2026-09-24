using DelayStart.Management.Models;
using DelayStart.Management.Services;
using DelayStart.Management.Sources;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ScheduledTaskSource.IsProtectedFolderPath"/> 的单元测试（D66，2026-09-21）。
/// </summary>
/// <remarks>
/// 出这组用例的原因是一次真机缺陷：判据早期写成 <c>task.Path.StartsWith("\Microsoft")</c>，
/// **少了尾部分隔符**，而 <c>task.Path</c> 是"文件夹 + 任务名"的完整路径 ——
/// 于是根级**名字以 Microsoft 开头**的第三方任务（典型样本是 Edge 自动更新的那两个）
/// 被当作系统任务一起过滤，用户设过的登录自启项在列表里凭空消失、且不留任何日志。
/// <para>
/// 本组用例把两条边界各自钉死：<b>在 <c>\Microsoft\</c> 文件夹里</b> → 过滤；
/// <b>只是名字里带 Microsoft</b> → 照常展示。
/// </para>
/// </remarks>
public sealed class ScheduledTaskFolderFilterTests
{
    // ── 受保护：\Microsoft 文件夹及其子文件夹 ───────────────────────────────

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag")]
    [InlineData(@"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan")]
    [InlineData(@"\Microsoft\Office\OfficeTelemetryAgentLogOn")]
    [InlineData(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start")]
    [InlineData(@"\microsoft\windows\defrag\scheduleddefrag")]
    public void IsProtectedFolderPath_UnderMicrosoftFolder_True(string path)
    {
        Assert.True(ScheduledTaskSource.IsProtectedFolderPath(path));
    }

    // ── 第三方：名字里带 Microsoft（本次缺陷的回归用例）────────────────────

    [Theory]
    [InlineData(@"\MicrosoftEdgeUpdateTaskMachineCore")]
    [InlineData(@"\MicrosoftEdgeUpdateTaskMachineUA")]
    [InlineData(@"\Microsoft Edge Update Task")]
    [InlineData(@"\Microsoft")]
    [InlineData(@"\MicrosoftX\Foo")]
    [InlineData(@"\MyVendor\Microsoft\Foo")]
    [InlineData(@"\MyTasks\MicrosoftSync")]
    [InlineData(@"\SyncTool")]
    public void IsProtectedFolderPath_NameMerelyLooksLikeMicrosoft_False(string path)
    {
        Assert.False(ScheduledTaskSource.IsProtectedFolderPath(path));
    }

    [Fact]
    public void IsOwnedTask_GuardTask_IsFilteredOut()
    {
        // B3 / D123：守卫任务的生命周期由守卫进程自管理（D74+），列给用户只会造成
        // "我什么时候建的任务"的困惑。路径取注册器里同一份常量 ——
        // 登记与过滤永远指同一条任务。
        Assert.True(ScheduledTaskSource.IsOwnedTask(GuardTaskRegistrar.TaskPathConstant));
        Assert.True(ScheduledTaskSource.IsOwnedTask(TaskRegistrationService.TaskPathConstant));
    }

    [Fact]
    public void IsOwnedTask_AnyTaskInsideOwnedFolder_IsFilteredOut()
    {
        // 🔴 D123：判据是**文件夹归属**而不是"列举我有哪几条任务"。
        // 枚举式的白名单会在将来加了第三条任务却忘了改判据时漏掉一条 ——
        // 让它混进用户的自启动项列表，那正是 B3 要防的问题复发。
        Assert.True(ScheduledTaskSource.IsOwnedTask(@"\DelayStart\SomeFutureTask"));
        Assert.True(ScheduledTaskSource.IsOwnedTask(@"\DelayStart\Sub\NestedTask"));
    }

    [Fact]
    public void IsOwnedTask_SimilarFolderNameOutsideOwnedFolder_IsNotFiltered()
    {
        // 🔴 尾部分隔符就是为这一条：少了它，\DelayStartExtra\Foo 会被误判成自有任务
        // 而凭空消失。IsProtectedFolderPath 踩过同一个坑（Edge 更新任务被整体误过滤，
        // 见 pitfalls.md 二），这里必须钉住。
        Assert.False(ScheduledTaskSource.IsOwnedTask(@"\DelayStartExtra\Foo"));
        Assert.False(ScheduledTaskSource.IsOwnedTask(@"\DelayStartBackup\Guard"));
    }

    [Fact]
    public void IsOwnedTask_OldRootLevelTasks_AreNotFiltered()
    {
        // D121 / D122：根级旧路径**不**在判据内 —— 本项目不做兼容清理。
        // 它们由旧版卸载器负责（D122 已要求跨版本先卸载）。
        // 换句话说：若用户跳过卸载直接装新版，旧任务会作为第三方任务被列出来，
        // 这是"必须先卸载"这条规则的可见后果，而不是过滤漏了。
        Assert.False(ScheduledTaskSource.IsOwnedTask(@"\DelayStartScheduler"));
        Assert.False(ScheduledTaskSource.IsOwnedTask(@"\DelayStartGuard"));
    }

    [Fact]
    public void IsOwnedTask_FolderPrefixIsSingleSourceOfTruth()
    {
        // 三条路径（两条任务的完整路径 + 过滤前缀）都从 OwnedTaskFolder.Prefix 派生，
        // 这条用例保证将来改文件夹名时三处不会各改各的、漏掉一处。
        Assert.StartsWith(OwnedTaskFolder.Prefix, GuardTaskRegistrar.TaskPathConstant, StringComparison.Ordinal);
        Assert.StartsWith(OwnedTaskFolder.Prefix, TaskRegistrationService.TaskPathConstant, StringComparison.Ordinal);
        Assert.EndsWith(@"\", OwnedTaskFolder.Prefix, StringComparison.Ordinal);
    }

    [Fact]
    public void IsOwnedTask_ThirdPartyTask_IsNotFiltered()
    {
        Assert.False(ScheduledTaskSource.IsOwnedTask(@"\SomeThirdParty\StartupTask"));
    }

    // ── 空白输入：读取失败的任务不能因为判据而误判为受保护 ──────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsProtectedFolderPath_BlankInput_False(string? path)
    {
        Assert.False(ScheduledTaskSource.IsProtectedFolderPath(path));
    }
}
