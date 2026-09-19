using DelayStart.Core.Abstractions;

namespace DelayStart.Core.Tests.Fakes;

/// <summary>
/// 可控时间源的测试替身。
/// </summary>
internal sealed class FakeClock : IClock
{
    private static readonly DateTimeOffset DefaultNow =
        new(2026, 9, 19, 8, 41, 12, TimeSpan.FromHours(8));

    /// <summary>构造假时钟。</summary>
    /// <param name="now">初始时间；为 <see langword="null"/> 时用固定默认值（保证测试可重现）。</param>
    public FakeClock(DateTimeOffset? now = null) => Now = now ?? DefaultNow;

    /// <inheritdoc />
    public DateTimeOffset Now { get; set; }

    /// <summary>把时钟往前拨。</summary>
    /// <param name="delta">时间增量。</param>
    public void Advance(TimeSpan delta) => Now += delta;
}
