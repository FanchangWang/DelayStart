using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

using Microsoft.Win32.TaskScheduler;

// 🔴 R2：TaskScheduler 包里的 Task 与 System.Threading.Tasks.Task 同名。
// 本文件明明只需要前者，仍然显式起别名 —— 这样以后有人在这个文件里加
// 一个 async 方法时，不会因为 "Task" 突然解析到错误的类型而写出难查的 bug。
// 见 docs/decisions.md 附表 R2。
using TaskSchedulerTask = Microsoft.Win32.TaskScheduler.Task;

namespace DelayStart.Management.Sources;

/// <summary>
/// 计划任务来源（FR-1 / <c>pitfalls.md</c> 二）。只收**登录触发**与**启动触发**的任务。
/// </summary>
/// <remarks>
/// <para>
/// 用 <c>TaskService</c> API 而不是 <c>schtasks.exe</c>（FR-11.3 的同一条理由）：
/// 能读结构化返回、支持中文任务名、参数更细。
/// </para>
/// <para>
/// 🔴 **刻意跳过 <c>\Microsoft\*</c> 下的任务**（FR-1.9 的"受保护项"）。理由是它们数量极大
/// （一台干净 Win11 上就有上百个）、全部由系统管理、且本程序按 D20 提权也改不动 ——
/// 展示出来的唯一效果是把用户真正关心的十几个任务淹掉。FR-1.9 的意图是"不可操作的项要标只读"，
/// 而"根本不展示"是更强的保证。
/// </para>
/// <para>
/// 🔴 **禁用是分层的**（D67）：只有一个触发器的任务切**任务级开关**；多触发器任务**只切登录 / 启动触发器** ——
/// 否则会把同一任务里的"每日定时"一并关掉（用户报的真缺陷）。规则与理由见 <c>SetEnabled</c>。
/// </para>
/// </remarks>
public sealed class ScheduledTaskSource : IStartupSource
{
    /// <summary>
    /// 受保护文件夹 <c>\Microsoft</c> 的路径前缀（FR-1.9）。
    /// </summary>
    /// <remarks>
    /// 🔴 **末尾那个反斜杠是承重的，不是笔误。** 判定用的 <c>task.Path</c> 是"文件夹 + 任务名"的**完整路径**，
    /// 少一个 <c>\</c> 就会把根级"名字以 Microsoft 开头"的任务一并吃掉 —— 典型样本是
    /// <c>\MicrosoftEdgeUpdateTaskMachineCore</c> / <c>\MicrosoftEdgeUpdateTaskMachineUA</c>
    /// （Edge 自动更新的第三方任务）。症状是"用户设过的登录自启项在列表里凭空消失"，且没有任何日志。
    /// </remarks>
    private const string ProtectedFolderPrefix = @"\Microsoft\";

    private readonly ILogSink _log;

    /// <summary>构造计划任务来源。</summary>
    /// <param name="log">日志接收端。</param>
    public ScheduledTaskSource(ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public StartupSource Kind => StartupSource.ScheduledTask;

    /// <inheritdoc />
    public StartupScope Scope => StartupScope.None;

    /// <inheritdoc />
    public string DisplayName => "计划任务";

    /// <inheritdoc />
    public bool RequiresElevation => true;

    /// <inheritdoc />
    public IReadOnlyList<StartupEntry> Scan(IReadOnlySet<string> takenOverKeys)
    {
        ArgumentNullException.ThrowIfNull(takenOverKeys);

        var entries = new List<StartupEntry>();

        using var service = new TaskService();
        foreach (var task in service.AllTasks)
        {
            try
            {
                if (TryBuildEntry(task, takenOverKeys) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                // FR-1.4 / pitfalls.md 二：坏任务必须跳过，不能让一个任务打断整次枚举。
                // 这里不筛异常类型是刻意的：TaskScheduler 会抛 COMException、TargetInvocationException、
                // XmlException 等多种类型，逐个列举必然漏。
                _log.Warn(ex, $"读取计划任务『{SafeName(task)}』失败，已跳过（FR-1.4）");
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public void Disable(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        SetEnabled(entry, enabled: false);
    }

    /// <inheritdoc />
    public void Enable(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        SetEnabled(entry, enabled: true);
    }

    /// <summary>
    /// 把「禁用 / 启用」落到正确的层级上（D67）。
    /// </summary>
    /// <param name="entry">要操作的条目（只用到 <see cref="StartupEntry.SourceKey"/> 与 <see cref="StartupEntry.Id"/>）。</param>
    /// <param name="enabled"><see langword="true"/> 启用，<see langword="false"/> 禁用。</param>
    /// <remarks>
    /// <para>
    /// 🔴 <b>为什么不能一律 <c>task.Enabled = false</c></b>：那是**任务级**开关，会把任务里的**所有**触发器一起关掉。
    /// 而真实世界里的第三方任务常常是「登录触发 + 每日定时」两个触发器共存（实测本机
    /// <c>\QuarkCloudDriveUpdaterUser\…</c> 与 <c>\GoogleUser\GoogleUpdater\…</c> 正是这个形状），
    /// 用户要接管的只是登录那一个 —— 一律关任务级开关，等于顺手毁掉一个他根本没打算动的定时任务。
    /// </para>
    /// <para>
    /// 规则（D67）：
    /// <list type="number">
    /// <item><description>**禁用**时若任务级开关已经是关的 —— 它本来就不会自启动，**一个字节都不动**（"全部可逆"）。</description></item>
    /// <item><description>触发器总数 ≤ 1 —— 切任务级开关。此时两者等价，而任务级开关语义更明确（任务计划程序里也看得见）。</description></item>
    /// <item><description>触发器多于 1 个 —— 只切**登录 / 启动触发器**的 <c>Enabled</c>，其余触发器原样保留。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// 🔴 <b>多于 1 个「登录 / 启动触发器」时是**全部**切掉，不是只切第一个。</b>只切一个的话，程序照旧在登录时自启，
    /// 再叠加我们的延时启动 —— 变成启动两次，比不修更糟。<c>DescribeTrigger</c> 只取第一个，那只是**展示**文案。
    /// </para>
    /// <para>
    /// 🔴 <b>启用时若任务级开关是关的，就先把任务打开再改触发器</b>：任务级关着的时候触发器怎么改都不会跑，
    /// 只改触发器等于点了没反应。代价是连同其余触发器一并恢复 —— 但"这个任务本来就被整体禁用"是既有事实，
    /// 不是我们额外关的。
    /// </para>
    /// </remarks>
    private static void SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            using var service = new TaskService();

            // 🔴 用完整路径（\Folder\Name）定位，不能用 task.Name —— 同名任务可以在不同文件夹下共存，
            // 用 Name 会改错对象（机制 1 的同一个道理）。
            var task = service.GetTask(entry.SourceKey)
                ?? throw new StartupOperationException(
                    StartupFailureReason.ScheduledTaskFailed,
                    entry.Id,
                    $"计划任务已不存在，无法{(enabled ? "启用" : "禁用")}：{entry.SourceKey}");

            var definition = task.Definition;
            if (definition is null)
            {
                throw new StartupOperationException(
                    StartupFailureReason.ScheduledTaskFailed,
                    entry.Id,
                    $"读取计划任务『{entry.SourceKey}』的触发器失败，无法{(enabled ? "启用" : "禁用")}。");
            }

            var triggers = definition.Triggers;
            var autostart = new List<int>(triggers.Count);
            for (var i = 0; i < triggers.Count; i++)
            {
                if (IsAutostartTrigger(triggers[i]))
                {
                    autostart.Add(i);
                }
            }

            if (!enabled && !task.Enabled)
            {
                // 任务级已经关着 = 它已经不会自启动。此时再去改触发器，只会留下一个"我们动过、
                // 但释放接管时无从判断原始值"的痕迹 —— 释放路径手上只有 WasEnabled，没有触发器原状。
                return;
            }

            if (!ShouldToggleWholeTask(triggers.Count, autostart.Count))
            {
                // 先改触发器并落盘，再动任务级开关 —— 若任务级 setter 内部会重注册整份定义，
                // 顺序反了会把尚未提交的触发器改动覆盖掉。
                SetTriggersEnabled(task, triggers, autostart, enabled);

                if (!enabled)
                {
                    // 禁用多触发器任务：任务级开关保持启用，其余触发器照常工作。
                    return;
                }
            }

            if (task.Enabled != enabled)
            {
                task.Enabled = enabled;
            }
        }
        catch (Exception ex) when (ex is not StartupOperationException)
        {
            throw new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entry.Id,
                $"{(enabled ? "启用" : "禁用")}计划任务『{entry.SourceKey}』失败：{ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// 把指定下标的触发器切成 <paramref name="enabled"/> 并落盘（D67）。
    /// </summary>
    /// <param name="task">触发器所属的任务（<c>RegisterChanges</c> 的落点）。</param>
    /// <param name="triggers">任务定义里的触发器集合。</param>
    /// <param name="indexes">要切的触发器下标（只含登录 / 启动触发器）。</param>
    /// <param name="enabled">目标状态。</param>
    /// <remarks>
    /// 一个触发器都没变时**不重写任务**：<c>RegisterChanges</c> 是把整份定义重新注册 ——
    /// 那会刷新任务时间戳、并触发任务里可能存在的 <c>RegistrationTrigger</c>（"当任务被创建或修改"）。
    /// 幂等调用（释放接管时必然会重复走到这里）不该产生这种副作用。
    /// </remarks>
    private static void SetTriggersEnabled(
        TaskSchedulerTask task,
        TriggerCollection triggers,
        List<int> indexes,
        bool enabled)
    {
        var changed = false;
        foreach (var index in indexes)
        {
            var trigger = triggers[index];
            if (trigger.Enabled == enabled)
            {
                continue;
            }

            trigger.Enabled = enabled;
            changed = true;
        }

        if (changed)
        {
            task.RegisterChanges();
        }
    }

    private StartupEntry? TryBuildEntry(TaskSchedulerTask task, IReadOnlySet<string> takenOverKeys)
    {
        var path = task.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (IsProtectedFolderPath(path))
        {
            return null;
        }

        // 自身的调度任务、守卫任务与普通用户代理任务不是"自启动项"，不列给用户 ——
        // 它们的生命周期由本程序管理（FR-11 / D38；守卫见 D74+）。
        if (IsOwnedTask(path))
        {
            return null;
        }

        var definition = task.Definition;
        if (definition is null)
        {
            return null;
        }

        var triggerLabel = DescribeTrigger(definition);
        if (triggerLabel is null)
        {
            // 既非登录触发也非启动触发 → 不是自启动项（可能是定时任务）。
            return null;
        }

        // ExecAction 才是"启动一个程序"；ComHandler / SendEmail 之类不构成自启动。
        var action = definition.Actions.OfType<ExecAction>().FirstOrDefault();
        if (action is null)
        {
            return null;
        }

        var id = ItemKeyBuilder.Build(Kind, Scope, path);
        var actionPath = action.Path ?? string.Empty;

        return new StartupEntry
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(task.Name) ? path : task.Name!,
            Path = actionPath,
            // 批复 9：动作常是 cmd.exe /c start "" "C:\app\x.exe" 这类包装 —— 把真正的
            // exe 解析出来供图标展示；启动语义不动（Path + Arguments 原样保留）。
            // 解析实现 2026-09-22 下沉到 Core（LaunchTargetResolver），调度端防双启动要用同一份。
            ExecutablePath = LaunchTargetResolver
                .Resolve(actionPath, action.Arguments, StartupSource.ScheduledTask)?.ExecutablePath
                ?? string.Empty,
            Arguments = action.Arguments ?? string.Empty,
            Source = Kind,
            Scope = Scope,
            // 机制 4：计划任务的 source_key 是任务的完整路径（\Folder\Name）。
            SourceKey = path,
            SourceDetail = BuildSourceDetail(triggerLabel, definition.Triggers.Count),
            // 计划任务的"软禁用"就是 Enabled 本身 —— 任务定义没有被破坏，随时可改回来。
            // 🔴 但多触发器任务上我们关的是**触发器**而不是任务（D67），所以不能只看 task.Enabled。
            IsEnabled = ComputeIsEnabled(task, definition),
            // 判据本体在 TargetFileProbe（2026-09-22 集中，此前三处各写一遍）。
            // ⚠️ 这里的 action.Path 常是 cmd.exe 这类**包装器**，包装器存在不代表真目标还在 ——
            // 那是已知的漏判方向，与三个来源一致（真目标解析只服务双启动检测，见 LaunchTargetResolver）。
            IsMissing = TargetFileProbe.IsMissing(action.Path),
            // \Microsoft\ 文件夹下的任务已在上面整体过滤（IsProtectedFolderPath），
            // 能走到这里的都是第三方任务 —— 包括根级名叫 \MicrosoftEdgeUpdateXxx 的那些。
            IsProtected = false,
            IsTakenOver = takenOverKeys.Contains(id),
        };
    }

    /// <summary>
    /// 判定是否落在本程序**独占**的计划任务文件夹里（<c>\DelayStart\</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 判据是**文件夹归属**，不是"列举我有哪几条任务"。程序自己创建这个文件夹
    /// （<c>EnsureFolder</c>）、空时自己删掉（<c>TryDeleteFolderIfEmpty</c>），
    /// 里面的任何任务都是本程序的 —— 列举式的白名单会在"将来加了第三条任务却忘了改这里"
    /// 时漏掉一条，让它混进用户的自启动项列表，那正是 B3 要防的问题复发（D123）。
    /// </para>
    /// <para>
    /// 🔴 前缀带尾部分隔符，少了它 <c>\DelayStartExtra\Foo</c> 也会被误判成自有任务 ——
    /// 与 <see cref="IsProtectedFolderPath"/> 踩过的同一个坑（<c>pitfalls.md</c> 二：
    /// 根级名字以 Microsoft 开头的 Edge 更新任务曾被整体误过滤）。
    /// </para>
    /// <para>
    /// 根级旧路径（<c>\DelayStartScheduler</c> / <c>\DelayStartGuard</c>）**不**在此判据内 ——
    /// 本项目不做兼容清理，它们由旧版卸载器负责（D121 / D122 要求先卸载旧版）。
    /// </para>
    /// </remarks>
    internal static bool IsOwnedTask(string path)
    {
        return path.StartsWith(OwnedTaskFolder.Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断某计划任务是否落在受保护的 <c>\Microsoft</c> 文件夹下（FR-1.9）。
    /// </summary>
    /// <param name="taskPath">计划任务的完整路径（<c>task.Path</c>，形如 <c>\Folder\Name</c>）。</param>
    /// <returns>在 <c>\Microsoft</c> 文件夹及其任意子文件夹下时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// 🔴 判据是**文件夹**，不是名字 —— 名字里带 Microsoft 的任务（例如
    /// <c>\MicrosoftEdgeUpdateTaskMachineCore</c>）是第三方任务，必须照常展示。
    /// 这条边界曾写错（少一个尾部分隔符），代价是一个真机缺陷。
    /// </para>
    /// <para>
    /// 只认**根级**的 <c>\Microsoft</c>：Windows 的保留文件夹只有这一个。
    /// <c>\MicrosoftX\Foo</c>、<c>\MyVendor\Microsoft\Foo</c> 这类自建文件夹照常展示。
    /// </para>
    /// <para>
    /// 用 <c>task.Path</c>（含任务名）而不是 <c>task.Folder.Path</c> 判定：前者是构造 <c>ItemKey</c> 时已在手的数据，
    /// 且本方法要能在不新建 <c>TaskService</c> 的纯逻辑单测里跑。
    /// </para>
    /// </remarks>
    public static bool IsProtectedFolderPath(string? taskPath) =>
        !string.IsNullOrWhiteSpace(taskPath)
        && taskPath.StartsWith(ProtectedFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断一个触发器是不是「自启动触发器」（登录触发 / 启动触发）—— 也就是本程序唯一接管的那一类（D67）。
    /// </summary>
    /// <param name="trigger">任务定义里的触发器。</param>
    /// <returns>登录触发或启动触发时为 <see langword="true"/>；<paramref name="trigger"/> 为 <see langword="null"/> 时也是 <see langword="false"/>。</returns>
    /// <remarks>
    /// 一个任务里可以同时挂着每日 / 每周定时、空闲、事件、会话状态变化、注册时触发等等 ——
    /// 它们与"开机 / 登录自启"无关，接管时刻意不碰。<c>SessionStateChangeTrigger</c>（会话解锁 / 连接）
    /// 特别容易被误当成登录触发：它**不是** <c>LogonTrigger</c> 的子类，本判据不会把它算进来。
    /// </remarks>
    public static bool IsAutostartTrigger(Trigger? trigger) => trigger is LogonTrigger or BootTrigger;

    /// <summary>
    /// 判断「禁用 / 启用」该切**任务级开关**，还是只切**触发器**的开关（D67）。
    /// </summary>
    /// <param name="triggerCount">任务里触发器的总数。</param>
    /// <param name="autostartTriggerCount">其中登录 / 启动触发器的数量。</param>
    /// <returns>应当切任务级开关时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 规则：只有一个触发器（或压根没有登录 / 启动触发器 —— 那种任务不会进列表，这里只是防呆）
    /// ⇒ 切任务级开关，此时两者效果等价、而任务级开关语义更明确；多触发器任务 ⇒ 只切自启动触发器，
    /// 把同一任务里的定时 / 空闲 / 事件触发器原样留给它自己。
    /// </remarks>
    public static bool ShouldToggleWholeTask(int triggerCount, int autostartTriggerCount) =>
        triggerCount <= 1 || autostartTriggerCount == 0;

    /// <summary>位置描述：把「另有几个触发器」一并写出来（D67）。</summary>
    /// <param name="triggerLabel">主导触发器的展示文案，如 <c>登录时</c>。</param>
    /// <param name="triggerCount">任务里触发器的总数。</param>
    /// <returns>形如 <c>计划任务（登录时）</c> / <c>计划任务（登录时；另有 1 个触发器）</c>。</returns>
    /// <remarks>
    /// 🔴 多出来的那个数字是**给用户看的**：它说明"我们只接管了登录那一个，其余触发器还在自己跑"。
    /// 少了它，用户看到「计划任务（登录时）」会以为整个任务都归我们管，而实际上每日定时照常在跑。
    /// </remarks>
    public static string BuildSourceDetail(string triggerLabel, int triggerCount)
    {
        var others = triggerCount - 1;
        return others > 0
            ? $"计划任务（{triggerLabel}；另有 {others} 个触发器）"
            : $"计划任务（{triggerLabel}）";
    }

    /// <summary>
    /// 条目「会不会在登录 / 启动时自启动」（D67）。
    /// </summary>
    /// <param name="task">计划任务。</param>
    /// <param name="definition">该任务的定义。</param>
    /// <returns>会自启动时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 不能只看 <c>task.Enabled</c>：接管多触发器任务后我们关的是**触发器**，任务级开关仍是启用的。
    /// 只看任务级开关的话，列表会一直显示「已启用」、「禁用」按钮永远可点、点完也不变 —— 界面上就是坏的。
    /// 判据与 <see cref="SetEnabled"/> 对称：任务级开关**且**至少一个自启动触发器启用。
    /// </remarks>
    private static bool ComputeIsEnabled(TaskSchedulerTask task, TaskDefinition definition)
    {
        if (!task.Enabled)
        {
            return false;
        }

        var triggers = definition.Triggers;
        var hasAutostart = false;
        for (var i = 0; i < triggers.Count; i++)
        {
            if (!IsAutostartTrigger(triggers[i]))
            {
                continue;
            }

            hasAutostart = true;
            if (triggers[i].Enabled)
            {
                return true;
            }
        }

        // 没有自启动触发器的任务根本不会进列表（DescribeTrigger 会返回 null）；走到这里只可能是
        // 扫描与操作之间任务被改了 —— 此时以任务级开关为准。
        return !hasAutostart;
    }

    // ---- 动作路径 → 真实 exe 的解析已下沉到 Core（2026-09-22，D76）----
    // 原先本文件里的包装器名单与三个解析辅助方法只服务图标展示，而调度端"防双启动"
    // 需要完全同一套推导规则。两份实现漂移的后果很具体：包装器名单少一个名字，那条被
    // 接管的计划任务就会每轮判"进程已存在"、永不启动。现在统一走 Core 的 LaunchTargetResolver。

    /// <summary>取触发时机的展示文案；不是登录/启动触发时返回 <see langword="null"/>。</summary>
    private static string? DescribeTrigger(TaskDefinition definition)
    {
        foreach (var trigger in definition.Triggers)
        {
            switch (trigger)
            {
                case LogonTrigger logon:
                    return logon.Delay is { TotalSeconds: > 0 } delay
                        ? $"登录时，延迟 {delay.TotalSeconds:0} 秒"
                        : "登录时";
                case BootTrigger boot:
                    return boot.Delay is { TotalSeconds: > 0 } bootDelay
                        ? $"启动时，延迟 {bootDelay.TotalSeconds:0} 秒"
                        : "启动时";
            }
        }

        return null;
    }

    /// <summary>取任务名用于日志；连名字都读不了时也不能让日志本身抛异常。</summary>
    private static string SafeName(TaskSchedulerTask task)
    {
        try
        {
            return task.Path ?? task.Name ?? "(未知任务)";
        }
        catch
        {
            return "(无法读取的任务)";
        }
    }
}
