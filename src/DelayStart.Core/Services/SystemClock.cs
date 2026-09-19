using DelayStart.Core.Abstractions;

namespace DelayStart.Core.Services;

/// <summary>
/// 基于系统时钟的 <see cref="IClock"/> 实现。
/// </summary>
/// <remarks>
/// 只在需要"记录真实时间"的地方使用；时序调度一律走
/// <see cref="System.Diagnostics.Stopwatch"/>，不经过本类型（机制 5 / E15）。
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <summary>共享实例。</summary>
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.Now;
}
