namespace DelayStart.Core.Models;

/// <summary>
/// 单次调度运行中，某个延时条目的状态。
/// </summary>
/// <remarks>
/// 该枚举直接决定托盘角标、管理端时间轴颜色与「上次运行结果」横幅，取值不得随意增删
/// （见 <c>docs/scheduler-design.md</c> 第九节状态文案矩阵）。
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
}
