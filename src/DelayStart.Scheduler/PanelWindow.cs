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
/// 托盘点击弹出的进度面板（信息分级 2 级，<c>design.md</c> 八）。
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

    /// <summary>面板配色（BGR 直填 GDI）。浅/深两套，2026-09-21 批复跟随设置主题（自动=读系统 <c>AppsUseLightTheme</c>）。</summary>
    /// <param name="Background">面板底色。</param>
    /// <param name="Row">条目行分隔/底色。</param>
    /// <param name="Track">进度条轨道。</param>
    /// <param name="Fill">进度条填充（强调金，浅色下加深保证对比度）。</param>
    /// <param name="Text">主文本。</param>
    /// <param name="Muted">次要文本（延时/倒计时）。</param>
    /// <param name="Green">成功状态点。</param>
    /// <param name="Blue">启动中状态点。</param>
    /// <param name="Red">失败状态点。</param>
    /// <param name="Gray">等待（空心）/跳过（实心）状态点。</param>
    /// <param name="Button">底部按钮底色。</param>
    private sealed record PanelPalette(
        uint Background,
        uint Row,
        uint Track,
        uint Fill,
        uint Text,
        uint Muted,
        uint Green,
        uint Blue,
        uint Red,
        uint Gray,
        uint Button);

    private static readonly PanelPalette DarkPalette = new(
        Background: 0x00201F1F, // #1F1F1F
        Row: 0x002B2A2A,
        Track: 0x003D3D3D,
        Fill: 0x00C8A857,       // 强调金
        Text: 0x00F2F0EE,
        Muted: 0x00A8A29C,
        Green: 0x006CC878,
        Blue: 0x00E0A050,
        Red: 0x00555CD6,
        Gray: 0x00808080,
        Button: 0x003D3838);

    private static readonly PanelPalette LightPalette = new(
        Background: 0x00F6F5F5, // #F5F5F6
        Row: 0x00ECEAE9,
        Track: 0x00DFDDDC,
        Fill: 0x003A82A8,       // 金加深（#A8823A），浅底上可读
        Text: 0x00201F1F,
        Muted: 0x00716C66,
        Green: 0x002E7D33,      // #337D2E
        Blue: 0x00C4771D,       // #1D77C4
        Red: 0x00323AC0,        // #C03A32
        Gray: 0x00808080,
        Button: 0x00E6E3E1);

    private readonly Func<PanelSnapshot> _snapshotProvider;
    private readonly Action _openRunLog;
    private readonly Func<ThemePreference> _themeProvider;

    private nint _window;
    private NativeMethods.Rect _buttonRect;

    /// <summary>构造面板。</summary>
    /// <param name="snapshotProvider">绘制时读取的数据源。</param>
    /// <param name="openRunLog">「查看运行日志」动作。</param>
    /// <param name="themeProvider">主题偏好来源（管理端设置 Theme；每帧求值，设置热改即时生效）。</param>
    public PanelWindow(Func<PanelSnapshot> snapshotProvider, Action openRunLog, Func<ThemePreference> themeProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(openRunLog);
        ArgumentNullException.ThrowIfNull(themeProvider);

        _snapshotProvider = snapshotProvider;
        _openRunLog = openRunLog;
        _themeProvider = themeProvider;
    }

    /// <summary>解析当前配色：浅/深直接取，自动读系统注册表（读不到默认深色）。</summary>
    private PanelPalette CurrentPalette => _themeProvider() switch
    {
        ThemePreference.Light => LightPalette,
        ThemePreference.Dark => DarkPalette,
        _ => NativeMethods.SystemPrefersLight() ? LightPalette : DarkPalette,
    };

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

        // 🔴 必须挂消息处理器（2026-09-21 修复「左键无法弹出面板」）：共享窗口过程的默认实现
        // 不处理任何消息 —— WM_PAINT 无人接，窗口显示出来也永远画不出内容（视觉上=没弹出）。
        // 此前只有托盘窗口挂了 handler，面板窗口一直缺失，属历史遗留 bug。
        NativeMethods.AttachHandler(_window, this);
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
        var palette = CurrentPalette;
        var paint = new NativeMethods.PaintStruct();
        var hdc = NativeMethods.BeginPaint(hwnd, ref paint);
        if (hdc == 0)
        {
            return;
        }

        try
        {
            _ = NativeMethods.GetClientRect(hwnd, out var client);
            var background = NativeMethods.CreateSolidBrush(palette.Background);
            _ = NativeMethods.FillRect(hdc, ref client, background);
            _ = NativeMethods.DeleteObject(background);

            _ = NativeMethods.SetBkMode(hdc, 1); // TRANSPARENT
            _ = NativeMethods.SetTextColor(hdc, palette.Text);

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
            var trackBrush = NativeMethods.CreateSolidBrush(palette.Track);
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
                    var fillBrush = NativeMethods.CreateSolidBrush(palette.Fill);
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

                DrawStatusDot(hdc, x + 6, rowTop + (RowHeight / 2), row.State, palette);

                _ = NativeMethods.SetTextColor(hdc, palette.Text);
                var nameRect = new NativeMethods.Rect
                {
                    Left = x + 18,
                    Top = rowTop,
                    Right = client.Right - Padding - 70,
                    Bottom = rowTop + RowHeight,
                };
                _ = NativeMethods.DrawTextW(hdc, row.Name, -1, ref nameRect, 0x0800); // END_ELLIPSIS

                _ = NativeMethods.SetTextColor(hdc, palette.Muted);
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
            _ = NativeMethods.SetTextColor(hdc, palette.Muted);
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
            var buttonBrush = NativeMethods.CreateSolidBrush(palette.Button);
            _ = NativeMethods.FillRect(hdc, ref _buttonRect, buttonBrush);
            _ = NativeMethods.DeleteObject(buttonBrush);

            _ = NativeMethods.SetTextColor(hdc, palette.Text);
            var buttonTextRect = _buttonRect;
            buttonTextRect.Left += 10;
            _ = NativeMethods.DrawTextW(hdc, "查看运行日志", -1, ref buttonTextRect, 0x0024); // VCENTER | SINGLELINE
        }
        finally
        {
            _ = NativeMethods.EndPaint(hwnd, ref paint);
        }
    }

    private static void DrawStatusDot(nint hdc, int centerX, int centerY, RunItemState state, PanelPalette palette)
    {
        var color = state switch
        {
            RunItemState.Done => palette.Green,
            RunItemState.Launching => palette.Blue,
            RunItemState.Failed => palette.Red,
            RunItemState.Skipped => palette.Gray, // 跳过：实心灰（等待是空心灰，视觉可区分）
            _ => palette.Gray,
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
