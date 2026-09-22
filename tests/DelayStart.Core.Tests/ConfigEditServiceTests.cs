using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>ConfigEditService.ConvertToManual</c> 的单元测试（D81）。
/// </summary>
/// <remarks>
/// <para>
/// 「转为手动」的语义是**留着条目、切断系统锚点**。它的两条前提各对应一类失败：
/// </para>
/// <list type="bullet">
/// <item><description>目标程序必须还在 —— 条目已经失去系统锚点，路径再失效的话，
/// 转成手动只会得到"每次登录失败一次"的定时炸弹（应抛 <c>TargetMissing</c>）；</description></item>
/// <item><description>本来就已经是手动条目 —— 无事可做，且**不得写盘**（否则每次点一下都产生一次 IO 与修改时间抖动）。</description></item>
/// </list>
/// <para>
/// 🔴 主键必须换掉是本方法最容易漏掉的一步，所以单独断言：旧主键里带着来源定位分量，
/// 留着它会让来源页把这条手动条目误判成"那个系统项仍被接管"。
/// </para>
/// <para>
/// 计划任务注册器走 <see cref="FakeSchedulerTaskRegistrar"/>（本方法根本不碰计划任务，
/// 但它要求构造函数注入）；配置走 <see cref="InMemoryConfigStore"/>，不碰文件系统。
/// </para>
/// </remarks>
public sealed class ConfigEditServiceTests
{
    /// <summary>一个确实存在的文件（本测试程序集自身），用于满足"目标程序还在"的前提。</summary>
    private static readonly string ExistingFile = typeof(ConfigEditServiceTests).Assembly.Location;

    [Fact]
    public void ConvertToManual_ManagedItem_DetachesFromSource()
    {
        var harness = new Harness();
        harness.Seed(Managed(ExistingFile, "甲"));

        var converted = harness.Service.ConvertToManual("registry:hkcu:a");

        Assert.True(converted);

        var item = Assert.Single(harness.Store.Snapshot().Items);
        Assert.True(item.IsManual);
        Assert.Equal(StartupSource.Manual, item.Source);
        Assert.Equal(StartupScope.None, item.Scope);
        Assert.Equal(string.Empty, item.SourceKey);
        Assert.Equal(string.Empty, item.SourceDetail);

        // 🔴 主键必须重新生成（见类备注）。
        Assert.NotEqual("registry:hkcu:a", item.Id);
        Assert.StartsWith("manual:none:", item.Id, StringComparison.Ordinal);

        // 转换只脱钩，不改启动方式：名称 / 路径 / 延时 / 排序原样保留。
        Assert.Equal("甲", item.Name);
        Assert.Equal(ExistingFile, item.Path);
        Assert.Equal(30, item.DelaySeconds);
        Assert.Equal(2, item.SortOrder);

        // 手动条目从不经历接管，但 OriginalState 必须有值 —— 调度端读它来判定"按谁的配置启动"。
        Assert.True(item.OriginalState.WasEnabled);
    }

    [Fact]
    public void ConvertToManual_TargetMissing_ThrowsAndLeavesConfigUntouched()
    {
        var harness = new Harness();
        harness.Seed(Managed(@"C:\这个路径一定不存在\gone.exe", "甲"));

        // 赋给丢弃变量让 lambda 明确是 Action，避免 Assert.Throws 的重载歧义。
        var ex = Assert.Throws<StartupOperationException>(
            () => _ = harness.Service.ConvertToManual("registry:hkcu:a"));

        Assert.Equal(StartupFailureReason.TargetMissing, ex.Reason);

        // 前提不满足时**一个字节都不该写**：写坏了用户就没有原始状态可还原了。
        Assert.Equal(0, harness.Store.SaveCount);

        var item = Assert.Single(harness.Store.Snapshot().Items);
        Assert.Equal("registry:hkcu:a", item.Id);
        Assert.Equal(StartupSource.Registry, item.Source);
    }

    [Fact]
    public void ConvertToManual_AlreadyManual_IsNoOp()
    {
        var harness = new Harness();
        harness.Seed(new DelayedItem
        {
            Id = "manual:none:abcd",
            Name = "手动项",
            Path = ExistingFile,
            Source = StartupSource.Manual,
            Scope = StartupScope.None,
            DelaySeconds = 30,
        });

        Assert.False(harness.Service.ConvertToManual("manual:none:abcd"));
        Assert.Equal(0, harness.Store.SaveCount);

        // 原主键不能被换掉 —— 否则同一条手动条目会被"转换"成另一个身份。
        Assert.Equal("manual:none:abcd", Assert.Single(harness.Store.Snapshot().Items).Id);
    }

    [Fact]
    public void ConvertToManual_UnknownId_Throws()
    {
        var harness = new Harness();

        // 列表过期（条目刚被别处删掉）时唯一正确的行为是报错并让用户刷新，不能静默成功。
        Assert.Throws<StartupOperationException>(
            () => _ = harness.Service.ConvertToManual("registry:hkcu:nope"));
        Assert.Equal(0, harness.Store.SaveCount);
    }

    private static DelayedItem Managed(string path, string name) => new()
    {
        Id = "registry:hkcu:a",
        Name = name,
        Path = path,
        Source = StartupSource.Registry,
        Scope = StartupScope.Hkcu,
        SourceKey = "a",
        SourceDetail = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
        DelaySeconds = 30,
        SortOrder = 2,
    };

    /// <summary>把待测服务与内存配置捆在一起。</summary>
    private sealed class Harness
    {
        public Harness()
        {
            Log = new FakeLogSink();
            Store = new InMemoryConfigStore();
            Service = new ConfigEditService(Store, new FakeSchedulerTaskRegistrar(), Log);
        }

        public FakeLogSink Log { get; }

        public InMemoryConfigStore Store { get; }

        public ConfigEditService Service { get; }

        public void Seed(params DelayedItem[] items) =>
            Store.Seed(new AppConfig { Items = [.. items] });
    }
}
