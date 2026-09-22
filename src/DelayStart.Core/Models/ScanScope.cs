namespace DelayStart.Core.Models;

/// <summary>
/// 「某个来源实例」的标识：来源类型 + 作用域。
/// </summary>
/// <remarks>
/// <para>
/// 存在的唯一理由：守卫的三份策略需要知道"哪些来源本次扫描失败了"，
/// 而失败是按<b>来源实例</b>（例如 HKLM 与 HKLM-Wow 是两个实例）而不是按来源类型记录的。
/// </para>
/// <para>
/// 管理端的 <c>ScanFailure</c> 落在 Management 层（Core 不能引用它），
/// 因此这里用最小投影类型承载同一对信息，策略层只依赖 Core。
/// </para>
/// </remarks>
/// <param name="Source">来源类型。</param>
/// <param name="Scope">作用域。</param>
public readonly record struct ScanScope(StartupSource Source, StartupScope Scope);
