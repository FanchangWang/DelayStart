using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 合成「界面上真正显示了什么」的渲染键（D139）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么光比指纹不够</b>：<c>ScanSnapshot.Fingerprint</c> 覆盖的是**整份快照**
/// （七个来源全在一起），而列表真正显示的内容是 <c>快照 ∩ 来源筛选</c>。
/// 只比指纹就漏掉了筛选这一维 —— 切子页面时系统毫无变化，指纹逐字节相同，
/// 于是"这一屏没变"的判断成立，列表却还停在上一个来源（2026-10-02 用户实测：
/// 四个子页面的条目混在一起）。
/// </para>
/// <para>
/// 🔴 <b>方向不能反</b>：宁可多画一遍（只是白做一遍，���不出错），
/// 也不能让两个不同的筛选拼出同一个键 —— 那会真的漏刷，用户看到的还是错的列表。
/// </para>
/// </remarks>
public static class RenderKey
{
    /// <summary>合成「快照 + 来源筛选」的复合键。</summary>
    /// <param name="filter">当前来源筛选；<see langword="null"/> 表示全部来源。</param>
    /// <param name="fingerprint">整份快照的内容指纹。</param>
    /// <returns>复合键。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fingerprint"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 来源用<b>数字</b>而不是 <c>ToString()</c>：枚举名一旦被改名，历史键就全对不上，
    /// 退化成每次都重画 —— 那属于上面说的"安全方向"。
    /// </remarks>
    public static string For(StartupSource? filter, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        return filter is { } kind
            ? string.Concat((int)kind, ":", fingerprint)
            : string.Concat("*:", fingerprint);
    }
}