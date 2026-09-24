using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Models;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="GuardRunSummaryText"/> 的文案锁定测试（D116）。
/// </summary>
/// <remarks>
/// 这份文案是三处消费者（guard.log 巡检完成行 / 守卫日志页组标题 / 总览卡第一行）的
/// **唯一**出处 —— 锁住格式，防止任何一处的口径悄悄漂移。
/// </remarks>
public sealed class GuardRunSummaryTextTests
{
    [Fact]
    public void Build_NoFailures_OmitsSourceFailureSegment()
    {
        var text = GuardRunSummaryText.Build(new GuardRunReport
        {
            ScannedCount = 12,
            Corrections = [new GuardCorrectionOutcome("a", "甲", Succeeded: true, Detail: "已重新禁用")],
            NewItems = [StaleEntryFakes.NewStartupEntry()],
            StaleItems = [StaleEntryFakes.NewStaleEntry()],
        });

        Assert.Equal("扫描 12 项 · 纠正 1（失败 0） · 新增 1 · 失效 1", text);
    }

    [Fact]
    public void Build_WithSourceFailures_AppendsIncompleteListWarning()
    {
        var text = GuardRunSummaryText.Build(new GuardRunReport
        {
            ScannedCount = 3,
            Failures =
            [
                new ScanFailure { DisplayName = "注册表（HKLM\\...32 位视图）", Message = "拒绝访问" },
            ],
        });

        Assert.Equal("扫描 3 项 · 纠正 0（失败 0） · 新增 0 · 失效 0 · 来源失败 1（列表不完整）", text);
    }

    [Fact]
    public void Build_NullReport_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => GuardRunSummaryText.Build(null!));
    }
}

/// <summary>给文案测试凑条目用的小工厂（避免构造真实来源）。</summary>
internal static class StaleEntryFakes
{
    public static StartupEntry NewStartupEntry() => new()
    {
        Id = "registry:hkcu:a",
        Name = "甲",
        Path = @"C:\Program Files\Demo\demo.exe",
        Source = StartupSource.Registry,
        Scope = StartupScope.Hkcu,
        SourceKey = "registry:hkcu:a",
        IsEnabled = true,
    };

    public static StaleEntry NewStaleEntry() => new(
        new DelayedItem { Id = "registry:hkcu:b", Name = "乙", Source = StartupSource.Registry, Scope = StartupScope.Hkcu },
        StaleKind.Missing,
        null);
}
