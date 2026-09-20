using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="PowerShellHost"/> 的单元测试（D47，2026-09-20）。
/// </summary>
/// <remarks>
/// <para>
/// 探测优先级是这一块的**契约**：<c>pwsh.exe</c>（PowerShell 7+）优先，找不到才用
/// <c>powershell.exe</c>。测试全部注入假的"文件存在性"，因此不依赖测试机上装没装 PowerShell 7 ——
/// 契约与机器状态解耦后才谈得上稳定。
/// </para>
/// <para>
/// 命令行形态同样是契约：<c>.ps1</c> 必须由宿主以 <c>-File</c> 承载，脚本路径要加引号
/// （否则含空格的路径会被拆成多个参数），<c>-NoProfile</c> / <c>-ExecutionPolicy Bypass</c>
/// 缺一个都会让登录时的无人值守启动失败。
/// </para>
/// </remarks>
public sealed class PowerShellHostTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string LocalAppData = @"C:\Users\tester\AppData\Local";
    private const string SystemRoot = @"C:\Windows";

    private static readonly string PwshStandard =
        Path.Combine(ProgramFiles, "PowerShell", "7", "pwsh.exe");

    private static readonly string PwshPreview =
        Path.Combine(ProgramFiles, "PowerShell", "7-preview", "pwsh.exe");

    private static readonly string PwshAlias =
        Path.Combine(LocalAppData, "Microsoft", "WindowsApps", "pwsh.exe");

    private static readonly string WindowsPowerShell =
        Path.Combine(SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public void ResolveExecutable_StandardPowerShell7Install_Wins()
    {
        var fromPath = Path.Combine(@"C:\Tools\ps", "pwsh.exe");

        var resolved = Resolve([PwshStandard, fromPath], pathVariable: @"C:\Tools\ps");

        Assert.Equal(PwshStandard, resolved);
    }

    [Fact]
    public void ResolveExecutable_OnlyPreviewInstalled_UsesPreview()
    {
        var resolved = Resolve([PwshPreview], pathVariable: @"C:\Tools");

        Assert.Equal(PwshPreview, resolved);
    }

    [Fact]
    public void ResolveExecutable_NotInProgramFiles_UsesPathEntry()
    {
        var fromPath = Path.Combine(@"C:\Tools\ps", "pwsh.exe");

        var resolved = Resolve([fromPath], pathVariable: @"C:\Windows\System32;C:\Tools\ps");

        Assert.Equal(fromPath, resolved);
    }

    [Fact]
    public void ResolveExecutable_PathEntryInQuotes_StillFound()
    {
        var fromPath = Path.Combine(@"C:\Tools\ps7", "pwsh.exe");

        var resolved = Resolve([fromPath], pathVariable: "\"C:\\Tools\\ps7\";C:\\Windows");

        Assert.Equal(fromPath, resolved);
    }

    [Fact]
    public void ResolveExecutable_OnlyAppExecutionAlias_UsesAlias()
    {
        var resolved = Resolve([PwshAlias], pathVariable: @"C:\Windows\System32");

        Assert.Equal(PwshAlias, resolved);
    }

    [Fact]
    public void ResolveExecutable_NoPwshAnywhere_FallsBackToWindowsPowerShell()
    {
        var resolved = Resolve([], pathVariable: @"C:\Windows\System32");

        Assert.Equal(WindowsPowerShell, resolved);
    }

    [Fact]
    public void ResolveExecutable_NoPathVariableAtAll_StillFallsBack()
        => Assert.Equal(WindowsPowerShell, Resolve([], pathVariable: null));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArguments_BlankScriptPath_Throws(string scriptPath)
        => Assert.Throws<ArgumentException>(() => PowerShellHost.BuildArguments(scriptPath, null));

    [Fact]
    public void BuildArguments_NullScriptPath_Throws()
        => Assert.ThrowsAny<ArgumentException>(() => PowerShellHost.BuildArguments(null!, null));

    [Fact]
    public void BuildArguments_PathWithSpaces_IsQuoted()
    {
        var arguments = PowerShellHost.BuildArguments(@"C:\my scripts\start up.ps1", null);

        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\my scripts\\start up.ps1\"", arguments);
    }

    [Fact]
    public void BuildArguments_WithScriptArguments_AppendsThem()
    {
        var arguments = PowerShellHost.BuildArguments(@"C:\scripts\a.ps1", "  -Mode fast --dry-run  ");

        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\scripts\\a.ps1\" -Mode fast --dry-run", arguments);
    }

    [Fact]
    public void BuildArguments_AlreadyQuotedPath_DoesNotDoubleQuote()
    {
        var arguments = PowerShellHost.BuildArguments("\"C:\\scripts\\a.ps1\"", null);

        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\scripts\\a.ps1\"", arguments);
    }

    [Fact]
    public void BuildCommandLine_HostWithSpaces_QuotesHostAndScript()
    {
        var commandLine = PowerShellHost.BuildCommandLine(PwshStandard, @"C:\scripts\a.ps1", "-Foo 1");

        Assert.Equal(
            "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoProfile -ExecutionPolicy Bypass "
                + "-File \"C:\\scripts\\a.ps1\" -Foo 1",
            commandLine);
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", false)]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe", true)]
    [InlineData("pwsh.exe", true)]
    [InlineData("PWSH.EXE", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPowerShell7_LooksAtFileName(string? executablePath, bool expected)
        => Assert.Equal(expected, PowerShellHost.IsPowerShell7(executablePath!));

    private static string Resolve(string[] existing, string? pathVariable) => PowerShellHost.ResolveExecutable(
        programFiles: ProgramFiles,
        localAppData: LocalAppData,
        systemRoot: SystemRoot,
        pathVariable: pathVariable,
        fileExists: new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase).Contains);
}
