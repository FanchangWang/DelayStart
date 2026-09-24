using System.Security.Principal;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Models;

using Microsoft.Win32.TaskScheduler;

namespace DelayStart.Management.Services;

/// <summary>
/// 守卫与调度两套计划任务**共用**的注册机制层（D114，2026-09-24 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 此前 <see cref="GuardTaskRegistrar"/> 与 <see cref="TaskRegistrationService"/> 各自持有一份
/// 逐字拷贝的注册 / 比对 / 删除实现（守卫注释里写着"改一处要改两处"）。D114 把机制收敛到本类：
/// 只认 <see cref="ScheduledTaskSpec"/>，不理解任何业务语义 —— "此刻该有什么样的任务"（策略）
/// 留在两个适配器里，"怎么把它写进任务计划程序"（机制）只有这里一份。
/// </para>
/// <para>
/// 🔴 **F1 语义（D112 / D113）在本类统一落实**：写入前先逐字段比对现有定义，完全一致则跳过
/// <c>RegisterTaskDefinition</c> 并返回 <see langword="false"/> —— <c>CreateOrUpdate</c> 会重建
/// 触发器，把"登录后 N 分钟 / 秒"这个一次性窗口丢掉（登录事件已经过去，新触发器在本会话内
/// 永远等不到下一次登录），并覆盖「上次运行时间」等统计（假成功）。只有缺失或定义确实变了
/// （档位调整 / 安装目录迁移 / 任务被改坏）时才真正写入。
/// </para>
/// <para>
/// 🔴 **账户字段必须经 <see cref="SameAccount"/> 归一化比较**（D114 真机首验修正，2026-09-24）：
/// 任务计划程序落盘时会把 Principal 的账户名**规范化成 SID**，而库读回
/// <c>Principal.UserId</c> 时给出的是**裸账户名**（<c>guyue</c>，丢了域前缀；完整名在
/// <c>Principal.Account</c>）。写的是 <c>MSI\guyue</c>、读的是 <c>guyue</c>，字符串比对永远
/// 不等 → 每次启动都误判"定义已变更"而重写（F1 修复被整体架空，见 pitfalls 二十四）。
/// 触发器的 UserId 虽然本机读回仍是全名，但同样存在被服务端改写成 SID 的可能，一并走
/// 归一化比较。
/// </para>
/// <para>
/// 🔴 **身份必须是当前交互用户**（<c>Interactive</c> + <c>RunLevel = Highest</c>），
/// 对守卫与调度同样成立：Interactive 才能发**系统通知**（D79），Highest 才能改写 HKLM 下的
/// Run 项与第三方计划任务，而计划任务提权**不弹 UAC**；SYSTEM 会话下 <c>%APPDATA%</c>
/// 解析到 systemprofile 且**不抛任何异常**（D20 体系下的红线）。
/// </para>
/// <para>
/// <see langword="internal"/> 且不进 DI（D3 批复）：仅被同程序集的两个适配器直接构造。
/// </para>
/// </remarks>
internal sealed class ScheduledTaskGateway
{
    private readonly ILogSink _log;

    /// <summary>构造计划任务机制层。</summary>
    /// <param name="log">日志接收端。</param>
    public ScheduledTaskGateway(ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
    }

    /// <summary>判断任务路径是否存在，不检查 action 是否指向当前安装目录。</summary>
    /// <param name="taskPath">任务根路径（如 <c>\DelayStartGuard</c>）。</param>
    /// <param name="displayName">日志里的中文简称（"守卫" / "调度"）。</param>
    /// <returns>任务存在则为 <see langword="true"/>。</returns>
    public bool Exists(string taskPath, string displayName)
    {
        try
        {
            using var service = new TaskService();
            return service.GetTask(taskPath) is not null;
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"查询{displayName}计划任务是否存在失败，按不存在处理");
            return false;
        }
    }

    /// <summary>
    /// 判断任务是否存在且至少有一个 ExecAction 指向给定的 exe。
    /// </summary>
    /// <remarks>
    /// 这是旧 <c>IsRegistered</c> 的兼容语义，不等于完整定义匹配；完整定义由
    /// <see cref="IsDefinitionUpToDate"/> 判定。
    /// </remarks>
    /// <param name="taskPath">任务根路径（如 <c>\DelayStartGuard</c>）。</param>
    /// <param name="executablePath">期望的动作 exe 路径。</param>
    /// <param name="displayName">日志里的中文简称（"守卫" / "调度"）。</param>
    /// <returns>存在且 action 路径匹配则为 <see langword="true"/>。</returns>
    public bool Matches(string taskPath, string executablePath, string displayName)
    {
        try
        {
            using var service = new TaskService();
            var task = service.GetTask(taskPath);
            if (task is null)
            {
                return false;
            }

            return task.Definition.Actions.OfType<ExecAction>().Any(
                action => string.Equals(action.Path, executablePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"查询{displayName}计划任务 action 是否匹配失败，按不匹配处理");
            return false;
        }
    }

    /// <summary>
    /// 幂等注册或更新任务：现有定义与期望完全一致时**跳过重写**（返回
    /// <see langword="false"/>，保留已武装的登录触发器与运行统计）；缺失或定义变更则写入。
    /// </summary>
    /// <param name="spec">期望的任务定义。</param>
    /// <returns>是否真正写入了任务定义。</returns>
    /// <exception cref="StartupOperationException">exe 缺失或注册失败时抛出（E12）。</exception>
    public bool RegisterOrUpdate(ScheduledTaskSpec spec)
    {
        if (!File.Exists(spec.ExecutablePath))
        {
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"{spec.DisplayName}程序不存在，无法注册计划任务：{spec.ExecutablePath}。"
                    + "请确认安装完整，或重新运行安装程序。");
        }

        // 🔴 取当前交互用户而不是 SYSTEM。提权进程的 WindowsIdentity 仍是那个人类用户，
        // 因为 UAC 提权只是换了令牌权限，没有换账户。
        var userId = WindowsIdentity.GetCurrent().Name;

        try
        {
            using var service = new TaskService();
            var wrote = RegisterTask(service, spec, userId);

            _log.Info(wrote
                ? $"已注册{spec.DisplayName}计划任务：{spec.TaskPath}（{spec.ScheduleDescription}，身份 {userId}）"
                : $"{spec.DisplayName}计划任务定义未变化，跳过重写（保留触发器与运行统计）：{spec.TaskPath}");

            return wrote;
        }
        catch (Exception ex)
        {
            // 注册失败必须留痕。CLI 与卸载脚本只拿得到最外层的一句话，而这里的异常常常是
            // TypeInitializationException 这类**三层嵌套**的形状，不落日志就只剩
            // "type initializer threw an exception"，无法定位（D34 实测踩到）。
            _log.Error(ex, $"注册{spec.DisplayName}计划任务失败");
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"注册{spec.DisplayName}计划任务失败：{ex.Message}",
                innerException: ex);
        }
    }

    /// <summary>删除任务。任务不存在时静默返回，不抛异常（守卫关闭与卸载路径都要能重复执行）。</summary>
    /// <param name="taskPath">任务根路径。</param>
    /// <param name="taskName">注册到任务库的任务名。</param>
    /// <param name="displayName">日志与异常消息里的中文简称。</param>
    /// <exception cref="StartupOperationException">删除失败时抛出。</exception>
    public void Delete(string taskPath, string taskName, string displayName)
    {
        try
        {
            using var service = new TaskService();

            if (service.GetTask(taskPath) is not null)
            {
                service.RootFolder.DeleteTask(taskName, exceptionOnNotExists: false);
                _log.Info($"已删除{displayName}计划任务：{taskPath}");
            }
        }
        catch (Exception ex)
        {
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: $"删除{displayName}计划任务失败：{ex.Message}",
                innerException: ex);
        }
    }

    /// <summary>
    /// 判断两个账户标识是否指同一账户（D114 真机首验修正）。
    /// </summary>
    /// <remarks>
    /// 兼容三种形态的任意组合：全名（<c>MSI\guyue</c>）、裸名（<c>guyue</c>）、SID
    /// （<c>S-1-5-…</c>）。先做字符串等值短路；不等时把两侧都归一化成 SID 再比，
    /// 任一侧无法映射（拼写错的名字 / 不可解析的裸名）则按不等处理。
    /// </remarks>
    /// <param name="left">左侧账户标识。</param>
    /// <param name="right">右侧账户标识。</param>
    /// <returns>同一账户则为 <see langword="true"/>。</returns>
    internal static bool SameAccount(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryToSid(left, out var leftSid)
            && TryToSid(right, out var rightSid)
            && string.Equals(leftSid, rightSid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把账户名 / SID 统一解析成 SID 字符串；无法映射时返回 <see langword="false"/>。</summary>
    private static bool TryToSid(string account, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sid)
    {
        sid = null;
        try
        {
            IdentityReference reference = account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(account)
                : new NTAccount(account);

            sid = reference.Translate(typeof(SecurityIdentifier)).Value;
            return true;
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>构建并（必要时）写入任务定义。</summary>
    /// <returns>
    /// <see langword="true"/> 表示真正写入了定义；<see langword="false"/> 表示现有定义与期望完全一致，
    /// 已跳过写入以保留触发器状态（F1 / D112 / D113）。
    /// </returns>
    private static bool RegisterTask(TaskService service, ScheduledTaskSpec spec, string userId)
    {
        // F1（D112 / D113）：先比对现有任务，完全一致则跳过写入，保留已武装的登录触发器
        // 与「上次运行时间」等统计。只有定义确实变了才真正写入。
        using (var existingTask = service.GetTask(spec.TaskPath))
        {
            if (existingTask is not null
                && IsDefinitionUpToDate(existingTask.Enabled, existingTask.Definition, spec, userId))
            {
                return false;
            }
        }

        using var definition = service.NewTask();

        definition.RegistrationInfo.Description = spec.Description;

        definition.Principal.RunLevel = TaskRunLevel.Highest;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.UserId = userId;

        var logonTrigger = new LogonTrigger
        {
            Delay = spec.LogonDelay,
            // 多个用户同时登录时，任务只在**该用户**的会话里跑 —— 与"每用户一套配置"一致。
            UserId = userId,
        };

        if (spec.RepeatInterval is { } interval)
        {
            // 只设 Interval，**不设 Duration**：留空即"无限期重复"（任务计划程序 GUI 的默认档位）。
            logonTrigger.Repetition.Interval = interval;
        }

        definition.Triggers.Add(logonTrigger);

        definition.Actions.Add(new ExecAction(
            spec.ExecutablePath,
            arguments: spec.Arguments,
            workingDirectory: spec.WorkingDirectory));

        var settings = definition.Settings;
        // 🔴 六项必须逐项显式设置，不能依赖默认值（守卫与调度共用的原因各占一半）：
        // ExecutionTimeLimit —— 默认 72h 会把"提示框等到超时才收尾"的守卫判成执行超时；
        //   调度端按设计要驻留到最后一个延时项启动完毕（D13），更不能有时限。
        // 电池两项 —— 默认 DisallowStartIfOnBatteries = true ⇒ 笔记本拔掉电源时**静默不跑**。
        // MultipleInstances —— 缺 IgnoreNew 时两次触发重叠会把同一轮工作并行跑两遍；
        //   调度端快速切换用户再次登录时也不该并行再起一个。
        // Idle / Network 两项 —— 登录瞬间条件可能尚未就绪，让任务在条件不满足时也能跑。
        settings.ExecutionTimeLimit = TimeSpan.Zero;
        settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries = false;
        settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        settings.RunOnlyIfIdle = false;
        settings.RunOnlyIfNetworkAvailable = false;

        _ = service.RootFolder.RegisterTaskDefinition(
            spec.TaskName,
            definition,
            TaskCreation.CreateOrUpdate,
            userId,
            password: null,
            logonType: TaskLogonType.InteractiveToken);

        return true;
    }

    /// <summary>判断现有任务定义是否与期望完全一致（F1 / D112 / D113，全项目唯一一份）。</summary>
    /// <remarks>
    /// 只比"我们显式写入的字段"，不比未设置的默认值（避免库版本差异造成的默认值漂移误判）。
    /// 任一字段不符即视为"需要重写"。账户字段经 <see cref="SameAccount"/> 归一化（见类注释）。
    /// 任务级 / 触发器级 Enabled 与 ExecAction arguments 也属于显式语义，必须比对。
    /// </remarks>
    /// <param name="taskEnabled">任务级启用状态。</param>
    /// <param name="existing">任务计划程序里的现有定义。</param>
    /// <param name="spec">期望的定义。</param>
    /// <param name="userId">注册时使用的交互用户（比对 Principal 与触发器的 UserId）。</param>
    internal static bool IsDefinitionUpToDate(
        bool taskEnabled,
        TaskDefinition existing,
        ScheduledTaskSpec spec,
        string userId)
    {
        if (!taskEnabled)
        {
            return false;
        }

        if (!string.Equals(existing.RegistrationInfo.Description, spec.Description, StringComparison.Ordinal))
        {
            return false;
        }

        // 仅支持单个 ExecAction，路径与工作目录都要一致。
        if (existing.Actions.Count != 1)
        {
            return false;
        }
        var exec = existing.Actions.OfType<ExecAction>().FirstOrDefault();
        if (exec is null
            || !string.Equals(exec.Path, spec.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(exec.WorkingDirectory, spec.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(exec.Arguments ?? string.Empty, spec.Arguments ?? string.Empty, StringComparison.Ordinal))
        {
            return false;
        }

        // Principal 三项。UserId 经归一化：任务库落盘可能是 SID、库读回可能是裸名。
        if (existing.Principal.LogonType != TaskLogonType.InteractiveToken
            || existing.Principal.RunLevel != TaskRunLevel.Highest
            || !SameAccount(existing.Principal.UserId, userId))
        {
            return false;
        }

        // 触发器：期望恰好一个 LogonTrigger。
        if (existing.Triggers.Count != 1)
        {
            return false;
        }
        var logon = existing.Triggers.OfType<LogonTrigger>().FirstOrDefault();
        if (logon is null || !logon.Enabled)
        {
            return false;
        }
        if (logon.Delay != spec.LogonDelay)
        {
            return false;
        }
        // 重复间隔：调度端不重复（期望 0）；守卫周期档位期望等于档位间隔。
        if (logon.Repetition.Interval != (spec.RepeatInterval ?? TimeSpan.Zero))
        {
            return false;
        }
        if (!SameAccount(logon.UserId, userId))
        {
            return false;
        }

        // Settings 六项关键项。
        var s = existing.Settings;
        if (s.ExecutionTimeLimit != TimeSpan.Zero
            || s.DisallowStartIfOnBatteries != false
            || s.StopIfGoingOnBatteries != false
            || s.MultipleInstances != TaskInstancesPolicy.IgnoreNew
            || s.RunOnlyIfIdle != false
            || s.RunOnlyIfNetworkAvailable != false)
        {
            return false;
        }

        return true;
    }
}
