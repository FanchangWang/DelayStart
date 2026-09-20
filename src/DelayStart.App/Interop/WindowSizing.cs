using System.Runtime.InteropServices;

using Microsoft.UI.Windowing;

namespace DelayStart.App.Interop;

/// <summary>
/// 主窗口的默认宽高与最小尺寸（2026-09-20 用户批复，参考 PowerToys 的设置窗口）。
/// </summary>
/// <remarks>
/// <para>
/// PowerToys 的设置窗口在本项目关心的这一点上做了两件事
/// （<c>src/settings-ui/Settings.UI/SettingsXAML/MainWindow.xaml</c> + <c>Helpers/WindowHelper.cs</c>）：
/// </para>
/// <list type="number">
/// <item><description>XAML 上声明 <c>MinWidth/MinHeight="480"</c> —— 这两个属性由 WinUIEx 的
/// <c>WindowEx</c> 提供，微软原生 <c>Microsoft.UI.Xaml.Window</c> **没有**最小尺寸 API；
/// 本项目不引 WinUIEx，改为自己接 <c>WM_GETMINMAXINFO</c>（见 <see cref="EnforceMinimum"/>）。</description></item>
/// <item><description>把窗口位置与大小序列化进 <c>settings-placement.json</c>，下次启动还原
/// （<c>WindowHelper.SerializePlacement</c> / <c>DeserializePlacementOrDefault</c>）。
/// 尺寸持久化本轮**不做** —— 那是另一件事（会多一个落盘文件），需要单独立项。</description></item>
/// </list>
/// <para>
/// 默认尺寸按 **DIP** 取 1400×900：内容区限宽 1000（Round H 批复）+ 展开的导航窗格 ~320，
/// 1400 正好把 1000 宽的卡片摆在中间且左右留白对称；900 高能一屏放下设置页的全部卡片。
/// 🔴 必须按 DPI 换算成像素再交给 <c>AppWindow</c>：本机 3840×2160 缩放 150%，
/// 直接写 1400 会得到一个"只有半屏大"的窗口（<c>AppWindow</c> 的单位是物理像素）。
/// </para>
/// </remarks>
public static partial class WindowSizing
{
    /// <summary>首次打开的目标**客户区**尺寸（DIP）。</summary>
    private const int DesiredWidthDip = 1400;
    private const int DesiredHeightDip = 900;

    /// <summary>窗口最小尺寸（DIP）：再窄延时启动页的列就会互相挤，再矮列表只剩一两行。</summary>
    private const int MinWidthDip = 960;
    private const int MinHeightDip = 600;

    /// <summary>默认尺寸最多占工作区的比例：小屏 / 200% 缩放下不能铺满整屏。</summary>
    private const double MaxWorkAreaRatio = 0.92;

    /// <summary>
    /// 设置首次打开时的窗口尺寸并居中。
    /// </summary>
    /// <param name="appWindow">主窗口的 <c>AppWindow</c>。</param>
    /// <param name="hwnd">主窗口句柄（取 DPI 用）。</param>
    /// <remarks>
    /// 只在启动时调用一次：不记录、不还原用户调整过的尺寸 —— 与 PowerToys 的差别已在类备注里说明。
    /// </remarks>
    public static void ApplyInitialSize(AppWindow appWindow, nint hwnd)
    {
        ArgumentNullException.ThrowIfNull(appWindow);

        var scale = DpiScale(hwnd);

        // Nearest 而不是 Primary：窗口被记住在副屏打开时也要贴着实际所在的那块屏居中。
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        var width = Math.Min(Scale(DesiredWidthDip, scale), (int)(area.Width * MaxWorkAreaRatio));
        var height = Math.Min(Scale(DesiredHeightDip, scale), (int)(area.Height * MaxWorkAreaRatio));

        // 🔴 用 Client 尺寸而不是窗口尺寸：XAML 的 1400×900 指的是内容区，
        // 折算成"外框"还得加上标题栏高度 —— 两者在 Tall 标题栏下差 48 DIP。
        appWindow.ResizeClient(new Windows.Graphics.SizeInt32 { Width = width, Height = height });
        appWindow.Move(new Windows.Graphics.PointInt32
        {
            X = area.X + ((area.Width - width) / 2),
            Y = area.Y + ((area.Height - height) / 2),
        });
    }

    /// <summary>
    /// 给窗口加最小尺寸限制（原生 <c>WM_GETMINMAXINFO</c>）。
    /// </summary>
    /// <param name="hwnd">主窗口句柄；为 <c>0</c> 时不做任何事。</param>
    /// <remarks>
    /// <para>
    /// 子类化方式与 <see cref="FileDropReceiver"/> 完全一致（换 <c>GWLP_WNDPROC</c>、
    /// 其余消息原样转发、原过程按 HWND 记账）。两者可以共存：后子类化的那个把先前的
    /// 过程当成自己的"原过程"，链条依次转发，不会互相吃掉消息。
    /// </para>
    /// <para>
    /// 最小尺寸在**每次收到消息时**按当前 DPI 重算：把窗口拖到另一块缩放比不同的显示器上，
    /// 限制会跟着变（<c>GetDpiForWindow</c> 返回的是窗口当前所在显示器的 DPI）。
    /// </para>
    /// </remarks>
    public static void EnforceMinimum(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        lock (Gate)
        {
            if (OriginalProcs.ContainsKey(hwnd))
            {
                return;
            }

            var current = GetWindowLongPtrW(hwnd, GwlpWndproc);
            if (current == WndProcPointer || current == 0)
            {
                return;
            }

            OriginalProcs[hwnd] = current;
            _ = SetWindowLongPtrW(hwnd, GwlpWndproc, WndProcPointer);
        }
    }

    private const uint WmGetMinMaxInfo = 0x0024;
    private const int GwlpWndproc = -4;

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>窗口过程委托 —— 必须**常驻**，被 GC 回收 = 原生回调进野指针。</summary>
    private static readonly WndProcDelegate WndProcInstance = WindowProc;

    /// <summary>委托的原生函数指针（一次性取好）。</summary>
    private static readonly nint WndProcPointer = Marshal.GetFunctionPointerForDelegate(WndProcInstance);

    /// <summary>已被子类化窗口的原过程，按 HWND 记账（子类化幂等 + 只转发不卸载）。</summary>
    private static readonly Dictionary<nint, nint> OriginalProcs = new();

    private static readonly object Gate = new();

    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmGetMinMaxInfo)
        {
            ApplyMinimumTrackSize(hwnd, lParam);
        }

        lock (Gate)
        {
            if (OriginalProcs.TryGetValue(hwnd, out var original) && original != 0)
            {
                return CallWindowProcW(original, hwnd, message, wParam, lParam);
            }
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary><c>MINMAXINFO</c> 的完整尺寸：5 个 <c>POINT</c>，x64/x86 同宽（各 4 字节）。</summary>
    private const int MinMaxInfoSize = 40;

    /// <summary><c>ptMinTrackSize</c> 的位置：前面是 ptReserved / ptMaxSize / ptMaxPosition 三个 POINT。</summary>
    private const int MinTrackSizeOffset = 3 * 8;

    /// <summary>一个 <c>POINT</c>（两个 <c>int</c>）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        /// <summary>X 分量。</summary>
        public int X;

        /// <summary>Y 分量。</summary>
        public int Y;
    }

    /// <summary>
    /// <c>MINMAXINFO</c> 里**本类要写的两个字段**。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意用显式布局只声明 <c>ptMinTrackSize</c> 一项，而不是把 5 个 POINT 全写出来：
    /// 全写出来会有 4 个"只由系统填、本类从不赋值"的字段，在
    /// <c>TreatWarningsAsErrors</c> 下直接编译失败（CS0649），而给它们赋值又纯属噪声。
    /// 偏移 24 不是手算魔法数：MSDN 的成员顺序就是 ptReserved → ptMaxSize → ptMaxPosition
    /// → ptMinTrackSize，前三个各是一个 POINT（见 <see cref="MinTrackSizeOffset"/>）。
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = MinMaxInfoSize)]
    private struct MinMaxInfo
    {
        /// <summary><c>ptMinTrackSize</c> —— 用户拖动边框时允许的最小尺寸。</summary>
        [FieldOffset(MinTrackSizeOffset)]
        public Win32Point MinTrackSize;
    }

    /// <summary>把当前显示器 DPI 下的最小尺寸写回 <c>lParam</c> 指向的 <c>MINMAXINFO</c>。</summary>
    private static unsafe void ApplyMinimumTrackSize(nint hwnd, nint lParam)
    {
        if (lParam == 0)
        {
            return;
        }

        var scale = DpiScale(hwnd);
        var info = (MinMaxInfo*)lParam;
        info->MinTrackSize.X = Scale(MinWidthDip, scale);
        info->MinTrackSize.Y = Scale(MinHeightDip, scale);
    }

    /// <summary>窗口当前所在显示器的缩放系数（96 DPI = 1.0）。</summary>
    private static double DpiScale(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>DIP → 物理像素（四舍五入，避免累计误差让最小尺寸差 1 像素来回抖）。</summary>
    private static int Scale(int dip, double scale)
        => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint CallWindowProcW(nint previousProc, nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
}
