using DelayStart.App.Services;
using DelayStart.Core.Models;
using DelayStart.Management.Models;

using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 「自启动项」页一行。<see cref="StartupEntry"/> 的只读展示包装。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不让页面直接绑 <see cref="StartupEntry"/>：行里要显示"状态文案""命令行""位置"
/// 这三样在模型里都不存在 —— 模型只有 <c>IsEnabled</c> / <c>Path</c> + <c>Arguments</c> /
/// <c>Source</c> + <c>Scope</c> + <c>SourceKey</c> 这些原始字段。
/// 把拼装逻辑放在这里，XAML 里就只剩 <c>x:Bind</c>，一旦文案要改只动一处。
/// </para>
/// <para>
/// 刻意**不带任何可变状态**：本页的筛选、搜索、选中都在 ViewModel 上，
/// 行对象只回答"这一行长什么样"。加了可变状态就会出现"行对象与 ViewModel 谁说了算"的问题。
/// </para>
/// </remarks>
public sealed class StartupEntryRow
{
    /// <summary>构造行。</summary>
    /// <param name="entry">扫描结果中的条目。</param>
    /// <param name="iconPixels">该条目的图标像素；提取失败为 <see langword="null"/>（显示占位符）。</param>
    public StartupEntryRow(StartupEntry entry, IconPixels? iconPixels)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        Pixels = iconPixels;
        Icon = IconRenderer.ToImageSource(iconPixels);
    }

    /// <summary>原始图标像素，供行级局部刷新时复用（新行沿用旧图标，不重新提取）。</summary>
    public IconPixels? Pixels { get; }

    /// <summary>原始条目，供后续操作（接管 / 禁用 / 打开文件位置）取用。</summary>
    public StartupEntry Entry { get; }

    /// <summary>条目图标；提取失败为 <see langword="null"/>，XAML 用占位符代替。</summary>
    /// <remarks>
    /// 在构造时由 <see cref="IconRenderer"/> 同步转换（UI 线程）—— 行对象保持
    /// "不可变"的设计（见类注释），图标不作为可变状态事后补挂。
    /// </remarks>
    public ImageSource? Icon { get; }

    /// <summary>是否拿到了图标。XAML 据此在「真图标」与「占位符」间切换。</summary>
    public bool HasIcon => Icon is not null;

    /// <summary><see cref="HasIcon"/> 的反面。<c>x:Bind</c> 不支持取反表达式。</summary>
    public bool HasNoIcon => Icon is null;

    /// <summary>显示名。</summary>
    public string Name => Entry.Name;

    /// <summary>路径 + 参数拼成的命令行；路径为空（UWP）时退回 AUMID。</summary>
    public string CommandLine => string.IsNullOrWhiteSpace(Entry.Arguments)
        ? Entry.Path
        : $"{Entry.Path}  {Entry.Arguments}";

    /// <summary>
    /// 位置描述：来源类型 · 具体位置 · 来源内的原始键。
    /// </summary>
    /// <remarks>
    /// 三段拼接而不是只显示 <c>SourceDetail</c>：<c>SourceDetail</c> 只回答"在哪儿"
    /// （如 <c>用户启动文件夹</c>），而用户排查时还需要知道"叫什么名字"
    /// （值名 / 文件名 / 任务路径），否则同名不同来源的两条根本分不出来。
    /// </remarks>
    public string LocationText
    {
        get
        {
            var kind = DisplayText.SourceOf(Entry.Source);
            var scope = Entry.Scope == StartupScope.None ? null : DisplayText.ScopeOf(Entry.Scope);

            var head = scope is null ? kind : $"{kind} · {scope}";
            var detail = string.IsNullOrWhiteSpace(Entry.SourceDetail) ? null : Entry.SourceDetail;

            // SourceDetail 有时与 scope 文案重复（如「用户启动文件夹」），去重后再拼。
            var middle = detail is null || detail == scope ? null : detail;

            return middle is null
                ? $"{head} · {Entry.SourceKey}"
                : $"{head} · {middle} · {Entry.SourceKey}";
        }
    }

    /// <summary>状态徽标文案。</summary>
    public string StatusText => DisplayText.StatusOf(Entry);

    /// <summary>
    /// 状态徽标的种类（配色用），与 <see cref="DisplayText.StatusOf"/> 同序判断。
    /// </summary>
    /// <remarks>
    /// 判断顺序**必须**与 <c>StatusOf</c> 完全一致（受保护 → 已失效 → 已接管 → 启用/禁用），
    /// 否则徽标颜色与文案会错配。这里是纯字符串（"protected"/"missing"/"taken"/"enabled"/"disabled"），
    /// 配色交给 <c>StatusKindToBrushConverter</c> —— 行对象不持有 <see cref="Brush"/>，
    /// 免得行构造期就得碰 UI 资源。
    /// </remarks>
    public string StatusKind => Entry.IsProtected
        ? "protected"
        : Entry.IsMissing
            ? "missing"
            : Entry.IsTakenOver
                ? "taken"
                : Entry.IsEnabled ? "enabled" : "disabled";

    /// <summary>
    /// 该行是否可执行「延时启动」。
    /// </summary>
    /// <remarks>
    /// 直接透传 <see cref="StartupEntry.CanTakeOver"/>，不在界面层复制一份判据 ——
    /// "能不能接管"是模型的语义，界面只负责把它显示成按钮的可用性。
    /// </remarks>
    public bool CanTakeOver => Entry.CanTakeOver;

    /// <summary>已接管项可执行「移出延时」（bug#4：此前行上没有这个入口）。</summary>
    public bool CanRelease => Entry.IsTakenOver;

    /// <summary>已接管项可打开编辑器改延时 / 参数 / 工作目录。</summary>
    public bool CanEdit => Entry.IsTakenOver;

    /// <summary>
    /// 可执行「禁用」（纯禁用，不接管 —— D3：与接管后的禁用同一软禁用机制，只是不写配置）。
    /// 受保护 / 已失效 / 已接管项不可再禁。
    /// </summary>
    public bool CanDisable => Entry.IsEnabled && !Entry.IsTakenOver && !Entry.IsProtected && !Entry.IsMissing;

    /// <summary>可执行「启用」（写标记的反向：删除 StartupApproved 标记）。</summary>
    public bool CanEnable => !Entry.IsEnabled && !Entry.IsTakenOver && !Entry.IsProtected && !Entry.IsMissing;
}
