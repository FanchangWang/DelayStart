using DelayStart.Core.Abstractions;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>GuardNotifyStateStore</c> 的读写测试（D148）：通知状态的原子落盘与"坏状态必须降级"。
/// </summary>
/// <remarks>
/// <para>
/// 失效方向与 <c>GuardBaselineStore</c> 同源但<b>取舍相反</b>，这一条是本类最容易写错的地方：
/// 读不到 / 解析失败一律返回 <see langword="null"/>（= "尚未通报过任何条目" ⇒ 本轮静默），
/// 而不是抛、或返回空集合。返回空集合的语义是"上一轮一条失效都没有"⇒ 本轮<b>全部该报</b>，
/// 状态文件一坏就引发一轮通知风暴。
/// </para>
/// <para>
/// 与基线另一处关键差别：<b>空集合 ≠ null 必须区分</b>。状态文件第一次写入时内容就是空的
/// （"本轮没有失效"），那时它必须是"空集合"而不是"无状态" —— 否则那一轮之后
/// 真正的失效会被当成首跑而静默掉。
/// </para>
/// </remarks>
public sealed class GuardNotifyStateStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Read_状态文件不存在_返回null()
    {
        // 首次运行：没有状态不是错误，也不该记 Warn。
        Assert.Null(Create().Store.Read());
    }

    [Fact]
    public void WriteThenRead_往返主键()
    {
        var harness = Create();

        harness.Store.Write(new HashSet<string>(["registry:hkcu:a", "manual:none:abc"]));

        var ids = harness.Store.Read();

        Assert.NotNull(ids);
        Assert.Equal(2, ids.Count);
        Assert.Contains("registry:hkcu:a", ids);
        Assert.Contains("manual:none:abc", ids);
    }

    [Fact]
    public void Write_空集合_读回空集合而非null()
    {
        // 🔴 方向不能反：null = "没有上一次" ⇒ 本轮静默；空集合 = "上一轮一条失效都没有"
        // ⇒ 本轮全部该报。折成 null 会把状态文件首次写入后的那一轮失效全吞掉。
        var harness = Create();

        harness.Store.Write(new HashSet<string>());

        var ids = harness.Store.Read();

        Assert.NotNull(ids);
        Assert.Empty(ids);
    }

    [Fact]
    public void Write_整体替换而非累加()
    {
        // 🔴 条目恢复后必须从状态里出去，否则"装回去又被清掉"（最该通知的一次）
        // 会因为"曾经报过"而永久静默。
        var harness = Create();
        harness.Store.Write(new HashSet<string>(["a", "b"]));

        harness.Store.Write(new HashSet<string>(["b", "c"]));

        var ids = harness.Store.Read();

        Assert.NotNull(ids);
        Assert.Equal(["b", "c"], [.. ids.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void Write_落点与基线分开()
    {
        // 两个文件在同一目录，但必须是两个文件 —— 把通知状态塞进基线会让基线不再是快照。
        var harness = Create();

        harness.Store.Write(new HashSet<string>(["a"]));

        Assert.True(File.Exists(harness.Paths.GuardNotifyStatePath));
        Assert.NotEqual(harness.Paths.GuardBaselinePath, harness.Paths.GuardNotifyStatePath);
        Assert.False(File.Exists(harness.Paths.GuardBaselinePath));
    }

    [Fact]
    public void Read_文件内容损坏_返回null且记Warn()
    {
        // 🔴 坏状态必须降级成"尚未通报"（静默一轮），不能抛 —— 守卫是周期任务，
        // 为一个 JSON 语法错误让整次巡检失败是事故级的失衡。
        var harness = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(harness.Paths.GuardNotifyStatePath)!);
        File.WriteAllText(harness.Paths.GuardNotifyStatePath, "{ 这不是 JSON");

        var ids = harness.Store.Read();

        Assert.Null(ids);
        Assert.True(harness.Log.Contains(LogLevel.Warn, "通知状态"));
    }

    [Fact]
    public void Read_JSON为null字面量_返回null()
    {
        var harness = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(harness.Paths.GuardNotifyStatePath)!);
        File.WriteAllText(harness.Paths.GuardNotifyStatePath, "null");

        Assert.Null(harness.Store.Read());
    }

    [Fact]
    public void Write_入参null_抛()
        => Assert.Throws<ArgumentNullException>(() => Create().Store.Write(null!));

    [Fact]
    public void Write_带注释与尾逗号_能读回来()
    {
        // 用户可能出于好奇打开这个文件改一下；读入侧宽容与配置同一口径。
        var harness = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(harness.Paths.GuardNotifyStatePath)!);
        File.WriteAllText(
            harness.Paths.GuardNotifyStatePath,
            """
            {
              // 我自己加的备注
              "version": 1,
              "notifiedItemIds": ["a", "b",],
            }
            """);

        var ids = harness.Store.Read();

        Assert.NotNull(ids);
        Assert.Equal(["a", "b"], [.. ids.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void Write_记时间戳()
    {
        var clock = new FakeClock();
        var harness = Create(clock);

        harness.Store.Write(new HashSet<string>(["a"]));

        var raw = File.ReadAllText(harness.Paths.GuardNotifyStatePath);
        Assert.Contains(clock.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), raw);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private HarnessState Create(IClock? clock = null)
    {
        var log = new FakeLogSink();
        var paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);
        var store = new GuardNotifyStateStore(paths, log, clock ?? new FakeClock());
        return new HarnessState(paths, log, store);
    }

    private sealed record HarnessState(PathService Paths, FakeLogSink Log, GuardNotifyStateStore Store);
}