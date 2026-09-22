namespace DelayStart.Core.Models;

/// <summary>
/// 正在运行的进程的一条快照，供"目标是否已在运行"判定使用（D76）。
/// </summary>
/// <param name="ProcessName">进程名（不含扩展名），即 <c>Process.ProcessName</c>。</param>
/// <param name="ModulePath">
/// 主模块的完整路径，即 <c>Process.MainModule.FileName</c>。
/// **读不到时为 <see langword="null"/>**（权限不足 / 受保护 / 进程刚退出）——
/// 判定侧必须把"读不到"当作"不算命中"，不能退化成按名字匹配。
/// </param>
public readonly record struct RunningProcessInfo(string ProcessName, string? ModulePath);
