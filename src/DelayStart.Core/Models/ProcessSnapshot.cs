namespace DelayStart.Core.Models;

/// <summary>
/// 启动后延时复查时，目标进程的状态快照（机制 7 / FR-5.9）。
/// </summary>
/// <remarks>
/// <para>
/// 抽象成一个值类型而不是直接用 <c>System.Diagnostics.Process</c>，是为了让
/// <c>LaunchResultEvaluator</c> 保持纯函数、可单元测试 —— 按 <c>docs/design.md</c>
/// §14.1，"进程实际启动"属于真机手工验证，不进单元测试。
/// </para>
/// <para>
/// <see cref="ExitCode"/> 仅在 <see cref="HasExited"/> 为 <see langword="true"/> 时有意义。
/// </para>
/// </remarks>
/// <param name="HasExited">复查时进程是否已退出。</param>
/// <param name="ExitCode">已退出时的退出码；未退出时的取值无意义。</param>
public readonly record struct ProcessSnapshot(bool HasExited, int ExitCode);
