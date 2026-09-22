using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>"这一条要不要放弃启动"的决策。</summary>
/// <param name="Skip">
/// 目标进程已在运行时为 <see langword="true"/>。**只保留这一个字段**：
/// 原因文案是常量，由调度端直接写（<c>进程已存在，未重复启动</c>），放进决策里即冗余。
/// </param>
public sealed record DuplicateLaunchDecision(bool Skip);

/// <summary>
/// 防双启动判定（D76，2026-09-22 用户批复）：目标进程已在运行 → 不再启动一次。
/// </summary>
/// <remarks>
/// <para>
/// 场景：某个应用既是 Windows 自启动项、又被本软件接管延时启动。若它在系统侧被写回启用
/// （见守卫机制）或本来就通过别的途径先起来了，延时到点后再拉一次就会出现第二个实例。
/// </para>
/// <para>
/// 🔴 **命中必须比对模块路径，不能只比进程名。** 只看名字会把两个完全不同产品的
/// <c>updater.exe</c> 判成同一个；而"误判已存在"的后果是该条目**永远不启动** ——
/// 比漏判严重得多。因此：读不到 <c>MainModule.FileName</c> 的进程一律不算命中
/// （这一条同时让"排除自身进程"这类特例判断变得不必要）。
/// </para>
/// <para>
/// 纯函数：不枚举进程、不读配置、不写日志。进程列表由调用方（调度端）采集后传入，
/// 这样判定逻辑可以逐条单测，而"真实进程状态"仍留在真机验证的范围里
/// （与 <c>LaunchResultEvaluator</c> 同一分工）。
/// </para>
/// </remarks>
public static class DuplicateLaunchPolicy
{
    /// <summary>
    /// 判断是否应当跳过启动。
    /// </summary>
    /// <param name="target">从条目推导出的启动目标；<see langword="null"/> 表示推导不出（放行）。</param>
    /// <param name="runningProcesses">当前同名进程的快照（调用方已按名字筛过，也可传全量）。</param>
    /// <returns>决策结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runningProcesses"/> 为 <see langword="null"/>。</exception>
    public static DuplicateLaunchDecision Decide(
        LaunchTarget? target,
        IReadOnlyList<RunningProcessInfo> runningProcesses)
    {
        ArgumentNullException.ThrowIfNull(runningProcesses);

        // 推导不出目标 → 无从判定 → 放行。整个设计都往"宁可多启动一次"一侧倒（见类注释）。
        if (target is null)
        {
            return new DuplicateLaunchDecision(Skip: false);
        }

        var wanted = TryNormalize(target.ExecutablePath);
        if (wanted is null)
        {
            return new DuplicateLaunchDecision(Skip: false);
        }

        foreach (var process in runningProcesses)
        {
            if (string.IsNullOrWhiteSpace(process.ModulePath))
            {
                // 读不到模块路径：不算命中。**不能**退化成按名字匹配。
                continue;
            }

            var actual = TryNormalize(process.ModulePath);
            if (actual is not null && string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return new DuplicateLaunchDecision(Skip: true);
            }
        }

        return new DuplicateLaunchDecision(Skip: false);
    }

    /// <summary>路径归一化；非法路径返回 <see langword="null"/>（视为不匹配而不是抛异常）。</summary>
    private static string? TryNormalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 进程的模块路径理论上总是合法的，但这里面对的是"外部世界的字符串"，
            // 一个畸形路径不该让整条调度路径崩掉。
            return null;
        }
    }
}
