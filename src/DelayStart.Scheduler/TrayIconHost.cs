namespace DelayStart.Scheduler;

/// <summary>
/// 托盘图标（信息分级 1 级，<c>scheduler-design.md</c> 第三节）。
/// </summary>
/// <remarks>
/// <para>
/// 隐藏消息窗口 + <c>Shell_NotifyIcon</c>。左键点击 → 打开弹出面板；
/// 气泡通知点击 → 打开管理端运行日志（D18）。
/// </para>
/// <para>
/// 🔴 tooltip 上限 63 字符且不支持换行 —— 写入时硬截断，超长文案宁可少一个字
/// 也不能让 <c>Shell_NotifyIcon</c> 报错。
/// </para>
/// </remarks>
internal sealed unsafe class TrayIconHost : IDisposable, NativeMethods.IMessageHandler
{
    private const ushort IconId = 1;
    private const int MaxTipLength = 63;

    private readonly IconResources? _icons;
    private readonly PanelWindow _panel;
    private readonly Action _openRunLog;

    private nint _window;
    private bool _iconAdded;

    /// <summary>构造托盘图标宿主。</summary>
    /// <param name="icons">图标资源（正常 / 告警两枚）；为 <see langword="null"/> 时只建窗口不显示图标。</param>
    /// <param name="snapshotProvider">面板数据源（引擎的即时快照）。</param>
    /// <param name="openRunLog">「查看运行日志」动作（气泡点击与面板按钮共用）。</param>
    public TrayIconHost(IconResources? icons, Func<PanelSnapshot> snapshotProvider, Action openRunLog)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(openRunLog);

        _icons = icons;
        _openRunLog = openRunLog;
        _panel = new PanelWindow(snapshotProvider, openRunLog);
    }

    /// <summary>内部窗口句柄（调度引擎的定时器挂在它上面）。</summary>
    public nint Window => _window;

    /// <summary>创建隐藏窗口并（可选地）注册托盘图标。</summary>
    /// <param name="initialTip">初始 tooltip。</param>
    /// <param name="withIcon">是否注册托盘图标。短任务或用户关闭设置时为 <see langword="false"/>，窗口只承载定时器。</param>
    /// <returns>是否成功。失败时调用方按「无托盘」降级运行（图标本就是可关闭设置）。</returns>
    public bool TryCreate(string initialTip, bool withIcon = true)
    {
        if (!NativeMethods.RegisterClass("DelayStart.TrayWnd", backgroundBrush: 0))
        {
            return false;
        }

        _window = NativeMethods.CreateWindowExW(
            0,
            "DelayStart.TrayWnd",
            string.Empty,
            0, // 不可见
            0, 0, 0, 0,
            0, 0, NativeMethods.GetModuleHandle(), 0);

        if (_window == 0)
        {
            return false;
        }

        NativeMethods.AttachHandler(_window, this);
        _panel.TryCreate();

        if (!withIcon || _icons is null)
        {
            return true;
        }

        var data = new NativeMethods.NotifyIconDataW
        {
            CbSize = (uint)sizeof(NativeMethods.NotifyIconDataW),
            HWnd = _window,
            UId = IconId,
            UCallbackMessage = NativeMethods.WmTrayCallback,
        };

        if (withIcon && _icons is not null)
        {
            data.UFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip;
            data.HIcon = _icons.Normal;
        }
        else
        {
            data.UFlags = NativeMethods.NifMessage | NativeMethods.NifTip;
        }

        CopyTip(ref data, initialTip);

        _iconAdded = NativeMethods.Shell_NotifyIconW(NativeMethods.NimAdd, ref data);
        return _iconAdded;
    }

    /// <summary>更新悬停 tooltip（超长自动截断到 63 字符）。</summary>
    /// <param name="tip">单行文案。</param>
    public void UpdateTip(string tip)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = new NativeMethods.NotifyIconDataW
        {
            CbSize = (uint)sizeof(NativeMethods.NotifyIconDataW),
            HWnd = _window,
            UId = IconId,
            UFlags = NativeMethods.NifTip,
        };
        CopyTip(ref data, tip);
        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NimModify, ref data);
    }

    /// <summary>切换托盘图标（完成但有失败时切红色角标）。</summary>
    /// <param name="icon">目标图标句柄。</param>
    public void SetIcon(nint icon)
    {
        if (!_iconAdded || icon == 0)
        {
            return;
        }

        var data = new NativeMethods.NotifyIconDataW
        {
            CbSize = (uint)sizeof(NativeMethods.NotifyIconDataW),
            HWnd = _window,
            UId = IconId,
            UFlags = NativeMethods.NifIcon,
            HIcon = icon,
        };
        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NimModify, ref data);
    }

    /// <summary>发一条气泡通知（信息分级 3 级）。</summary>
    /// <param name="title">标题（≤ 64 字符）。</param>
    /// <param name="text">正文（≤ 256 字符）。</param>
    public void ShowBalloon(string title, string text)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = new NativeMethods.NotifyIconDataW
        {
            CbSize = (uint)sizeof(NativeMethods.NotifyIconDataW),
            HWnd = _window,
            UId = IconId,
            UFlags = NativeMethods.NifInfo,
            DwInfoFlags = 0, // NIIF_NONE
        };
        CopyInfo(ref data, title);
        CopyText(ref data, text);

        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NimModify, ref data);
    }

    /// <summary>挂定时器（调度引擎的节拍）。</summary>
    /// <param name="intervalMilliseconds">节拍间隔毫秒数。</param>
    public void StartTimer(uint intervalMilliseconds) => NativeMethods.SetTimer(_window, 1, intervalMilliseconds, 0);

    /// <summary>卸掉定时器。</summary>
    public void StopTimer() => NativeMethods.KillTimer(_window, 1);

    /// <summary>调度引擎的定时器回调（<c>WM_TIMER</c> 节拍）。</summary>
    public Action? TimerTick { get; set; }

    /// <summary>面板状态变化后的重绘入口。</summary>
    public void RefreshPanel() => _panel.Refresh();

    /// <inheritdoc />
    public nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == NativeMethods.WmTimer)
        {
            TimerTick?.Invoke();
            return 0;
        }

        if (message == NativeMethods.WmTrayCallback)
        {
            var action = (uint)(lParam.ToInt64() & 0xFFFF);
            if (action == NativeMethods.NimLup)
            {
                _panel.Toggle();
            }
            else if (action == NativeMethods.NinBalloonUserClick)
            {
                _openRunLog();
            }

            return 0;
        }

        if (message == NativeMethods.WmDestroy)
        {
            RemoveIcon();
        }

        return 0;
    }

    public void Dispose()
    {
        RemoveIcon();
        _panel.Dispose();

        if (_window != 0)
        {
            _ = NativeMethods.DestroyWindow(_window);
            _window = 0;
        }
    }

    private void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = new NativeMethods.NotifyIconDataW
        {
            CbSize = (uint)sizeof(NativeMethods.NotifyIconDataW),
            HWnd = _window,
            UId = IconId,
        };
        _ = NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref data);
        _iconAdded = false;
    }

    private static void CopyTip(ref NativeMethods.NotifyIconDataW data, string tip)
    {
        fixed (char* buffer = data.SzTip)
        {
            CopyInto(buffer, 128, tip, MaxTipLength);
        }
    }

    private static void CopyInfo(ref NativeMethods.NotifyIconDataW data, string info)
    {
        fixed (char* buffer = data.SzInfo)
        {
            CopyInto(buffer, 256, info, 256);
        }
    }

    private static void CopyText(ref NativeMethods.NotifyIconDataW data, string text)
    {
        fixed (char* buffer = data.SzInfoTitle)
        {
            CopyInto(buffer, 64, text, 64);
        }
    }

    /// <summary>把字符串写进 NOTIFYICONDATA 的定长字符缓冲，超长截断并以 0 结尾。</summary>
    private static unsafe void CopyInto(char* destination, int capacity, string? source, int displayLimit)
    {
        if (string.IsNullOrEmpty(source))
        {
            *destination = '\0';
            return;
        }

        var writable = Math.Min(source.Length, Math.Min(capacity - 1, displayLimit));
        source.AsSpan(0, writable).CopyTo(new Span<char>(destination, writable));
        destination[writable] = '\0';
    }
}
