namespace DelayStart.Management.Models;

/// <summary>
/// 一条 DelayStart 计划任务"期望长什么样"的纯数据描述（D114，2026-09-24 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 守卫与调度两条计划任务的**全部差异**都收在这一个 record 里：机制层
/// <see cref="Services.ScheduledTaskGateway"/> 只认本类型，不含任何业务语义。
/// 构造后不可变，可安全复用。
/// </para>
/// <para>
/// 🔴 触发器**只支持单个 LogonTrigger**：本项目两条任务都是"登录后延迟 N 执行"的形状，
/// 周期性靠 <see cref="RepeatInterval"/> 表达。将来若需要别的触发器类型，扩展本类型而不是
/// 在 Gateway 里开分支。
/// </para>
/// </remarks>
/// <param name="TaskName">注册到任务库时使用的任务名（如 <c>DelayStartGuard</c>，无空格，省掉脚本引用时的引号问题）。</param>
/// <param name="TaskPath">任务的根路径，作为唯一标识（如 <c>\DelayStartGuard</c>）。</param>
/// <param name="Description">写入 RegistrationInfo 的任务描述；定义比对时逐字符比较。</param>
/// <param name="ExecutablePath">动作指向的 exe 完整路径（安装目录下）。</param>
/// <param name="WorkingDirectory">动作的工作目录（安装根目录）。</param>
/// <param name="LogonDelay">登录后延迟多久执行第一次。</param>
/// <param name="RepeatInterval">
/// 之后每隔多久重复一次；<see langword="null"/> 表示不重复（调度端 / 守卫单次档位）。
/// </param>
/// <param name="DisplayName">日志与异常消息里的中文简称（"守卫" / "调度"），保证文案与旧版逐字一致。</param>
/// <param name="ScheduleDescription">写入成功日志里括号内的调度说明（如"登录后 3 秒"）。</param>
public sealed record ScheduledTaskSpec(
    string TaskName,
    string TaskPath,
    string Description,
    string ExecutablePath,
    string WorkingDirectory,
    TimeSpan LogonDelay,
    TimeSpan? RepeatInterval,
    string DisplayName,
    string ScheduleDescription);
