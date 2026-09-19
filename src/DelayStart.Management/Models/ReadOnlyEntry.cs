namespace DelayStart.Management.Models;

/// <summary>
/// 一条系统关键启动位置的只读条目（FR-7.3）。
/// </summary>
/// <param name="Location">所在位置（注册表键路径 / 策略来源）。</param>
/// <param name="Name">条目名（值名 / 脚本文件名）。</param>
/// <param name="Detail">内容摘要（值内容 / 脚本路径）。</param>
public sealed record ReadOnlyEntry(string Location, string Name, string Detail);
