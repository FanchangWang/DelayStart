using DelayStart.Core.Models;

namespace DelayStart.Scheduler;

/// <summary>弹出面板的一帧数据快照。引擎在状态变化后重建，面板绘制时直接读。</summary>
/// <param name="Title">标题行，形如「延时启动 · 登录后 12 秒」。</param>
/// <param name="DoneCount">已启动数。</param>
/// <param name="TotalCount">计划数。</param>
/// <param name="Items">条目行。</param>
/// <param name="FooterText">倒计时行：「下一项还有 48 秒」/「已全部启动」。</param>
/// <param name="HasFailure">是否存在失败（面板据此保持可见入口）。</param>
internal sealed record PanelSnapshot(
    string Title,
    int DoneCount,
    int TotalCount,
    IReadOnlyList<PanelItemRow> Items,
    string FooterText,
    bool HasFailure);

/// <summary>面板里的一行条目。</summary>
/// <param name="State">条目状态（决定状态点的颜色）。</param>
/// <param name="Name">显示名。</param>
/// <param name="RightText">行尾文本：延时或失败原因。</param>
internal sealed record PanelItemRow(RunItemState State, string Name, string RightText);

/// <summary>
/// 托盘点击弹出的进度面板（信息分级 2 级，<c>scheduler-design.md</c> 第四节）。
/// </summary>
/// <remarks>
/// <para>
/// 无边框 + 置顶 + 不进任务栏（工具窗）+ **失焦即关**（<c>WM_ACTIVATE</c> 变非激活即隐藏）——
/// 与 Windows 音量/网络面板同一套交互惯例。
/// </para>
/// <para>
/// 自绘走 GDI：深色底 + 状态点（绿 = 成功、蓝 = 启动中、灰 = 等待、红 = 失败）。
/// <c>D24=B</c> 纯 Win32 方案下这是官方支持路径（无边框窗口 + 自定义绘制），不涉及任何 UI 框架。
/// </para>
/// </remarks>
internal sealed unsafe partial class PanelWindow : IDisposable, NativeMethods.IMessageHandler
{
    private const int PanelWidth = 340;
    private const int MaxPanelHeight = 420;
    private const int HeaderHeight = 34;
    private const int ProgressHeight = 16;
    private const int RowHeight = 24;
    private const int FooterHeight = 26;
    private const int ButtonHeight = 34;
    private const int Padding = 12;

    private const uint BackgroundColor = 0x00201F1F; // BGR：#1F1F1F
    private const uint RowColor = 0x002B2A2A;
    private const uint BarTrackColor = 0x003D3D3D;
    private const uint BarFillColor = 0x00C8A857;   // 强调金
    private const uint TextColor = 0x00F2F0EE;
    private const uint MutedTextColor = 0x00A8A29C;
    private const uint GreenColor = 0x006CC878;
    private const uint BlueColor = 0x00E0A050;
    private const uint GrayColor = 0x00808080;
    private const uint RedColor = 0x00555CD6;

    private readonly Func<PanelSnapshot> _snapshotProvider;
    private readonly Action _openRunLog;

    private nint _window;
    private NativeMethods.Rect _buttonRect;

    /// <summary>构造面板。</summary>
    /// <param name="snapshotProvider">绘制时读取的数据源。</param>
    /// <param name="openRunLog">「查看运行日志」动作。</param>
    public PanelWindow(Func<PanelSnapshot> snapshotProvider, Action openRunLog)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(openRunLog);

        _snapshotProvider = snapshotProvider;
        _openRunLog = openRunLog;
    }

    /// <summary>面板是否可见。</summary>
    public bool IsVisible => _window != 0 && IsWindowVisible(_window);

    /// <summary>创建面板窗口。失败静默（面板是可选增强）。</summary>
    public void TryCreate()
    {
        if (!NativeMethods.RegisterClass("DelayStart.PanelWnd", 0))
        {
            return;
        }

        _window = NativeMethods.CreateWindowExW(
            NativeMethods.WsExToolWindow | NativeMethods.WsExTopmost,
            "DelayStart.PanelWnd",
            string.Empty,
            NativeMethods.WsPopup, // 无边框
            0, 0, PanelWidth, 200,
            0, 0, NativeMethods.GetModuleHandle(), 0);
    }

    /// <summary>显示或隐藏面板（托盘左键点击）。</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
    }

    /// <summary>在工作区右下角显示面板并夺取前台（失焦即关依赖前台状态）。</summary>
    public void Show()
    {
        if (_window == 0)
        {
            return;
        }

        var snapshot = _snapshotProvider();
        var height = MeasureHeight(snapshot);
        var (x, y) = ComputePosition(height);

        _ = NativeMethods.SetWindowPos(
            _window,
            (-1), // HWND_TOPMOST
            x, y, PanelWidth, height,
            0);

        _ = NativeMethods.ShowWindow(_window, 4); // SW_SHOWNOACTIVATE
        _ = NativeMethods.SetForegroundWindow(_window);
        _ = NativeMethods.InvalidateRect(_window, 0, true);
    }

    /// <summary>隐藏面板。</summary>
    public void Hide()
    {
        if (_window != 0)
        {
            _ = NativeMethods.ShowWindow(_window, NativeMethods.SwHide);
        }
    }

    /// <summary>状态变化后调用：面板可见时重绘。</summary>
    public void Refresh()
    {
        if (IsVisible)
        {
            _ = NativeMethods.InvalidateRect(_window, 0, true);
        }
    }

    /// <inheritdoc />
    public nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.WmPaint:
                Paint(hwnd);
                return 0;

            case NativeMethods.WmEraseBkgnd:
                return 1; // 自绘全量覆盖，避免先白后黑

            case NativeMethods.WmActivate:
                // 失焦即关：LOWORD(wParam) == WA_INACTIVE
                if ((uint)(wParam & 0xFFFF) == NativeMethods.WaInactive)
                {
                    Hide();
                }

                return 0;

            case NativeMethods.WmLButtonDown:
                HandleClick(lParam);
                return 0;

            default:
                return 0;
        }
    }

    public void Dispose()
    {
        if (_window != 0)
        {
            _ = NativeMethods.DestroyWindow(_window);
            _window = 0;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    private static int MeasureHeight(PanelSnapshot snapshot)
    {
        var rows = Math.Max(snapshot.Items.Count, 1);
        var needed = Padding + HeaderHeight + ProgressHeight + (rows * RowHeight)
            + FooterHeight + ButtonHeight + Padding;
        return Math.Min(needed, MaxPanelHeight);
    }

    private static (int X, int Y) ComputePosition(int height)
    {
        // 距工作区右下角 12px（设计稿：不跟随托盘图标定位）。
        _ = NativeMethods.GetCursorPos(out var cursor);
        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MonitorDefaulttonearest);
        var info = new NativeMethods.MonitorInfoW { CbSize = (uint)sizeof(NativeMethods.MonitorInfoW) };
        _ = NativeMethods.GetMonitorInfoW(monitor, ref info);

        var x = info.WorkArea.Right - PanelWidth - 12;
        var y = info.WorkArea.Bottom - height - 12;
        return (x, y);
    }

    private void Paint(nint hwnd)
    {
        var snapshot = _snapshotProvider();
        var paint = new NativeMethods.PaintStruct();
        var hdc = NativeMethods.BeginPaint(hwnd, ref paint);
        if (hdc == 0)
        {
            return;
        }

        try
        {
            _ = NativeMethods.GetClientRect(hwnd, out var client);
            var background = NativeMethods.CreateSolidBrush(BackgroundColor);
            _ = NativeMethods.FillRect(hdc, ref client, background);
            _ = NativeMethods.DeleteObject(background);

            _ = NativeMethods.SetBkMode(hdc, 1); // TRANSPARENT
            _ = NativeMethods.SetTextColor(hdc, TextColor);

            var x = client.Left + Padding;
            var width = client.Right - client.Left - (Padding * 2);

            // 标题
            var titleRect = new NativeMethods.Rect
            {
                Left = x,
                Top = client.Top + Padding - 4,
                Right = client.Right - Padding,
                Bottom = client.Top + Padding + HeaderHeight,
            };
            _ = NativeMethods.DrawTextW(hdc, snapshot.Title, -1, ref titleRect, 0x0000);

            // 进度条
            var barTop = titleRect.Bottom + 2;
            var track = new NativeMethods.Rect
            {
                Left = x,
                Top = barTop,
                Right = x + width,
                Bottom = barTop + 8,
            };
            var trackBrush = NativeMethods.CreateSolidBrush(BarTrackColor);
            _ = NativeMethods.FillRect(hdc, ref track, trackBrush);
            _ = NativeMethods.DeleteObject(trackBrush);

            if (snapshot.TotalCount > 0)
            {
                var fillWidth = (width * Math.Min(snapshot.DoneCount, snapshot.TotalCount)) / snapshot.TotalCount;
                if (fillWidth > 0)
                {
                    var fill = new NativeMethods.Rect
                    {
                        Left = x,
                        Top = barTop,
                        Right = x + fillWidth,
                        Bottom = barTop + 8,
                    };
                    var fillBrush = NativeMethods.CreateSolidBrush(BarFillColor);
                    _ = NativeMethods.FillRect(hdc, ref fill, fillBrush);
                    _ = NativeMethods.DeleteObject(fillBrush);
                }
            }

            // 条目行
            var rowTop = barTop + 8 + 4;
            foreach (var row in snapshot.Items)
            {
                if (rowTop + RowHeight > client.Bottom - FooterHeight - ButtonHeight)
                {
                    break; // 超高面板裁掉溢出行（MaxPanelHeight 内滚动不做，条目数量级小）
                }

                DrawStatusDot(hdc, x + 6, rowTop + (RowHeight / 2), row.State);

                _ = NativeMethods.SetTextColor(hdc, TextColor);
                var nameRect = new NativeMethods.Rect
                {
                    Left = x + 18,
                    Top = rowTop,
                    Right = client.Right - Padding - 70,
                    Bottom = rowTop + RowHeight,
                };
                _ = NativeMethods.DrawTextW(hdc, row.Name, -1, ref nameRect, 0x0800); // END_ELLIPSIS

                _ = NativeMethods.SetTextColor(hdc, MutedTextColor);
                var rightRect = new NativeMethods.Rect
                {
                    Left = client.Right - Padding - 66,
                    Top = rowTop,
                    Right = client.Right - Padding,
                    Bottom = rowTop + RowHeight,
                };
                _ = NativeMethods.DrawTextW(hdc, row.RightText, -1, ref rightRect, 0x0000 | 0x0800);

                rowTop += RowHeight;
            }

            // 倒计时行
            _ = NativeMethods.SetTextColor(hdc, MutedTextColor);
            var footerRect = new NativeMethods.Rect
            {
                Left = x,
                Top = rowTop + 2,
                Right = client.Right - Padding,
                Bottom = rowTop + 2 + FooterHeight,
            };
            _ = NativeMethods.DrawTextW(hdc, snapshot.FooterText, -1, ref footerRect, 0x0000);

            // 底部按钮
            _buttonRect = new NativeMethods.Rect
            {
                Left = x,
                Top = client.Bottom - ButtonHeight - Padding,
                Right = x + 130,
                Bottom = client.Bottom - Padding,
            };
            var buttonBrush = NativeMethods.CreateSolidBrush(0x003D3838);
            _ = NativeMethods.FillRect(hdc, ref _buttonRect, buttonBrush);
            _ = NativeMethods.DeleteObject(buttonBrush);

            _ = NativeMethods.SetTextColor(hdc, TextColor);
            var buttonTextRect = _buttonRect;
            buttonTextRect.Left += 10;
            _ = NativeMethods.DrawTextW(hdc, "查看运行日志", -1, ref buttonTextRect, 0x0024); // VCENTER | SINGLELINE
        }
        finally
        {
            _ = NativeMethods.EndPaint(hwnd, ref paint);
        }
    }

    private static void DrawStatusDot(nint hdc, int centerX, int centerY, RunItemState state)
    {
        var color = state switch
        {
            RunItemState.Done => GreenColor,
            RunItemState.Launching => BlueColor,
            RunItemState.Failed => RedColor,
            _ => GrayColor,
        };

        var brush = NativeMethods.CreateSolidBrush(color);
        var previous = NativeMethods.SelectObject(hdc, brush);

        if (state == RunItemState.Waiting)
        {
            // 等待中画空心点。
            _ = NativeMethods.Ellipse(hdc, centerX - 4, centerY - 4, centerX + 4, centerY + 4);
        }
        else
        {
            _ = NativeMethods.Ellipse(hdc, centerX - 5, centerY - 5, centerX + 5, centerY + 5);
        }

        _ = NativeMethods.SelectObject(hdc, previous);
        _ = NativeMethods.DeleteObject(brush);
    }

    private void HandleClick(nint lParam)
    {
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        if (x >= _buttonRect.Left && x <= _buttonRect.Right && y >= _buttonRect.Top && y <= _buttonRect.Bottom)
        {
            Hide();
            _openRunLog();
        }
    }
}
