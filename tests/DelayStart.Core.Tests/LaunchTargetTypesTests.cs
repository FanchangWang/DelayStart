using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="LaunchTargetTypes"/> 的单元测试（D47，2026-09-20）。
/// </summary>
/// <remarks>
/// 白名单是"能选"与"能启"的交集，两边任何一侧漂移都会让用户得到一条永远失败的条目：
/// 选得进来却启不动（旧 <c>.msi</c> 就是这样，<c>CreateProcess</c> 报 193），
/// 或启得动却选不进来。故这里把清单内容本身也钉死。
/// </remarks>
public sealed class LaunchTargetTypesTests
{
    [Fact]
    public void Extensions_ContainExactlyTheSupportedSet()
    {
        string[] expected = [".exe", ".lnk", ".bat", ".cmd", ".ps1"];

        Assert.Equal(expected, LaunchTargetTypes.Extensions);
    }

    [Fact]
    public void Extensions_DoNotContainMsi()
    {
        // 🔴 D47 明确移除：.msi 不是 PE 映像，CreateProcess 家族返回 193 ERROR_BAD_EXE_FORMAT
        // （真机实测，同一链路上 .bat / .cmd 正常）。
        Assert.DoesNotContain(".msi", LaunchTargetTypes.Extensions);
    }

    [Theory]
    [InlineData("C:\\tools\\a.exe")]
    [InlineData("C:\\tools\\a.lnk")]
    [InlineData("C:\\tools\\a.bat")]
    [InlineData("C:\\tools\\a.cmd")]
    [InlineData("C:\\tools\\a.ps1")]
    public void IsSupported_SupportedExtensions_ReturnsTrue(string path)
        => Assert.True(LaunchTargetTypes.IsSupported(path));

    [Theory]
    [InlineData("C:\\tools\\A.EXE")]
    [InlineData("C:\\tools\\A.PS1")]
    [InlineData("C:\\tools\\A.Bat")]
    public void IsSupported_IsCaseInsensitive(string path)
        => Assert.True(LaunchTargetTypes.IsSupported(path));

    [Theory]
    [InlineData("C:\\tools\\setup.msi")]
    [InlineData("C:\\tools\\lib.dll")]
    [InlineData("C:\\tools\\readme.txt")]
    [InlineData("C:\\tools\\folder")]
    [InlineData("C:\\tools\\aps1")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsSupported_OtherInputs_ReturnsFalse(string? path)
        => Assert.False(LaunchTargetTypes.IsSupported(path));

    [Theory]
    [InlineData("C:\\tools\\a.ps1", true)]
    [InlineData("C:\\tools\\A.PS1", true)]
    [InlineData("C:\\tools\\a.exe", false)]
    [InlineData("C:\\tools\\a.mui.ps1", true)]
    [InlineData(null, false)]
    public void IsPowerShellScript_RecognizesPs1Only(string? path, bool expected)
        => Assert.Equal(expected, LaunchTargetTypes.IsPowerShellScript(path));

    [Fact]
    public void DisplayList_MatchesExtensionsInOrder()
        => Assert.Equal(".exe / .lnk / .bat / .cmd / .ps1", LaunchTargetTypes.DisplayList);
}
