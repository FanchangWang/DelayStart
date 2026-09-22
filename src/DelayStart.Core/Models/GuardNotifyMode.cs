namespace DelayStart.Core.Models;

/// <summary>
/// 守卫的通知策略（D80，2026-09-22 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 它只管"要不要打扰用户"，**与巡检本身无关** —— 两个取值下守卫都照常扫描、纠正写回、
/// 更新基线；差别仅在"有变化时是否发一条系统通知"。所以这是一个纯通知开关，
/// 不能被当成"关掉守卫"的替代品（那是 <see cref="GuardMode.Disabled"/>）。
/// </para>
/// <para>
/// 单独一个枚举而不是复用 <see cref="NotifyMode"/>：后者描述的是**调度端完成后是否弹进度面板**，
/// 两者的触发点（登录后巡检 vs 每次启动计划结束）、载体（系统通知 vs 应用内面板）都不同，
/// 合成一个字段会让"改了调度通知却把守卫也关掉"这种事故变得可能。
/// </para>
/// </remarks>
public enum GuardNotifyMode
{
    /// <summary>有变化时通知：本轮出现新增 / 失效条目时发一条系统通知（默认）。</summary>
    OnChange,

    /// <summary>从不通知：照样巡检与纠正，只是不发任何通知。</summary>
    Never,
}
