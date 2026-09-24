using DelayStart.Management.Models;
using DelayStart.Management.Services;

using Microsoft.Win32.TaskScheduler;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ScheduledTaskGateway.SameAccount"/> 的归一化比较单测（D114 真机首验修正）。
/// </summary>
/// <remarks>
/// 背景：任务计划程序落盘时把 Principal 账户名规范化成 SID，库读回 <c>Principal.UserId</c>
/// 却是裸账户名（<c>guyue</c>，丢域前缀），字符串比对永远不等 → 每次启动误判"定义已变更"
/// 而重写计划任务。本组用例锁住"全名 / 裸名 / SID 任意两形态必须判同一账户"。
/// 刻意选用 WellKnown 账户（Everyone = S-1-1-0、BUILTIN\Administrators = S-1-5-32-544），
/// 不依赖开发机的具体用户名，保证在任意 Windows 上结果一致。
/// </remarks>
public class ScheduledTaskGatewayTests
{
    [Theory]
    [InlineData("S-1-1-0", "S-1-1-0", true)]            // 字符串等值短路（同为 SID 形态）
    [InlineData("S-1-1-0", "Everyone", true)]           // SID ↔ 账户名（Everyone 是 WellKnown WorldSid）
    [InlineData("Everyone", "S-1-1-0", true)]           // 对称
    [InlineData("S-1-5-32-544", @"BUILTIN\Administrators", true)] // 另一组 WellKnown 对
    [InlineData("S-1-1-0", "NoSuchAccountOnThisMachine", false)]  // 右侧无法映射成 SID ⇒ 不等
    [InlineData(null, null, true)]                      // 双空视为相等（防御分支）
    [InlineData("S-1-1-0", null, false)]                // 单侧空 ⇒ 不等
    [InlineData("", "S-1-1-0", false)]                  // 空串 ⇒ 不等
    public void SameAccount_NormalizesSidAndNameForms(string? left, string? right, bool expected)
        => Assert.Equal(expected, ScheduledTaskGateway.SameAccount(left, right));

    [Fact]
    public void DefinitionUpToDate_AllExpectedFieldsMatch_ReturnsTrue()
    {
        using var service = new TaskService();
        using var definition = CreateDefinition(service, Spec(), "Everyone");

        Assert.True(ScheduledTaskGateway.IsDefinitionUpToDate(
            taskEnabled: true,
            definition,
            Spec(),
            "Everyone"));
    }

    [Fact]
    public void DefinitionUpToDate_TaskDisabled_ReturnsFalse()
    {
        using var service = new TaskService();
        using var definition = CreateDefinition(service, Spec(), "Everyone");

        Assert.False(ScheduledTaskGateway.IsDefinitionUpToDate(
            taskEnabled: false,
            definition,
            Spec(),
            "Everyone"));
    }

    [Fact]
    public void DefinitionUpToDate_TriggerDisabled_ReturnsFalse()
    {
        using var service = new TaskService();
        using var definition = CreateDefinition(service, Spec(), "Everyone", triggerEnabled: false);

        Assert.False(ScheduledTaskGateway.IsDefinitionUpToDate(
            taskEnabled: true,
            definition,
            Spec(),
            "Everyone"));
    }

    [Fact]
    public void DefinitionUpToDate_ActionArgumentsChanged_ReturnsFalse()
    {
        using var service = new TaskService();
        using var definition = CreateDefinition(
            service,
            Spec(),
            "Everyone",
            actionArguments: "-changed");

        Assert.False(ScheduledTaskGateway.IsDefinitionUpToDate(
            taskEnabled: true,
            definition,
            Spec(),
            "Everyone"));
    }

    [Fact]
    public void DefinitionUpToDate_NullAndEmptyArgumentsAreEquivalent()
    {
        using var service = new TaskService();
        using var definition = CreateDefinition(
            service,
            Spec(),
            "Everyone",
            actionArguments: string.Empty);

        Assert.True(ScheduledTaskGateway.IsDefinitionUpToDate(
            taskEnabled: true,
            definition,
            Spec(),
            "Everyone"));
    }

    private static ScheduledTaskSpec Spec(string? arguments = null) => new(
        TaskName: "TestTask",
        TaskPath: @"\TestTask",
        Description: "DelayStart test task",
        ExecutablePath: @"C:\DelayStart\Test.exe",
        WorkingDirectory: @"C:\DelayStart",
        LogonDelay: TimeSpan.FromSeconds(3),
        RepeatInterval: null,
        DisplayName: "测试",
        ScheduleDescription: "登录后 3 秒",
        Arguments: arguments);

    private static TaskDefinition CreateDefinition(
        TaskService service,
        ScheduledTaskSpec spec,
        string userId,
        bool triggerEnabled = true,
        string? actionArguments = null)
    {
        var definition = service.NewTask();
        definition.RegistrationInfo.Description = spec.Description;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = TaskRunLevel.Highest;
        definition.Principal.UserId = userId;

        var logonTrigger = new LogonTrigger
        {
            Delay = spec.LogonDelay,
            UserId = userId,
            Enabled = triggerEnabled,
        };
        definition.Triggers.Add(logonTrigger);

        definition.Actions.Add(new ExecAction(
            spec.ExecutablePath,
            arguments: actionArguments,
            workingDirectory: spec.WorkingDirectory));

        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        definition.Settings.RunOnlyIfIdle = false;
        definition.Settings.RunOnlyIfNetworkAvailable = false;

        return definition;
    }
}
