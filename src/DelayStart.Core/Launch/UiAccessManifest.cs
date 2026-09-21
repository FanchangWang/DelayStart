using System.Text.RegularExpressions;

namespace DelayStart.Core.Launch;

/// <summary>
/// 嵌入清单（RT_MANIFEST）中 <c>uiAccess</c> 声明的解析（D70，2026-09-21 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么必须解析而不是试错</b>：对 <c>uiAccess="true"</c> 的目标
/// （如 Quicker）直接走 <c>CreateProcessWithTokenW</c> 必报
/// <c>Win32Error=740</c>（<c>ERROR_ELEVATION_REQUIRED</c>）——
/// <c>TokenUIAccess</c> 需要 SeTcbPrivilege（仅 SYSTEM），AppInfo 是唯一认可通道。
/// 详见 docs/pitfalls.md「降权」条目（2026-09-21 demo2 AppA 实测）。
/// </para>
/// <para>
/// 🔴 <b>不能裸匹配字符串</b>：Quicker 的清单里有 Visual Studio 模板注释
/// <c>&lt;!-- UAC Manifest Options --&gt;</c>，里面躺着
/// <c>level="requireAdministrator" uiAccess="false"</c> —— 先剥掉 XML 注释，
/// 再逐个检查 <c>&lt;requestedExecutionLevel&gt;</c> 标签的属性。
/// </para>
/// </remarks>
public static partial class UiAccessManifest
{
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<\s*requestedExecutionLevel\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutionLevelPattern();

    [GeneratedRegex("""uiAccess\s*=\s*["']true["']""", RegexOptions.IgnoreCase)]
    private static partial Regex UiAccessTruePattern();

    [GeneratedRegex("""level\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex LevelPattern();

    /// <summary>
    /// 判断清单 XML 是否声明了 <c>uiAccess="true"</c>。
    /// </summary>
    /// <param name="manifestXml">清单 XML 全文（RT_MANIFEST 资源解码后的文本）。</param>
    /// <returns>任一生效的 <c>requestedExecutionLevel</c> 标签声明 uiAccess=true 则为 true；
    /// 没有该标签或全部声明为 false 则为 false。</returns>
    public static bool HasUiAccessFlag(string? manifestXml)
    {
        if (string.IsNullOrWhiteSpace(manifestXml))
        {
            return false;
        }

        // ① 先剥注释：VS 模板注释里埋着 requireAdministrator / uiAccess=false 的示例标签，
        //    不剥会被当成"生效声明"（demo2 Quicker 实锤）。
        var effective = CommentPattern().Replace(manifestXml, string.Empty);

        foreach (Match tag in ExecutionLevelPattern().Matches(effective))
        {
            if (UiAccessTruePattern().IsMatch(tag.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 取生效的 <c>requestedExecutionLevel</c> 标签的 <c>level</c> 属性值。
    /// </summary>
    /// <param name="manifestXml">清单 XML 全文（RT_MANIFEST 资源解码后的文本）。</param>
    /// <returns>第一个生效标签的 level（如 <c>asInvoker</c>）；无标签或解析不出则 <see langword="null"/>。</returns>
    public static string? GetRequestedExecutionLevel(string? manifestXml)
    {
        if (string.IsNullOrWhiteSpace(manifestXml))
        {
            return null;
        }

        // 与 HasUiAccessFlag 同规则：先剥注释，只看生效标签（Quicker 注释模板实锤）。
        var effective = CommentPattern().Replace(manifestXml, string.Empty);
        var tag = ExecutionLevelPattern().Match(effective);
        if (!tag.Success)
        {
            return null;
        }

        var level = LevelPattern().Match(tag.Value);
        return level.Success ? level.Groups[1].Value : null;
    }
}
