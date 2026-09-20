using System.Runtime.InteropServices;

namespace DelayStart.Scheduler;

/// <summary>
/// 调度端用到的全部 Win32 API（D24=B：纯 Win32，零 UI 框架、零 COM）。
/// </summary>
/// <remarks>
/// <para>
/// 窗口过程统一走 <see cref="WndProcThunk"/>：注册窗口类时把这个静态入口交给系统，
/// 系统回调后从 <c>GWLP_USERDATA</c> 取回托管实例（<see cref="IMessageHandler"/>）。
/// 这是 AOT 下"类 → 窗口"的标准桥接方式，不涉及任何反射。
/// </para>
/// <para>
/// 消息常量只定义用到的；额外取值见 <c>WinUser.h</c>。
/// </para>
/// </remarks>
internal static unsafe partial class NativeMethods
{
    // ---- 常量 ----

    public const uint WmDestroy = 0x0002;
    public const uint WmPaint = 0x000F;
    public const uint WmTimer = 0x0113;
    public const uint WmActivate = 0x0006;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmEraseBkgnd = 0x0014;
    public const ushort WaInactive = 0;

    /// <summary>托盘回调消息基址（<c>WM_APP</c> 段，应用私有）。</summary>
    public const uint WmTrayCallback = 0x8000;

    /// <summary>Shell_NotifyIcon 返回的鼠标动作：左键抬起（WM_LBUTTONUP）。</summary>
    public const uint NimLup = 0x0202;

    /// <summary>气泡通知被用户点击（NIN_BALLOONUSERCLICK）。</summary>
    public const uint NinBalloonUserClick = 0x0405;

    public const uint NimAdd = 0x00000000;
    public const uint NimModify = 0x00000001;
    public const uint NimDelete = 0x00000002;

    public const uint NifMessage = 0x00000001;
    public const uint NifIcon = 0x00000002;
    public const uint NifTip = 0x00000004;
    public const uint NifInfo = 0x00000010;

    public const uint NifRealtime = 0x00000040;

    public const uint WsPopup = 0x80000000;
    public const uint WsVisible = 0x10000000;
    public const uint WsExToolWindow = 0x00000080;
    public const uint WsExTopmost = 0x00000008;
    public const uint WsExNoActivate = 0x08000000;

    public const uint MonitorDefaulttonearest = 2;

    public const int SwHide = 0;
    public const int SwShowNoActivate = 4;

    public const nint IdiApplication = 32512;

    public const int GwlpUserdata = -21;

    public const int LrDefaultsize = 0x00000040;

    // ---- 托管 / 原生桥 ----

    /// <summary>窗口消息的处理者。托盘窗口与弹出面板都实现它。</summary>
    internal interface IMessageHandler
    {
        /// <summary>处理一条窗口消息。返回非 0 表示已处理、不走 <c>DefWindowProc</c>。</summary>
        nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    }

    // x64 只有一种调用约定，无需显式 CallConvStdcall。
    [UnmanagedCallersOnly]
    private static nint WndProcThunk(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        var handler = GetHandler(hwnd);
        if (handler is null)
        {
            // WM_NCCREATE 之前（WM_GETMINMAXINFO 等）没有实例可路由。
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        var result = handler.HandleMessage(hwnd, message, wParam, lParam);
        return result != 0 ? result : DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static IMessageHandler? GetHandler(nint hwnd)
    {
        var handle = GetWindowLongPtrW(hwnd, GwlpUserdata);
        return handle != 0 ? GCHandle.FromIntPtr(handle).Target as IMessageHandler : null;
    }

    /// <summary>把托管实例挂到窗口上（在 <c>WM_NCCREATE</c> 时调用一次）。</summary>
    public static void AttachHandler(nint hwnd, IMessageHandler handler)
    {
        var handle = GCHandle.Alloc(handler);
        SetWindowLongPtrW(hwnd, GwlpUserdata, GCHandle.ToIntPtr(handle));
    }

    /// <summary>创建一个窗口类并注册。进程内每个类名只注册一次，由调用方保证。</summary>
    public static bool RegisterClass(string className, nint backgroundBrush)
    {
        var classEx = new WndClassExW
        {
            CbSize = (uint)sizeof(WndClassExW),
            Style = 0,
            LpfnWndProc = &WndProcThunk,
            HInstance = GetModuleHandle(),
            HCursor = LoadCursorW(0, 32512), // IDC_ARROW
            HbrBackground = backgroundBrush,
        };

        fixed (char* classNamePointer = className)
        {
            classEx.LpszClassName = classNamePointer;
            return RegisterClassExW(ref classEx) != 0;
        }
    }

    // ---- 结构 ----

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassExW
    {
        public uint CbSize;
        public uint Style;
        public delegate* unmanaged<nint, uint, nuint, nint, nint> LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public nint LpszMenuName;
        public char* LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyIconDataW
    {
        public uint CbSize;
        public nint HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public nint HIcon;
        public fixed char SzTip[128];
        public uint DwState;
        public uint DwStateMask;
        public fixed char SzInfo[256];
        public uint UVersion;
        public fixed char SzInfoTitle[64];
        public uint DwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfoW
    {
        public uint CbSize;
        public Rect Monitor;
        public Rect WorkArea;
        public uint DwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint Hdc;
        public int FErase; // BOOL，保持 blittable（禁用运行时封送）
        public Rect RcPaint;
        public int FRestore;
        public int FIncUpdate;
        public fixed byte RgbReserved[32];
    }

    // ---- P/Invoke ----

    // 🔴 GetModuleHandleW 在 kernel32.dll，不在 user32.dll（2026-09-20 真机踩坑：
    //    托盘恒显示后首次踩到这条路径，EntryPointNotFoundException 直接闪退）。
    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(nint moduleName);

    /// <summary>本进程模块句柄（注册窗口类用）。</summary>
    public static nint GetModuleHandle() => GetModuleHandleW(0);

    [LibraryImport("user32.dll")]
    public static partial nint LoadCursorW(nint instance, nint cursorName);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(ref WndClassExW windowClass);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint newLong);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfoW info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(nint hwnd, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hwnd, ref PaintStruct paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, ref PaintStruct paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial nint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostQuitMessage(int exitCode);

    [StructLayout(LayoutKind.Sequential)]
    public struct WinMessage
    {
        public nint Hwnd;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Pt;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out WinMessage message, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref WinMessage message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref WinMessage message);

    /// <summary>标准消息循环。返回退出码（<c>WM_QUIT</c> 的 wParam）。</summary>
    public static int RunMessageLoop()
    {
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessageW(ref message);
        }

        return (int)0;
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIconW(uint message, ref NotifyIconDataW data);

    // GDI —— 面板自绘用

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint objectHandle);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FillRect(nint hdc, ref Rect rect, nint brush);

    [LibraryImport("gdi32.dll", EntryPoint = "CreatePen")]
    public static partial nint CreatePen(int style, int width, uint color);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint objectHandle);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Ellipse(nint hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Rectangle(nint hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetBkMode(nint hdc, int transparent);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawTextW(
        nint hdc,
        string text,
        int length,
        ref Rect rect,
        uint format);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFontW(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeout,
        uint charset,
        uint outPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveToEx(nint hdc, int x, int y, nint previous);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LineTo(nint hdc, int x, int y);

    /// <summary>从内存中的 ICO 条目创建图标（用于嵌入资源加载）。</summary>
    [LibraryImport("user32.dll")]
    public static partial nint CreateIconFromResourceEx(
        byte* bits,
        uint bitsSize,
        [MarshalAs(UnmanagedType.Bool)] bool icon,
        int version,
        int desiredWidth,
        int desiredHeight,
        uint flags);
}
