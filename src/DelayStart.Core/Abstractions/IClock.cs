namespace DelayStart.Core.Abstractions;

/// <summary>
/// 时间源抽象，让日志与运行记录的时间戳可在测试中控制。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **它不参与时序计算。** 延时调度必须用 <see cref="System.Diagnostics.Stopwatch"/>
/// 这样的单调时钟，绝不能用墙上时钟 —— 系统时间或时区被改动时，墙上时钟会让
/// 已排定的延时错乱（E15 / 机制 5）。
/// </para>
/// <para>
/// 本接口只服务于"记录发生了什么"（日志时间戳、<c>RunRecord.StartedAt</c> 等）。
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>当前本地时间（含时区偏移）。</summary>
    DateTimeOffset Now { get; }
}
