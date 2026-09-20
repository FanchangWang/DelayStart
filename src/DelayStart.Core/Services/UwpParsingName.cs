namespace DelayStart.Core.Services;

/// <summary>
/// UWP 条目的外壳解析名（<c>shell:AppsFolder\&lt;AUMID&gt;</c>）构造与识别（D41，2026-09-20）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么需要一个专门的 helper</b>：UWP 条目在 <see cref="Models.DelayedItem.Path"/>
/// 里存的是<b>裸 AUMID</b>（<c>&lt;PackageFamilyName&gt;!&lt;TaskId&gt;</c>，
/// 见 <c>UwpStartupSource.CollectPackageEntries</c>），不是解析名。
/// 而启动 UWP 唯一可行的零 COM 手段是把它交给外壳
/// （<c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c>，D28=A），所以"要不要补前缀"
/// 这件事在调度端（启动）与管理端（取图标）都要做 —— 分散写必然写歪。
/// 2026-09-20 真机就是这么栽的：调度端拿裸 AUMID 去判 <c>shell:AppsFolder\</c> 前缀，
/// 恒为 false，于是 UWP 被当成普通 exe 走 <c>File.Exists</c>，直接判"目标文件不存在"。
/// </para>
/// </remarks>
public static class UwpParsingName
{
    /// <summary>外壳解析名前缀（大小写不敏感）。</summary>
    public const string Prefix = "shell:AppsFolder\\";

    /// <summary>判断一个字符串是否已经是完整的解析名（已带前缀）。</summary>
    public static bool IsParsingName(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把 AUMID（或已是解析名的字符串）规范化成完整解析名。
    /// </summary>
    /// <param name="aumid">裸 AUMID，或已带前缀的解析名。</param>
    /// <returns>完整解析名；输入为空时返回空串（调用方负责判空）。</returns>
    public static string Build(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return string.Empty;
        }

        var value = aumid.Trim();
        return IsParsingName(value) ? value : Prefix + value;
    }
}
