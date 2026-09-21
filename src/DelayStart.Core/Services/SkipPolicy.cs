using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>触发"跳过剩余条目"的入口。</summary>
public enum SkipEntry
{
    /// <summary>进度面板上的按钮。</summary>
    Panel,

    /// <summary>托盘右键菜单。</summary>
    TrayMenu,
}

/// <summary>"这一条能不能跳过"的决策。</summary>
/// <param name="CanSkip">
/// 是否可跳过。**只保留这一个字段**：跳过原因完全由 <see cref="SkipEntry"/> 推导
/// （与条目状态无关），那属于策略的**输入**，不应出现在输出里 —— 放进输出即冗余。
/// </param>
public sealed record SkipDecision(bool CanSkip);

/// <summary>
/// 「跳过剩余条目」的判定策略（2026-09-21 批复 D2 = 不启动）。
/// </summary>
/// <remarks>
/// <para>
/// 两个入口的粒度不同（D72「收尾动作按入口分流」）：
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="SkipEntry.Panel"/>：只跳 <see cref="RunItemState.Waiting"/> ——
/// 正在复查的条目照常出结果，用户还在面板上等着看。
/// </item>
/// <item>
/// <see cref="SkipEntry.TrayMenu"/>：连 <see cref="RunItemState.Launching"/> 一起跳 ——
/// 菜单语义是"跳过并走人"，用户不想等那 1.5 秒复查窗口；代价是这些条目拿不到真实成败判定。
/// </item>
/// </list>
/// <para>
/// 策略只返回决策：不修改 <c>RunItemResult</c>、不写文件、不写日志、不碰 UI。
/// 状态与原因文案由 <c>SchedulerEngine</c> 依据决策落地。
/// </para>
/// </remarks>
public static class SkipPolicy
{
    /// <summary>判断某条目在给定入口下是否可跳过。</summary>
    /// <param name="state">条目当前状态。</param>
    /// <param name="entry">触发跳过的入口。</param>
    /// <returns>决策结果。</returns>
    public static SkipDecision Decide(RunItemState state, SkipEntry entry)
        => new(state == RunItemState.Waiting
            || (state == RunItemState.Launching && entry == SkipEntry.TrayMenu));
}
