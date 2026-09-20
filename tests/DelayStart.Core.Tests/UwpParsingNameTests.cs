using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="UwpParsingName"/> 的单元测试（D41，2026-09-20）。
/// </summary>
/// <remarks>
/// 覆盖的是真机踩到的那个坑：UWP 条目在配置里存的是<b>裸 AUMID</b>
/// （<c>&lt;PFN&gt;!&lt;TaskId&gt;</c>），而启动 UWP 必须交给外壳的解析名
/// <c>shell:AppsFolder\&lt;AUMID&gt;</c>。前缀补没补、有没有重复补，
/// 决定了 UWP 是"正常启动"还是"目标文件不存在"。
/// </remarks>
public sealed class UwpParsingNameTests
{
    private const string SampleAumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";

    [Fact]
    public void Build_BareAumid_PrefixesIt()
    {
        var parsingName = UwpParsingName.Build(SampleAumid);

        Assert.Equal($"shell:AppsFolder\\{SampleAumid}", parsingName);
    }

    [Fact]
    public void Build_AlreadyPrefixed_DoesNotDuplicate()
    {
        var input = $"shell:AppsFolder\\{SampleAumid}";

        var parsingName = UwpParsingName.Build(input);

        Assert.Equal(input, parsingName);
    }

    [Fact]
    public void Build_PrefixWithDifferentCase_DoesNotDuplicate()
    {
        // 大小写不敏感：外壳解析名的协议段不区分大小写，重复补前缀会得到无法解析的名字
        var parsingName = UwpParsingName.Build($"SHELL:APPSFOLDER\\{SampleAumid}");

        Assert.Equal($"SHELL:APPSFOLDER\\{SampleAumid}", parsingName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_BlankInput_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, UwpParsingName.Build(input));
    }

    [Fact]
    public void Build_SurroundingWhitespace_TrimsIt()
    {
        var parsingName = UwpParsingName.Build($"  {SampleAumid}  ");

        Assert.Equal($"shell:AppsFolder\\{SampleAumid}", parsingName);
    }

    [Theory]
    [InlineData("shell:AppsFolder\\A!B", true)]
    [InlineData("SHELL:APPSFOLDER\\A!B", true)]
    [InlineData("Microsoft.X_8wekyb3d8bbwe!App", false)]
    [InlineData("shell:AppsFolder", false)]
    public void IsParsingName_RecognizesPrefixedNames(string input, bool expected)
    {
        Assert.Equal(expected, UwpParsingName.IsParsingName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsParsingName_BlankInput_ReturnsFalse(string? input)
    {
        Assert.False(UwpParsingName.IsParsingName(input));
    }
}
