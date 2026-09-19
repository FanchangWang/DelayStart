using DelayStart.Core.Models;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <c>StartupApprovedStore</c> 的**纯逻辑**部分（机制 2 / 机制 4）。
/// </summary>
/// <remarks>
/// 只测不碰注册表的三件事：键名三级回退的候选序列、标记子键路径的映射矩阵、标记字节格式。
/// 真正写注册表的部分需要管理员权限，由 <c>build-and-test.md</c> 9.1 的注册表导出对比覆盖 ——
/// 测试项目按约定**不得**触碰真实注册表。
/// </remarks>
public sealed class StartupApprovedStoreTests
{
    private static readonly DateTimeOffset ClockNow =
        new(2026, 9, 19, 8, 41, 12, TimeSpan.FromHours(8));

    // ── GetCandidateNames：三级回退（坑 1 / FR-1.5）────────────────────────

    [Fact]
    public void GetCandidateNames_BareName_AddsExeSuffixAndDeduplicates()
    {
        var candidates = StartupApprovedStore.GetCandidateNames("Weixin");

        // 第三级（去扩展名）与第一级相同，必须去重，否则会白读一次注册表。
        Assert.Equal(["Weixin", "Weixin.exe"], candidates);
    }

    [Fact]
    public void GetCandidateNames_NameWithExtension_YieldsThreeDistinctCandidates()
    {
        var candidates = StartupApprovedStore.GetCandidateNames("WeChat.lnk");

        Assert.Equal(["WeChat.lnk", "WeChat.lnk.exe", "WeChat"], candidates);
    }

    [Fact]
    public void GetCandidateNames_ExeName_KeepsOriginalAsFirstCandidate()
    {
        var candidates = StartupApprovedStore.GetCandidateNames("App.exe");

        Assert.Equal("App.exe", candidates[0]);
        Assert.Equal("App", candidates[^1]);
    }

    [Fact]
    public void GetCandidateNames_EmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(() => StartupApprovedStore.GetCandidateNames("  "));
    }

    // ── GetMarkerSubKeyPath：机制 4 的矩阵 ─────────────────────────────────

    [Theory]
    [InlineData(StartupSource.Registry, StartupScope.Hkcu, @"\Run")]
    [InlineData(StartupSource.Registry, StartupScope.Hklm, @"\Run")]
    [InlineData(StartupSource.Registry, StartupScope.HklmWow, @"\Run32")]   // ⚠️ 坑 2
    [InlineData(StartupSource.StartupFolder, StartupScope.UserFolder, @"\StartupFolder")]
    [InlineData(StartupSource.StartupFolder, StartupScope.SystemFolder, @"\StartupFolder")]
    public void GetMarkerSubKeyPath_SupportedCombination_EndsWithExpectedLeaf(
        StartupSource source,
        StartupScope scope,
        string expectedLeaf)
    {
        var path = StartupApprovedStore.GetMarkerSubKeyPath(source, scope);

        Assert.NotNull(path);
        Assert.EndsWith(expectedLeaf, path, StringComparison.Ordinal);
        Assert.Contains("StartupApproved", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMarkerSubKeyPath_Wow64UsesRun32NotRun()
    {
        var path = StartupApprovedStore.GetMarkerSubKeyPath(StartupSource.Registry, StartupScope.HklmWow);

        // 坑 2：写到 Run 会完全失效且不报错 —— 这条断言是防回归的最后一道。
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", path);
    }

    [Theory]
    [InlineData(StartupSource.ScheduledTask, StartupScope.None)]
    [InlineData(StartupSource.Uwp, StartupScope.None)]
    [InlineData(StartupSource.Manual, StartupScope.None)]
    public void GetMarkerSubKeyPath_SourceWithoutMarker_ReturnsNull(StartupSource source, StartupScope scope)
    {
        Assert.Null(StartupApprovedStore.GetMarkerSubKeyPath(source, scope));
    }

    // ── CreateDisabledMarker：12 字节格式（FR-2.4）─────────────────────────

    [Fact]
    public void CreateDisabledMarker_LayoutMatchesSpecification()
    {
        var marker = StartupApprovedStore.CreateDisabledMarker(ClockNow);

        Assert.Equal(12, marker.Length);
        Assert.Equal((byte)0x03, marker[0]);          // 字节 0：0x03 = 禁用
        Assert.Equal(0, marker[1]);                    // 字节 1..3：保留 0
        Assert.Equal(0, marker[2]);
        Assert.Equal(0, marker[3]);
    }

    [Fact]
    public void CreateDisabledMarker_EncodesUtcFileTimeAtOffsetFour()
    {
        var marker = StartupApprovedStore.CreateDisabledMarker(ClockNow);

        var expected = ClockNow.UtcDateTime.ToFileTime();
        Assert.Equal(expected, BitConverter.ToInt64(marker, 4));
    }

    [Fact]
    public void CreateDisabledMarker_UsesUtcNotLocalTime()
    {
        // 同一时刻用不同偏移量表示，编码结果必须一致 —— 否则跨时区会产生不同的标记。
        var shifted = ClockNow.ToOffset(TimeSpan.FromHours(-5));

        Assert.Equal(
            StartupApprovedStore.CreateDisabledMarker(ClockNow),
            StartupApprovedStore.CreateDisabledMarker(shifted));
    }

    [Fact]
    public void DisabledFlagAndEnabledFlag_MatchDocumentedValues()
    {
        Assert.Equal((byte)0x03, StartupApprovedStore.DisabledFlag);
        Assert.Equal((byte)0x02, StartupApprovedStore.EnabledFlag);
    }
}
