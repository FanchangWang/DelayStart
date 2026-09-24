using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 计划任务 <c>\DelayStartGuard</c> 的注册 / 更新 / 删除（D74，2026-09-22 批复）。
/// </summary>
/// <remarks>
/// <para>
/// **策略适配器**（D114，2026-09-24 批复）：本类只决定"此刻该有什么样的守卫任务"——
/// 按当前档位经 <see cref="GuardSchedulePlan"/> 翻译出触发规则，组装
/// <see cref="ScheduledTaskSpec"/> 交给 <see cref="ScheduledTaskGateway"/> 执行；
/// <see cref="GuardMode.Disabled"/> 的语义是"不该有这条任务"，等价于 <see cref="Delete"/>。
/// 与调度任务共用的全部机制（构建定义、逐字段比对、跳过重写、异常包装）都在
/// Gateway 单份实现，本类不再持有任何 <c>Microsoft.Win32.TaskScheduler</c> 调用。
/// </para>
/// <para>
/// 🔴 守卫需要 <c>Interactive</c> + <c>RunLevel = Highest</c> 的身份才能发**系统通知**
/// （D79）并改写 HKLM 下的 Run 项——这条共性约束统一由 Gateway 落实，此处不再重复。
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

    /// <summary>任务描述（构建定义与"定义是否已最新"比对时共用，避免两处串不一致）。</summary>
    private const string TaskDescription =
        "DelayStart 的自启动项守卫。周期巡检：把被应用写回启用的已接管项重新软禁用，"
        + "并通报新增 / 失效的自启动项，然后退出。";

    /// <summary>日志与异常消息里的中文简称（与旧版文案逐字一致）。</summary>
    private const string DisplayName = "守卫";

    private readonly PathService _paths;
    private readonly ScheduledTaskGateway _gateway;

    /// <summary>构造守卫任务注册服务。</summary>
    /// <param name="paths">路径服务，取守卫端 exe 的完整路径。</param>
    /// <param name="log">日志接收端。</param>
    public GuardTaskRegistrar(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _gateway = new ScheduledTaskGateway(log);
    }

    /// <inheritdoc />
    public string TaskPath => TaskPathConstant;

    /// <inheritdoc />
    public bool IsRegistered()
        => _gateway.IsRegistered(TaskPathConstant, _paths.GuardExecutablePath, DisplayName);

    /// <inheritdoc />
    /// <returns>是否真正写入了任务定义（<see langword="false"/> 表示现有定义已与期望一致、已跳过重写）。</returns>
    public bool RegisterOrUpdate(GuardMode mode, int minutes)
    {
        var trigger = GuardSchedulePlan.Build(mode, minutes);
        if (trigger is null)
        {
            // Disabled 的语义就是"不该有这条任务"。
            Delete();
            return true;
        }

        return _gateway.RegisterOrUpdate(new ScheduledTaskSpec(
            TaskName: TaskName,
            TaskPath: TaskPathConstant,
            Description: TaskDescription,
            ExecutablePath: _paths.GuardExecutablePath,
            WorkingDirectory: _paths.InstalledRoot,
            LogonDelay: trigger.InitialDelay,
            RepeatInterval: trigger.RepeatInterval,
            DisplayName: DisplayName,
            ScheduleDescription: DescribeMode(mode, minutes)));
    }

    /// <inheritdoc />
    public void Delete()
        => _gateway.Delete(TaskPathConstant, TaskName, DisplayName);

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
