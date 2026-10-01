using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 调度端的目标存活预筛结果（S1.1）。
/// </summary>
/// <param name="Launchable">目标程序仍存在、可以进入本轮启动队列的条目。</param>
/// <param name="MissingTargets">
/// 目标程序已不存在的条目。
/// <b>它们仍全部进调度日志</b>（记 <c>Failed</c> + 原因，见
/// <c>SchedulerEngine.AppendMissingTargetItems</c>），只是不进入启动队列。
/// </param>
public sealed record TargetPrefilterResult(
    List<ScheduleEntry> Launchable,
    List<ScheduleEntry> MissingTargets);

/// <summary>
/// 把本轮计划按"目标程序还在不在"分成两堆（S1.1）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 存在的理由：这些项是**必然失败**的。让它们进启动队列等于排一个注定失败的队 ——
/// 用户要盯着进度条等完整个延时，最后收到一条失败通知，而结果从一开始就已确定。
/// </para>
/// <para>
/// 🔴 <b>兜底方向与 D87 / D90 一致：只能更宽松，绝不更严格。</b>
/// 默认判据 <see cref="TargetFileProbe.IsMissing"/> 对无路径、UWP 解析名、裸命令名、
/// 相对路径<b>一律返回 <c>false</c></b>（按"在"处理）—— 它们由 PATH 或系统解析，
/// 对它们调 <c>File.Exists</c> 必然为 false，直接判"已不存在"会造成大量误报。
/// 所以这一筛<b>只会少启动，绝不会多判失败</b>。
/// </para>
/// <para>
/// 放在 Core 而不是调度端私有方法：调度端是 NativeAOT 的 WinExe，<b>没有单元测试工程</b>
/// （与 <c>CompletionPolicy</c> 下沉同款理由）。而这一段是纯逻辑、且判错方向的代价很大
/// ——误判"目标没了"会让用户以为配置失效。
/// </para>
/// </remarks>
public static class TargetPrefilter
{
    /// <summary>
    /// 拆分计划：可启动项与目标已不存在的项。
    /// </summary>
    /// <param name="plan">本轮计划（<c>SchedulePlan.BuildWithSkipped(...).Entries</c>）。</param>
    /// <param name="isMissing">
    /// "目标程序已不存在"的判据；<see langword="null"/> 时用 <see cref="TargetFileProbe.IsMissing"/>。
    /// 🔴 做成可注入是为了让单测<b>不碰真实文件系统</b>（硬约束 10）—— 预筛的分支逻辑
    /// 与探测实现是两件事，前者该被独立钉住。
    /// </param>
    /// <returns>两堆，均为新列表（不改原 <paramref name="plan"/>）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> 为 <see langword="null"/>。</exception>
    public static TargetPrefilterResult Split(
        IReadOnlyList<ScheduleEntry> plan,
        Func<string?, bool>? isMissing = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var probe = isMissing ?? TargetFileProbe.IsMissing;

        var launchable = new List<ScheduleEntry>(plan.Count);
        var missing = new List<ScheduleEntry>();

        foreach (var entry in plan)
        {
            if (probe(entry.Item.Path))
            {
                missing.Add(entry);
            }
            else
            {
                launchable.Add(entry);
            }
        }

        return new TargetPrefilterResult(launchable, missing);
    }
}
