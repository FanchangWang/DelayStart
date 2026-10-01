using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>"这一条能不能跳过"的决策。</summary>
/// <param name="CanSkip">
/// 是否可跳过。**只保留这一个字段**：跳过原因由入口语义决定（与条目状态无关），
/// 那属于策略的**输入**，不应出现在输出里 —— 放进输出即冗余。
/// </param>
public sealed record SkipDecision(bool CanSkip);

/// <summary>
/// 「跳过剩余条目」的判定策略（2026-09-21 批复 D2 = 不启动）。
/// </summary>
/// <remarks>
/// <para>
/// 可跳的粒度：<see cref="RunItemState.Waiting"/> 一律可跳，
/// <see cref="RunItemState.Launching"/> 也可跳 —— 菜单语义是"跳过并走人"，
/// 用户不想等那 1.5 秒复查窗口；代价是这些条目拿不到真实成败判定。
/// 已完成 / 已失败 / 已跳过的都不可跳（没什么可跳的）。
/// </para>
/// <para>
/// 🔴 面板已于 v0.6.1 取消，所以**不再按入口分流**：原先面板那条只跳 <c>Waiting</c>、
/// 托盘这条连 <c>Launching</c> 一起跳，两条粒度的差别来自"用户还在面板上等着看结果"。
/// 唯一的入口是托盘菜单，差别消失，于是 <c>SkipEntry</c> 枚举与
/// <c>Decide</c> 的第二个参数一并删除 —— 留着就是一条指向已取消功能的死引用。
/// </para>
/// <para>
/// 策略只返回决策：不修改 <c>RunItemResult</c>、不写文件、不写日志、不碰 UI。
/// 状态与原因文案由 <c>SchedulerEngine</c> 依据决策落地。
/// </para>
/// </remarks>
public static class SkipPolicy
{
    /// <summary>判断某条目当前是否可跳过。</summary>
    /// <param name="state">条目当前状态。</param>
    /// <returns>决策结果。</returns>
    public static SkipDecision Decide(RunItemState state)
        => new(state is RunItemState.Waiting or RunItemState.Launching);
}
