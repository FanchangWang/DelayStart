using DelayStart.Core.Launch;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="UiAccessManifest"/> 清单解析（D70）。
/// 🔴 样本取自真机：Quicker.exe 的清单里有 VS 模板注释，注释里埋着
/// <c>requireAdministrator / uiAccess="false"</c> 示例标签 —— 不剥注释就会误判。
/// </summary>
public class UiAccessManifestTests
{
    [Fact]
    public void QuickerStyleManifest_TemplateCommentsOnly_ReturnsFalse()
    {
        const string manifest = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
                <security>
                  <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
                    <!-- UAC 清单选项（VS 模板注释）：
                         <requestedExecutionLevel level="asInvoker" uiAccess="false" />
                         <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
                         <requestedExecutionLevel level="highestAvailable" uiAccess="false" /> -->
                    <requestedExecutionLevel level="asInvoker" uiAccess="true" />
                  </requestedPrivileges>
                </security>
              </trustInfo>
            </assembly>
            """;

        // 注释里的示例全带 uiAccess="false"，生效行是 true —— 结果应由生效行决定。
        Assert.True(UiAccessManifest.HasUiAccessFlag(manifest));
    }

    [Fact]
    public void QuickerStyleManifest_CommentContainsRequireAdmin_EffectiveLineWins()
    {
        // 反向样例：注释里 requireAdministrator，生效行 uiAccess=false —— 不能被注释骗到 true 之外，
        // 也不能因 requireAdministrator 出现在文本里就做无谓提权假设。
        const string manifest = """
            <assembly>
              <!--
                <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
              -->
              <requestedExecutionLevel level="asInvoker" uiAccess="false" />
            </assembly>
            """;

        Assert.False(UiAccessManifest.HasUiAccessFlag(manifest));
    }

    [Theory]
    [InlineData("""<requestedExecutionLevel level="asInvoker" uiAccess="true" />""", true)]
    [InlineData("""<requestedExecutionLevel level="asInvoker" uiAccess="false" />""", false)]
    [InlineData("""<requestedExecutionLevel level="asInvoker" uiAccess="false"/>""", false)]
    [InlineData("""<requestedExecutionLevel level='asInvoker' uiAccess='true' />""", true)]
    [InlineData("""<requestedExecutionLevel uiAccess="true" level="asInvoker" />""", true)]
    [InlineData("""<requestedExecutionLevel UIACCESS = "TRUE" />""", true)]
    [InlineData("""<requestedExecutionLevel level="highestAvailable" />""", false)]
    public void SingleTagVariants_ParsedCorrectly(string tag, bool expected)
    {
        Assert.Equal(expected, UiAccessManifest.HasUiAccessFlag(tag));
    }

    [Fact]
    public void MultipleTags_AnyTrueWins()
    {
        const string manifest = """
            <assembly>
              <requestedExecutionLevel level="asInvoker" uiAccess="false" />
              <requestedExecutionLevel level="asInvoker" uiAccess="true" />
            </assembly>
            """;

        Assert.True(UiAccessManifest.HasUiAccessFlag(manifest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<assembly><trustInfo /></assembly>")]
    [InlineData("not xml at all")]
    public void MissingOrMalformedManifest_ReturnsFalse(string? manifest)
    {
        Assert.False(UiAccessManifest.HasUiAccessFlag(manifest));
    }

    [Theory]
    [InlineData("""<requestedExecutionLevel level="asInvoker" uiAccess="true" />""", "asInvoker")]
    [InlineData("""<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />""", "requireAdministrator")]
    [InlineData("""<requestedExecutionLevel level='highestAvailable' />""", "highestAvailable")]
    public void GetRequestedExecutionLevel_EffectiveTag_ReturnsLevel(string tag, string expected)
    {
        Assert.Equal(expected, UiAccessManifest.GetRequestedExecutionLevel(tag));
    }

    [Fact]
    public void GetRequestedExecutionLevel_CommentTemplateIgnored()
    {
        const string manifest = """
            <assembly>
              <!--
                <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
              -->
              <requestedExecutionLevel level="asInvoker" uiAccess="true" />
            </assembly>
            """;

        Assert.Equal("asInvoker", UiAccessManifest.GetRequestedExecutionLevel(manifest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<assembly />")]
    [InlineData("""<requestedExecutionLevel uiAccess="true" />""")]
    public void GetRequestedExecutionLevel_MissingTagOrMalformed_ReturnsNull(string? manifest)
    {
        Assert.Null(UiAccessManifest.GetRequestedExecutionLevel(manifest));
    }
}
