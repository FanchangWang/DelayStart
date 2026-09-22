using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="TargetFileProbe"/> 的单元测试（FR-1.10）。
/// </summary>
/// <remarks>
/// 这条规则被四处共用（注册表 / 启动文件夹 / 计划任务三个来源 + 守卫的手动条目判定），
/// 所以它的**排除项**必须逐条钉住：误判"已失效"是这条规则唯一危险的失败方向 ——
/// 用户会看到一堆其实好好的程序被打上失效标记，进而把它们删掉。
/// </remarks>
public sealed class TargetFileProbeTests
{
    [Fact]
    public void IsMissing_FullyQualifiedPathThatDoesNotExist_IsTrue()
    {
        Assert.True(TargetFileProbe.IsMissing(DeletedFile));
    }

    [Fact]
    public void IsMissing_ExistingFile_IsFalse()
    {
        Assert.False(TargetFileProbe.IsMissing(ExistingFile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMissing_EmptyPath_IsFalse(string? path)
    {
        // 空路径 = "不知道指向哪儿"，不是"目标没了"。
        Assert.False(TargetFileProbe.IsMissing(path));
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("relative\\tool.exe")]
    [InlineData("OneDrive")]
    [InlineData("SecurityHealth")]
    public void IsMissing_NotFullyQualified_IsFalse(string path)
    {
        // 裸命令名由 PATH 解析 —— 对它们调 File.Exists 必然为 false，
        // 直接判"已失效"会造成大量误报（注册表 Run 里这类值很常见）。
        Assert.False(TargetFileProbe.IsMissing(path));
    }

    [Theory]
    [InlineData("shell:AppsFolder\\Package_abc!App")]
    [InlineData("Package_abc!App")]
    public void IsMissing_UwpName_IsFalse(string path)
    {
        // UWP 条目存的是外壳解析名 / 裸 AUMID，与文件系统无关（D41 真机教训）。
        Assert.False(TargetFileProbe.IsMissing(path));
    }

    private static string DeletedFile => Path.Combine(
        Path.GetTempPath(),
        $"delaystart-tests-deleted-{Guid.NewGuid():N}.exe");

    private static string ExistingFile => typeof(TargetFileProbeTests).Assembly.Location;
}
