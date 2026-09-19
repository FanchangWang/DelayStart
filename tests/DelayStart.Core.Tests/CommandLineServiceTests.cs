using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="CommandLineService"/> 的单元测试：命令行拆分与拼接（§2.2 / FR-1.7）。
/// </summary>
/// <remarks>
/// 这个类是本项目最"值得测"的地方之一：注册表值里存的是"一行完整命令行"，
/// 拆分规则没有官方 API 可依，全靠本实现自己定。这里每一条用例都对应
/// 真实注册表里出现过的写法。
/// </remarks>
public sealed class CommandLineServiceTests
{
    [Theory]
    // 无引号、路径无空格、带参数
    [InlineData("C:\\App\\a.exe -arg", "C:\\App\\a.exe", "-arg")]
    // 有引号：引号内是路径
    [InlineData("\"C:\\Program Files\\App\\app.exe\" -arg", "C:\\Program Files\\App\\app.exe", "-arg")]
    // 无引号、路径含空格、无参数 —— 整串都是路径
    [InlineData("C:\\Program Files\\App\\app.exe", "C:\\Program Files\\App\\app.exe", "")]
    // 无引号、路径无空格、无参数
    [InlineData("C:\\App\\a.exe", "C:\\App\\a.exe", "")]
    // 斜杠形式的开关
    [InlineData("C:\\App\\a.exe /silent", "C:\\App\\a.exe", "/silent")]
    // 参数不以 - / 开头，但前半段是可执行文件 —— 靠扩展名判据切开（rundll32 是真实写法）
    [InlineData(
        "C:\\Windows\\System32\\rundll32.exe C:\\a.dll,EntryPoint",
        "C:\\Windows\\System32\\rundll32.exe",
        "C:\\a.dll,EntryPoint")]
    // cmd /c 后跟带引号的整段
    [InlineData("cmd.exe /c \"start foo\"", "cmd.exe", "/c \"start foo\"")]
    // 引号不配对：整串按路径处理，不猜
    [InlineData("\"C:\\unclosed.exe", "C:\\unclosed.exe", "")]
    // 多个参数
    [InlineData("C:\\App\\a.exe -a -b", "C:\\App\\a.exe", "-a -b")]
    // 前后空白
    [InlineData("   C:\\App\\a.exe  ", "C:\\App\\a.exe", "")]
    // 空输入
    [InlineData("", "", "")]
    [InlineData("   ", "", "")]
    public void Parse_VariousRegistryForms_SplitsPathAndArguments(string raw, string expectedPath, string expectedArgs)
    {
        // Arrange / Act
        var parsed = CommandLineService.Parse(raw);

        // Assert
        Assert.Equal(expectedPath, parsed.Path);
        Assert.Equal(expectedArgs, parsed.Arguments);
    }

    [Fact]
    public void Parse_NullInput_ReturnsEmptyResult()
    {
        var parsed = CommandLineService.Parse(null);

        Assert.Equal(string.Empty, parsed.Path);
        Assert.Equal(string.Empty, parsed.Arguments);
    }

    [Fact]
    public void Parse_PathContainingExeLikeSegment_MaySplitEarly_KnownLimitation()
    {
        // 记录已知局限而不是假装它不存在：路径里出现"看着像可执行文件"的目录名时，
        // 无引号的写法有歧义，本实现按"前半段是程序"解释。
        // Windows 自己的要求也是这种路径必须加引号 —— 下面这条断言就是那个"正确写法"。
        var ambiguous = CommandLineService.Parse("C:\\my.exe folder\\app.exe");
        var quoted = CommandLineService.Parse("\"C:\\my.exe folder\\app.exe\"");

        Assert.Equal("C:\\my.exe", ambiguous.Path);
        Assert.Equal("C:\\my.exe folder\\app.exe", quoted.Path);
        Assert.Equal(string.Empty, quoted.Arguments);
    }

    [Theory]
    [InlineData("C:\\App\\a.exe", "", "C:\\App\\a.exe")]
    [InlineData("C:\\App\\a.exe", "-arg", "C:\\App\\a.exe -arg")]
    // 含空格路径必须加引号，否则 CreateProcess 会解析错
    [InlineData("C:\\Program Files\\a.exe", "", "\"C:\\Program Files\\a.exe\"")]
    [InlineData("C:\\Program Files\\a.exe", "-arg", "\"C:\\Program Files\\a.exe\" -arg")]
    // 已带引号的不重复加
    [InlineData("\"C:\\Program Files\\a.exe\"", "", "\"C:\\Program Files\\a.exe\"")]
    // 参数两端空白被吃掉
    [InlineData("C:\\App\\a.exe", "   -arg   ", "C:\\App\\a.exe -arg")]
    // 路径为空 → 没有可执行的命令行
    [InlineData("", "-arg", "")]
    [InlineData("   ", "-arg", "")]
    public void Build_PathAndArguments_ProducesRunnableCommandLine(
        string path,
        string arguments,
        string expected)
    {
        // Arrange / Act
        var commandLine = CommandLineService.Build(path, arguments);

        // Assert
        Assert.Equal(expected, commandLine);
    }

    [Fact]
    public void Build_NullArguments_AppendsNothing()
    {
        var commandLine = CommandLineService.Build("C:\\App\\a.exe", null);

        Assert.Equal("C:\\App\\a.exe", commandLine);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" -arg")]
    [InlineData("C:\\App\\a.exe -arg")]
    [InlineData("C:\\Program Files\\App\\app.exe")]
    public void ParseThenBuild_ValidForms_RoundTripsToEquivalentCommandLine(string raw)
    {
        // 拆开再拼回去必须能跑：路径被正确加引号、参数原样保留
        var parsed = CommandLineService.Parse(raw);
        var rebuilt = CommandLineService.Build(parsed.Path, parsed.Arguments);

        var reparsed = CommandLineService.Parse(rebuilt);

        Assert.Equal(parsed.Path, reparsed.Path);
        Assert.Equal(parsed.Arguments, reparsed.Arguments);
    }
}
