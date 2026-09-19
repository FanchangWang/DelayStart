using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>TakeoverService</c> 的事务性测试（FR-3.1：三步都成功才算成功，任一步失败要回滚）。
/// </summary>
/// <remarks>
/// <para>
/// 这是本阶段**最有价值**的一组测试：接管的失败路径在真实系统上几乎不可能按需复现
/// （要一边让组策略拒绝写标记、一边让计划任务注册失败），而它一旦出错，
/// 用户面对的可能是"程序被禁用了、配置里却没有记录，没人知道怎么恢复"。
/// </para>
/// <para>
/// <c>D33</c> 正是为此批复"本期就做"。
/// </para>
/// </remarks>
public sealed class TakeoverServiceTests
{
    // ── 成功路径 ────────────────────────────────────────────────────────────

    [Fact]
    public void Takeover_AllStepsSucceed_DisablesSourceRegistersTaskAndWritesConfig()
    {
        var harness = new Harness();

        var outcome = harness.Service.Takeover(Entry(), new TakeoverOptions { DelaySeconds = 45 });

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, harness.Source.DisableCount);
        Assert.Equal(1, harness.Registrar.RegisterCount);

        var item = Assert.Single(harness.Config.Snapshot().Items);
        Assert.Equal("registry:hkcu:weixin", item.Id);
        Assert.Equal(45, item.DelaySeconds);
        Assert.True(item.Enabled);
    }

    [Fact]
    public void Takeover_PersistsScopeAndSourceKeyForRecovery()
    {
        var harness = new Harness();

        _ = harness.Service.Takeover(Entry(), new TakeoverOptions());

        // 坑 5：恢复时要写回哪个 hive 全靠 scope，靠字符串猜会静默失效。
        var item = Assert.Single(harness.Config.Snapshot().Items);
        Assert.Equal(StartupScope.Hkcu, item.Scope);
        Assert.Equal(StartupSource.Registry, item.Source);
        Assert.Equal("Weixin", item.SourceKey);
    }

    [Fact]
    public void Takeover_RecordsOriginalEnabledState()
    {
        var harness = new Harness();

        _ = harness.Service.Takeover(Entry(isEnabled: true), new TakeoverOptions());

        Assert.True(Assert.Single(harness.Config.Snapshot().Items).OriginalState.WasEnabled);
    }

    [Fact]
    public void Takeover_WithoutArguments_KeepsOriginalArguments()
    {
        var harness = new Harness();

        _ = harness.Service.Takeover(Entry(), new TakeoverOptions { Arguments = null });

        // FR-4.5：用户没填参数时应当沿用原自启动项自带的参数，而不是把它清空。
        Assert.Equal("-autorun", Assert.Single(harness.Config.Snapshot().Items).Arguments);
    }

    [Fact]
    public void Takeover_WithArguments_OverridesOriginal()
    {
        var harness = new Harness();

        _ = harness.Service.Takeover(Entry(), new TakeoverOptions { Arguments = "-silent" });

        Assert.Equal("-silent", Assert.Single(harness.Config.Snapshot().Items).Arguments);
    }

    // ── 前置拒绝（不产生任何副作用）─────────────────────────────────────────

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Takeover_UnavailableEntry_RejectedWithoutSideEffects(
        bool isTakenOver,
        bool isProtected,
        bool isMissing)
    {
        var harness = new Harness();

        var outcome = harness.Service.Takeover(
            Entry(isTakenOver: isTakenOver, isProtected: isProtected, isMissing: isMissing),
            new TakeoverOptions());

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, harness.Source.DisableCount);
        Assert.Equal(0, harness.Registrar.RegisterCount);
        Assert.Empty(harness.Config.Snapshot().Items);
    }

    [Fact]
    public void Takeover_AlreadyInConfig_Rejected()
    {
        var harness = new Harness();
        harness.Config.Seed(new AppConfig { Items = [Item()] });

        var outcome = harness.Service.Takeover(Entry(), new TakeoverOptions());

        Assert.False(outcome.Succeeded);
        Assert.Equal(0, harness.Source.DisableCount);
    }

    // ── 失败与回滚（FR-3.1 的核心）─────────────────────────────────────────

    [Fact]
    public void Takeover_DisableFails_RollsBackConfigAndLeavesSourceUntouched()
    {
        var harness = new Harness(disableException: new StartupOperationException(
            StartupFailureReason.AccessDenied,
            "registry:hkcu:weixin",
            "被组策略拒绝"));

        var outcome = harness.Service.Takeover(Entry(), new TakeoverOptions());

        Assert.False(outcome.Succeeded);
        Assert.Equal(StartupFailureReason.AccessDenied, outcome.FailureReason);
        Assert.True(outcome.RolledBack);
        // 禁用没生效 → 不需要恢复，Enable 不该被调用。
        Assert.Equal(0, harness.Source.EnableCount);
        // 已经写进去的配置必须被撤销，否则会留下一个永远不会被启动的"已接管"条目。
        Assert.Empty(harness.Config.Snapshot().Items);
        Assert.Equal(0, harness.Registrar.RegisterCount);
    }

    [Fact]
    public void Takeover_TaskRegistrationFails_RollsBackDisableAndConfig()
    {
        var harness = new Harness(registerException: new StartupOperationException(
            StartupFailureReason.ScheduledTaskFailed,
            entryId: string.Empty,
            message: "任务注册失败"));

        var outcome = harness.Service.Takeover(Entry(), new TakeoverOptions());

        Assert.False(outcome.Succeeded);
        Assert.Equal(StartupFailureReason.ScheduledTaskFailed, outcome.FailureReason);
        Assert.True(outcome.RolledBack);

        Assert.Equal(1, harness.Source.DisableCount);
        // 逆序回滚：先恢复系统项（步骤 3），再删配置（步骤 2）。
        Assert.Equal(1, harness.Source.EnableCount);
        Assert.Empty(harness.Config.Snapshot().Items);
    }

    [Fact]
    public void Takeover_RollbackItselfFails_ReportsNotRolledBackAndLogsError()
    {
        var harness = new Harness(
            enableException: new StartupOperationException(
                StartupFailureReason.AccessDenied,
                "registry:hkcu:weixin",
                "恢复也被拒绝"),
            registerException: new StartupOperationException(
                StartupFailureReason.ScheduledTaskFailed,
                entryId: string.Empty,
                message: "任务注册失败"));

        var outcome = harness.Service.Takeover(Entry(), new TakeoverOptions());

        Assert.False(outcome.Succeeded);
        // 🔴 这是"需要人工介入"的信号，UI 必须把它显示出来而不是只说一句"失败"。
        Assert.False(outcome.RolledBack);
        Assert.True(harness.Log.Contains(LogLevel.Error, "回滚失败"));
    }

    [Fact]
    public void Takeover_ConfigSaveFails_NothingElseIsTouched()
    {
        var harness = new Harness(saveException: new IOException("磁盘已满"));

        var outcome = harness.Service.Takeover(Entry(), new TakeoverOptions());

        Assert.False(outcome.Succeeded);
        // 配置没写进去 = 系统尚未被改动 → 无需回滚，也不该去动系统。
        Assert.Equal(0, harness.Source.DisableCount);
        Assert.Equal(0, harness.Registrar.RegisterCount);
    }

    // ── Release ─────────────────────────────────────────────────────────────

    [Fact]
    public void Release_ExistingItem_RestoresSourceAndRemovesConfig()
    {
        var harness = new Harness();
        harness.Config.Seed(new AppConfig { Items = [Item(), Item(id: "registry:hkcu:other")] });

        var outcome = harness.Service.Release(Item());

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, harness.Source.EnableCount);

        var remaining = Assert.Single(harness.Config.Snapshot().Items);
        Assert.Equal("registry:hkcu:other", remaining.Id);
    }

    [Fact]
    public void Release_LastItem_DeletesScheduledTask()
    {
        var harness = new Harness();
        harness.Config.Seed(new AppConfig { Items = [Item()] });

        _ = harness.Service.Release(Item());

        // 已无任何延时条目 → 调度任务没有必要继续存在。
        Assert.Equal(1, harness.Registrar.DeleteCount);
    }

    [Fact]
    public void Release_NotLastItem_KeepsScheduledTask()
    {
        var harness = new Harness();
        harness.Config.Seed(new AppConfig { Items = [Item(), Item(id: "registry:hkcu:other")] });

        _ = harness.Service.Release(Item());

        Assert.Equal(0, harness.Registrar.DeleteCount);
    }

    [Fact]
    public void Release_RestoreFails_KeepsConfigForRetry()
    {
        var harness = new Harness(enableException: new StartupOperationException(
            StartupFailureReason.AccessDenied,
            "registry:hkcu:weixin",
            "恢复被拒绝"));

        harness.Config.Seed(new AppConfig { Items = [Item()] });

        var outcome = harness.Service.Release(Item());

        Assert.False(outcome.Succeeded);
        // 🔴 配置是唯一的还原依据 —— 系统项还没恢复成功时绝不能把它删掉。
        Assert.Single(harness.Config.Snapshot().Items);
    }

    [Fact]
    public void Release_ManualItem_SkipsAllSystemActions()
    {
        var harness = new Harness();
        var manual = new DelayedItem
        {
            Id = "manual:none:abc",
            Name = "手动添加的程序",
            Source = StartupSource.Manual,
            Scope = StartupScope.None,
        };

        harness.Config.Seed(new AppConfig { Items = [manual] });

        var outcome = harness.Service.Release(manual);

        Assert.True(outcome.Succeeded);
        // FR-3.4：手动条目在系统里没有任何对应物，移除时不做任何恢复动作。
        Assert.Equal(0, harness.Source.EnableCount);
    }

    // ── 恢复成接管前的状态（FR-2.7 / requirements.md 9.3 第 4 条）────────────

    [Fact]
    public void Release_ItemDisabledBeforeTakeover_KeepsItDisabled()
    {
        var harness = new Harness();
        var item = Item(wasEnabled: false);
        harness.Config.Seed(new AppConfig { Items = [item] });

        var outcome = harness.Service.Release(item);

        Assert.True(outcome.Succeeded);
        // 🔴 接管前用户已在任务管理器里禁用过它 → 恢复动作必须是"保持禁用"。
        // 无条件 Enable 会删掉那个 0x03 标记，用户明明禁过的程序就此开始自启动。
        Assert.Equal(1, harness.Source.DisableCount);
        Assert.Equal(0, harness.Source.EnableCount);
    }

    [Fact]
    public void Release_ItemEnabledBeforeTakeover_DeletesMarker()
    {
        var harness = new Harness();
        var item = Item(wasEnabled: true);
        harness.Config.Seed(new AppConfig { Items = [item] });

        _ = harness.Service.Release(item);

        // 原本会自启动 → 删标记即可恢复原样（FR-2.2）。
        Assert.Equal(1, harness.Source.EnableCount);
        Assert.Equal(0, harness.Source.DisableCount);
    }

    [Fact]
    public void Takeover_TaskRegistrationFailsOnItemDisabledBeforeTakeover_RollbackKeepsItDisabled()
    {
        var harness = new Harness(registerException: new StartupOperationException(
            StartupFailureReason.ScheduledTaskFailed,
            entryId: string.Empty,
            message: "任务注册失败"));

        var outcome = harness.Service.Takeover(Entry(isEnabled: false), new TakeoverOptions());

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.RolledBack);
        // 接管 1 次 + 回滚 1 次，两次都是 Disable：回滚不能把"原本禁用"变成"启用"。
        Assert.Equal(2, harness.Source.DisableCount);
        Assert.Equal(0, harness.Source.EnableCount);
        Assert.Empty(harness.Config.Snapshot().Items);
    }

    // ── RestoreAll（D22 的卸载路径）──────────────────────────────────────────

    [Fact]
    public void RestoreAll_AllItemsRestored_ReturnsZeroExitCode()
    {
        var harness = new Harness();
        harness.Config.Seed(new AppConfig
        {
            Items = [Item(), Item(id: "registry:hkcu:other")],
        });

        var outcome = harness.Service.RestoreAll();

        Assert.Equal(2, outcome.RestoredCount);
        Assert.Equal(0, outcome.FailedCount);
        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.TaskDeleted);
        Assert.Equal(2, harness.Source.EnableCount);
    }

    [Fact]
    public void RestoreAll_NoItems_StillDeletesTask()
    {
        var harness = new Harness();

        var outcome = harness.Service.RestoreAll();

        Assert.Equal(0, outcome.RestoredCount);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(1, harness.Registrar.DeleteCount);
    }

    [Fact]
    public void RestoreAll_AnyFailure_ReturnsNonZeroExitCode()
    {
        var harness = new Harness(enableException: new StartupOperationException(
            StartupFailureReason.AccessDenied,
            "registry:hkcu:weixin",
            "恢复被拒绝"));

        harness.Config.Seed(new AppConfig { Items = [Item()] });

        var outcome = harness.Service.RestoreAll();

        Assert.Equal(0, outcome.RestoredCount);
        Assert.Equal(1, outcome.FailedCount);
        // 🔴 非 0 = 卸载脚本必须中止卸载（Inno 的 [UninstallRun] 读不到退出码，只能走 [Code]）。
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("恢复被拒绝", outcome.Failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreAll_TaskDeletionFails_IsReportedButItemsStillRestored()
    {
        var harness = new Harness(deleteException: new StartupOperationException(
            StartupFailureReason.ScheduledTaskFailed,
            entryId: string.Empty,
            message: "删除任务失败"));

        harness.Config.Seed(new AppConfig { Items = [Item()] });

        var outcome = harness.Service.RestoreAll();

        // 条目还原成功，但任务没删掉 → 整体仍算失败，卸载应当中止。
        Assert.Equal(1, outcome.RestoredCount);
        Assert.Equal(1, outcome.FailedCount);
        Assert.False(outcome.TaskDeleted);
        Assert.Equal(1, outcome.ExitCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static StartupEntry Entry(
        bool isEnabled = true,
        bool isProtected = false,
        bool isMissing = false,
        bool isTakenOver = false)
        => new()
        {
            Id = "registry:hkcu:weixin",
            Name = "微信",
            Path = @"C:\Program Files\Tencent\Weixin\Weixin.exe",
            Arguments = "-autorun",
            Source = StartupSource.Registry,
            Scope = StartupScope.Hkcu,
            SourceKey = "Weixin",
            SourceDetail = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            IsEnabled = isEnabled,
            IsProtected = isProtected,
            IsMissing = isMissing,
            IsTakenOver = isTakenOver,
        };

    private static DelayedItem Item(string id = "registry:hkcu:weixin", bool wasEnabled = true)
        => new()
        {
            Id = id,
            Name = "微信",
            Path = @"C:\Program Files\Tencent\Weixin\Weixin.exe",
            Arguments = "-autorun",
            Source = StartupSource.Registry,
            Scope = StartupScope.Hkcu,
            SourceKey = "Weixin",
            OriginalState = new OriginalState { WasEnabled = wasEnabled },
        };

    /// <summary>把一组替身与待测服务捆在一起，省去每个测试重复装配。</summary>
    private sealed class Harness
    {
        public Harness(
            Exception? disableException = null,
            Exception? enableException = null,
            Exception? registerException = null,
            Exception? deleteException = null,
            Exception? saveException = null)
        {
            Source = new FakeStartupSource
            {
                DisableException = disableException,
                EnableException = enableException,
            };

            Registrar = new FakeSchedulerTaskRegistrar
            {
                RegisterException = registerException,
                DeleteException = deleteException,
            };

            Config = new InMemoryConfigStore { SaveException = saveException };
            Log = new FakeLogSink();
            Service = new TakeoverService(Config, Registrar, [Source], Log);
        }

        public FakeStartupSource Source { get; }

        public FakeSchedulerTaskRegistrar Registrar { get; }

        public InMemoryConfigStore Config { get; }

        public FakeLogSink Log { get; }

        public TakeoverService Service { get; }
    }
}
