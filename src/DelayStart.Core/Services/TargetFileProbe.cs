namespace DelayStart.Core.Services;

/// <summary>
/// 「条目的目标文件还在不在」的**唯一**判据（FR-1.10 / E3）。
/// </summary>
/// <remarks>
/// <para>
/// 三处扫描来源（注册表 / 启动文件夹 / 计划任务）原先各自内联写了一遍同一条规则，
/// 守卫的手动条目判定又需要第四份 —— 集中到这里是因为这条规则**很容易写歪**，
/// 而歪掉的代价不对称：漏判只是少标记一条，误判会让用户看到一堆其实好好的程序
/// 被打上"已失效"，进而把它们删掉。
/// </para>
/// <para>
/// 两条排除（都不是"文件确实没了"，而是"这个字符串根本不是文件路径"）：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>非全限定路径</b>：注册表里合法地存在 <c>OneDrive</c>、<c>SecurityHealth</c>
/// 这类裸命令名（由 <c>PATH</c> 解析），对它们调 <see cref="File.Exists(string)"/>
/// 必然为 <see langword="false"/>；手动条目也可能是相对路径或裸文件名。
/// </description></item>
/// <item><description>
/// <b>UWP 解析名</b>（<c>shell:AppsFolder\&lt;AUMID&gt;</c>）与裸 AUMID：
/// 它们由外壳解析，与文件系统无关（D41 真机教训 —— 当年就是拿裸 AUMID 去判
/// <c>File.Exists</c>，把 UWP 全判成了"目标文件不存在"）。
/// </description></item>
/// </list>
/// <para>
/// 🔴 刻意**不**处理 UNC / 网络盘：那类路径不可达时同样返回
/// <see langword="false"/>，会被判成"已失效"。这是已知的假阳性方向 ——
/// 现状与三个扫描来源一致（它们也不区分），要改必须四处一起改并想清楚
/// "暂时不可达"与"确实不存在"如何区分，不适合在这里顺手做。
/// </para>
/// </remarks>
public static class TargetFileProbe
{
    /// <summary>判断目标是否已不存在（= 已失效）。</summary>
    /// <param name="path">目标路径；空值、非全限定路径、UWP 解析名一律返回 <see langword="false"/>。</param>
    /// <returns>目标文件不存在时为 <see langword="true"/>。</returns>
    public static bool IsMissing(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (UwpParsingName.IsParsingName(path))
        {
            return false;
        }

        return Path.IsPathFullyQualified(path) && !File.Exists(path);
    }
}
