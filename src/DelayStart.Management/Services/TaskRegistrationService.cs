using System.Security.Principal;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;

using Microsoft.Win32.TaskScheduler;

namespace DelayStart.Management.Services;

/// <summary>
/// 计划任务 <c>\DelayStartScheduler</c> 的注册 / 更新 / 删除
/// （FR-11 / D39 单任务方案 / <c>api-analysis.md</c> 3.1）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **只有一条任务**（D39，2026-09-20 用户批复：Agent 由调度端按需经 explorer
/// 委托预热拉起，不再单独注册计划任务，也就不需要 DelayStart 任务文件夹）。
/// D38 的 <c>\DelayStart\Agent</c> / <c>\DelayStart\Scheduler</c> 文件夹双任务在
/// 注册与删除时一并清理（迁移回根任务）。
/// </para>
/// <para>
/// 🔴 **身份必须是当前交互用户**（<c>Interactive</c> 登录类型）+ <c>RunLevel = Highest</c>。
/// 绝不使用 SYSTEM 或服务账户：SYSTEM 会话下 <c>%APPDATA%</c> 解析到
/// <c>C:\Windows\System32\config\systemprofile</c>，且**不抛任何异常**。
/// </para>
/// <para>
/// 用 <c>TaskService</c> API 而非 <c>schtasks.exe</c>（FR-11.3）：能读到结构化返回、
/// 支持中文任务名、参数更细，且不需要解析命令行输出。
/// </para>
/// </remarks>
public sealed class TaskRegistrationService : ISchedulerTaskRegistrar
{
    /// <summary>调度端任务的根路径，作为唯一标识。</summary>
    public const string TaskPathConstant = @"\DelayStartScheduler";

    /// <summary>D38 曾用过任务文件夹收纳双任务 —— 已废弃，注册/删除时清理。</summary>
    public const string LegacyFolderName = "DelayStart";

    /// <summary>D38 文件夹内的调度端任务名（清理用）。</summary>
    public const string LegacyFolderSchedulerTaskName = "Scheduler";

    /// <summary>D38 文件夹内的代理任务名（清理用）。</summary>
    public const string LegacyFolderAgentTaskName = "Agent";

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
            RegisterSchedulerTask(service, schedulerPath, userId);
            DeleteLegacyFolderTasks(service);

            _log.Info($"已注册调度计划任务：{TaskPathConstant}（登录后 {LogonDelaySeconds} 秒，身份 {userId}）");
        }
        catch (Exception ex)
        {
            // FR-11：注册失败必须留痕。CLI 与卸载脚本只拿得到最外层的一句话，
            // 而这里的异常常常是 TypeInitializationException 这类**三层嵌套**的形状，
            // 不落日志就只剩"type initializer threw an exception"，无法定位（D34 实测踩到）。
            _log.Error(ex, "注册调度计划任务失败");
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"注册计划任务失败：{ex.Message}",
                innerException: ex);
        }
    }

    /// <summary>注册调度端任务（提权，根路径单条）。</summary>
    private void RegisterSchedulerTask(TaskService service, string schedulerPath, string userId)
    {
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
            "DelayStartScheduler",
            definition,
            TaskCreation.CreateOrUpdate,
            userId,
            password: null,
            logonType: TaskLogonType.InteractiveToken);
    }

    /// <summary>清理 D38 遗留的 <c>\DelayStart</c> 任务文件夹与其中双任务（不存在时静默跳过）。</summary>
    private void DeleteLegacyFolderTasks(TaskService service)
    {
        var folderExists = service.RootFolder.SubFolders.Any(
            candidate => string.Equals(candidate.Name, LegacyFolderName, StringComparison.OrdinalIgnoreCase));
        if (!folderExists)
        {
            return;
        }

        using var folder = service.GetFolder(LegacyFolderName);
        folder.DeleteTask(LegacyFolderSchedulerTaskName, exceptionOnNotExists: false);
        folder.DeleteTask(LegacyFolderAgentTaskName, exceptionOnNotExists: false);
        service.RootFolder.DeleteFolder(LegacyFolderName, exceptionOnNotExists: false);
        _log.Info($"已清理旧版计划任务文件夹：\\{LegacyFolderName}（D39 还原为根任务）");
    }

    /// <inheritdoc />
    public void Delete()
    {
        try
        {
            using var service = new TaskService();

            if (service.GetTask(TaskPathConstant) is not null)
            {
                service.RootFolder.DeleteTask("DelayStartScheduler", exceptionOnNotExists: false);
                _log.Info($"已删除调度计划任务：{TaskPathConstant}");
            }

            // D38 文件夹双任务也要清（升级到 D39 前注册过的场景）。
            DeleteLegacyFolderTasks(service);
        }
        catch (Exception ex)
        {
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"删除计划任务失败：{ex.Message}",
                innerException: ex);
        }
    }
}
