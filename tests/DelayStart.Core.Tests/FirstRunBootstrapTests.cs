using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>FirstRunBootstrap</c> 的测试（D63：装完第一次打开管理端时自动注册调度计划任务）。
/// </summary>
/// <remarks>
/// <para>
/// 要守住的**核心不变量**只有一条：这个自动动作只能发生在用户表达意图之前。
/// 总览页的开关（D3：开 = 注册，关 = 删除）是唯一表达方式，而它一旦被用户关掉，
/// 之后任何一次启动都不许把它自动打开 —— 否则用户永远关不掉这个功能。
/// </para>
/// <para>
/// 第二条是失败语义：注册失败**不落标记**，这样下次启动会重试。
/// 这条在真机上几乎没法按需复现（要让计划任务注册恰好失败一次），只能靠替身守。
/// </para>
/// </remarks>
public sealed class FirstRunBootstrapTests
{
    // ── 首次运行 ────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureSchedulerTask_FirstRun_RegistersTaskAndPersistsMarker()
    {
        var harness = new Harness();

        harness.Service.EnsureSchedulerTask();

        Assert.Equal(1, harness.Registrar.RegisterCount);
        Assert.True(harness.Registrar.Registered);

        // 标记必须**真的落盘**：只改内存里的对象，下次启动又会注册一遍。
        Assert.Equal(1, harness.Config.SaveCount);
        Assert.True(harness.Config.Snapshot().Settings.SchedulerTaskInitialized);
    }

    [Fact]
    public void EnsureSchedulerTask_SecondCallInSameSession_DoesNotRegisterAgain()
    {
        var harness = new Harness();

        harness.Service.EnsureSchedulerTask();
        harness.Service.EnsureSchedulerTask();

        Assert.Equal(1, harness.Registrar.RegisterCount);
        Assert.Equal(1, harness.Config.SaveCount);
    }

    // ── 用户关掉开关之后 ────────────────────────────────────────────────────

    [Fact]
    public void EnsureSchedulerTask_AlreadyInitialized_LeavesTaskAlone()
    {
        // 模拟"用户上次关掉了开关"：配置里有标记，系统里任务不存在。
        var config = new AppConfig();
        config.Settings.SchedulerTaskInitialized = true;

        var harness = new Harness();
        harness.Config.Seed(config);

        harness.Service.EnsureSchedulerTask();

        // 🔴 这是本类存在的意义：任务不存在 ≠ 该建任务。
        Assert.Equal(0, harness.Registrar.RegisterCount);
        Assert.Equal(0, harness.Config.SaveCount);
    }

    // ── 用户已经自己开过开关 ────────────────────────────────────────────────

    [Fact]
    public void EnsureSchedulerTask_TaskAlreadyRegistered_OnlyWritesMarker()
    {
        var harness = new Harness();

        // 用户手动拨开了总览页的开关（或上一次安装手跑过 --reinstall-task）。
        harness.Registrar.RegisterOrUpdate();

        harness.Service.EnsureSchedulerTask();

        Assert.Equal(1, harness.Registrar.RegisterCount); // 没有重复注册
        Assert.Equal(1, harness.Config.SaveCount);        // 只补标记
        Assert.True(harness.Config.Snapshot().Settings.SchedulerTaskInitialized);
    }

    // ── 失败路径 ────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureSchedulerTask_RegisterFails_DoesNotMarkAndRetriesNextLaunch()
    {
        var harness = new Harness(registerException: new StartupOperationException(
            StartupFailureReason.ScheduledTaskFailed,
            entryId: string.Empty,
            message: "模拟注册失败"));

        harness.Service.EnsureSchedulerTask();

        // 不落标记 —— 否则这次失败会让用户永久停在"没有调度任务"的状态里。
        Assert.Equal(0, harness.Config.SaveCount);
        Assert.False(harness.Config.Snapshot().Settings.SchedulerTaskInitialized);
        Assert.True(harness.Log.HasExceptionAt(LogLevel.Error));

        // 下一次启动会再来一遍。
        harness.Service.EnsureSchedulerTask();
        Assert.Equal(2, harness.Registrar.RegisterCount);
    }

    [Fact]
    public void EnsureSchedulerTask_MarkerSaveFails_StillKeepsTaskRegistered()
    {
        var harness = new Harness(saveException: new IOException("磁盘满了"));

        harness.Service.EnsureSchedulerTask(); // 不抛异常

        Assert.True(harness.Registrar.Registered);
        Assert.True(harness.Log.HasExceptionAt(LogLevel.Warn));
        // 标记没落盘 ⇒ 下次会重查一遍，但那次 IsRegistered() 已为真，不会再注册。
        Assert.False(harness.Config.Snapshot().Settings.SchedulerTaskInitialized);
    }

    [Fact]
    public void EnsureSchedulerTask_ConfigUnreadable_SkipsWithoutTouchingTask()
    {
        var registrar = new FakeSchedulerTaskRegistrar();
        var log = new FakeLogSink();
        var service = new FirstRunBootstrap(new ThrowingConfigStore(), registrar, log);

        service.EnsureSchedulerTask(); // 启动路径上不允许抛异常

        Assert.Equal(0, registrar.RegisterCount);
        Assert.True(log.HasExceptionAt(LogLevel.Error));
    }

    /// <summary>把替身与待测服务捆在一起，省去每个测试重复装配。</summary>
    private sealed class Harness
    {
        public Harness(Exception? registerException = null, Exception? saveException = null)
        {
            Config = new InMemoryConfigStore { SaveException = saveException };
            Log = new FakeLogSink();
            Registrar = new FakeSchedulerTaskRegistrar { RegisterException = registerException };
            Service = new FirstRunBootstrap(Config, Registrar, Log);
        }

        public InMemoryConfigStore Config { get; }

        public FakeSchedulerTaskRegistrar Registrar { get; }

        public FakeLogSink Log { get; }

        public FirstRunBootstrap Service { get; }
    }

    /// <summary>配置读取永远失败的替身（模拟配置文件被占用 / 权限异常）。</summary>
    private sealed class ThrowingConfigStore : IAppConfigStore
    {
        public string ConfigFilePath => "(抛异常)";

        public AppConfig Load() => throw new IOException("模拟读取失败");

        public void Save(AppConfig config) => throw new IOException("模拟读取失败");
    }
}
