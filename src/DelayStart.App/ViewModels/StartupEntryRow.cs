using DelayStart.App.Services;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
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
    /// <remarks>
    /// 🔴 原先这里还有一个 <c>isNew</c> 参数与 <c>IsNew</c> 属性（「新增」徽标，A2.5 / A2.6），
    ///   已按用户决策删除：它靠 <c>ScanCacheService</c> 内存里的上一份快照求差，
    ///   **只在本次运行内有效** —— 关掉管理端再打开，判定基准就没了；
    ///   而用户对"新增"的心智模型几乎必然是**跨会话**的。
    /// </remarks>
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

    /// <summary>
    /// 两条扫描结果渲染出来的行是否**完全一样**（D138）。
    /// </summary>
    /// <param name="leftEntry">旧条目。</param>
    /// <param name="leftPixels">旧图标像素（可为 <see langword="null"/>）。</param>
    /// <param name="rightEntry">新条目。</param>
    /// <param name="rightPixels">新图标像素（可为 <see langword="null"/>）。</param>
    /// <returns>界面上不会有任何可见差异为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 存在的唯一理由：<c>SyncInPlace</c> 用<b>引用相等</b>判断"这一行没变"，
    /// 而 <see cref="ItemsViewModel.Apply"/> 原先每次都 <c>new</c> 出全部行 ——
    /// 引用永不相等，于是差量同步退化成"每一行都 Replace"，
    /// F10.3 那套按 Id 增删移动的机制等于白做（2026-10-02 用户实测：刷新仍是全量）。
    /// <para>
    /// 🔴 **只比界面上真正显示或影响可点性的字段**，不比"反射全字段"——
    /// 那会把一个只影响日志的字段也变成"变了"，等于没复用。
    /// </para>
    /// <para>
    /// 🔴 图标按<b>引用</b>比：<c>IconProvider</c> 有缓存，图标没换时返回的就是同一个
    /// <see cref="IconPixels"/> 实例；而它里面是 <c>byte[]</c>，逐字节比一遍的开销
 /// 比重新提取一次图标还贵。引用不同就当作「变了」，最坏结果只是多刷一行。
    /// </para>
    /// </remarks>
    public static bool SameContent(
        StartupEntry leftEntry,
        IconPixels? leftPixels,
        StartupEntry rightEntry,
        IconPixels? rightPixels)
    {
        return string.Equals(leftEntry.Id, rightEntry.Id, StringComparison.Ordinal)
            && string.Equals(leftEntry.Name, rightEntry.Name, StringComparison.Ordinal)
            && string.Equals(leftEntry.Path, rightEntry.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftEntry.ExecutablePath, rightEntry.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(leftEntry.Arguments, rightEntry.Arguments, StringComparison.Ordinal)
            && string.Equals(leftEntry.SourceDetail, rightEntry.SourceDetail, StringComparison.Ordinal)
            && leftEntry.IsEnabled == rightEntry.IsEnabled
            && leftEntry.IsProtected == rightEntry.IsProtected
            && leftEntry.IsMissing == rightEntry.IsMissing
            && leftEntry.IsTakenOver == rightEntry.IsTakenOver
            && ReferenceEquals(leftPixels, rightPixels);
    }

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
