using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 守卫计划任务的触发规则（纯数据描述）。
/// </summary>
/// <param name="InitialDelay">登录后延迟多久执行第一次。</param>
/// <param name="RepeatInterval">
/// 之后每隔多久重复一次；<see langword="null"/> 表示不重复（<see cref="GuardMode.OnceAfterLogin"/>）。
/// </param>
public sealed record GuardTrigger(TimeSpan InitialDelay, TimeSpan? RepeatInterval);

/// <summary>
/// 把守卫档位翻译成计划任务触发规则（D74，2026-09-22 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **本类只产出纯数据，不碰 <c>Microsoft.Win32.TaskScheduler</c>。** 理由有两个：
/// ① Core 被 NativeAOT 的调度端引用，绝不能引入与 AOT 不兼容的任务库类型；
/// ② 触发规则是这套机制里唯一"算错了也不会报错、只会静默不跑"的部分，
/// 必须能进单元测试。管理端负责把 <see cref="GuardTrigger"/> 翻译成
/// <c>LogonTrigger</c> / <c>RepetitionPattern</c>。
/// </para>
/// <para>
/// <see cref="GuardMode.Periodic"/> 的第一次同样是"登录后延迟 N 分钟"而不是"登录即跑"：
/// 登录瞬间是磁盘与 CPU 最拥挤的窗口，全量扫描（注册表 + 计划任务枚举 + COM 解析快捷方式）
/// 不该去挤那一段；而且用户刚登录时看到提示框的时机也最差。
/// </para>
/// </remarks>
public static class GuardSchedulePlan
{
    /// <summary>
    /// 生成触发规则。
    /// </summary>
    /// <param name="mode">守卫模式。</param>
    /// <param name="minutes">间隔分钟数；不是合法档位时按 <see cref="GuardPresets.DefaultMinutes"/> 处理。</param>
    /// <returns>触发规则；<see cref="GuardMode.Disabled"/> 时返回 <see langword="null"/>（该删除计划任务）。</returns>
    public static GuardTrigger? Build(GuardMode mode, int minutes)
    {
        if (mode is GuardMode.Disabled)
        {
            return null;
        }

        var normalized = NormalizeMinutes(minutes);
        var delay = TimeSpan.FromMinutes(normalized);

        return mode switch
        {
            GuardMode.OnceAfterLogin => new GuardTrigger(delay, RepeatInterval: null),
            GuardMode.Periodic => new GuardTrigger(delay, RepeatInterval: delay),
            _ => null,
        };
    }

    /// <summary>
    /// 把手改配置里出现的非法间隔收拢到合法档位。
    /// </summary>
    /// <param name="minutes">配置里的值。</param>
    /// <returns>合法档位；非法时返回 <see cref="GuardPresets.DefaultMinutes"/>。</returns>
    /// <remarks>
    /// 与 <c>ConfigService.NormalizeSettings</c> 的分工：那边负责把值写回配置（持久化纠正），
    /// 这里负责"即使拿到非法值也不产生非法触发器"。两道都有，是因为配置可能被外部工具改成
    /// 任意值，而计划任务的重复间隔一旦是 0 或负数，任务会变得不可预测。
    /// </remarks>
    public static int NormalizeMinutes(int minutes)
        => GuardPresets.IsValid(minutes) ? minutes : GuardPresets.DefaultMinutes;
}
