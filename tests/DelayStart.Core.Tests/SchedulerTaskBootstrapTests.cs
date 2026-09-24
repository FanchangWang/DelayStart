using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>SchedulerTaskBootstrap</c> 的测试（2026-09-21 批复：每次启动检测，缺失即补建）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 本类语义已于 2026-09-21 改判，取代 D63 的「仅首启一次」：
/// 计划任务缺失不再是"用户可以关掉的状态"，而是要自动修复的故障 ——
/// 总览页开关已删除，旧的"自动补建会推翻用户意图"这条不变量随之废止。
/// 现在要守住的核心行为只有两条：**缺失必补建**、**已存在不重复注册**。
/// </para>
/// <para>
/// 失败语义：补建 / 查询失败**不抛异常**（启动路径），记日志后由总览页状态卡承担呈现与重试。
/// </para>
/// </remarks>
public sealed class SchedulerTaskBootstrapTests
{
    // ── 正常路径 ────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureSchedulerTask_TaskMissing_RegistersTask()
    {
        var harness = new Harness();

        harness.Service.EnsureSchedulerTask();

        Assert.Equal(1, harness.Registrar.RegisterCount);
        Assert.True(harness.Registrar.Registered);
    }

    [Fact]
    public void EnsureSchedulerTask_TaskAlreadyRegistered_DoesNotRegisterAgain()
    {
        var harness = new Harness();

        // 用户手动重建过（或上一次启动已补建）。
        harness.Registrar.RegisterOrUpdate();

        harness.Service.EnsureSchedulerTask();

        Assert.Equal(1, harness.Registrar.WriteCount); // 只真正写入过一次，未重复写入
        Assert.True(harness.Registrar.Registered);
    }

    [Fact]
    public void EnsureSchedulerTask_UpToDateDefinition_SkipsRewriteAndLogsNoRebuild()
    {
        var harness = new Harness();

        // 模拟上一次启动已正确注册、且定义没有变化（F1 调度端镜像 / D113）。
        harness.Registrar.RegisterOrUpdate();
        var before = harness.Registrar.WriteCount;

        harness.Service.EnsureSchedulerTask();

        // 定义一致时不重写：写入次数不应增长，且日志明确"无需重建"。
        Assert.Equal(before, harness.Registrar.WriteCount);
        Assert.True(harness.Registrar.Registered);
        Assert.True(harness.Log.Contains(LogLevel.Info, "已是最新，无需重建"));
    }

    [Fact]
    public void EnsureSchedulerTask_CalledTwiceInSameSession_RegistersOnce()
    {
        var harness = new Harness();

        harness.Service.EnsureSchedulerTask();
        harness.Service.EnsureSchedulerTask();

        Assert.Equal(1, harness.Registrar.WriteCount);
    }

    [Fact]
    public void EnsureSchedulerTask_ExistingDefinitionChanged_RewritesAndReportsChanged()
    {
        var harness = new Harness();
        harness.Registrar.RegisterOrUpdate();
        harness.Registrar.DefinitionUpToDate = false;
        harness.Registrar.ExecutableMatches = false;
        Assert.False(harness.Registrar.Matches());
        var before = harness.Registrar.WriteCount;

        harness.Service.EnsureSchedulerTask();

        Assert.Equal(before + 1, harness.Registrar.WriteCount);
        Assert.True(harness.Registrar.Matches());
        Assert.True(harness.Log.Contains(LogLevel.Info, "定义已变更"));
    }

    // ── 失败路径 ────────────────────────────────────────────────────────────

    [Fact]
    public void EnsureSchedulerTask_RegisterFails_DoesNotThrowAndRetriesNextCall()
    {
        var harness = new Harness(registerException: new StartupOperationException(
            StartupFailureReason.ScheduledTaskFailed,
            entryId: string.Empty,
            message: "模拟注册失败"));

        // 启动路径上不允许抛异常。
        harness.Service.EnsureSchedulerTask();

        Assert.True(harness.Log.HasExceptionAt(LogLevel.Error));
        Assert.False(harness.Registrar.Registered);

        // 下一次调用（下一次启动 / 用户点重试）会再来一遍。
        harness.Service.EnsureSchedulerTask();
        Assert.Equal(2, harness.Registrar.RegisterCount);
    }

    [Fact]
    public void EnsureSchedulerTask_StatusCheckFails_ReportsWithoutThrowing()
    {
        var harness = new Harness(queryException: new InvalidOperationException("模拟查询失败"));

        harness.Service.EnsureSchedulerTask();

        Assert.Equal(0, harness.Registrar.RegisterCount);
        Assert.True(harness.Log.HasExceptionAt(LogLevel.Error));
    }

    /// <summary>把替身与待测服务捆在一起，省去每个测试重复装配。</summary>
    private sealed class Harness
    {
        public Harness(Exception? registerException = null, Exception? queryException = null)
        {
            Log = new FakeLogSink();
            Registrar = new FakeSchedulerTaskRegistrar
            {
                RegisterException = registerException,
                QueryException = queryException,
            };
            Service = new SchedulerTaskBootstrap(Registrar, Log);
        }

        public FakeLogSink Log { get; }

        public FakeSchedulerTaskRegistrar Registrar { get; }

        public SchedulerTaskBootstrap Service { get; }
    }
}
