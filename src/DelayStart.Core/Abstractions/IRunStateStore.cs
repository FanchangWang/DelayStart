using DelayStart.Core.Models;

namespace DelayStart.Core.Abstractions;

/// <summary>
/// 运行记录的读取端（D19 / D125 / FR-5.12 / FR-5.13）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这个接口只剩**归档**一条通路（D125 删掉了 <c>scheduler/current-run.json</c>）。
/// 删它的三条理由：
/// </para>
/// <list type="number">
/// <item><description>一轮调度要写它几十次（每个条目状态变化一次），而它承载的"最新 RunId"
/// 归档里已经有了 —— 纯重复的 I/O。</description></item>
/// <item><description>消费方（总览卡、结果轮询）会在"刚写完还没收尾"的时候读到**半截状态**，
/// 显示出一个用户从没见过的中间态。归档是收尾时才写的，天然是一个自洽的快照。</description></item>
/// <item><description>"有没有归档"本来就是更好的信号：没有归档 = 还没有哪一轮跑完过，
/// 而这正是空态要表达的事。</description></item>
/// </list>
/// <para>
/// 代价（刻意接受）：一轮调度**正在进行中**时，总览卡仍显示上一轮的结果。
/// 这是对的 —— 卡片语义是"上次怎么样"，"正在跑"由托盘图标与进度面板表达。
/// </para>
/// </remarks>
public interface IRunStateStore
{
    /// <summary>归档目录（<c>scheduler/archive/</c>，D116 从 <c>runs/</c> 迁移而来）。</summary>
    string ArchiveRoot { get; }

    /// <summary>归档一轮刚跑完的运行记录；<see cref="RunRecord.RunId"/> 是幂等键（FR-5.13）。</summary>
    /// <param name="record">刚跑完的那一轮记录。</param>
    void Archive(RunRecord record);

    /// <summary>按开始时刻倒序读取最近若干份已归档记录（日志页；失败时按空列表处理，FR-8.3）。</summary>
    /// <param name="maxCount">最多读取的条数。</param>
    /// <returns>最近若干轮记录；无可用归档时为空列表。</returns>
    IReadOnlyList<RunRecord> ReadRecent(int maxCount);
}
