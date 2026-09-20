using DelayStart.App.Services;
using DelayStart.Core.Services;
using DelayStart.Management.Models;

using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.Dialogs;

/// <summary>
/// 「选择 UWP 应用」列表的一行（D46）。
/// </summary>
/// <remarks>
/// 与 <c>StartupEntryRow</c> 同一套路：XAML 里只做 <c>x:Bind</c>，
/// 判断与拼装留在行对象里。图标在构造时同步转换（UI 线程），行保持不可变。
/// </remarks>
public sealed class UwpAppListItem
{
    /// <summary>构造一行。</summary>
    /// <param name="entry">目录里的应用条目。</param>
    /// <param name="icon">已提取的图标像素；无图标为 <see langword="null"/>。</param>
    public UwpAppListItem(UwpAppEntry entry, IconPixels? icon)
    {
        ArgumentNullException.ThrowIfNull(entry);

        DisplayName = entry.DisplayName;
        AppUserModelId = entry.AppUserModelId;
        ParsingName = UwpParsingName.Build(entry.AppUserModelId);
        Icon = IconRenderer.ToImageSource(icon);
    }

    /// <summary>显示名。</summary>
    public string DisplayName { get; }

    /// <summary>应用用户模型 ID（<c>&lt;PackageFamilyName&gt;!&lt;ApplicationId&gt;</c>）。</summary>
    public string AppUserModelId { get; }

    /// <summary>
    /// 交给外壳的解析名（<c>shell:AppsFolder\&lt;AUMID&gt;</c>）—— **这条就是要存进配置的值**。
    /// </summary>
    /// <remarks>
    /// 🔴 存解析名而不是裸 AUMID：调度端与图标链路都靠它认出这是 UWP
    /// （<c>UwpParsingName.IsParsingName</c>，D41/D45）。存裸 AUMID 的话，
    /// 手工条目会被当成普通 exe 走到 <c>File.Exists</c> 判"目标文件不存在"（已踩过一次）。
    /// </remarks>
    public string ParsingName { get; }

    /// <summary>图标；提取失败为 <see langword="null"/>，XAML 用占位符代替。</summary>
    public ImageSource? Icon { get; }

    /// <summary>是否拿到了图标。</summary>
    public bool HasIcon => Icon is not null;

    /// <summary><see cref="HasIcon"/> 的反面（<c>x:Bind</c> 不支持取反表达式）。</summary>
    public bool HasNoIcon => Icon is null;

    /// <summary>是否命中筛选词（匹配显示名或 AUMID，忽略大小写）。</summary>
    /// <param name="filter">用户输入的筛选词；为空表示全部命中。</param>
    /// <returns>命中为 <see langword="true"/>。</returns>
    public bool Matches(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var keyword = filter.Trim();

        // AUMID 也参与匹配：显示名是本地化的，用户可能记得包名或从别处抄来的 AUMID。
        return DisplayName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
            || AppUserModelId.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
