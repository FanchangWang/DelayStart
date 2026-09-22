using System.Security.Principal;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;

using Microsoft.Win32.TaskScheduler;

namespace DelayStart.Management.Services;

/// <summary>
/// 计划任务 <c>\DelayStartGuard</c> 的注册 / 更新 / 删除（D74，2026-09-22 批复）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **身份必须是当前交互用户**（<c>Interactive</c>）+ <c>RunLevel = Highest</c>，
/// 与调度端任务同款：<c>Interactive</c> 才能发**系统通知**（守卫的"新增 / 失效"通报要用，
/// 通知必须有交互会话，见 D79），
/// <c>Highest</c> 才能改写 HKLM 下的 Run 项与第三方计划任务，而计划任务提权**不弹 UAC**。
/// </para>
/// <para>
/// 🔴 **<c>Settings</c> 六项必须逐项显式设置**，不能依赖默认值：任务默认
/// <c>DisallowStartIfOnBatteries = true</c> ⇒ 笔记本拔掉电源时守卫**静默不跑**；
/// 缺 <c>MultipleInstances = IgnoreNew</c> 时，两次触发重叠会让同一轮巡检被并行跑两遍
/// （改动系统状态的是同一批项，重复跑没有收益，只会让日志里出现两份汇总）。
/// 这六项与 <see cref="TaskRegistrationService"/> 一模一样，改一处要改两处。
/// </para>
/// <para>
/// 任务名用 <c>DelayStartGuard</c>（无空格）而不是界面上的「DelayStart 守卫」：
/// 与 <c>DelayStartScheduler</c> 的命名保持一致，且省掉脚本 / CLI 引用时的引号问题。
/// </para>
/// </remarks>
public sealed class GuardTaskRegistrar : IGuardTaskRegistrar
{
    /// <summary>守卫端任务的根路径，作为唯一标识。</summary>
    public const string TaskPathConstant = @"\DelayStartGuard";

    /// <summary>注册到任务库时使用的任务名。</summary>
    public const string TaskName = "DelayStartGuard";

    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>构造守卫任务注册服务。</summary>
    /// <param name="paths">路径服务，取守卫端 exe 的完整路径。</param>
    /// <param name="log">日志接收端。</param>
    public GuardTaskRegistrar(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _log = log;
    }

    /// <inheritdoc />
    public string TaskPath => TaskPathConstant;

    /// <inheritdoc />
    public bool IsRegistered()
    {
        try
        {
            using var service = new TaskService();
            var task = service.GetTask(TaskPathConstant);
            if (task is null)
            {
                return false;
            }

            // "存在"不够：安装目录变化后旧任务会指向一个不存在的 exe。
            // 这里只判断可读性，具体更新交给 RegisterOrUpdate 的幂等写入。
            return task.Definition.Actions.OfType<ExecAction>().Any(
                action => string.Equals(action.Path, _paths.GuardExecutablePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "查询守卫计划任务失败，按未注册处理");
            return false;
        }
    }

    /// <inheritdoc />
    public void RegisterOrUpdate(GuardMode mode, int minutes)
    {
        var trigger = GuardSchedulePlan.Build(mode, minutes);
        if (trigger is null)
        {
            // Disabled 的语义就是"不该有这条任务"。
            Delete();
            return;
        }

        var guardPath = _paths.GuardExecutablePath;
        if (!File.Exists(guardPath))
        {
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"守卫程序不存在，无法注册计划任务：{guardPath}。"
                    + "请确认安装完整，或重新运行安装程序。");
        }

        // 🔴 取当前交互用户而不是 SYSTEM（同 TaskRegistrationService）：SYSTEM 会话下
        // %APPDATA% 解析到 systemprofile，配置读不到，且**不抛任何异常**。
        var userId = WindowsIdentity.GetCurrent().Name;

        try
        {
            using var service = new TaskService();
            RegisterGuardTask(service, guardPath, userId, trigger);

            _log.Info(
                $"已注册守卫计划任务：{TaskPathConstant}"
                + $"（{DescribeMode(mode, minutes)}，身份 {userId}）");
        }
        catch (Exception ex)
        {
            // 异常必须带栈：这类失败常是三层嵌套的 TypeInitializationException（D34 实测）。
            _log.Error(ex, "注册守卫计划任务失败");
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"注册守卫计划任务失败：{ex.Message}",
                innerException: ex);
        }
    }

    /// <inheritdoc />
    public void Delete()
    {
        try
        {
            using var service = new TaskService();

            if (service.GetTask(TaskPathConstant) is not null)
            {
                service.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
                _log.Info($"已删除守卫计划任务：{TaskPathConstant}");
            }
        }
        catch (Exception ex)
        {
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"删除守卫计划任务失败：{ex.Message}",
                innerException: ex);
        }
    }

    /// <summary>构造并写入守卫任务定义（提权，根路径单条）。</summary>
    private void RegisterGuardTask(TaskService service, string guardPath, string userId, GuardTrigger trigger)
    {
        using var definition = service.NewTask();

        definition.RegistrationInfo.Description =
            "DelayStart 的自启动项守卫。周期巡检：把被应用写回启用的已接管项重新软禁用，"
            + "并通报新增 / 失效的自启动项，然后退出。";

        definition.Principal.RunLevel = TaskRunLevel.Highest;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.UserId = userId;

        var logonTrigger = new LogonTrigger
        {
            Delay = trigger.InitialDelay,
            // 多用户同时登录时只在该用户的会话里跑 —— 与"每用户一套配置"一致。
            UserId = userId,
        };

        if (trigger.RepeatInterval is { } interval)
        {
            // 只设 Interval，**不设 Duration**：留空即"无限期重复"（任务计划程序 GUI 的默认档位）。
            // ⚠️ 这一条在真机验收里要确认（重复任务持续时间显示为「无限期」）。
            logonTrigger.Repetition.Interval = interval;
        }

        definition.Triggers.Add(logonTrigger);

        definition.Actions.Add(new ExecAction(
            guardPath,
            arguments: null,
            workingDirectory: _paths.InstalledRoot));

        var settings = definition.Settings;
        // 巡检本身秒级结束，但提示框可能等到超时才收尾 —— 不能因此把任务判成"执行超时"。
        settings.ExecutionTimeLimit = TimeSpan.Zero;
        settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries = false;
        settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        settings.RunOnlyIfIdle = false;
        settings.RunOnlyIfNetworkAvailable = false;

        // CreateOrUpdate 是幂等的：任务已存在时更新定义而不重建，上次运行时间等统计不丢。
        _ = service.RootFolder.RegisterTaskDefinition(
            TaskName,
            definition,
            TaskCreation.CreateOrUpdate,
            userId,
            password: null,
            logonType: TaskLogonType.InteractiveToken);
    }

    /// <summary>给日志与界面用的模式描述。</summary>
    /// <param name="mode">守卫模式。</param>
    /// <param name="minutes">间隔档位（分钟）。</param>
    /// <returns>中文描述。</returns>
    public static string DescribeMode(GuardMode mode, int minutes)
        => mode switch
        {
            GuardMode.OnceAfterLogin => $"登录后 {minutes} 分钟执行一次",
            GuardMode.Periodic => $"登录后 {minutes} 分钟起，每 {minutes} 分钟一次",
            _ => "已关闭",
        };
}
