using System.Globalization;

using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 稳定主键生成（机制 1）。
/// </summary>
/// <remarks>
/// <para>
/// 主键回答的唯一问题是"**扫描出来的这一项，是不是已经被我接管了**"。
/// 因此它必须满足：同一项在任意一次重新扫描后得到同一个主键，即使它的显示名、
/// 目标路径发生了变化。
/// </para>
/// <para>
/// 🔴 **禁止**用 <c>(Name, Source)</c> 二元组做这个判断 —— 不同位置的同名项会被
/// 互相误判（坑 6）。主键取"来源 + 作用域 + 来源内原始键"这个三元组，
/// 三个分量都由系统本身决定，不会因用户改名而漂移。
/// </para>
/// </remarks>
public static class ItemKeyBuilder
{
    private const string Separator = ":";

    /// <summary>
    /// 为系统来源的条目生成稳定主键，格式 <c>{source}:{scope}:{sourceKey}</c>。
    /// </summary>
    /// <param name="source">来源类型。</param>
    /// <param name="scope">作用域。</param>
    /// <param name="sourceKey">来源内的原始键：注册表值名 / 文件名 / 任务路径 / TaskId。</param>
    /// <returns>规范化后的稳定主键。</returns>
    /// <remarks>
    /// <paramref name="sourceKey"/> 会做 <c>Trim</c> + 小写规范化，
    /// 因此注册表里 <c>Weixin</c> 与 <c>weixin</c> 视为同一项（系统本身也不区分）。
    /// </remarks>
    public static string Build(StartupSource source, StartupScope scope, string sourceKey)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);

        var normalizedKey = sourceKey.Trim().ToLowerInvariant();
        return $"{SourceToken(source)}{Separator}{ScopeToken(scope)}{Separator}{normalizedKey}";
    }

    /// <summary>
    /// 为手动添加的条目生成主键。
    /// </summary>
    /// <remarks>
    /// 手动条目在系统中没有任何可锚定的东西，重复添加同一个 exe 应当得到两个独立条目
    /// （用户可能就是想让它启动两次），因此主键随机生成。三段的形状与系统条目保持一致，
    /// 免得下游解析代码要处理两种格式。
    /// </remarks>
    /// <returns>形如 <c>manual:none:&lt;guid&gt;</c> 的稳定主键。</returns>
    public static string ForManual() => ForManual(Guid.NewGuid());

    /// <summary>用手动条目已有 ID 重建主键，便于往返与测试。</summary>
    /// <param name="id">已有 ID。</param>
    /// <returns>形如 <c>manual:none:&lt;guid&gt;</c> 的稳定主键。</returns>
    public static string ForManual(Guid id)
        => Build(StartupSource.Manual, StartupScope.None, id.ToString("N", CultureInfo.InvariantCulture));

    /// <summary>取来源类型的稳定令牌。改动它会破坏所有既有配置的匹配，不得随意变更。</summary>
    /// <param name="source">来源类型。</param>
    /// <returns>小写令牌。</returns>
    public static string SourceToken(StartupSource source) => source switch
    {
        StartupSource.Registry => "registry",
        StartupSource.StartupFolder => "startupfolder",
        StartupSource.ScheduledTask => "scheduledtask",
        StartupSource.Uwp => "uwp",
        StartupSource.Manual => "manual",
        _ => "unknown",
    };

    /// <summary>取作用域的稳定令牌。改动它会破坏所有既有配置的匹配，不得随意变更。</summary>
    /// <param name="scope">作用域。</param>
    /// <returns>小写令牌。</returns>
    public static string ScopeToken(StartupScope scope) => scope switch
    {
        StartupScope.None => "none",
        StartupScope.Hkcu => "hkcu",
        StartupScope.Hklm => "hklm",
        StartupScope.HklmWow => "hklmwow",
        StartupScope.UserFolder => "userfolder",
        StartupScope.SystemFolder => "systemfolder",
        _ => "none",
    };
}
