using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 调度完成通知的判定（N2 / N5，2026-09-22 批复）：
/// <see cref="NotifyMode"/> 从此**只管发不发系统通知**，与面板再无关系。
/// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 **本判据只读这两个入参，不读任何运行状态**（A6）。具体说：不读中转器的
    /// <c>ToastService</c>、不读它是否已启动、退出码、进程句柄 —— 中转器是
    /// fire-and-forget 的哑进程（N12），调度端**从不等待它的回执**。
    /// </para>
    /// <para>
    /// 为什么这条值得单独钉住：一旦判据改成"看通知发出去没有再决定要不要弹面板"
    /// 或"按中转器的状态降级"，通知与面板就又绑在一起了 —— 而那正是 D83 / N5 要拆开的东西。
    /// 而且那样的判据在**真机上不可测**：中转器是独立进程，它的内部状态跨进程不可见，
    /// 判定就只能靠"读文件 / 发消息"，把一个纯函数变成一段带 I/O 的流程。
    /// </para>
    /// <para>
    /// 另：0 条条目**无特判**（D4 批复 A：简单一致）—— 空计划与"全被周期过滤掉"
    /// 都有各自的通知文案覆盖（`S1.3`），这里不必再分一次。
    /// </para>
    /// <para>
    /// 本类只回答"发不发系统通知"一件事（<c>NotifyMode</c> 从此只管这个）。
    /// 面板的退出时机**不再**有任何判定：D83 之后通知归本类、面板归通知中转器之外的面板进程，
    /// 而面板拆成独立进程后它比调度端活得久（N5 / S4.6），"等不等面板"这个问题本身消失了 ——
    /// 原先那个判定（Core 的 <c>CompletionPolicy</c>）已随 S5 一并删除。
    /// </para>
    /// <para>
    /// 判定表：<see cref="NotifyMode.Always"/> → 发；<see cref="NotifyMode.FailuresOnly"/>
    /// → 有失败才发；<see cref="NotifyMode.Never"/> → 不发（调用方记一行"已跳过通知"日志）。
    /// </para>
    /// </remarks>
public static class NotifyDecision
{
    /// <summary>按通知策略判定是否发送调度完成通知。</summary>
    /// <param name="mode">管理端设置的通知策略。</param>
    /// <param name="failedCount">本批次失败条目数。</param>
    /// <returns>应发送通知时为 <see langword="true"/>。</returns>
    public static bool Decide(NotifyMode mode, int failedCount) => mode switch
    {
        NotifyMode.Always => true,
        NotifyMode.FailuresOnly => failedCount > 0,
        _ => false,
    };
}
