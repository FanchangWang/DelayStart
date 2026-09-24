using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>GuardTaskBootstrap</c> 的测试（D74，2026-09-22 批复：管理端每次启动检测，缺失即补建）。
/// </summary>
/// <remarks>
/// <para>
/// 用户的原话是"守卫的计划任务也要跟调度器的计划任务一样进行检测复建，目的都是防丢失"。
/// 但守卫与调度**有一处本质差异**，本类要把它钉死：守卫的档位是用户可调的设置项，
/// 所以每次启动都按当前设置**同步**任务 —— 档位变了才真正重写、定义已一致则跳过重写
/// （保留已武装的登录触发器，F1 / D112），而不是"已存在就永远不动"。
/// </para>
/// <para>
/// 由此派生出的四条核心行为：
/// </para>
/// <list type="number">
/// <item><description>守卫启用、任务缺失 ⇒ 按当前档位补建；</description></item>
/// <item><description>守卫启用、任务已存在 ⇒ 按当前档位**同步**（定义一致则跳过重写保留触发器，档位改动即时重写）；</description></item>
/// <item><description>守卫关闭、任务存在 ⇒ **删除**任务（不能让它按旧档位偷偷跑）；</description></item>
/// <item><description>守卫关闭、任务本来就没有 ⇒ 什么都不做（不算改动、不报错）。</description></item>
/// </list>
/// <para>
/// 🔴 失败语义：<see cref="GuardTaskBootstrap.SyncWithSettings"/> 跑在启动路径上，
/// **任何失败都不抛异常**，只记日志并把原因放进返回值（由总览页的守卫设置区呈现 + 重试）。
/// 尤其是"配置读不出来"这一条 —— 此时**绝不能**拿默认档位去覆盖任务，
/// 那等于用一份我们不理解的配置改写用户的守卫设置。
/// </para>
/// </remarks>
public sealed class GuardTaskBootstrapTests
{
    private const int DefaultMinutes = 30;

    // ── 正常路径 ────────────────────────────────────────────────────────────

    [Fact]
    public void SyncWithSettings_EnabledAndTaskMissing_RegistersWithCurrentMode()
    {
        var harness = new Harness(GuardMode.Periodic, minutes: 60);

        var outcome = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.Registered, outcome.Result);
        Assert.Equal(1, harness.Registrar.RegisterCount);
        Assert.Equal(GuardMode.Periodic, harness.Registrar.LastMode);
        Assert.Equal(60, harness.Registrar.LastMinutes);
    }

    [Fact]
    public void SyncWithSettings_EnabledAndTaskExists_RewritesToPickUpModeChange()
    {
        // 任务已存在（上一次启动补建过），但用户刚把档位从"登录后一次"改成了"周期"。
        var harness = new Harness(GuardMode.Periodic, minutes: 60);
        harness.Registrar.RegisterOrUpdate(GuardMode.OnceAfterLogin, DefaultMinutes);
        harness.Registrar.ResetCounters();

        var outcome = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.Updated, outcome.Result);
        // 🔴 与调度任务不同：已存在也要重写，否则档位改动要等下次重建任务才生效。
        Assert.Equal(1, harness.Registrar.RegisterCount);
        Assert.Equal(GuardMode.Periodic, harness.Registrar.LastMode);
        Assert.Equal(60, harness.Registrar.LastMinutes);
    }

    [Fact]
    public void SyncWithSettings_DisabledAndTaskExists_DeletesTask()
    {
        var harness = new Harness(GuardMode.Disabled, minutes: 0);
        harness.Registrar.RegisterOrUpdate(GuardMode.Periodic, DefaultMinutes);
        harness.Registrar.ResetCounters();

        var outcome = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.Deleted, outcome.Result);
        Assert.Equal(1, harness.Registrar.DeleteCount);
        // 关闭路径绝不能顺手把任务又建回来。
        Assert.Equal(0, harness.Registrar.RegisterCount);
        Assert.False(harness.Registrar.Registered);
    }

    [Fact]
    public void SyncWithSettings_DisabledAndTaskMissing_IsNoChange()
    {
        var harness = new Harness(GuardMode.Disabled, minutes: 0);

        var outcome = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.NoChange, outcome.Result);
        Assert.Equal(0, harness.Registrar.DeleteCount);
        Assert.Equal(0, harness.Registrar.RegisterCount);
    }

    [Fact]
    public void SyncWithSettings_CalledTwiceInSameSession_SkipsRewriteWhenUpToDate()
    {
        var harness = new Harness(GuardMode.OnceAfterLogin, DefaultMinutes);

        var first = harness.Service.SyncWithSettings();
        var second = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.Registered, first.Result);
        // F1 / D112：同一次会话里档位没变，第二次不应重建登录触发器（否则该次巡检被静默吞掉）。
        Assert.Equal(GuardTaskSyncResult.NoChange, second.Result);
        Assert.Equal(2, harness.Registrar.RegisterCount); // 仍被调用两次，但第二次判定无需写入
        Assert.Equal(DefaultMinutes, harness.Registrar.LastMinutes);
    }

    // ── 失败路径 ────────────────────────────────────────────────────────────

    [Fact]
    public void SyncWithSettings_ConfigUnreadable_FailsWithoutTouchingTask()
    {
        // 配置版本高于本程序会拒绝加载 —— 此时**不能**拿默认档位去改任务。
        var harness = new Harness(
            GuardMode.Periodic,
            DefaultMinutes,
            loadException: new InvalidOperationException("配置版本过高"));

        var outcome = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.Failed, outcome.Result);
        Assert.Equal(0, harness.Registrar.RegisterCount);
        Assert.Equal(0, harness.Registrar.DeleteCount);
        Assert.True(harness.Log.HasExceptionAt(LogLevel.Error));
        Assert.True(harness.Log.Contains(LogLevel.Error, "读取守卫设置失败"));
    }

    [Fact]
    public void SyncWithSettings_StatusQueryThrows_ReportsFailureWithoutThrowing()
    {
        var harness = new Harness(
            GuardMode.Periodic,
            DefaultMinutes,
            queryException: new InvalidOperationException("任务服务不可用"));

        var outcome = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.Failed, outcome.Result);
        Assert.Equal(0, harness.Registrar.RegisterCount);
        Assert.True(harness.Log.HasExceptionAt(LogLevel.Error));
    }

    [Fact]
    public void SyncWithSettings_RegisterThrows_ReportsFailureWithoutThrowing()
    {
        var harness = new Harness(
            GuardMode.Periodic,
            DefaultMinutes,
            registerException: new InvalidOperationException("访问被拒绝"));

        var outcome = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.Failed, outcome.Result);
        Assert.False(harness.Registrar.Registered);
        Assert.True(harness.Log.HasExceptionAt(LogLevel.Error));
    }

    [Fact]
    public void SyncWithSettings_DeleteThrows_ReportsFailureWithoutThrowing()
    {
        var harness = new Harness(
            GuardMode.Disabled,
            minutes: 0,
            deleteException: new InvalidOperationException("任务正被占用"));
        harness.Registrar.RegisterOrUpdate(GuardMode.Periodic, DefaultMinutes);

        var outcome = harness.Service.SyncWithSettings();

        Assert.Equal(GuardTaskSyncResult.Failed, outcome.Result);
        Assert.True(harness.Log.HasExceptionAt(LogLevel.Error));
    }

    [Fact]
    public void SyncWithSettings_Success_OutcomeMessageCarriesModeDescription()
    {
        var harness = new Harness(GuardMode.Periodic, minutes: 60);

        var outcome = harness.Service.SyncWithSettings();

        // 结果消息会被总览页直接展示，必须说明**同步成了什么**，光说"成功"没有信息量。
        Assert.Contains("每 60 分钟一次", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>把替身、内存配置与待测服务捆在一起，省去每个测试重复装配。</summary>
    private sealed class Harness
    {
        public Harness(
            GuardMode mode,
            int minutes,
            Exception? loadException = null,
            Exception? registerException = null,
            Exception? deleteException = null,
            Exception? queryException = null)
        {
            Log = new FakeLogSink();
            Store = new InMemoryConfigStore { LoadException = loadException };
            Store.Seed(new AppConfig
            {
                Settings = new Settings
                {
                    GuardMode = mode,
                    GuardMinutes = minutes,
                },
            });

            Registrar = new FakeGuardTaskRegistrar
            {
                RegisterException = registerException,
                DeleteException = deleteException,
                QueryException = queryException,
            };

            Service = new GuardTaskBootstrap(Registrar, Store, Log);
        }

        public FakeLogSink Log { get; }

        public InMemoryConfigStore Store { get; }

        public FakeGuardTaskRegistrar Registrar { get; }

        public GuardTaskBootstrap Service { get; }
    }
}
