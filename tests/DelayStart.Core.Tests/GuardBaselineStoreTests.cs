using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>GuardBaselineStore</c> 的读写测试（D74）：基线快照的原子落盘与"坏基线必须降级"。
/// </summary>
/// <remarks>
/// <para>
/// 基线是"新增检测"的唯一依据，它的失效方向必须是**安全**的：
/// 读不到、解析不了都返回 <see langword="null"/>（等价于"首次运行 ⇒ 不通报新增"），
/// 而不是抛出或返回半截数据 —— 拿一份坏基线去测差集，会把满屏老条目报成"新增"。
/// </para>
/// <para>
/// 落点在临时目录（真实的 <c>GuardBaselinePath</c> 由 <see cref="PathService"/> 决定），
/// 因此"覆盖写"与"文件不存在"两条路径都能真实走一遍文件系统。
/// </para>
/// </remarks>
public sealed class GuardBaselineStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Read_NoFileYet_ReturnsNull()
    {
        var outcome = Create().Store.Read();

        // 首次运行：没有基线不是错误，也不该记 Warn。
        Assert.Null(outcome);
    }

    [Fact]
    public void WriteThenRead_RoundTripsEntries()
    {
        var harness = Create();

        harness.Store.Write(
        [
            Entry("registry:hkcu:a", "甲", isEnabled: true, isMissing: false),
            Entry("startupfolder:userfolder:b", "乙", isEnabled: false, isMissing: true),
        ]);

        var baseline = harness.Store.Read();

        Assert.NotNull(baseline);
        Assert.Equal(2, baseline.Entries.Count);

        var first = baseline.Entries.Single(static entry => entry.Id == "registry:hkcu:a");
        Assert.Equal("甲", first.Name);
        Assert.True(first.IsEnabled);
        Assert.False(first.IsMissing);

        var second = baseline.Entries.Single(static entry => entry.Id == "startupfolder:userfolder:b");
        Assert.False(second.IsEnabled);
        Assert.True(second.IsMissing);
    }

    [Fact]
    public void Write_SecondTime_ReplacesPreviousBaseline()
    {
        var harness = Create();
        harness.Store.Write([Entry("registry:hkcu:a", "甲", isEnabled: true, isMissing: false)]);

        // 第二轮：甲被删掉，只剩乙。基线必须整体替换，不能累加。
        harness.Store.Write([Entry("registry:hkcu:b", "乙", isEnabled: true, isMissing: false)]);

        var baseline = harness.Store.Read();

        Assert.NotNull(baseline);
        var only = Assert.Single(baseline.Entries);
        Assert.Equal("registry:hkcu:b", only.Id);
    }

    [Fact]
    public void Read_CorruptJson_ReturnsNullAndWarns()
    {
        var harness = Create();

        // 先正常落一次基线（顺带把目录建出来），再把它破坏掉。
        // 落盘本身走 AtomicFileWriter，所以真实场景里这属于"外部破坏"。
        harness.Store.Write([Entry("registry:hkcu:a", "甲", isEnabled: true, isMissing: false)]);
        File.WriteAllText(harness.Paths.GuardBaselinePath, "{ \"Version\": 1, \"Entries\": [ {");

        var baseline = harness.Store.Read();

        Assert.Null(baseline);
        Assert.True(harness.Log.Contains(LogLevel.Warn, "守卫基线解析失败"));
    }

    [Fact]
    public void Read_EmptyFile_ReturnsNullWithoutWarning()
    {
        var harness = Create();
        harness.Store.Write([Entry("registry:hkcu:a", "甲", isEnabled: true, isMissing: false)]);
        File.WriteAllText(harness.Paths.GuardBaselinePath, "   ");

        var baseline = harness.Store.Read();

        Assert.Null(baseline);
        // 空文件等价于"还没有基线"，不是损坏 —— 不该在日志里制造噪声。
        Assert.Equal(0, harness.Log.Count);
    }

    [Fact]
    public void ToIdSet_NullBaseline_ReturnsNull()
    {
        // null 是"首次运行"的哨兵值，与"空集合"（基线存在但没有条目）语义不同。
        Assert.Null(GuardBaselineStore.ToIdSet(baseline: null));
    }

    [Fact]
    public void ToIdSet_NonNullBaseline_ReturnsAllIds()
    {
        var harness = Create();
        harness.Store.Write(
        [
            Entry("registry:hkcu:a", "甲", isEnabled: true, isMissing: false),
            Entry("registry:hkcu:b", "乙", isEnabled: true, isMissing: false),
        ]);

        var ids = GuardBaselineStore.ToIdSet(harness.Store.Read());

        Assert.NotNull(ids);
        Assert.Equal(2, ids.Count);
        Assert.Contains("registry:hkcu:a", ids);
        Assert.Contains("registry:hkcu:b", ids);
    }

    [Fact]
    public void Write_SetsCapturedAtFromClock()
    {
        var clock = new FakeClock();
        var harness = Create(clock);

        harness.Store.Write([Entry("registry:hkcu:a", "甲", isEnabled: true, isMissing: false)]);

        var baseline = harness.Store.Read();
        Assert.NotNull(baseline);
        Assert.Equal(
            clock.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            baseline.CapturedAt);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static StartupEntry Entry(string id, string name, bool isEnabled, bool isMissing) => new()
    {
        Id = id,
        Name = name,
        Path = @"C:\Program Files\Demo\demo.exe",
        Source = StartupSource.Registry,
        Scope = StartupScope.Hkcu,
        SourceKey = id,
        IsEnabled = isEnabled,
        IsMissing = isMissing,
    };

    private HarnessState Create(IClock? clock = null)
    {
        var log = new FakeLogSink();
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);
        var store = new GuardBaselineStore(paths, log, clock ?? new FakeClock());
        return new HarnessState(paths, log, store);
    }

    private sealed record HarnessState(PathService Paths, FakeLogSink Log, GuardBaselineStore Store);
}
