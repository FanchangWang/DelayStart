using System.Runtime.InteropServices;

namespace DelayStart.App.Interop;

/// <summary>
/// 经典 <c>WM_DROPFILES</c> 文件拖放接收器（2026-09-19 批复 4：提权进程接收资源管理器拖放）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **为什么放弃 XAML 拖放（DragOver / Drop）**：WinUI 3 的 XAML 拖放走 OLE
/// （<c>DoDragDrop</c> + 跨进程封送 IDropTarget），提权（高完整性）进程里这条链路
/// 被 UIPI 拦死 —— 即便按微软文档放行 WM_DROPFILES / WM_COPYDATA / WM_COPYGLOBALDATA
/// 三条消息，封送的 <c>GetObject</c> 数据交换仍会在 <c>DragOver</c> 阶段失败，
/// 表现为鼠标恒为 🚫（2026-09-19 用户三轮实测：主窗口、弹层、进程级放行全试过，均无效）。
/// </para>
/// <para>
/// 可靠路径是**经典拖放**：对窗口调 <c>DragAcceptFiles</c>（WS_EX_ACCEPTFILES）。
/// 资源管理器的拖放源在目标窗口上找不到可用的 OLE 放置目标时，会在松手时直接
/// <c>PostMessage(WM_DROPFILES)</c> —— 不涉及任何跨进程 COM 封送，UIPI 白名单
/// （本进程已放行）足以放行这一条消息。接收靠**子类化**窗口过程：
/// <c>SetWindowLongPtrW(GWLP_WNDPROC)</c> 换成本类的过程，其余消息全部原样转发。
/// </para>
/// <para>
/// WinUI 的内容在**子 HWND**（DesktopChildSiteBridge / 弹层宿主）里，拖放落点按
/// 光标下的窗口算 —— 所以要对主窗口 + 当前全部子窗口逐个启用；弹窗打开后再跑一次
/// （此时弹层 HWND 已创建）。子类化幂等：已在册的窗口直接跳过。
/// </para>
/// </remarks>
public static partial class FileDropReceiver
{
    private const uint WmDropfiles = 0x0233;
    private const int GwlpWndproc = -4;

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>拖放文件落下的回调。参数是拖入的完整路径列表（至少一项）。</summary>
    public static event Action<string[]>? FilesDropped;

    /// <summary>窗口过程委托 —— 必须**常驻**，被 GC 回收 = 原生回调进野指针。</summary>
    private static readonly WndProcDelegate WndProcInstance = WindowProc;

    /// <summary>委托的原生函数指针（一次性取好）。</summary>
    private static readonly nint WndProcPointer = Marshal.GetFunctionPointerForDelegate(WndProcInstance);

    /// <summary>已被子类化窗口的原过程，按 HWND 记录（子类化幂等 + 只转发不卸载）。</summary>
    private static readonly Dictionary<nint, nint> OriginalProcs = new();

    private static readonly object Gate = new();

    /// <summary>
    /// 对窗口及其**当前**全部子窗口启用文件拖放（DragAcceptFiles + 子类化）。
    /// </summary>
    /// <param name="rootHwnd">主窗口句柄；为 <c>0</c> 时不做任何事。</param>
    /// <remarks>
    /// 幂等，可反复调用 —— 弹窗打开 / 窗口激活后再跑一遍，覆盖新创建的弹层 HWND。
    /// </remarks>
    public static void EnableTree(nint rootHwnd)
    {
        if (rootHwnd == 0)
        {
            return;
        }

        EnableSingle(rootHwnd);
        _ = EnumChildWindows(rootHwnd, OnChildWindow, 0);
    }

    private static bool OnChildWindow(nint hwnd, nint lParam)
    {
        EnableSingle(hwnd);
        return true;
    }

    private static void EnableSingle(nint hwnd)
    {
        DragAcceptFiles(hwnd, true);

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

    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmDropfiles)
        {
            try
            {
                HandleDrop(wParam);
            }
            catch
            {
                // 拖放是增强路径：解析失败绝不能让异常沿原生窗口过程冒出去。
            }
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

    /// <summary>从 HDROP 取出全部文件路径并广播。</summary>
    private static void HandleDrop(nint hDrop)
    {
        var count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        if (count == 0)
        {
            _ = DragFinish(hDrop);
            return;
        }

        var paths = new List<string>((int)count);
        for (var index = 0u; index < count; index++)
        {
            var length = DragQueryFileW(hDrop, index, null, 0);
            if (length <= 0)
            {
                continue;
            }

            var buffer = new char[length + 1];
            _ = DragQueryFileW(hDrop, index, buffer, (uint)buffer.Length);
            var end = Array.IndexOf(buffer, '\0');
            paths.Add(new string(buffer, 0, end < 0 ? buffer.Length : end));
        }

        _ = DragFinish(hDrop);

        if (paths.Count > 0)
        {
            FilesDropped?.Invoke([.. paths]);
        }
    }

    private delegate bool EnumChildWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumChildWindows(nint hwndParent, EnumChildWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint CallWindowProcW(nint previousProc, nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DragAcceptFiles(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint DragQueryFileW(nint hDrop, uint fileIndex, char[]? destination, uint bufferChars);

    [LibraryImport("shell32.dll")]
    private static partial nint DragFinish(nint hDrop);
}
