using Microsoft.UI.Xaml;

namespace DelayStart.App.Services;

/// <summary>
/// 主窗口的原生句柄，供需要 HWND 的 WinRT 互操作使用（文件选择器 / 拖放）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 unpackaged 的 WinUI 3 里，**文件选择器不知道自己属于哪个窗口**。
/// 不调 <c>InitializeWithWindow</c> 就在运行时抛异常；而 <c>ContentDialog</c>
/// 本身拿不到 HWND（它只是窗口里的一个 XAML 弹出层），只能由外向内传。
/// </para>
/// <para>
/// 做成容器里的一个单例而不是 <c>App.Current</c> 上的静态属性，是为了让"谁需要窗口句柄"
/// 出现在构造函数签名里 —— 静态定位器会让这个依赖消失
/// （同 <c>App</c> 里注入 <see cref="IServiceProvider"/> 的理由）。
/// </para>
/// </remarks>
public sealed class WindowHandleProvider
{
    private nint _handle;

    /// <summary>主窗口句柄；窗口尚未创建时为 <c>0</c>。</summary>
    public nint Handle => _handle;

    /// <summary>登记主窗口句柄。</summary>
    /// <param name="handle">窗口的原生句柄。</param>
    /// <remarks>
    /// 重复登记以最后一次为准。主窗口在本程序里只有一个，
    /// 刻意不做"只允许设一次"的校验 —— 窗口重建（将来若有）时应当能覆盖。
    /// </remarks>
    public void Set(nint handle) => _handle = handle;

    /// <summary>取一个已激活窗口的句柄。</summary>
    /// <param name="window">WinUI 3 窗口。</param>
    /// <returns>该窗口的原生句柄。</returns>
    public static nint FromWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        return WinRT.Interop.WindowNative.GetWindowHandle(window);
    }
}
