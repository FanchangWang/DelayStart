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
    public const uint WmLButtonUp = 0x0202;
    public const uint WmEraseBkgnd = 0x0014;
    public const ushort WaInactive = 0;

    /// <summary>鼠标在窗口内移动（面板用它首帧判定"指针已进入"）。</summary>
    public const uint WmMouseMove = 0x0200;

    /// <summary>鼠标离开窗口（由 <c>TrackMouseEvent(TME_LEAVE)</c> 订阅后才会收到）。</summary>
    public const uint WmMouseLeave = 0x02A3;

    /// <summary>置顶 / 取消置顶的 <c>SetWindowPos</c> 插入位置。</summary>
    public const nint HwndTopmost = -1;

    /// <summary>取消置顶。</summary>
    public const nint HwndNotopmost = -2;

    /// <summary>TrackMouseEvent 只订阅"离开"通知。</summary>
    public const uint TmeLeave = 0x00000002;

    /// <summary>托盘回调消息基址（<c>WM_APP</c> 段，应用私有）。</summary>
    public const uint WmTrayCallback = 0x8000;

    /// <summary>Shell_NotifyIcon 返回的鼠标动作：左键抬起（WM_LBUTTONUP）。</summary>
    public const uint NimLup = 0x0202;

    /// <summary>Shell_NotifyIcon 返回的鼠标动作：右键抬起（WM_RBUTTONUP）→ 托盘右键菜单入口。</summary>
    public const uint NimRup = 0x0205;

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

    // ⚠️ 刻意不声明 LR_DEFAULTSIZE：CreateIconFromResourceEx 带上它会把条目放大到
    //    SM_CXICON(32)，再被托盘缩回 16 —— 白搭两次重采样。见 IconResources.TrayIconSize。

    // ---- 托管 / 原生桥 ----

    /// <summary>窗口消息的处理者。托盘窗口与弹出面板都实现它。</summary>
    internal interface IMessageHandler
    {
        /// <summary>处理一条窗口消息。返回非 0 表示已处理、不走 <c>DefWindowProc</c>。</summary>
        nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    }

    /// <summary>窗口回调内未捕获异常的记录钩子（引擎启动时接入 FileLogger）。
    /// 🔴 异常绝不能冲出 <see cref="WndProcThunk"/>：AOT 下托管异常穿越原生帧无法展开，
    /// 运行时直接 fail-fast（0xC0000409）整个进程闪退 —— 只能就地吞掉并留日志。</summary>
    public static Action<Exception>? MessageCallbackExceptionLogger { get; set; }

    // x64 只有一种调用约定，无需显式 CallConvStdcall。
    [UnmanagedCallersOnly]
    private static nint WndProcThunk(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
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
        catch (Exception ex)
        {
            // 兜底防闪退：P/Invoke 签名错误、空引用等在此落地为一条日志，而不是整个进程消失。
            try { MessageCallbackExceptionLogger?.Invoke(ex); } catch { /* 日志失败不能再抛 */ }
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
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

    /// <summary>TRACKMOUSEEVENT（<c>TrackMouseEvent</c> 入参）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TrackMouseEventType
    {
        public uint CbSize;
        public uint DwFlags;
        public nint HWndTrack;
        public uint DwHoverTime;
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

    /// <summary>订阅"鼠标离开窗口"通知（面板 hover 暂停自动关闭用）。
    /// 未订阅就收不到 <see cref="WmMouseLeave"/>。</summary>
    [LibraryImport("user32.dll", EntryPoint = "TrackMouseEvent")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TrackMouseEvent(ref TrackMouseEventType track);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hwnd);

    /// <summary>给窗口套一个圆角裁剪区域（面板圆角外观）。
    /// 区域句柄交给系统后由系统负责释放，本进程不要再 DeleteObject。</summary>
    [LibraryImport("user32.dll")]
    public static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

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

    // ---- 托盘右键菜单（2026-09-21 批复 D1=A）----

    public const uint MfString = 0x00000000;
    public const uint MfGrayed = 0x00000001;
    public const uint MfSeparator = 0x00000800;

    public const uint TpmRightButton = 0x0002;
    public const uint TpmNonotify = 0x0080;
    public const uint TpmReturncmd = 0x0100;

    public const uint WmNull = 0x0000;

    [LibraryImport("user32.dll")]
    public static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(nint menu, uint flags, nuint id, string text);

    /// <summary>弹出菜单。<paramref name="flags"/> 含 <see cref="TpmReturncmd"/> 时返回所选项 id（未选返回 0）。</summary>
    [LibraryImport("user32.dll")]
    public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint hwnd, uint message, nuint wparam, nint lparam);

    // ---- 提权检测（2026-09-21 批复：非管理员静默退出，防手动双击）----

    public const int TokenElevation = 20;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevationValue
    {
        public uint IsElevated;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(
        nint token,
        int infoClass,
        TokenElevationValue* info,
        uint length,
        out uint returnLength);

    /// <summary>当前进程令牌是否已提权（Admin Approval已批准 / RunLevel=Highest）。</summary>
    public static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            var value = new TokenElevationValue();
            var ok = GetTokenInformation(token, TokenElevation, &value, (uint)sizeof(TokenElevationValue), out _);
            return ok && value.IsElevated != 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    // ---- 系统主题探测（面板双主题，2026-09-21 批复：跟随设置「自动/浅色/深色」）----

    private static readonly nint HkeyCurrentUser = unchecked((nint)0x80000001);
    private const uint RrfRtRegDword = 0x00000010;

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValueW(
        nint hkey,
        string subKey,
        string value,
        uint flags,
        out uint type,
        ref uint data,
        ref uint size);

    /// <summary>系统应用主题是否为浅色（读 <c>AppsUseLightTheme</c>；读不到默认深色）。</summary>
    public static bool SystemPrefersLight()
    {
        var data = 0u;
        uint size = sizeof(uint);
        return RegGetValueW(
            HkeyCurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            RrfRtRegDword,
            out _,
            ref data,
            ref size) == 0 && data != 0;
    }

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

    // ---- 降权启动（D40，2026-09-20 用户批复）----
    //
    // 🔴 关键约束（demo2 七方案真机实测，本机结论）：
    //   · 把 explorer 的**进程令牌**直接交给 CreateProcessWithTokenW → 必 Win32Error=5
    //   · 先 DuplicateTokenEx 成主令牌再交给它 → 0x2000 Medium 降权成功
    //   · CreateProcessAsUserW 走不通（1314，SeAssignPrimaryToken 只有 SYSTEM 有）
    //   · COM Shell.Application.ShellExecute **不降权**（子进程仍是 0x3000 High），禁用

    public const uint ProcessQueryLimitedInformation = 0x1000;

    public const uint TokenAssignPrimary    = 0x0001;
    public const uint TokenDuplicate        = 0x0002;
    public const uint TokenQuery            = 0x0008;
    public const uint TokenAdjustPrivileges = 0x0020;
    public const uint TokenAdjustDefault    = 0x0080;
    public const uint TokenAdjustSessionId  = 0x0100;

    public const uint SePrivilegeEnabled = 0x00000002;

    public const int SecurityImpersonation = 2;
    public const int TokenPrimaryType = 1;

    /// <summary>STARTUPINFOW。Desktop 用 <see cref="nint"/> 而非 string —— 降权时它必须是 NULL
    /// （指定 winsta0\default 是失败诱因之一），同时让结构体保持 blittable，AOT 下零封送。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoW
    {
        public uint CbSize;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved2Pointer;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>TOKEN_PRIVILEGES（单元素）。AdjustTokenPrivileges 只需要一条。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(
        nint existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out nint newToken);

    /// <summary>
    /// 以指定主令牌创建进程（真正的降权点）。
    /// <c>commandLine</c> 走 <c>char*</c>：该参数对系统是可写缓冲区，不能传只读托管字符串。
    /// </summary>
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool CreateProcessWithTokenW(
        nint token,
        uint logonFlags,
        string applicationName,
        char* commandLine,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoW startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValueW(nint systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);

    // GDI —— 面板自绘用

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateSolidBrush(uint color);

    /// <summary>取库存 GDI 对象。面板画空心状态点用 <c>NULL_BRUSH</c>（索引 5）。</summary>
    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int index);

    /// <summary>NULL_BRUSH（不填充，配合 pen 得到空心图形）。</summary>
    public const int NullBrush = 5;

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint objectHandle);

    // 🔴 FillRect 是 user32.dll 的导出（winuser.h），不是 gdi32（2026-09-21 真机踩坑：
    //    声明到 gdi32 → WM_PAINT 里 EntryPointNotFoundException，在 UnmanagedCallersOnly
    //    窗口回调中无法穿越原生帧展开 → AOT fail-fast 0xC0000409 托盘左键闪退）。
    [LibraryImport("user32.dll")]
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

    /// <summary>圆角矩形（当前 brush 填充 + 当前 pen 描边）。面板里的卡片/按钮都用它。</summary>
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(nint hdc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    /// <summary>圆角区域（交给 <see cref="SetWindowRgn"/> 做无边框窗口的圆角裁剪）。</summary>
    [LibraryImport("gdi32.dll")]
    public static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

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
