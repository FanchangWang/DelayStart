using System.Runtime.InteropServices;

namespace DelayStart.App.Interop;

/// <summary>
/// UIPI 消息过滤放行（R10：提权窗口接收拖放）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 本程序全程提权（D20，高完整性级别），而拖放来源是中完整性级别的
/// <c>explorer.exe</c>。UIPI 会**静默丢弃**从低完整性进程发往高完整性窗口的
/// 输入消息 —— 用户看到的现象就是拖进去鼠标变 🚫、松手没有任何反应。
/// </para>
/// <para>
/// 系统对这一场景提供的唯一合法出口是 <see cref="ChangeWindowMessageFilterEx"/>：
/// 由高完整性窗口**主动声明**自己愿意接收指定的跨完整性消息。需要放行的三条：
/// <c>WM_DROPFILES(0x0233)</c>、<c>WM_COPYDATA(0x004A)</c>、
/// <c>WM_COPYGLOBALDATA(0x0049)</c>（最后一条未公开文档，是 OLE 拖放
/// 内部传递全局数据用的；缺它则 OLE 拖放仍然失败）。
/// </para>
/// <para>
/// 这是接收侧的单方面修复，不需要对 <c>explorer.exe</c> 做任何事，也不会
/// 降低本进程对其它消息的防护 —— 只多开了"拖放"这一条明确的白名单。
/// </para>
/// </remarks>
public static partial class UipiMessageFilter
{
    private const int MsgfltAllow = 1;

    private const uint WmCopydata = 0x004A;
    private const uint WmCopyglobaldata = 0x0049;
    private const uint WmDropfiles = 0x0233;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(
        nint hwnd,
        uint message,
        int action,
        nint changeFilterStruct);

    /// <summary>让窗口可以接收来自低完整性进程（资源管理器）的拖放。</summary>
    /// <param name="hwnd">目标窗口句柄；为 <c>0</c> 时不做任何事。</param>
    /// <remarks>
    /// 某条消息放行失败时**不抛异常**：三条全部失败等价于"拖放不可用"，
    /// 但程序的主路径（点击选择）不受影响 —— 与"拖放是增强而非主路径"的定位一致。
    /// </remarks>
    public static void AllowDragDrop(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        _ = ChangeWindowMessageFilterEx(hwnd, WmDropfiles, MsgfltAllow, 0);
        _ = ChangeWindowMessageFilterEx(hwnd, WmCopydata, MsgfltAllow, 0);
        _ = ChangeWindowMessageFilterEx(hwnd, WmCopyglobaldata, MsgfltAllow, 0);
    }
}
