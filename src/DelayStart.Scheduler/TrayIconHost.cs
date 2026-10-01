namespace DelayStart.Scheduler;

using DelayStart.Core.Models;

/// <summary>
/// 托盘图标（信息分级 1 级，<c>design.md</c> 八）。
/// </summary>
/// <remarks>
/// <para>
/// 托盘气泡 + <c>Shell_NotifyIcon</c>，负责「打开管理端 / 打开调度日志 / 启动剩余项 / 跳过剩余」
/// 四个菜单动作与状态头（2026-09-21 批复 D1=A）。面板已于 v0.6.1 取消，菜单里不再有面板项。
/// 气泡通知点击 → 打开管理端调度日志（D18）。
/// </para>
/// <para>
/// 🔴 tooltip 上限 127 字符（<c>szTip</c> 缓冲 128 字符，含终止符）—— 写入时硬截断，
/// 超长文案宁可少一个字也不能让 <c>Shell_NotifyIcon</c> 报错。
/// 🔴 <b>多行是支持的</b>（<c>\r\n</c>，v0.6.1 起三行）：原先注释写的"不支持换行、标准托盘
/// tooltip 是单行控件"是**错的**，托盘 tooltip 本来就能按 \r\n 分行显示。
/// 所以真正的约束不是"单行"，而是 127 字的总预算 —— 文案合成放在 Core 的
/// <see cref="Core.Services.SchedulerTip"/>，它在合成时就把全文压进预算、
/// 且只截第三行，而不是写完再让这里砍（砍掉一半的句子比短句糟糕得多）。
/// </para>
/// <para>
/// 🔴 右键菜单必须先 <c>SetForegroundWindow</c> 再 <c>TrackPopupMenu</c>，选完或点掉后补发
/// <c>WM_NULL</c> —— 否则菜单点外面不会消失（经典前台窗口要求，微软 KB135788）。
/// </para>
/// </remarks>
internal sealed unsafe class TrayIconHost : IDisposable, NativeMethods.IMessageHandler
{
    private const ushort IconId = 1;
    private const int MaxTipLength = 127;

    // 菜单项 id（TrackPopupMenu + TPM_RETURNCMD 直接返回）
    private const uint MenuOpenManager = 1;
    private const uint MenuOpenRunLog = 2;
    private const uint MenuLaunchRemaining = 3;
    private const uint MenuSkipRemaining = 4;

    /// <summary>完成态菜单的「退出」：结束调度端进程（托盘一并移除）。</summary>
    private const uint MenuQuit = 6;

    /// <summary>菜单顶部的状态头：灰显不可点（id 0 不会被选中）。</summary>
    private const uint MenuStatusHeader = 0;

    private readonly IconResources? _icons;
    private readonly Action _openRunLog;
    private readonly Action _openManager;
    private readonly Action _launchRemainingNow;
    private readonly Action _skipRemaining;



    private readonly Action _quit;
    private readonly Func<bool> _hasWaitingItems;
    private readonly Func<bool> _isFinished;
    private readonly Func<string> _statusText;

    private nint _window;
    private bool _iconAdded;

    /// <summary>构造托盘图标宿主。</summary>
    /// <param name="icons">图标资源（正常 / 告警两枚）；为 <see langword="null"/> 时只建窗口不显示图标。</param>
    /// <param name="openRunLog">「查看调度日志」动作（气泡点击用）。</param>
    /// <param name="openManager">「打开管理端」动作（右键菜单）。</param>
    /// <param name="launchRemainingNow">「立即启动全部剩余条目」动作（右键菜单）。</param>
    /// <param name="skipRemaining">「跳过剩余任务并退出」动作（启动中菜单，文案不动；跳过后直接收尾退出）。</param>
    /// <param name="quit">「退出」动作（完成态菜单用）：结束调度端进程，托盘一并移除。</param>
    /// <param name="hasWaitingItems">是否还有等待条目（决定启动中菜单后两项的可用态）。</param>
    /// <param name="isFinished">是否已完成（决定菜单取「启动中」还是「启动完毕」那一套）。</param>
    /// <param name="statusText">菜单顶部的状态头文案。</param>
    public TrayIconHost(
        IconResources? icons,
        Action openRunLog,
        Action openManager,
        Action launchRemainingNow,
        Action skipRemaining,
        Action quit,
        Func<bool> hasWaitingItems,
        Func<bool> isFinished,
        Func<string> statusText)
    {
        ArgumentNullException.ThrowIfNull(openRunLog);
        ArgumentNullException.ThrowIfNull(openManager);
        ArgumentNullException.ThrowIfNull(launchRemainingNow);
        ArgumentNullException.ThrowIfNull(skipRemaining);
        ArgumentNullException.ThrowIfNull(quit);
        ArgumentNullException.ThrowIfNull(hasWaitingItems);
        ArgumentNullException.ThrowIfNull(isFinished);
        ArgumentNullException.ThrowIfNull(statusText);

        _icons = icons;
        _openRunLog = openRunLog;
        _openManager = openManager;
        _launchRemainingNow = launchRemainingNow;
        _skipRemaining = skipRemaining;
        _quit = quit;
        _hasWaitingItems = hasWaitingItems;
        _isFinished = isFinished;
        _statusText = statusText;
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

    /// <summary>挂定时器（调度引擎的节拍）。</summary>
    /// <param name="intervalMilliseconds">节拍间隔毫秒数。</param>
    public void StartTimer(uint intervalMilliseconds) => NativeMethods.SetTimer(_window, 1, intervalMilliseconds, 0);

    /// <summary>卸掉定时器。</summary>
    public void StopTimer() => NativeMethods.KillTimer(_window, 1);

    /// <summary>调度引擎的定时器回调（<c>WM_TIMER</c> 节拍）。</summary>
    public Action? TimerTick { get; set; }





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
            // 🔴 左右键**都**弹同一个菜单。
            //
            // 之前左键是 `_panel.Toggle()`，而面板已于 v0.6.1 取消 —— 左键若还留 Toggle
            // 就没有任何调用方了，那一行会变成死代码，"左键什么也不发"对用户来说
            // 就是托盘图标坏了。
            //
            // ⚠️ 待验：左键弹菜单在 Windows 惯例上略反常（惯例是左键直接单击），
            // 需真机确认 ShowContextMenu 里的前台约束处理（SetForegroundWindow + KB135788）
            // 在左键下同样成立 —— 那个约束是菜单能正常消失的关键。
            if (action is NativeMethods.NimLup or NativeMethods.NimRup)
            {
                ShowContextMenu();
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

        if (_window != 0)
        {
            _ = NativeMethods.DestroyWindow(_window);
            _window = 0;
        }
    }

    /// <summary>弹出右键上下文菜单（2026-09-21 批复 D1=A）。</summary>
    /// <remarks>
    /// 菜单在打开时动态构建：有等待条目时「立即启动/跳过」可用，否则置灰 ——
    /// 调度收尾阶段菜单不应给出无效操作。走 <c>TPM_RETURNCMD</c> 同步拿回选择，
    /// 免去 <c>WM_COMMAND</c> 分发（隐藏窗口没有命令路由需求）。
    /// </remarks>
    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            // UI v2（2026-09-21 批复）：菜单随状态切换两套 —— 启动中 / 启动完毕。
            // 顶部状态头灰显不可点（id 0，TrackPopupMenu 不会返回它）。
            var finished = _isFinished();
            _ = NativeMethods.AppendMenuW(
                menu,
                NativeMethods.MfString | NativeMethods.MfGrayed,
                MenuStatusHeader,
                _statusText());
            _ = NativeMethods.AppendMenuW(menu, NativeMethods.MfString, MenuOpenManager, "打开 DelayStart");

            if (finished)
            {
                _ = NativeMethods.AppendMenuW(menu, NativeMethods.MfString, MenuOpenRunLog, "查看调度日志");
                _ = NativeMethods.AppendMenuW(menu, NativeMethods.MfSeparator, 0, string.Empty);
                _ = NativeMethods.AppendMenuW(menu, NativeMethods.MfString, MenuQuit, "退出");
            }
            else
            {
                var hasWaiting = _hasWaitingItems();
                _ = NativeMethods.AppendMenuW(menu, NativeMethods.MfSeparator, 0, string.Empty);
                _ = NativeMethods.AppendMenuW(
                    menu,
                    NativeMethods.MfString | (hasWaiting ? 0 : NativeMethods.MfGrayed),
                    MenuLaunchRemaining,
                    "立即启动剩余任务");
                _ = NativeMethods.AppendMenuW(
                    menu,
                    NativeMethods.MfString | (hasWaiting ? 0 : NativeMethods.MfGrayed),
                    MenuSkipRemaining,
                    "跳过剩余任务并退出");
            }

            // 经典前台窗口技巧：TrackPopupMenu 前抢前台、选完后补 WM_NULL，
            // 否则菜单在点击菜单外区域时不会消失（KB135788）。
            _ = NativeMethods.SetForegroundWindow(_window);
            _ = NativeMethods.GetCursorPos(out var cursor);
            var chosen = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmNonotify | NativeMethods.TpmReturncmd,
                cursor.X,
                cursor.Y,
                0,
                _window,
                0);
            _ = NativeMethods.PostMessageW(_window, NativeMethods.WmNull, 0, 0);

            switch ((uint)chosen)
            {
                case MenuOpenManager:
                    _openManager();
                    break;
                case MenuOpenRunLog:
                    _openRunLog();
                    break;
                case MenuLaunchRemaining:
                    _launchRemainingNow();
                    break;
                case MenuSkipRemaining:
                    _skipRemaining();
                    break;
                case MenuQuit:
                    _quit();
                    break;
            }
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
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