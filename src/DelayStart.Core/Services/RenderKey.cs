using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 合成"这一屏显示的内容有没有变"的渲染键（D139）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>它的作用范围比当初以为的小</b>（2026-10-02 更正）：<c>ItemsPage</c> 与
/// <c>ItemsViewModel</c> 都是 <c>AddTransient</c>，每次导航都是全新 VM ⇒
/// <c>_renderedKey</c> 在每次进页的第一次 <c>Apply</c> 时都是 <see langword="null"/>
/// ⇒ 这个门在"切子页面"这条路上**一次都不会短路**。它在同一 VM 生命周期内仍然有用：
/// <c>LoadAsync</c> 先摆缓存、再拿重扫结果调 <c>ApplyIfChanged</c>，两份可能逐字节相同。
/// </para>
/// <para>
/// 🔴 <b>它没有修好"子页面条目混在一起"</b> —— 那条是 <c>Apply</c> 里的来源过滤谓词
/// 被取反漏了（D140）。本类的注释曾把两件事混为一谈，一并更正。
/// </para>
/// <para>
/// 那为什么还留着筛选这一维：<b>方向不能反</b>。宁可多画一遍（只是白做一遍，不出错），
/// 也不能让两个不同的筛选拼出同一个键 —— 那会真的漏刷，而漏刷只能真机才看得出来。
/// 万一哪天页面改成单例复用，这一位就是唯一挡得住"标签变了、列表没变"的东西。
/// </para>
/// </remarks>
public static class RenderKey
{
    /// <summary>合成"快照 + 来源筛选"的复合键。</summary>
    /// <param name="filter">当前来源筛选；<see langword="null"/> 表示全部来源。</param>
    /// <param name="fingerprint">整份快照的内容指纹。</param>
    /// <returns>复合键。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fingerprint"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// <c>ScanSnapshot.Fingerprint</c> 覆盖的是**整份快照**，而列表显示的是
    /// <c>快照 ∩ 来源筛选</c>（<see cref="SourceFilterPolicy"/>），所以键里必须带上筛选。
    /// <para>
    /// 来源用<b>数字</b>而不是 <c>ToString()</c>：枚举名一旦被改名，历史键就全对不上，
    /// 退化成每次都重画 —— 那属于上面说的"安全方向"。
    /// </para>
    /// </remarks>
    public static string For(StartupSource? filter, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        return filter is { } kind
            ? string.Concat((int)kind, ":", fingerprint)
            : string.Concat("*:", fingerprint);
    }
}