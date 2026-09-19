using System.Security.Principal;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;

using Microsoft.Win32.TaskScheduler;

namespace DelayStart.Management.Services;

/// <summary>
/// <c>DelayStartScheduler</c> 计划任务的注册 / 更新 / 删除（FR-11 / <c>api-analysis.md</c> 3.1）。
/// </summary>
/// <remarks>
/// <para>
/// 用 <c>TaskService</c> API 而非 <c>schtasks.exe</c>（FR-11.3）：能读到结构化返回、
/// 支持中文任务名、参数更细，且不需要解析命令行输出。
/// </para>
/// <para>
/// 🔴 <b>身份必须是当前交互用户</b>（<c>Interactive</c> 登录类型 + <c>RunLevel = Highest</c>）。
/// 绝不使用 SYSTEM 或服务账户：SYSTEM 会话下 <c>%APPDATA%</c> 解析到
/// <c>C:\Windows\System32\config\systemprofile</c>，调度端会读不到配置、把日志写到错误位置，
/// 而且**不抛任何异常** —— 属于最难发现的一类故障（<c>api-analysis.md</c> 3.1）。
/// </para>
/// </remarks>
public sealed class TaskRegistrationService : ISchedulerTaskRegistrar
{
    /// <summary>计划任务名（不带路径）。</summary>
    public const string TaskName = "DelayStartScheduler";

    /// <summary>计划任务的完整路径，作为唯一标识。</summary>
    public const string TaskPathConstant = @"\" + TaskName;

    /// <summary>登录后延迟多少秒启动调度端（FR-11.1）。留给桌面与资源管理器加载的时间。</summary>
    public const int LogonDelaySeconds = 3;

    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>构造计划任务注册服务。</summary>
    /// <param name="paths">路径服务，取调度端 exe 的完整路径。</param>
    /// <param name="log">日志接收端。</param>
    public TaskRegistrationService(PathService paths, ILogSink log)
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

            // "存在"不够：安装目录变化后旧任务会指向一个不存在的 exe（FR-11.2 要处理的情形）。
            // 这里只判断可读性，具体重注册交给 RegisterOrUpdate 的幂等更新。
            return task.Definition.Actions.OfType<ExecAction>().Any(
                action => string.Equals(action.Path, _paths.SchedulerExecutablePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "查询调度计划任务失败，按未注册处理");
            return false;
        }
    }

    /// <inheritdoc />
    public void RegisterOrUpdate()
    {
        var schedulerPath = _paths.SchedulerExecutablePath;

        if (!File.Exists(schedulerPath))
        {
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"调度端程序不存在，无法注册计划任务：{schedulerPath}。"
                    + "请确认安装完整，或重新运行安装程序。");
        }

        // 🔴 取当前交互用户而不是 SYSTEM。提权进程的 WindowsIdentity 仍是那个人类用户，
        // 因为 UAC 提权只是换了令牌权限，没有换账户。
        var userId = WindowsIdentity.GetCurrent().Name;

        try
        {
            using var service = new TaskService();
            using var definition = service.NewTask();

            definition.RegistrationInfo.Description =
                "延时启动管理器的调度端。登录后按用户配置的延时逐个拉起被接管的程序。";

            definition.Principal.RunLevel = TaskRunLevel.Highest;
            definition.Principal.LogonType = TaskLogonType.InteractiveToken;
            definition.Principal.UserId = userId;

            definition.Triggers.Add(new LogonTrigger
            {
                Delay = TimeSpan.FromSeconds(LogonDelaySeconds),
                // 多个用户同时登录时，任务只在**该用户**的会话里跑 —— 这与"每用户一套配置"一致。
                UserId = userId,
            });

            definition.Actions.Add(new ExecAction(
                schedulerPath,
                arguments: null,
                workingDirectory: _paths.InstalledRoot));

            var settings = definition.Settings;
            // 调度端按设计要驻留到最后一个延时项启动完毕，可能长达数小时（D13），不能有执行时限。
            settings.ExecutionTimeLimit = TimeSpan.Zero;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            // 上一次的调度还没结束就再次登录（快速切换用户）时，不并行再起一个。
            settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
            // 登录瞬间网络/电源可能尚未就绪，让任务在条件不满足时也能跑。
            settings.RunOnlyIfIdle = false;
            settings.RunOnlyIfNetworkAvailable = false;

            // CreateOrUpdate 是幂等的：任务已存在时更新定义而不重建，
            // 这样上次运行时间等统计信息不会被清掉（D22 要求首启可重复执行）。
            _ = service.RootFolder.RegisterTaskDefinition(
                TaskName,
                definition,
                TaskCreation.CreateOrUpdate,
                userId,
                password: null,
                logonType: TaskLogonType.InteractiveToken);

            _log.Info($"已注册调度计划任务：{TaskPathConstant}（登录后 {LogonDelaySeconds} 秒，身份 {userId}）");
        }
        catch (Exception ex)
        {
            // FR-11：注册失败必须留痕。CLI 与卸载脚本只拿得到最外层的一句话，
            // 而这里的异常常常是 TypeInitializationException 这类**三层嵌套**的形状，
            // 不落日志就只剩"type initializer threw an exception"，无法定位（D34 实测踩到）。
            _log.Error(ex, $"注册计划任务『{TaskName}』失败");
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"注册计划任务『{TaskName}』失败：{ex.Message}",
                innerException: ex);
        }
    }

    /// <inheritdoc />
    public void Delete()
    {
        try
        {
            using var service = new TaskService();
            if (service.GetTask(TaskPathConstant) is null)
            {
                // 不存在直接返回：卸载路径必须可以重复执行。
                return;
            }

            service.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
            _log.Info($"已删除调度计划任务：{TaskPathConstant}");
        }
        catch (Exception ex)
        {
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"删除计划任务『{TaskName}』失败：{ex.Message}",
                innerException: ex);
        }
    }
}
