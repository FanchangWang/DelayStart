namespace DelayStart.Core.Models;

/// <summary>
/// 单次调度运行中，某个延时条目的状态。
/// </summary>
/// <remarks>
/// 该枚举直接决定托盘角标、管理端时间轴颜色与「上次运行结果」横幅，取值不得随意增删
/// （见 <c>docs/design.md</c> 八状态文案矩阵）。
/// </remarks>
public enum RunItemState
{
    /// <summary>等待中：尚未到延时时刻。</summary>
    Waiting,

    /// <summary>启动中：已到点、正在发起进程，尚未完成结果判定。</summary>
    Launching,

    /// <summary>已完成：判定为启动成功（含「拉起已有实例后退出码 0」的情形，E4）。</summary>
    Done,

    /// <summary>已失败：创建失败，或启动后立即以非零退出码退出（E5）。</summary>
    Failed,

    /// <summary>
    /// 已跳过：**今天不该启动**或**用户放弃了启动**。三种来源：用户在调度期间经托盘右键菜单 /
    /// 面板按钮「跳过剩余任务」主动放弃（D2 批复 2026-09-21：跳过 = 不启动）；启动前防双启动判定
    /// 命中"进程已存在"（D76）；条目的调度周期今天不含此日（FR-15.26，原因写明"今天不在启动周期内"）。
    /// </summary>
    /// <remarks>
    /// 不计入失败（<c>FailureStreakService</c> 只认 <see cref="Failed"/>），但调度日志与时间轴可见。
    /// 🔴 三种来源共用同一个状态、**靠 <see cref="RunItemResult.Reason"/> 区分** ——
    /// 它们的共同语义是"不是失败、也没有启动"，而这个语义正是角标与失败连击需要的那一个。
    /// </remarks>
    Skipped,
}
