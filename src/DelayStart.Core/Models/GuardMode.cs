namespace DelayStart.Core.Models;

/// <summary>
/// 守卫（DelayStart.Guard.exe）的运行模式（D74，2026-09-22 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 守卫不是常驻进程：由计划任务按本模式周期拉起，完成一次巡检即退出（D74）。
/// 模式与间隔分别存储，是为了让"档位"这个概念有确切的落点 ——
/// <see cref="Settings.GuardMinutes"/> 只允许取 <see cref="GuardPresets.Minutes"/> 里的值，
/// 合法性与默认值都由 <c>DelayStart.Core.Services.GuardSchedulePlan</c> 兜底。
/// </para>
/// </remarks>
public enum GuardMode
{
    /// <summary>不启动守卫：管理端启动时会删除守卫计划任务。</summary>
    Disabled = 0,

    /// <summary>登录后延迟 N 分钟执行一次，之后不再重复。</summary>
    OnceAfterLogin = 1,

    /// <summary>登录后延迟 N 分钟执行一次，此后每 N 分钟重复一次。</summary>
    Periodic = 2,
}

/// <summary>一个守卫档位（模式 + 间隔）—— 管理端下拉里的一行。</summary>
/// <param name="Mode">模式。</param>
/// <param name="Minutes">间隔分钟数；<see cref="GuardMode.Disabled"/> 时无意义，固定为 0。</param>
public sealed record GuardPreset(GuardMode Mode, int Minutes);

/// <summary>守卫可选的间隔档位（分钟）—— 即白名单，不开放自由输入。</summary>
/// <remarks>
/// 三档足够：守卫是巡检性质，10 分钟还是 30 分钟对纠正 / 通报效果无实质差异（D74）。
/// 档位即白名单直接映射计划任务触发规则，免去输入校验与非法状态。
/// </remarks>
public static class GuardPresets
{
    /// <summary>允许的间隔档位（分钟），升序。</summary>
    public static IReadOnlyList<int> Minutes { get; } = [10, 30, 60];

    /// <summary>默认档位（分钟）：2026-09-22 用户批复。</summary>
    public const int DefaultMinutes = 30;

    /// <summary>
    /// 下拉档位目录 —— **集合顺序即界面显示顺序**，索引是可以持久化的稳定标识。
    /// </summary>
    /// <remarks>
    /// 放在 Core 而不是 ViewModel，是为了让"界面索引 ↔ 配置值"的映射有一处可单测的定义：
    /// 这是唯一一处"算错了不报错、只会静默不跑或跑错档位"的映射
    /// （与 <see cref="DelayStart.Core.Services.GuardSchedulePlan"/> 同理）。
    /// 档位顺序刻意排成"不启动 → 一次性三档 → 周期三档"，即破坏性递增。
    /// </remarks>
    public static IReadOnlyList<GuardPreset> Options { get; } =
    [
        new(GuardMode.Disabled, 0),
        new(GuardMode.OnceAfterLogin, 10),
        new(GuardMode.OnceAfterLogin, 30),
        new(GuardMode.OnceAfterLogin, 60),
        new(GuardMode.Periodic, 10),
        new(GuardMode.Periodic, 30),
        new(GuardMode.Periodic, 60),
    ];

    /// <summary>判断某间隔值是否为合法档位。</summary>
    /// <param name="minutes">间隔分钟数。</param>
    /// <returns>属于白名单时为 <see langword="true"/>。</returns>
    public static bool IsValid(int minutes) => Minutes.Contains(minutes);

    /// <summary>把配置值（模式 + 间隔）映射为下拉索引。</summary>
    /// <param name="mode">模式。</param>
    /// <param name="minutes">间隔分钟数。</param>
    /// <returns>下拉索引；非法组合一律收拢到默认档位，不会返回越界值。</returns>
    public static int ToIndex(GuardMode mode, int minutes)
    {
        if (mode is GuardMode.Disabled)
        {
            return 0;
        }

        var normalized = IsValid(minutes) ? minutes : DefaultMinutes;
        for (var i = 0; i < Options.Count; i++)
        {
            if (Options[i].Mode == mode && Options[i].Minutes == normalized)
            {
                return i;
            }
        }

        // 模式既不是 Disabled 也不在目录里（枚举被写坏）→ 落到默认档位。
        return IndexOf(GuardMode.OnceAfterLogin, DefaultMinutes);
    }

    /// <summary>把下拉索引映射回配置值。</summary>
    /// <param name="index">下拉索引。</param>
    /// <returns>档位；越界时返回默认档位。</returns>
    public static GuardPreset FromIndex(int index)
        => index >= 0 && index < Options.Count
            ? Options[index]
            : Options[IndexOf(GuardMode.OnceAfterLogin, DefaultMinutes)];

    private static int IndexOf(GuardMode mode, int minutes)
    {
        for (var i = 0; i < Options.Count; i++)
        {
            if (Options[i].Mode == mode && Options[i].Minutes == minutes)
            {
                return i;
            }
        }

        return 0;
    }
}
