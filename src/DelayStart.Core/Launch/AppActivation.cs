namespace DelayStart.Core.Launch;

/// <summary>
/// 管理端启动参数的**跨进程契约**（D74 / D79 / D82）。
/// </summary>
/// <remarks>
/// <para>
/// 谁在写这些字符串：调度端的托盘通知、守卫的系统通知、
/// <c>HKCU\Software\Classes\delaystart</c> 这个协议处理器的命令行，
/// 以及管理端**提权重拉自己**时追加的参数（D82）。
/// 谁在读：<c>DelayStart.App.Program</c>。
/// </para>
/// <para>
/// 🔴 放在 Core 而不是各写一遍字面量：这些值一旦漂移，症状是"点了通知没反应"——
/// 而"没反应"与"通知根本没发出来"在用户眼里一模一样，几乎无法从现场倒推。
/// 常量集中一处，改了编译期就能发现漏改。
/// </para>
/// </remarks>
public static class AppActivation
{
    /// <summary>唤起管理端并落到「运行日志」页（调度端通知点击，D18）。</summary>
    public const string GotoLogArgument = "--goto-log";

    /// <summary>唤起管理端并落到「自启动项」某处（守卫通知点击，D74）。</summary>
    public const string GotoStartupArgument = "--goto-startup";

    /// <summary>定位到失效条目所在位置（D77；并入「延时启动」页后等价于定位到该页）。</summary>
    public const string StaleArgument = "--stale";

    /// <summary>指定定位到哪个来源页，形如 <c>--source=registry</c>。</summary>
    public const string SourceArgumentPrefix = "--source=";

    /// <summary>
    /// 「本进程是本程序自己提权重拉起来的」标记（D82）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 唯一用途是**防重拉死循环**：入口那道门在未提权时会把命令原样重拉一遍，
    /// 万一同一个进程又被判成未提权（UAC 被禁用、令牌异常……），没有这个标记就会
    /// 无限弹 UAC。带上它之后再遇到"未提权 + 已尝试过"就只报错退出。
    /// </para>
    /// <para>
    /// 🔴 入口必须在把参数交给 <c>CliHost</c> **之前**把它过滤掉：
    /// 它以 <c>--</c> 开头，留在参数里会被当成未知子命令（退出码 2、GUI 起不来）。
    /// </para>
    /// </remarks>
    public const string ElevationAttemptArgument = "--elevation-attempted";

    /// <summary>协议处理器注册用的 scheme（<c>HKCU\Software\Classes\delaystart</c>）。</summary>
    public const string ProtocolScheme = "delaystart";

    /// <summary>通知里 <c>launch</c> 属性的前缀，形如 <c>delaystart://delay</c>。</summary>
    public const string ProtocolUriPrefix = "delaystart://";

    /// <summary>判断一个参数是不是"提权重拉"标记（入口与 CLI 分流都要用）。</summary>
    /// <param name="argument">命令行参数。</param>
    /// <returns>是标记时为 <see langword="true"/>。</returns>
    public static bool IsElevationAttempt(string? argument)
        => string.Equals(argument, ElevationAttemptArgument, StringComparison.OrdinalIgnoreCase);
}
