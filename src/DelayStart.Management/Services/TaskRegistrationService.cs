using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 计划任务 <c>\DelayStart\Scheduler</c> 的注册 / 更新 / 删除
/// （FR-11 / D39 单任务方案 / <c>pitfalls.md</c> 二）。
/// </summary>
/// <remarks>
/// <para>
/// **策略适配器**（D114，2026-09-24 批复）：调度端是"必须存在的固定规则"——固定一条
/// <see cref="ScheduledTaskSpec"/>（登录后 <see cref="LogonDelaySeconds"/> 秒、不重复）直接交给
/// <see cref="ScheduledTaskGateway"/> 执行。与守卫任务共用的全部机制（构建定义、逐字段比对、
/// 跳过重写、异常包装）都在 Gateway 单份实现，本类不再持有任何
/// <c>Microsoft.Win32.TaskScheduler</c> 调用。
/// </para>
/// <para>
/// 🔴 **只有一条任务**（D39，2026-09-20 用户批复：Agent 由调度端按需经
/// explorer 委托预热拉起，不再单独注册计划任务）。D40 起连普通用户代理进程都不再需要
/// （调度端亲自降权），因此本程序**只创建自己的那一个任务文件夹**（2026-09-25 起
/// <c>\DelayStart</c>，守卫与调度两条任务都在里面），不涉及任何第三方目录。
/// </para>
/// <para>
/// 任务从根级 <c>\DelayStart\Scheduler</c> 迁到 <c>\DelayStart\Scheduler</c>（2026-09-25）——
/// 任务计划程序里两个自有任务因此归在一处，不会被根目录下上百个第三方任务淹没。
/// </para>
/// <para>
/// 🔴 身份必须是当前交互用户（<c>Interactive</c>）+ <c>RunLevel = Highest</c>，绝不使用
/// SYSTEM 或服务账户（SYSTEM 会话下 <c>%APPDATA%</c> 解析到 systemprofile 且不抛异常）——
/// 这条共性约束统一由 Gateway 落实。
/// </para>
/// <para>
/// 用 <c>TaskService</c> API 而非 <c>schtasks.exe</c>（FR-11.3）：能读到结构化返回、
/// 支持中文任务名、参数更细，且不需要解析命令行输出。Gateway 即此 API 的唯一封装点。
/// </para>
/// </remarks>
public sealed class TaskRegistrationService : ISchedulerTaskRegistrar
{
    /// <summary>调度端任务的完整路径（<c>\DelayStart\Scheduler</c>），作为唯一标识（<c>ScheduledTaskSource</c> 等处引用）。</summary>
    public const string TaskPathConstant = OwnedTaskFolder.Prefix + "Scheduler";

    /// <summary>登录后延迟多少秒启动调度端（FR-11.1）。留给桌面与资源管理器加载的时间。</summary>
    public const int LogonDelaySeconds = 3;

    /// <summary>注册到任务库时使用的任务名（不含文件夹前缀）。</summary>
    public const string TaskName = "Scheduler";

    /// <summary>任务描述（构建定义与"定义是否已最新"比对时共用，避免两处串不一致）。</summary>
    private const string TaskDescription =
        "DelayStart 的调度端。登录后按用户配置的延时逐个拉起被接管的程序。";

    /// <summary>日志与异常消息里的中文简称（与旧版文案逐字一致）。</summary>
    private const string DisplayName = "调度";

    private readonly PathService _paths;
    private readonly ScheduledTaskGateway _gateway;

    /// <summary>构造计划任务注册服务。</summary>
    /// <param name="paths">路径服务，取调度端 exe 的完整路径。</param>
    /// <param name="log">日志接收端。</param>
    public TaskRegistrationService(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _gateway = new ScheduledTaskGateway(log);
    }

    /// <inheritdoc />
    public string TaskPath => TaskPathConstant;

    /// <inheritdoc />
    public bool Exists()
        => _gateway.Exists(TaskPathConstant, DisplayName);

    /// <inheritdoc />
    public bool Matches()
        => _gateway.Matches(TaskPathConstant, _paths.SchedulerExecutablePath, DisplayName);

    /// <inheritdoc />
    /// <returns>是否真正写入了任务定义（<see langword="false"/> 表示现有定义已与期望一致、已跳过重写）。</returns>
    public bool RegisterOrUpdate()
        => _gateway.RegisterOrUpdate(BuildSpec());

    /// <inheritdoc />
    public void Delete()
        => _gateway.Delete(TaskPathConstant, TaskName, DisplayName);

    /// <summary>组装调度端的期望定义（固定规则，不随任何设置变化）。</summary>
    private ScheduledTaskSpec BuildSpec() => new(
        TaskName: TaskName,
        TaskPath: TaskPathConstant,
        Description: TaskDescription,
        ExecutablePath: _paths.SchedulerExecutablePath,
        WorkingDirectory: _paths.InstalledRoot,
        LogonDelay: TimeSpan.FromSeconds(LogonDelaySeconds),
        RepeatInterval: null,
        DisplayName: DisplayName,
        ScheduleDescription: $"登录后 {LogonDelaySeconds} 秒",
        Arguments: null);
}
