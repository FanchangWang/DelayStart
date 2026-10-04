using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="GuardStaleChangePolicy"/> 的单元测试（D148）：通报差集判据。
/// </summary>
/// <remarks>
/// <para>
/// 需求是"同一条失效<b>只</b>通知一次"：用户卸载一个程序，守卫收到一条通知，
/// 之后每轮巡检日志里仍列着它（那是全量现状），但通知中心不再新增。
/// </para>
/// <para>
/// 判据只吃一件事：<b>这一条之前有没有被通报过</b>。早期版本吃的是"上一轮基线的失效标志"，
/// 代价是手动条目永远不在基线里（七个来源实例没有 Manual）⇒ 那一类永久静默。
/// 现在判据与载体的无关性由下面两条用例钉住。
/// </para>
/// </remarks>
public sealed class GuardStaleChangePolicyTests
{
    [Fact]
    public void Select_无状态_返回空()
    {
        // 首次运行 / 状态文件损坏 / 状态写失败过。方向必须与 GuardNewItemPolicy 一致：静默。
        var result = GuardStaleChangePolicy.SelectUnnotified(
            [Stale("registry:hkcu:a", StaleKind.TargetLost)],
            notifiedIds: null);

        Assert.Empty(result);
    }

    [Fact]
    public void Select_不在已通报集合里_判为本轮该通报()
    {
        var result = GuardStaleChangePolicy.SelectUnnotified(
            [Stale("registry:hkcu:a", StaleKind.TargetLost)],
            Set());

        Assert.Equal(["registry:hkcu:a"], Ids(result));
    }

    [Fact]
    public void Select_已在已通报集合里_不再通报()
    {
        // 🔴 D148 的核心：持续失效不重复打扰用户。
        var result = GuardStaleChangePolicy.SelectUnnotified(
            [Stale("registry:hkcu:a", StaleKind.TargetLost)],
            Set("registry:hkcu:a"));

        Assert.Empty(result);
    }

    [Fact]
    public void Select_手动条目_与系统条目一视同仁()
    {
        // 🔴 这条是本方案存在的理由：判据不再依赖"条目在不在扫描基线里"，
        // 所以手动添加的 exe 被卸载后同样会收到通知（早期版本会永久静默）。
        var pending = GuardStaleChangePolicy.SelectUnnotified(
            [Stale("manual:none:abc", StaleKind.TargetLost)],
            Set());

        Assert.Equal(["manual:none:abc"], Ids(pending));

        // 通报过一次之后同样静默。
        var again = GuardStaleChangePolicy.SelectUnnotified(
            [Stale("manual:none:abc", StaleKind.TargetLost)],
            Set("manual:none:abc"));

        Assert.Empty(again);
    }

    [Fact]
    public void Select_两档StaleKind_判据不因档位而异()
    {
        // 早期版本对 SourceLost 有"一律算新的"特判（因为源丢失的条目下一轮就不在基线里了）。
        // 载体换掉之后两档走同一条路 —— 这条用例钉住"别再把特判加回来"。
        var pending = GuardStaleChangePolicy.SelectUnnotified(
            [
                Stale("registry:hkcu:a", StaleKind.SourceLost),
                Stale("registry:hkcu:b", StaleKind.TargetLost),
            ],
            Set("registry:hkcu:a"));

        Assert.Equal(["registry:hkcu:b"], Ids(pending));
    }

    [Fact]
    public void Select_混在一起_只返回没通报过的那几条()
    {
        // 精确断言是哪几条 id（不是"非空"、不是"恰好 2 条"）。
        var result = GuardStaleChangePolicy.SelectUnnotified(
            [
                Stale("a", StaleKind.SourceLost),      // 未通报 → 报
                Stale("b", StaleKind.TargetLost),      // 未通报 → 报
                Stale("c", StaleKind.TargetLost),      // 已通报 → 静默
                Stale("d", StaleKind.SourceLost),      // 已通报 → 静默（SourceLost 不再是特判）
                Stale("e", StaleKind.TargetLost),      // 未通报 → 报
            ],
            Set("c", "d"));

        Assert.Equal(["a", "b", "e"], Ids(result));
    }

    [Fact]
    public void Select_入参为空或null_按契约处理()
    {
        Assert.Empty(GuardStaleChangePolicy.SelectUnnotified([], Set()));
        Assert.Throws<ArgumentNullException>(
            () => GuardStaleChangePolicy.SelectUnnotified(null!, Set()));
    }

    [Fact]
    public void ToNotifiedIds_取本轮全量失效的主键()
    {
        var ids = GuardStaleChangePolicy.ToNotifiedIds(
            [
                Stale("a", StaleKind.SourceLost),
                Stale("b", StaleKind.TargetLost),
            ]);

        Assert.Equal(["a", "b"], [.. ids.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void ToNotifiedIds_失效列表为空_返回空集合()
    {
        // 🔴 这一支决定了"恢复之后再失效会不会再通知"：本轮一条失效都没有时，
        // 写回的状态必须是空的，否则曾经报过的条目会永久留在已通报集合里。
        var ids = GuardStaleChangePolicy.ToNotifiedIds([]);

        Assert.NotNull(ids);
        Assert.Empty(ids);
    }

    [Fact]
    public void ToNotifiedIds_入参null_抛()
        => Assert.Throws<ArgumentNullException>(() => GuardStaleChangePolicy.ToNotifiedIds(null!));

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>主键以 <c>manual:</c> 开头的构造成手动条目，其余按注册表接管项构造。</summary>
    private static StaleEntry Stale(string id, StaleKind kind)
    {
        var manual = id.StartsWith("manual:", StringComparison.Ordinal);

        return new StaleEntry(
            new DelayedItem
            {
                Id = id,
                Name = id,
                Source = manual ? StartupSource.Manual : StartupSource.Registry,
                Scope = manual ? StartupScope.None : StartupScope.Hkcu,
            },
            kind,
            Entry: null);
    }

    private static HashSet<string> Set(params string[] ids)
        => new(ids, StringComparer.Ordinal);

    private static string[] Ids(IReadOnlyList<StaleEntry> items)
        => [.. items.Select(static stale => stale.Item.Id)];
}