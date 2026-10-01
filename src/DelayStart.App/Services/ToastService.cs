namespace DelayStart.App.Services;

/// <summary>
/// 应用内右下角通知（2026-09-21 首批成功提示 / v0.6.1 扩成两行 + 可关闭 + 失败常驻）。
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 <b>没有</b>内置的应用内 toast；能弹系统通知的是 <c>AppNotification</c> 走 OS 通知中心。
/// 这里是在应用窗口内自绘一块，由 <see cref="MainWindow"/> 承接广播。广播只走一条消息通道，
/// 这样 ViewModel 里只管"我要展示这个文本"，不必关心它落在哪个窗口上，也不必关心 CLI 模式下没有窗口。
/// </para>
/// <para>
/// 🔴 <b>v0.6.1 的形状变化有原因</b>：单条消息装不下"启动失败"这件事 ——
/// 用户需要知道<b>哪一项</b>失败了、<b>为什么</b>失败，而原因往往是一整句带 Win32 错误号的文本。
/// 挤在一行里要么被截断、要么缩到看不清。所以拆成标题 + 详情两行。
/// </para>
/// <para>
/// 🔴 <b>失败不自动消失</b>：成功 3 秒够用户确认"点了有反应"，而失败的原因是用户下一步要照着做的
/// （装回程序 / 检查权限 / 看日志）。让它自己消失等于把唯一一条线索拿走，用户只能重试一次碰运气。
/// </para>
/// </remarks>
public sealed class ToastService
{
    /// <summary>成功提示的默认展示秒数。</summary>
    public const int SuccessSeconds = 3;

    /// <summary>
    /// 收到通知时的广播：标题 + 详情 + 是否自动消失。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>autoDismiss</c> 走事件参数而不是由订阅方自己判断：MainWindow 是唯一订阅者，
    /// 把"这条要不要自己收起来"的知识放在广播侧，订阅方就只管显示，漏不掉。
    /// </remarks>
    public event Action<ToastRequest>? Requested;

    /// <summary>一条成功提示（自动消失）。</summary>
    /// <param name="message">要展示的文本。</param>
    public void Show(string message)
        => Post(ToastRequest.Success(message, detail: null, autoDismiss: true));

    /// <summary>一条成功提示（标题 + 详情，自动消失）。</summary>
    /// <param name="title">标题行。</param>
    /// <param name="detail">详情行；<see langword="null"/> 或空则只显示标题。</param>
    public void ShowSuccess(string title, string? detail = null)
        => Post(ToastRequest.Success(title, detail, autoDismiss: true));

    /// <summary>
    /// 一条失败提示（<b>不自动消失</b>，必须由用户关闭）。
    /// </summary>
    /// <param name="title">标题行。</param>
    /// <param name="detail">详情行（原因）。</param>
    /// <remarks>
    /// 🔴 这里刻意<b>不</b>提供"失败但自动消失"的变体：失败提示一旦会自己走掉，
    /// 调用方就会图省事给失败也传 <c>true</c>，而那条失败原因正是用户唯一能照着做的线索。
    /// API 层面堵死比在文档里叮嘱可靠。
    /// </remarks>
    public void ShowError(string title, string detail)
        => Post(ToastRequest.Error(title, detail));

    /// <summary>广播一条通知（没有订阅者时静默忽略）。</summary>
    /// <param name="request">通知内容。</param>
    private void Post(ToastRequest request) => Requested?.Invoke(request);
}

/// <summary>一条应用内通知。</summary>
/// <param name="Title">标题行。</param>
/// <param name="Detail">详情行；<see langword="null"/> 或空则只显示标题。</param>
/// <param name="IsError">是否失败（决定配色与图标）。</param>
/// <param name="AutoDismiss">是否自动消失（失败恒为 <see langword="false"/>）。</param>
/// <remarks>
/// 🔴 只提供两个工厂方法、**不开放**公开构造：<c>AutoDismiss</c> 与 <c>IsError</c> 必须一致，
/// 而"失败但自动消失"是一种调用方迟早会写出来的组合。真要让它可表达，得先想清楚
/// 为什么失败提示会被允许自己消失 —— 那时候再改这个类型，而不是现在就留口子。
/// </remarks>
public sealed record ToastRequest(
    string Title,
    string? Detail,
    bool IsError,
    bool AutoDismiss)
{
    /// <summary>一条成功通知。</summary>
    /// <param name="title">标题行。</param>
    /// <param name="detail">详情行。</param>
    /// <param name="autoDismiss">是否自动消失。</param>
    /// <returns>通知内容。</returns>
    public static ToastRequest Success(string title, string? detail, bool autoDismiss)
        => new(title, detail, IsError: false, AutoDismiss: autoDismiss);

    /// <summary>一条失败通知（恒不自动消失）。</summary>
    /// <param name="title">标题行。</param>
    /// <param name="detail">详情行（原因）。</param>
    /// <returns>通知内容。</returns>
    public static ToastRequest Error(string title, string detail)
        => new(title, detail, IsError: true, AutoDismiss: false);

    /// <summary>详情行是否需要显示。</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}