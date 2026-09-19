using DelayStart.Core.Models;

namespace DelayStart.Management.Abstractions;

/// <summary>
/// 快捷方式（<c>.lnk</c>）的目标解析（FR-1.7 / D9）。
/// </summary>
/// <remarks>
/// <para>
/// 没有它，启动文件夹里的条目只能显示成 <c>WeChat.lnk</c>，而用户认的是"微信"、
/// 系统认的是 <c>Weixin.exe</c> —— 三方各说各话，列表可读性会很差（demo 就没做这一步）。
/// </para>
/// <para>
/// 抽成接口是为了让 <c>StartupFolderSource</c> 能在单元测试里跑起来：
/// 真实实现走 COM（<c>IShellLinkW</c> + <c>IPersistFile</c>），测试里换成假实现即可。
/// </para>
/// </remarks>
public interface IShellLinkResolver
{
    /// <summary>
    /// 解析快捷方式指向的目标与参数。
    /// </summary>
    /// <param name="shortcutPath">快捷方式文件的完整路径。</param>
    /// <returns>
    /// 解析成功时返回目标路径与参数；文件不存在、不是合法快捷方式或 COM 调用失败时返回
    /// <see langword="null"/>（调用方据此回退为"显示 .lnk 本身"，**不得**中断扫描，FR-1.4）。
    /// </returns>
    ParsedCommandLine? Resolve(string shortcutPath);
}
