using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// 🔴 **复现真机问题**：配置里有一条
/// <c>"id": "registry:hkcu:fluxdown"</c>，
/// <c>"path": "C:\Users\guyue\AppData\Local\Programs\FluxDown\flux_down.exe"</c> 已不存在，
/// 界面上却**不显示任何失效徽标**（不是被列宽裁掉，是纯粹没出现）。
/// </summary>
/// <remarks>
/// 这组用例走的是「扫描项 → 策略 → 徽标文案」的完整判定链，把每一环单独钉住，
/// 这样下次再出现"徽标不显示"就能立刻定位到是哪一环，而不是像这次一样反复猜。
/// </remarks>
public sealed class StaleBadgeChainTests
{
    private const string MissingExe =
        @"C:\Users\guyue\AppData\Local\Programs\FluxDown\flux_down.exe";

    private static DelayedItem ManagedItem(string id, string path, bool manual = false)
        => new()
        {
            Id = id,
            Name = "FluxDown",
            Path = path,
            // 🔴 IsManual 是**派生**属性（`Source == Manual`），不是可写字段 ——
            // 想造手动条目只能改 Source，这也是"手动"在数据层真正的含义。
            Source = manual ? StartupSource.Manual : StartupSource.Registry,
            Scope = manual ? StartupScope.None : StartupScope.Hkcu,
        };

    private static StartupEntry ScannedEntry(string id, string path, bool isMissing)
        => new()
        {
            Id = id,
            Name = "fluxdown",
            Path = path,
            Source = StartupSource.Registry,
            Scope = StartupScope.Hkcu,
            SourceKey = "fluxdown",
            IsMissing = isMissing,
        };

    [Fact]
    public void Probe_SaysFullyQualifiedAbsentPathIsMissing()
    {
        // 第 1 环：文件探测。一个绝对路径、文件不存在 ⇒ 必须判"已丢失"。
        Assert.False(File.Exists(MissingExe), "夹具前提失效：这个路径居然存在");
        Assert.True(TargetFileProbe.IsMissing(MissingExe));
    }

    [Fact]
    public void Policy_SourceStillThere_TargetGone_ReportsTargetLost()
    {
        // 第 2 环：注册表项还在、程序没了 ⇒ **目标丢失**（不再启动）。
        // 这是用户那条 config 的形态：源在（found=true），`entry.IsMissing=true`。
        var item = ManagedItem("registry:hkcu:fluxdown", MissingExe);
        var scanned = new[] { ScannedEntry(item.Id, MissingExe, isMissing: true) };

        var stale = GuardStalePolicy.SelectStaleItems([item], scanned, []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.TargetLost, entry.Kind);
    }

    [Fact]
    public void Policy_SourceAlsoGone_TargetGone_StillReportsTargetLost()
    {
        // 第 2 环的另一分支：注册表项**也**没了。
        // 此时没有 StartupEntry 可问 IsMissing，改用 TargetFileProbe 直查文件系统 ——
        // 若这一步没走通，就会掉进「源丢失」分支，而用户看到的是一条"去重新接管"的
        // 误导性提示（重新接管一个永远跑不起来的东西）。
        var item = ManagedItem("registry:hkcu:fluxdown", MissingExe);

        var stale = GuardStalePolicy.SelectStaleItems([item], [], []);

        var entry = Assert.Single(stale);
        Assert.Equal(StaleKind.TargetLost, entry.Kind);
        Assert.Null(entry.Entry);
    }

    [Fact]
    public void Policy_ScanFailureForThatScope_ReportsNothingRatherThanFalsePositive()
    {
        // 🔴 反向：来源整体失败时**一律不判**。一次整体失败（服务没起 / 键被 ACL 拒绝）
        // 绝不能把整份清单报成失效 —— 那会诱导用户把好好的条目清理掉。
        // 这条与上面两条成对：只钉"该判的判了"的话，一个"永远返回空"也能全绿。
        var item = ManagedItem("registry:hkcu:fluxdown", MissingExe);
        var failures = new[] { new ScanScope(StartupSource.Registry, StartupScope.Hkcu) };

        var stale = GuardStalePolicy.SelectStaleItems([item], [], failures);

        Assert.Empty(stale);
    }

    [Fact]
    public void Policy_ManualItemWithGoneTarget_ReportsTargetLost()
    {
        // 手动条目在系统里没有锚点，"扫描结果里找不到"是正常状态 ——
        // 但目标文件在不在与来源无关，同样要让用户看见（2026-09-22 用户回报的漏判）。
        var item = ManagedItem(ItemKeyBuilder.ForManual(), MissingExe, manual: true);

        var stale = GuardStalePolicy.SelectStaleItems([item], [], []);

        Assert.Equal(StaleKind.TargetLost, Assert.Single(stale).Kind);
    }

    [Fact]
    public void Policy_HealthyItem_ReportsNothing()
    {
        var existing = typeof(StaleBadgeChainTests).Assembly.Location;
        var item = ManagedItem("registry:hkcu:healthy", existing);
        var scanned = new[] { ScannedEntry(item.Id, existing, isMissing: false) };

        Assert.Empty(GuardStalePolicy.SelectStaleItems([item], scanned, []));
    }
}
