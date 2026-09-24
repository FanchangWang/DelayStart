using DelayStart.Core.Models;

namespace DelayStart.Scheduler;

/// <summary>弹出面板的一帧数据快照。引擎在状态变化后重建，面板绘制时直接读。</summary>
/// <param name="ChipText">头部状态徽标：「启动中」/「启动完成」/「完成 · 1 项失败」。</param>
/// <param name="HasFailure">是否存在失败（决定徽标配色）。</param>
/// <param name="IsFinished">是否已完成（完成态：倒计时自动关闭 + 关闭即退出应用）。</param>
/// <param name="DoneCount">已启动数。</param>
/// <param name="TotalCount">计划数。</param>
/// <param name="MetaText">进度区右侧的统计行（单行，GDI 不换行）。</param>
/// <param name="Items">条目行（分段进度条按它着色）。</param>
/// <param name="Current">当前项卡片。</param>
/// <param name="NextName">下一项名称（完成态传「—」）。</param>
/// <param name="AutoCloseTotalSeconds">完成态自动关闭总秒数（全成功 10 / 有失败 60）；0 = 不自动关闭。</param>
/// <param name="PrimaryButtonText">主按钮文案（启动中「立即启动剩余 N 项」/ 完成态「打开 DelayStart」）。</param>
/// <param name="StatusText">托盘右键菜单的状态头文案。</param>
internal sealed record PanelSnapshot(
    string ChipText,
    bool HasFailure,
    bool IsFinished,
    int DoneCount,
    int TotalCount,
    string MetaText,
    IReadOnlyList<PanelItemRow> Items,
    PanelCurrentRow Current,
    string NextName,
    int AutoCloseTotalSeconds,
    string PrimaryButtonText,
    string StatusText);

/// <summary>面板里的一行条目。</summary>
/// <param name="State">条目状态（决定状态点颜色与进度条分段颜色）。</param>
/// <param name="Name">显示名。</param>
/// <param name="RightText">行尾文本：延时或失败原因。</param>
internal sealed record PanelItemRow(RunItemState State, string Name, string RightText);

/// <summary>面板中部的「当前项」卡片。</summary>
/// <param name="State">状态点颜色。</param>
/// <param name="Name">主文案（启动中=正在启动的条目；完成态=结果摘要）。</param>
/// <param name="Detail">副文案（命令行 / 失败原因）。</param>
/// <param name="EtaSeconds">启动中距下一项的秒数（完成态为 <see langword="null"/>）。</param>
internal sealed record PanelCurrentRow(RunItemState State, string Name, string Detail, int? EtaSeconds);

/// <summary>面板可触发的动作集合（UI v2，2026-09-21 批复）。</summary>
/// <param name="OpenRunLog">「调度日志」按钮 / 菜单项。</param>
/// <param name="OpenManager">「打开 DelayStart」按钮 / 菜单项。</param>
/// <param name="LaunchRemainingNow">启动中主按钮「立即启动剩余 N 项」。</param>
/// <param name="SkipRemaining">启动中「跳过剩余任务」（面板入口：跳过后留在面板看结果，不退出）。</param>
/// <param name="Quit">完成态收尾出口（倒计时归零 / 失焦关闭，N9-D3）与菜单「退出」：结束调度端进程（托盘一并消失）。</param>
internal sealed record PanelActions(
    Action OpenRunLog,
    Action OpenManager,
    Action LaunchRemainingNow,
    Action SkipRemaining,
    Action Quit);

/// <summary>
/// 托盘点击弹出的进度面板（信息分级 2 级，<c>design.md 八</c>）。
/// </summary>
/// <remarks>
/// <para>
/// N8–N10（2026-09-22 批复，<c>design.md</c> FR-14.1，取代 UI v2 的"置顶 + ✕"）：
/// </para>
/// <list type="bullet">
/// <item>右上角只留一枚 <b>钉</b>（语义重定义）：<b>未选中（默认）</b> = 失焦自动关闭 ——
/// 启动中仅收起（可随时从托盘再打开），完成态收起后进程退出（D3 批复 A）；<b>选中</b> =
/// 按钮高亮 + <c>HWND_TOPMOST</c>，失焦不再自动关闭，再次点击取消并立即退出置顶；</item>
/// <item>完成倒计时逻辑全部保持不变：全成功 10 秒 / 有失败 60 秒，鼠标移入暂停、
/// 移出继续且不重置，归零退出 —— 钉住<b>不</b>暂停倒计时；</item>
/// <item>面板<b>仅手动弹出</b>（托盘左键 / 菜单），收尾不再自动弹（N4）；通知归通知中转器（N1）。</item>
/// </list>
/// <para>
/// 自绘走 GDI：只有 FillRect / RoundRect / Ellipse / LineTo / DrawText，没有模糊与亚克力
/// （<c>D24=B</c> 纯 Win32 路径）。所有绘制都在 <c>WM_PAINT</c> 里完成，状态变化靠
/// <c>InvalidateRect</c> 触发重绘，AOT 下无任何反射。
/// </para>
/// </remarks>
internal sealed unsafe partial class PanelWindow : IDisposable, NativeMethods.IMessageHandler
{
    private const int PanelWidth = 360;
    private const int MaxPanelHeight = 560;
    private const int Padding = 14;
    private const int HeaderHeight = 46;
    private const int IconButtonSize = 30;
    private const int ProgressHeight = 46;
    private const int SegmentBarHeight = 14;
    private const int CurrentCardHeight = 52;
    private const int NextRowHeight = 22;
    private const int RowHeight = 26;
    private const int MaxVisibleRows = 5;
    private const int CountdownHeight = 50;
    private const int ButtonHeight = 34;
    private const int ButtonGap = 8;
    private const int CornerRadius = 10;

    /// <summary>面板自己的 1 秒节拍（完成态倒计时）。托盘宿主用的是 id=1，不冲突。</summary>
    private const nuint CountdownTimerId = 2;

    // DrawText 格式
    private const uint DtLeft = 0x0000;
    private const uint DtCenter = 0x0001;
    private const uint DtRight = 0x0002;
    private const uint DtVcenter = 0x0004;
    private const uint DtSingleline = 0x0020;
    private const uint DtEndEllipsis = 0x8000;

    // 命中目标（hover 高亮）
    private const int HitNone = 0;
    private const int HitPin = 1;
    private const int HitClose = 2;
    private const int HitPrimary = 3;
    private const int HitSecondary = 4;
    private const int HitSkip = 5;

    /// <summary>面板配色（BGR 直填 GDI）。浅/深两套，跟随设置主题（自动=读系统 <c>AppsUseLightTheme</c>）。</summary>
    private sealed record PanelPalette(
        uint Background,
        uint Card,
        uint CardAlt,
        uint Border,
        uint Track,
        uint Text,
        uint Text2,
        uint Muted,
        uint Accent,
        uint AccentFg,
        uint AccentSoft,
        uint Green,
        uint Gold,
        uint Red,
        uint Gray);

    private static readonly PanelPalette DarkPalette = new(
        Background: 0x001F1F1F, // #1F1F1F
        Card: 0x002A2A2A,       // #2A2A2A
        CardAlt: 0x00313131,    // #313131
        Border: 0x003A3A3A,     // #3A3A3A
        Track: 0x003A3A3A,
        Text: 0x00F5F5F5,
        Text2: 0x00C9C9C9,
        Muted: 0x009A9A9A,
        Accent: 0x00FFC24C,     // #4CC2FF
        AccentFg: 0x003A2806,   // #06283A（强调底上的字）
        AccentSoft: 0x004C4230, // 强调色 16% 混 #2A2A2A（GDI 无 alpha，手工混）
        Green: 0x0078C86C,      // #6CC878
        Gold: 0x0050A0E0,       // #E0A050
        Red: 0x006B6BFF,        // #FF6B6B
        Gray: 0x007A7A7A);

    private static readonly PanelPalette LightPalette = new(
        Background: 0x00FBFBFB, // #FBFBFB
        Card: 0x00FFFFFF,
        CardAlt: 0x00F0F0F0,
        Border: 0x00E0E0E0,
        Track: 0x00E2E2E2,
        Text: 0x001B1B1B,
        Text2: 0x003D3D3D,
        Muted: 0x006B6B6B,
        Accent: 0x00BB6C0A,     // #0A6CBB（浅底上加深保证对比度）
        AccentFg: 0x00FFFFFF,
        AccentSoft: 0x00F7EDE2, // 强调色 12% 混 #FFFFFF
        Green: 0x00337D2E,      // #2E7D33
        Gold: 0x000C72A8,       // #A8720C
        Red: 0x002B39C0,        // #C0392B
        Gray: 0x008A8A8A);

    private readonly Func<PanelSnapshot> _snapshotProvider;
    private readonly PanelActions _actions;
    private readonly Func<ThemePreference> _themeProvider;

    private nint _window;
    private nint _fontLarge;
    private nint _fontNormal;
    private nint _fontBold;
    private nint _fontSmall;

    private bool _pinned;
    private bool _hoverPaused;
    private bool _trackingLeave;
    private int _hoverTarget = HitNone;

    private int _autoCloseTotal;
    private int _autoCloseRemaining;

    private NativeMethods.Rect _pinRect;
    private NativeMethods.Rect _primaryRect;
    private NativeMethods.Rect _secondaryRect;
    private NativeMethods.Rect _skipRect;

    /// <summary>构造面板。</summary>
    /// <param name="snapshotProvider">绘制时读取的数据源。</param>
    /// <param name="actions">面板可触发的动作。</param>
    /// <param name="themeProvider">主题偏好来源（管理端设置 Theme；每帧求值，设置热改即时生效）。</param>
    public PanelWindow(Func<PanelSnapshot> snapshotProvider, PanelActions actions, Func<ThemePreference> themeProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(themeProvider);

        _snapshotProvider = snapshotProvider;
        _actions = actions;
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
            NativeMethods.WsExToolWindow,
            "DelayStart.PanelWnd",
            string.Empty,
            NativeMethods.WsPopup, // 无边框（圆角靠 SetWindowRgn 裁）
            0, 0, PanelWidth, 200,
            0, 0, NativeMethods.GetModuleHandle(), 0);

        // 🔴 必须挂消息处理器（2026-09-21 修复「左键无法弹出面板」）：共享窗口过程的默认实现
        // 不处理任何消息 —— WM_PAINT 无人接，窗口显示出来也永远画不出内容（视觉上=没弹出）。
        NativeMethods.AttachHandler(_window, this);

        _fontLarge = CreateFont(20, 700);
        _fontNormal = CreateFont(13, 400);
        _fontBold = CreateFont(13, 600);
        _fontSmall = CreateFont(11, 400);
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

    /// <summary>在工作区右下角显示面板并夺取前台。</summary>
    public void Show()
    {
        if (_window == 0)
        {
            return;
        }

        var snapshot = _snapshotProvider();
        var height = MeasureHeight(snapshot);
        var (x, y) = ComputePosition(height);

        _autoCloseTotal = snapshot.IsFinished ? Math.Max(snapshot.AutoCloseTotalSeconds, 0) : 0;
        _autoCloseRemaining = _autoCloseTotal;
        _hoverPaused = false;
        _trackingLeave = false;
        _hoverTarget = HitNone;

        _ = NativeMethods.SetWindowPos(
            _window,
            _pinned ? NativeMethods.HwndTopmost : NativeMethods.HwndNotopmost,
            x, y, PanelWidth, height,
            0);

        // 圆角裁剪（区域句柄交给系统，系统负责释放；高度变化后要重设）
        _ = NativeMethods.SetWindowRgn(
            _window,
            NativeMethods.CreateRoundRectRgn(0, 0, PanelWidth, height, CornerRadius * 2, CornerRadius * 2),
            true);

        _ = NativeMethods.ShowWindow(_window, 5); // SW_SHOW（UI v2：弹出即抢前台，不再 SW_SHOWNOACTIVATE）
        _ = NativeMethods.SetForegroundWindow(_window);
        _ = NativeMethods.InvalidateRect(_window, 0, true);

        if (_autoCloseTotal > 0)
        {
            _ = NativeMethods.SetTimer(_window, CountdownTimerId, 1000, 0);
        }
    }

    /// <summary>面板已显示时激活它（完成后通知改为弹面板：已开则不重复弹，只激活）。</summary>
    public void ShowOrActivate()
    {
        if (_window == 0)
        {
            return;
        }

        if (IsVisible)
        {
            _ = NativeMethods.SetForegroundWindow(_window);
            _ = NativeMethods.BringWindowToTop(_window);

            // 🔴 面板可能在"启动中"就被用户打开了（那时没有倒计时）。进入完成态后
            // 必须补上倒计时，否则面板永远不自动关闭 → 调度端永远不退出。
            EnsureCountdown();
            _ = NativeMethods.InvalidateRect(_window, 0, true);
            return;
        }

        Show();
    }

    /// <summary>按当前快照补启 / 重置完成态倒计时（已开面板进入完成态时用）。</summary>
    private void EnsureCountdown()
    {
        var snapshot = _snapshotProvider();
        var total = snapshot.IsFinished ? Math.Max(snapshot.AutoCloseTotalSeconds, 0) : 0;
        if (total <= 0)
        {
            return;
        }

        if (_autoCloseTotal != total || _autoCloseRemaining <= 0)
        {
            _autoCloseTotal = total;
            _autoCloseRemaining = total;
            _ = NativeMethods.SetTimer(_window, CountdownTimerId, 1000, 0);
        }
    }

    /// <summary>隐藏面板（启动中 ✕ 的行为：只收起，不退出应用）。</summary>
    public void Hide()
    {
        if (_window == 0)
        {
            return;
        }

        _ = NativeMethods.KillTimer(_window, CountdownTimerId);
        _hoverPaused = false;
        _trackingLeave = false;
        _ = NativeMethods.ShowWindow(_window, NativeMethods.SwHide);
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

            case NativeMethods.WmLButtonDown:
                HandleClick(lParam);
                return 0;

            case NativeMethods.WmMouseMove:
                HandleMouseMove(hwnd, lParam);
                return 0;

            case NativeMethods.WmMouseLeave:
                HandleMouseLeave(hwnd);
                return 0;

            case NativeMethods.WmTimer:
                if (wParam == CountdownTimerId)
                {
                    TickCountdown(hwnd);
                }

                return 0;

            // N9（2026-09-22 批复）：未钉住时失焦自动关闭 —— 启动中仅收起（可从托盘再开），
            // 完成态收起后进程退出（D3 批复 A：通知已发，进程无存在意义）。钉住则忽略本消息。
            case NativeMethods.WmActivate:
                if ((wParam & 0xFFFF) == 0 /* WA_INACTIVE */ && !_pinned && IsWindowVisible(hwnd))
                {
                    Hide();
                    if (_snapshotProvider().IsFinished)
                    {
                        _actions.Quit();
                    }
                }

                return 0;

            default:
                return 0;
        }
    }

    public void Dispose()
    {
        DeleteFont(ref _fontLarge);
        DeleteFont(ref _fontNormal);
        DeleteFont(ref _fontBold);
        DeleteFont(ref _fontSmall);

        if (_window != 0)
        {
            _ = NativeMethods.KillTimer(_window, CountdownTimerId);
            _ = NativeMethods.DestroyWindow(_window);
            _window = 0;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    private static nint CreateFont(int pixelHeight, int weight) =>
        NativeMethods.CreateFontW(
            -pixelHeight, 0, 0, 0,
            weight,
            0, 0, 0,
            1,     // DEFAULT_CHARSET（按字体名解析，中文正常）
            0, 0,
            5,     // CLEARTYPE_QUALITY
            0,
            "Microsoft YaHei UI");

    private static void DeleteFont(ref nint font)
    {
        if (font != 0)
        {
            _ = NativeMethods.DeleteObject(font);
            font = 0;
        }
    }

    private static int MeasureHeight(PanelSnapshot snapshot)
    {
        var rows = Math.Min(Math.Max(snapshot.Items.Count, 1), MaxVisibleRows);
        // 启动中没有脚下提示行（2026-09-21 批复：与当前项 ETA、MetaText 重复，已删除）。
        var footer = snapshot.IsFinished ? CountdownHeight : 0;
        var buttons = snapshot.IsFinished ? ButtonHeight : (ButtonHeight * 2) + ButtonGap;

        var needed = (Padding * 2) + HeaderHeight + ProgressHeight + SegmentBarHeight
            + CurrentCardHeight + NextRowHeight + (rows * RowHeight) + footer + buttons;
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

    // ---- 交互 ----

    private void HandleClick(nint lParam)
    {
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var snapshot = _snapshotProvider();

        if (HitTest(_pinRect, x, y))
        {
            _pinned = !_pinned;
            _ = NativeMethods.SetWindowPos(
                _window,
                _pinned ? NativeMethods.HwndTopmost : NativeMethods.HwndNotopmost,
                0, 0, 0, 0,
                0x0001 | 0x0002); // SWP_NOSIZE | SWP_NOMOVE
            _ = NativeMethods.InvalidateRect(_window, 0, true);
            return;
        }

        if (HitTest(_primaryRect, x, y))
        {
            if (snapshot.IsFinished)
            {
                _actions.OpenManager();
            }
            else
            {
                _actions.LaunchRemainingNow();
            }

            return;
        }

        if (HitTest(_secondaryRect, x, y))
        {
            _actions.OpenRunLog();
            return;
        }

        if (HitTest(_skipRect, x, y) && !snapshot.IsFinished)
        {
            _actions.SkipRemaining();
        }
    }

    private void HandleMouseMove(nint hwnd, nint lParam)
    {
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        var target = HitTest(_pinRect, x, y) ? HitPin
            : HitTest(_primaryRect, x, y) ? HitPrimary
            : HitTest(_secondaryRect, x, y) ? HitSecondary
            : HitTest(_skipRect, x, y) ? HitSkip
            : HitNone;

        if (target != _hoverTarget)
        {
            _hoverTarget = target;
            _ = NativeMethods.InvalidateRect(hwnd, 0, true);
        }

        // 完成态：指针进入面板即暂停自动关闭倒计时（移出后继续，不重置）。
        if (_autoCloseTotal > 0 && !_hoverPaused)
        {
            _hoverPaused = true;
            _ = NativeMethods.InvalidateRect(hwnd, 0, true);
        }

        if (!_trackingLeave)
        {
            var track = new NativeMethods.TrackMouseEventType
            {
                CbSize = (uint)sizeof(NativeMethods.TrackMouseEventType),
                DwFlags = NativeMethods.TmeLeave,
                HWndTrack = hwnd,
            };
            _trackingLeave = NativeMethods.TrackMouseEvent(ref track);
        }
    }

    private void HandleMouseLeave(nint hwnd)
    {
        _trackingLeave = false;
        _hoverTarget = HitNone;

        if (_hoverPaused)
        {
            _hoverPaused = false;
        }

        _ = NativeMethods.InvalidateRect(hwnd, 0, true);
    }

    private void TickCountdown(nint hwnd)
    {
        if (_autoCloseTotal <= 0 || _hoverPaused || !IsVisible)
        {
            return;
        }

        _autoCloseRemaining--;
        if (_autoCloseRemaining <= 0)
        {
            Hide();
            _actions.Quit();
            return;
        }

        _ = NativeMethods.InvalidateRect(hwnd, 0, true);
    }

    private static bool HitTest(NativeMethods.Rect rect, int x, int y) =>
        rect.Right > rect.Left && x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;

    // ---- 绘制 ----

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

            var left = client.Left + Padding;
            var right = client.Right - Padding;
            var width = right - left;
            var y = client.Top + Padding - 4;

            DrawHeader(hdc, snapshot, palette, left, right, ref y);
            DrawProgress(hdc, snapshot, palette, left, right, ref y);
            DrawSegments(hdc, snapshot, palette, left, width, ref y);
            DrawCurrentCard(hdc, snapshot, palette, left, right, ref y);
            DrawNextRow(hdc, snapshot, palette, left, right, ref y);
            DrawQueue(hdc, snapshot, palette, left, right, client.Bottom, ref y);

            if (snapshot.IsFinished)
            {
                DrawCountdown(hdc, palette, left, width, ref y);
            }

            DrawButtons(hdc, snapshot, palette, left, width);
        }
        finally
        {
            _ = NativeMethods.EndPaint(hwnd, ref paint);
        }
    }

    private void DrawHeader(
        nint hdc,
        PanelSnapshot snapshot,
        PanelPalette palette,
        int left,
        int right,
        ref int y)
    {
        var top = y;
        var centerY = top + (HeaderHeight / 2);

        // logo 方块
        var logoRect = new NativeMethods.Rect
        {
            Left = left,
            Top = centerY - 10,
            Right = left + 20,
            Bottom = centerY + 10,
        };
        FillRounded(hdc, logoRect, 5, palette.Accent, palette.Accent);
        DrawText(hdc, _fontBold, palette.AccentFg, "D", logoRect.Left, logoRect.Top, logoRect.Right, logoRect.Bottom, DtCenter | DtVcenter | DtSingleline);

        // 品牌
        DrawText(hdc, _fontBold, palette.Text, "DelayStart", left + 28, top, left + 110, top + HeaderHeight, DtLeft | DtVcenter | DtSingleline);

        // 状态徽标
        var chipColor = snapshot.IsFinished
            ? (snapshot.HasFailure ? palette.Red : palette.Green)
            : palette.Accent;
        var chipText = snapshot.ChipText;
        var chipWidth = 14 + (chipText.Length * 12);
        var chipRect = new NativeMethods.Rect
        {
            Left = left + 112,
            Top = centerY - 9,
            Right = left + 112 + chipWidth,
            Bottom = centerY + 9,
        };
        FillRounded(hdc, chipRect, 9, palette.Card, palette.Card);
        DrawText(hdc, _fontSmall, chipColor, chipText, chipRect.Left, chipRect.Top, chipRect.Right, chipRect.Bottom, DtCenter | DtVcenter | DtSingleline);

        // 右上角唯一图标：钉（N8 —— 旧"置顶 + 关闭 ✕"已由钉 + 失焦关闭接替）
        var pinLeft = right - IconButtonSize;
        var iconTop = centerY - (IconButtonSize / 2);

        _pinRect = new NativeMethods.Rect
        {
            Left = pinLeft,
            Top = iconTop,
            Right = pinLeft + IconButtonSize,
            Bottom = iconTop + IconButtonSize,
        };

        DrawIconButton(hdc, _pinRect, palette, _hoverTarget == HitPin);

        y = top + HeaderHeight;
    }

    /// <summary>图标按钮底 + 图钉图形（GDI 线条，不依赖 MDL2 字体）。</summary>
    private void DrawIconButton(nint hdc, NativeMethods.Rect rect, PanelPalette palette, bool hover)
    {
        var active = _pinned;
        var background = hover || active ? palette.AccentSoft : palette.Background;
        FillRounded(hdc, rect, 6, background, background);

        var stroke = active ? palette.Accent : (hover ? palette.Text : palette.Text2);
        var centerX = rect.Left + (IconButtonSize / 2);
        var centerY = rect.Top + (IconButtonSize / 2);

        // 图钉：针杆 + 头部圆环
        DrawPin(hdc, centerX, centerY, stroke);
    }

    private static void DrawPin(nint hdc, int centerX, int centerY, uint color)
    {
        // 针杆
        DrawLine(hdc, centerX, centerY - 2, centerX, centerY + 6, color);

        // 头部空心圆（NULL_BRUSH 效果：用背景色刷 + 彩色笔）
        var pen = NativeMethods.CreatePen(0, 2, color);
        var previousPen = NativeMethods.SelectObject(hdc, pen);
        var brush = NativeMethods.CreateSolidBrush(color);
        var previousBrush = NativeMethods.SelectObject(hdc, brush);
        _ = NativeMethods.Ellipse(hdc, centerX - 4, centerY - 8, centerX + 4, centerY - 1);
        _ = NativeMethods.SelectObject(hdc, previousBrush);
        _ = NativeMethods.SelectObject(hdc, previousPen);
        _ = NativeMethods.DeleteObject(brush);
        _ = NativeMethods.DeleteObject(pen);
    }

    private static void DrawLine(nint hdc, int x1, int y1, int x2, int y2, uint color)
    {
        var pen = NativeMethods.CreatePen(0, 2, color);
        var previous = NativeMethods.SelectObject(hdc, pen);
        _ = NativeMethods.MoveToEx(hdc, x1, y1, 0);
        _ = NativeMethods.LineTo(hdc, x2, y2);
        _ = NativeMethods.SelectObject(hdc, previous);
        _ = NativeMethods.DeleteObject(pen);
    }

    private void DrawProgress(
        nint hdc,
        PanelSnapshot snapshot,
        PanelPalette palette,
        int left,
        int right,
        ref int y)
    {
        var top = y;

        DrawText(
            hdc,
            _fontLarge,
            palette.Text,
            $"{snapshot.DoneCount} / {snapshot.TotalCount}",
            left, top, left + 120, top + 30,
            DtLeft | DtVcenter | DtSingleline);
        DrawText(
            hdc,
            _fontSmall,
            palette.Muted,
            "已启动",
            left + 120, top, left + 170, top + 30,
            DtLeft | DtVcenter | DtSingleline);
        DrawText(
            hdc,
            _fontSmall,
            palette.Muted,
            snapshot.MetaText,
            left + 170, top, right, top + ProgressHeight,
            DtRight | DtVcenter | DtSingleline | DtEndEllipsis);

        y = top + ProgressHeight;
    }

    private static void DrawSegments(
        nint hdc,
        PanelSnapshot snapshot,
        PanelPalette palette,
        int left,
        int width,
        ref int y)
    {
        var top = y + 4;
        var count = Math.Max(snapshot.Items.Count, 1);
        var gap = count > 1 ? 3 : 0;
        var segmentWidth = (width - (gap * (count - 1))) / count;
        var extra = width - (gap * (count - 1)) - (segmentWidth * count);

        for (var index = 0; index < count; index++)
        {
            var state = index < snapshot.Items.Count ? snapshot.Items[index].State : RunItemState.Waiting;
            var segmentLeft = left + (index * (segmentWidth + gap));
            var segmentRight = segmentLeft + segmentWidth + (index == count - 1 ? extra : 0);
            var rect = new NativeMethods.Rect
            {
                Left = segmentLeft,
                Top = top,
                Right = segmentRight,
                Bottom = top + 6,
            };
            var brush = NativeMethods.CreateSolidBrush(SegmentColor(state, palette));
            _ = NativeMethods.FillRect(hdc, ref rect, brush);
            _ = NativeMethods.DeleteObject(brush);
        }

        y = top + 6 + 4;
    }

    private static uint SegmentColor(RunItemState state, PanelPalette palette) => state switch
    {
        RunItemState.Done => palette.Green,
        RunItemState.Launching => palette.Gold,
        RunItemState.Failed => palette.Red,
        RunItemState.Skipped => palette.Gray,
        _ => palette.Track,
    };

    private void DrawCurrentCard(
        nint hdc,
        PanelSnapshot snapshot,
        PanelPalette palette,
        int left,
        int right,
        ref int y)
    {
        var top = y;
        var cardRect = new NativeMethods.Rect
        {
            Left = left,
            Top = top,
            Right = right,
            Bottom = top + CurrentCardHeight - 8,
        };
        FillRounded(hdc, cardRect, 8, palette.Card, palette.Card);

        var centerY = cardRect.Top + ((cardRect.Bottom - cardRect.Top) / 2);
        DrawStatusDot(hdc, left + 12, centerY, snapshot.Current.State, palette);

        var textLeft = left + 26;
        DrawText(
            hdc,
            _fontBold,
            palette.Text,
            snapshot.Current.Name,
            textLeft, cardRect.Top + 8, right - 12, cardRect.Top + 26,
            DtLeft | DtSingleline | DtEndEllipsis);
        DrawText(
            hdc,
            _fontSmall,
            palette.Muted,
            snapshot.Current.Detail,
            textLeft, cardRect.Top + 26, right - 12, cardRect.Bottom - 6,
            DtLeft | DtSingleline | DtEndEllipsis);

        if (snapshot.Current.EtaSeconds is int eta)
        {
            DrawText(
                hdc,
                _fontLarge,
                palette.Accent,
                eta.ToString(System.Globalization.CultureInfo.InvariantCulture),
                right - 90, cardRect.Top + 6, right - 12, cardRect.Bottom - 14,
                DtRight | DtSingleline);
            DrawText(
                hdc,
                _fontSmall,
                palette.Muted,
                "秒后启动",
                right - 90, cardRect.Bottom - 16, right - 12, cardRect.Bottom - 2,
                DtRight | DtSingleline);
        }

        y = top + CurrentCardHeight;
    }

    private void DrawNextRow(
        nint hdc,
        PanelSnapshot snapshot,
        PanelPalette palette,
        int left,
        int right,
        ref int y)
    {
        var top = y;
        DrawText(hdc, _fontSmall, palette.Muted, "下一项", left, top, left + 50, top + NextRowHeight, DtLeft | DtVcenter | DtSingleline);
        DrawText(hdc, _fontSmall, palette.Text2, snapshot.NextName, left + 50, top, right, top + NextRowHeight, DtRight | DtVcenter | DtSingleline | DtEndEllipsis);
        y = top + NextRowHeight;
    }

    private void DrawQueue(
        nint hdc,
        PanelSnapshot snapshot,
        PanelPalette palette,
        int left,
        int right,
        int clientBottom,
        ref int y)
    {
        var top = y;
        var limit = clientBottom - Padding
            - (snapshot.IsFinished ? CountdownHeight : 0)
            - (snapshot.IsFinished ? ButtonHeight : (ButtonHeight * 2) + ButtonGap);
        var shown = 0;
        foreach (var row in snapshot.Items)
        {
            if (shown >= MaxVisibleRows || top + RowHeight > limit)
            {
                break;
            }

            DrawStatusDot(hdc, left + 6, top + (RowHeight / 2), row.State, palette);
            DrawText(
                hdc,
                _fontNormal,
                palette.Text2,
                row.Name,
                left + 18, top, right - 80, top + RowHeight,
                DtLeft | DtVcenter | DtSingleline | DtEndEllipsis);
            DrawText(
                hdc,
                _fontSmall,
                palette.Muted,
                row.RightText,
                right - 78, top, right, top + RowHeight,
                DtRight | DtVcenter | DtSingleline | DtEndEllipsis);

            top += RowHeight;
            shown++;
        }

        y = top;
    }

    private void DrawCountdown(nint hdc, PanelPalette palette, int left, int width, ref int y)
    {
        var top = y;
        var cardRect = new NativeMethods.Rect
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + CountdownHeight - 8,
        };
        FillRounded(hdc, cardRect, 8, palette.Card, _hoverPaused ? palette.Accent : palette.Card);

        var text = _hoverPaused
            ? "鼠标已移入 · 已暂停自动关闭（移开后继续）"
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "面板 {0} 秒后自动关闭", _autoCloseRemaining);
        DrawText(
            hdc,
            _fontSmall,
            _hoverPaused ? palette.Accent : palette.Muted,
            text,
            cardRect.Left + 11, cardRect.Top + 7, cardRect.Right - 11, cardRect.Top + 25,
            DtLeft | DtVcenter | DtSingleline);

        var barTop = cardRect.Top + 28;
        var trackRect = new NativeMethods.Rect
        {
            Left = cardRect.Left + 11,
            Top = barTop,
            Right = cardRect.Right - 11,
            Bottom = barTop + 3,
        };
        var trackBrush = NativeMethods.CreateSolidBrush(palette.Border);
        _ = NativeMethods.FillRect(hdc, ref trackRect, trackBrush);
        _ = NativeMethods.DeleteObject(trackBrush);

        if (_autoCloseTotal > 0 && _autoCloseRemaining > 0)
        {
            var span = trackRect.Right - trackRect.Left;
            var filled = (span * _autoCloseRemaining) / _autoCloseTotal;
            var fillRect = new NativeMethods.Rect
            {
                Left = trackRect.Left,
                Top = barTop,
                Right = trackRect.Left + filled,
                Bottom = barTop + 3,
            };
            var fillBrush = NativeMethods.CreateSolidBrush(_hoverPaused ? palette.Accent : palette.Muted);
            _ = NativeMethods.FillRect(hdc, ref fillRect, fillBrush);
            _ = NativeMethods.DeleteObject(fillBrush);
        }

        y = top + CountdownHeight;
    }

    private void DrawButtons(nint hdc, PanelSnapshot snapshot, PanelPalette palette, int left, int width)
    {
        if (snapshot.IsFinished)
        {
            // 完成态：横向两枚等宽 —— 打开 DelayStart（主）+ 调度日志（次）
            var half = (width - ButtonGap) / 2;
            _primaryRect = ButtonRect(left, half);
            _secondaryRect = ButtonRect(left + half + ButtonGap, half);
            _skipRect = default;

            DrawButton(hdc, _primaryRect, palette, snapshot.PrimaryButtonText, primary: true, enabled: true, hover: _hoverTarget == HitPrimary);
            DrawButton(hdc, _secondaryRect, palette, "调度日志", primary: false, enabled: true, hover: _hoverTarget == HitSecondary);
            return;
        }

        // 启动中：主按钮全宽 + 次行两枚（跳过剩余任务 / 调度日志）
        _primaryRect = ButtonRect(left, width);
        var secondTop = _primaryRect.Bottom + ButtonGap;
        var secondaryHalf = (width - ButtonGap) / 2;
        _skipRect = new NativeMethods.Rect
        {
            Left = left,
            Top = secondTop,
            Right = left + secondaryHalf,
            Bottom = secondTop + ButtonHeight,
        };
        _secondaryRect = new NativeMethods.Rect
        {
            Left = left + secondaryHalf + ButtonGap,
            Top = secondTop,
            Right = left + width,
            Bottom = secondTop + ButtonHeight,
        };

        var canSkip = false;
        foreach (var item in snapshot.Items)
        {
            if (item.State == RunItemState.Waiting)
            {
                canSkip = true;
                break;
            }
        }
        DrawButton(hdc, _primaryRect, palette, snapshot.PrimaryButtonText, primary: true, enabled: true, hover: _hoverTarget == HitPrimary);
        DrawButton(hdc, _skipRect, palette, "跳过剩余任务", primary: false, enabled: canSkip, hover: _hoverTarget == HitSkip);
        DrawButton(hdc, _secondaryRect, palette, "调度日志", primary: false, enabled: true, hover: _hoverTarget == HitSecondary);
    }

    private NativeMethods.Rect ButtonRect(int left, int width)
    {
        _ = NativeMethods.GetClientRect(_window, out var client);
        var bottom = client.Bottom - Padding;
        var top = bottom - ButtonHeight;

        // 启动中还有一行次按钮，主按钮要上移一行
        var snapshot = _snapshotProvider();
        if (!snapshot.IsFinished)
        {
            top -= ButtonHeight + ButtonGap;
            bottom -= ButtonHeight + ButtonGap;
        }

        return new NativeMethods.Rect { Left = left, Top = top, Right = left + width, Bottom = bottom };
    }

    private void DrawButton(
        nint hdc,
        NativeMethods.Rect rect,
        PanelPalette palette,
        string text,
        bool primary,
        bool enabled,
        bool hover)
    {
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            return;
        }

        var fill = primary ? palette.Accent : (hover ? palette.CardAlt : palette.Card);
        var border = primary ? palette.Accent : palette.Border;
        var foreground = primary ? palette.AccentFg : (enabled ? palette.Text2 : palette.Muted);

        FillRounded(hdc, rect, 7, fill, border);
        DrawText(hdc, _fontNormal, foreground, text, rect.Left, rect.Top, rect.Right, rect.Bottom, DtCenter | DtVcenter | DtSingleline | DtEndEllipsis);
    }

    /// <summary>圆角填充 + 描边（RoundRect 用当前 brush 填充、当前 pen 描边）。</summary>
    private static void FillRounded(nint hdc, NativeMethods.Rect rect, int radius, uint fill, uint border)
    {
        var brush = NativeMethods.CreateSolidBrush(fill);
        var pen = NativeMethods.CreatePen(0, 1, border);
        var previousBrush = NativeMethods.SelectObject(hdc, brush);
        var previousPen = NativeMethods.SelectObject(hdc, pen);

        _ = NativeMethods.RoundRect(
            hdc,
            rect.Left, rect.Top, rect.Right, rect.Bottom,
            radius, radius);

        _ = NativeMethods.SelectObject(hdc, previousPen);
        _ = NativeMethods.SelectObject(hdc, previousBrush);
        _ = NativeMethods.DeleteObject(pen);
        _ = NativeMethods.DeleteObject(brush);
    }

    private static void DrawText(
        nint hdc,
        nint font,
        uint color,
        string text,
        int left,
        int top,
        int right,
        int bottom,
        uint format)
    {
        if (font == 0)
        {
            return;
        }

        var rect = new NativeMethods.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        var previous = NativeMethods.SelectObject(hdc, font);
        _ = NativeMethods.SetTextColor(hdc, color);
        _ = NativeMethods.DrawTextW(hdc, text, -1, ref rect, format);
        _ = NativeMethods.SelectObject(hdc, previous);
    }

    private static void DrawStatusDot(nint hdc, int centerX, int centerY, RunItemState state, PanelPalette palette)
    {
        var color = state switch
        {
            RunItemState.Done => palette.Green,
            RunItemState.Launching => palette.Gold,
            RunItemState.Failed => palette.Red,
            RunItemState.Skipped => palette.Gray, // 跳过：实心灰（等待是空心灰，视觉可区分）
            _ => palette.Gray,
        };

        var brush = NativeMethods.CreateSolidBrush(color);

        if (state == RunItemState.Waiting)
        {
            // 等待中画空心点：灰笔描边 + NULL_BRUSH（不填充）
            var pen = NativeMethods.CreatePen(0, 1, palette.Gray);
            var nullBrush = NativeMethods.GetStockObject(NativeMethods.NullBrush);
            var previousPen = NativeMethods.SelectObject(hdc, pen);
            var previousBrush = NativeMethods.SelectObject(hdc, nullBrush);
            _ = NativeMethods.Ellipse(hdc, centerX - 4, centerY - 4, centerX + 4, centerY + 4);
            _ = NativeMethods.SelectObject(hdc, previousBrush);
            _ = NativeMethods.SelectObject(hdc, previousPen);
            _ = NativeMethods.DeleteObject(pen);
        }
        else
        {
            var previous = NativeMethods.SelectObject(hdc, brush);
            _ = NativeMethods.Ellipse(hdc, centerX - 5, centerY - 5, centerX + 5, centerY + 5);
            _ = NativeMethods.SelectObject(hdc, previous);
        }

        _ = NativeMethods.DeleteObject(brush);
    }
}
