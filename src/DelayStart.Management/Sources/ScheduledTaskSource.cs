using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

using Microsoft.Win32.TaskScheduler;

// 🔴 R2：TaskScheduler 包里的 Task 与 System.Threading.Tasks.Task 同名。
// 本文件明明只需要前者，仍然显式起别名 —— 这样以后有人在这个文件里加
// 一个 async 方法时，不会因为 "Task" 突然解析到错误的类型而写出难查的 bug。
// 见 docs/architecture.md 第九节 R2。
using TaskSchedulerTask = Microsoft.Win32.TaskScheduler.Task;

namespace DelayStart.Management.Sources;

/// <summary>
/// 计划任务来源（FR-1 / <c>api-analysis.md</c> 1.4）。只收**登录触发**与**启动触发**的任务。
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
/// </remarks>
public sealed class ScheduledTaskSource : IStartupSource
{
    private const string ProtectedFolderPrefix = @"\Microsoft";

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
                // FR-1.4 / api-analysis.md 1.4：坏任务必须跳过，不能让一个任务打断整次枚举。
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

            task.Enabled = enabled;
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

    private StartupEntry? TryBuildEntry(TaskSchedulerTask task, IReadOnlySet<string> takenOverKeys)
    {
        var path = task.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.StartsWith(ProtectedFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 自身的调度任务不是"自启动项"，不列给用户 —— 它的生命周期由本程序管理（FR-11）。
        if (path.Equals(TaskRegistrationService.TaskPathConstant, StringComparison.OrdinalIgnoreCase))
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

        return new StartupEntry
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(task.Name) ? path : task.Name!,
            Path = action.Path ?? string.Empty,
            Arguments = action.Arguments ?? string.Empty,
            Source = Kind,
            Scope = Scope,
            // 机制 4：计划任务的 source_key 是任务的完整路径（\Folder\Name）。
            SourceKey = path,
            SourceDetail = $"计划任务（{triggerLabel}）",
            // 计划任务的"软禁用"就是 Enabled 本身 —— 任务定义没有被破坏，随时可改回来。
            IsEnabled = task.Enabled,
            IsMissing = !string.IsNullOrWhiteSpace(action.Path)
                && Path.IsPathFullyQualified(action.Path)
                && !File.Exists(action.Path),
            // \Microsoft\* 已在上面整体过滤，能走到这里的都是第三方任务。
            IsProtected = false,
            IsTakenOver = takenOverKeys.Contains(id),
        };
    }

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
