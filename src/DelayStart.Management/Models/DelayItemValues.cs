using DelayStart.Core.Models;

namespace DelayStart.Management.Models;

/// <summary>
/// 延时条目的可编辑字段（编辑已有条目 / 手动添加新条目共用）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>哪些字段真正生效由条目的来源决定，不由调用方决定</b>：
/// 系统来源的条目（注册表 / 启动文件夹 / 计划任务 / UWP）只允许改
/// <see cref="DelaySeconds"/> / <see cref="RunAsAdmin"/> / <see cref="Arguments"/>；
/// <see cref="Name"/> / <see cref="Path"/> / <see cref="WorkingDirectory"/> 三项
/// 只在**手动条目**上生效。
/// </para>
/// <para>
/// 这条分叉来自 <c>design.md</c> 7.5：分叉的唯一依据是"这条记录有没有系统来源"。
/// 有来源 → 路径由系统项决定，改了就和"接管时记录的原始状态"对不上，
/// 释放时按 <see cref="DelayedItem.SourceKey"/> 恢复的是另一个位置的项；
/// 没来源 → 路径就是这个记录的全部意义，必须能改。
/// </para>
/// </remarks>
public sealed class DelayItemValues
{
    /// <summary>延时秒数，相对登录时刻的绝对时间点（机制 5 / FR-5.3）。</summary>
    public int DelaySeconds { get; init; } = DelayedItem.DefaultDelaySeconds;

    /// <summary>启动身份：<see langword="false"/> 走降权启动（NFR-3.3）。</summary>
    public bool RunAsAdmin { get; init; }

    /// <summary>命令行参数，留空表示不附加（FR-4.5）。</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>显示名。仅手动条目可改。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>目标程序路径。仅手动条目可改。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>工作目录，留空则用程序所在目录。仅手动条目可改。</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>
    /// 所属调度周期 id（FR-15.1）。缺省即内置「每天」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 这里存的是**引用**（周期 id），不是星期组合的快照（D87 = 🅑）：改周期会同步影响
    /// 所有引用它的条目，所以这个字段在编辑条目时是"改指向"，不是"改内容"。
    /// </para>
    /// <para>
    /// 与条目开关（<c>DelayedItem.Enabled</c>）的关系：<b>不存在"不选周期"这种状态</b>（FR-15.1）——
    /// 空值一律按「每天」处理。真想让它不启动就关条目开关，不要用周期表达。
    /// 因此本字段对**所有来源**的条目都生效（系统接管条目与手动条目无差别）。
    /// </para>
    /// </remarks>
    public string ScheduleCycleId { get; init; } = BuiltinCycleIds.Everyday;
}
