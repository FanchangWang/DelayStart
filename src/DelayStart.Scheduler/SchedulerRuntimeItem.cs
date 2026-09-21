using DelayStart.Core.Models;

namespace DelayStart.Scheduler;

/// <summary>
/// 单个条目的运行时状态（原 <c>SchedulerEngine</c> 的私有嵌套类，2026-09-22 移出，只搬家不改语义）。
/// </summary>
/// <remarks>
/// <para>
/// 移出的动机：<c>SchedulerEngine</c> 同时承担消息循环、托盘、面板、时序、启动、判定与持久化，
/// 这个类型是它唯一持有的"进行中状态"，独立成文件后类内聚合度下降、边界也更清楚。
/// </para>
/// <para>
/// 🔴 字段名、字段类型、初值与持久化结构均与原来一致。它不引用 UI 句柄、托盘、消息循环或
/// 任何 Win32 对象，但**仍然留在 <c>DelayStart.Scheduler</c>**，暂不下沉 Core ——
/// 下沉的前提是先确认它零平台依赖且不需要跟着调度循环一起演进。
/// </para>
/// </remarks>
internal sealed class SchedulerRuntimeItem
{
    public required DelayedItem Item { get; init; }

    public required RunItemResult Result { get; init; }

    public int? ProcessId { get; set; }

    public TimeSpan RecheckAt { get; set; }

    /// <summary>到期启动时刻（自本次调度开始起算的绝对秒）。
    /// 初值 = 配置延时；「立即启动全部剩余」把它拨到当下（托盘右键菜单，2026-09-21 批复）。</summary>
    public TimeSpan LaunchAt { get; set; }
}
