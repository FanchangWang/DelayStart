using DelayStart.Core.Models;

namespace DelayStart.Core.Abstractions;

/// <summary>
/// 调度运行状态的读写抽象（D19 / FR-5.12 / FR-5.13）。
/// </summary>
/// <remarks>
/// 调度端写、管理端读，通过文件而不是 IPC 通信 —— 这使管理端的「调度进行中」判断
/// 不依赖任何跨进程协议，也让调度端崩溃后的现场得以保留（E9）。
/// </remarks>
public interface IRunStateStore
{
    /// <summary>调度实时状态文件完整路径（<c>scheduler/current-run.json</c>，D116 起从 <c>state/</c> 迁来）。</summary>
    string CurrentStateFilePath { get; }

    /// <summary>运行归档目录（<c>scheduler/archive/</c>，D116 起从 <c>runs/</c> 迁来）。</summary>
    string ArchiveRoot { get; }

    /// <summary>读取实时状态。文件不存在或已损坏时返回 <see langword="null"/>。</summary>
    /// <returns>当前运行记录，或 <see langword="null"/>。</returns>
    RunRecord? ReadCurrent();

    /// <summary>
    /// 原子重写实时状态（机制 8）。每次状态变化都应调用，管理端据此显示进度。
    /// </summary>
    /// <param name="record">当前运行记录。</param>
    void WriteCurrent(RunRecord record);

    /// <summary>
    /// 归档一次已完成的运行，并清理超出上限的旧文件（FR-5.13：保留最近 30 次）。
    /// </summary>
    /// <param name="record">已完成的运行记录，<see cref="RunRecord.RunId"/> 必须非空。</param>
    void Archive(RunRecord record);

    /// <summary>按开始时间倒序读取最近若干份归档，供调度日志页与失败连击统计使用（FR-8.3）。</summary>
    /// <param name="maxCount">最多读取数量。</param>
    /// <returns>倒序排列（最新在前）的运行记录集合；无归档时为空集合。</returns>
    IReadOnlyList<RunRecord> ReadRecent(int maxCount);
}
