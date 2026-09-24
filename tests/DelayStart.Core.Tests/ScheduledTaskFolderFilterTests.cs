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
        // B3：守卫任务（\DelayStartGuard）的生命周期由守卫进程自管理（D74+），
        // 列给用户只会造成"我什么时候建的任务"的困惑。守卫注册器里同一份常量，
        // 过滤与登记永远指同一条任务（singleton of truth）。
        Assert.True(ScheduledTaskSource.IsOwnedTask(GuardTaskRegistrar.TaskPathConstant));
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
