using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 「当前子页面该显示哪些来源」的判定（D140）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>存在的唯一理由：这段谓词曾经被手写三遍，而其中一份是反的。</b>
/// <c>Apply</c>（列表）、<c>BuildSubtitle</c>（副标题计数）、<c>Apply</c> 里的
/// 扫描失败文案 —— 三处各写一次「筛选为空则全要，否则只要来源相同」。
/// 其中一份把「谓词为真 ⇒ 加入」改写成「谓词为真 ⇒ <c>continue</c>」时
/// **忘了取反谓词**，于是该显示的条目全被跳过、不该显示的全被摆上屏幕，
/// 四个子页面的内容混在一起（2026-10-02 用户实测，我引入的）。
/// </para>
/// <para>
/// 🔴 <b>这类错误静态检查抓不到、构建也抓不到。</b>签名里只有一个 bool，
/// 调用点写对了就编译通过，而写错了要真机点四个标签才看得见。
/// 所以它必须住在有测试工程的层，且必须有"每个来源各筛一次、
/// 断言留下的正好是它自己"那种用例 —— 不能只测一两个组合。
/// </para>
/// <para>
/// 🔴 <b>筛选为 <see langword="null"/> = 全部来源</b>，不是"什么都不匹配"：
/// 左侧父菜单「自启动项」（<c>items</c>）就走这一支，它要的是七个来源的合集。
/// </para>
/// </remarks>
public static class SourceFilterPolicy
{
    /// <summary>某来源的条目在当前筛选下是否该显示。</summary>
    /// <param name="entrySource">条目自己的来源。</param>
    /// <param name="filter">当前来源筛选；<see langword="null"/> 表示全部来源。</param>
    /// <returns>该显示为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 写成"要显示"的肯定式（<c>==</c> / <c>!=</c>），而不是"要不要跳过"的否定式。
    /// 调用点因此只能写成 <c>if (!IsVisible(...)) continue;</c> ——
    /// 取反留在**调用点**一眼可见的地方，而不是藏在谓词里等人去推。
    /// </remarks>
    public static bool IsVisible(StartupSource entrySource, StartupSource? filter)
        => filter is not { } kind || entrySource == kind;

    /// <summary>按当前筛选挑出该显示的条目（保持原顺序）。</summary>
    /// <param name="entries">候选条目。</param>
    /// <param name="filter">当前来源筛选；<see langword="null"/> 表示全部来源。</param>
    /// <returns>该显示的条目。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> 为 <see langword="null"/>。</exception>
    public static IEnumerable<StartupEntry> Visible(
        IReadOnlyList<StartupEntry> entries,
        StartupSource? filter)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries.Where(entry => IsVisible(entry.Source, filter));
    }
}