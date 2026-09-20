namespace DelayStart.App.Services;

/// <summary>
/// 应用内右下角通知（2026-09-21 批复：成功类提示自动消除）。
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 **没有**内置「窗口内右下角 toast」控件（系统 <c>AppNotification</c> 是弹 OS 通知，
/// 不在窗口内），所以自绘：<see cref="MainWindow"/> 在内容层右下角挂一块通知面板，
/// 本服务只负责把消息广播过去 —— ViewModel 不依赖窗口实例，测试与 CLI 均不受影响。
/// </para>
/// <para>
/// 单例：通知面板全窗口只有一块，广播给谁都必须是同一个目标。
/// </para>
/// </remarks>
public sealed class ToastService
{
    /// <summary>收到消息时由通知面板订阅显示；参数为要展示的文本。</summary>
    public event Action<string>? Requested;

    /// <summary>弹一条右下角通知（面板侧自动定时消失）。</summary>
    /// <param name="message">要展示的文本。</param>
    public void Show(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Requested?.Invoke(message);
    }
}
