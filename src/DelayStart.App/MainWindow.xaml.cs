using System.Runtime.InteropServices;

using DelayStart.App.Services;
using DelayStart.Core.Launch;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App;

/// <summary>
/// 主窗口：标题栏 + 左侧导航 + 内容框架（UI v2，底部状态条已移除）。
/// </summary>
/// <remarks>
/// 导航壳的职责只有两件：把菜单项映射到页面、向 <see cref="ShellNavigator"/> 登记外壳。
/// 页面内容一律在各自的 Page + ViewModel 里，本类不碰业务
/// （<c>design.md</c> 9.5：code-behind 只放视图相关逻辑）。
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;
    private readonly ShellNavigator _shellNavigator;
    private readonly ThemeService _themeService;

    /// <summary>通知自动消失的定时器（右下角 toast，2026-09-21 批复）。</summary>
    private DispatcherQueueTimer? _toastTimer;

    /// <summary>构造主窗口。</summary>
    /// <param name="navigation">导航服务，由容器注入。</param>
    /// <param name="shellNavigator">跨页导航器，由容器注入；页面用它跳菜单。</param>
    /// <param name="handles">窗口句柄提供者。文件选择器 / 拖放等 WinRT 互操作要它。</param>
    /// <param name="themeService">主题服务。读取配置里的主题偏好并在切换时联动。</param>
    /// <param name="toastService">应用内通知服务。设置页等处的成功提示经它广播到右下角。</param>
    public MainWindow(
        NavigationService navigation,
        ShellNavigator shellNavigator,
        WindowHandleProvider handles,
        ThemeService themeService,
        ToastService toastService)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(shellNavigator);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(toastService);

        _navigation = navigation;
        _shellNavigator = shellNavigator;
        _themeService = themeService;

        InitializeComponent();

        // FR-9 主题设置：主题偏好由 ThemeService 构造时读入；设置页切换时实时跟随。
        RootGrid.RequestedTheme = ThemeService.ToElementTheme(_themeService.Current);
        _themeService.ThemeChanged += theme =>
            RootGrid.RequestedTheme = ThemeService.ToElementTheme(theme);

        // 右下角通知：广播 → 显示 → 3.5 秒后自动消失；连发时重置计时。
        toastService.Requested += ShowToast;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 登记主窗口句柄：unpackaged 的文件选择器必须经 InitializeWithWindow 挂到这个
        // 句柄上，否则 PickSingleFileAsync 在运行时直接抛异常。此刻 AppWindow 已可用。
        handles.Set(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // 2026-09-20 批复：默认宽高 1400×900 DIP、最小 960×600（参考 PowerToys 的设置窗口，
        // 见 Interop/WindowSizing）。必须赶在窗口首次显示前落定 —— 显示后再改尺寸会看到跳变。
        Interop.WindowSizing.ApplyInitialSize(AppWindow, handles.Handle);
        Interop.WindowSizing.EnforceMinimum(handles.Handle);

        // R10：本程序提权运行（高完整性），不放开 UIPI 过滤，资源管理器往窗口里
        // 拖文件会被系统静默拦截（鼠标变禁止）。窗口创建时放行拖放消息白名单；
        // 🔴 再做一次**进程级**放行：ContentDialog / Popup 的弹层句柄是懒创建的，
        // 按句柄放行有创建时序漏洞 —— 进程级一次放行覆盖现在和将来的一切窗口。
        Interop.UipiMessageFilter.AllowProcessWide();

        // 批复 4：XAML 拖放在提权进程里被 UIPI 拦死（OLE 封送失败），改走经典
        // WM_DROPFILES 路径 —— DragAcceptFiles + WndProc 子类化。子窗口此刻可能
        // 尚未创建，首次激活时再补一轮（幂等）；弹窗打开后还会再跑一次。
        Interop.FileDropReceiver.EnableTree(handles.Handle);
        Activated += OnWindowActivated;

        // 页面 → 菜单的跨页跳转（总览计数 chip 等）从这里拿外壳引用。
        _shellNavigator.Attach(NavFrame, NavView);

        // 🔴 运行时插入分隔线：XAML 里的 <NavigationViewSeparator /> 会让 XamlCompiler
        // pass-1 静默崩溃（WinAppSDK 1.8.260224000，退出码 1 零输出），只能绕行。
        InsertSeparatorAfter("delay");
        InsertSeparatorAfter("system");

        // ⚠️ 顺序：必须在 InitializeComponent 之后设 SelectedItem ——
        // 写在 XAML 的 IsSelected 上会让 SelectionChanged 在 NavFrame 还没构造出来时触发。
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>窗口激活：补一轮拖放启用，覆盖激活后才创建的子 / 弹层 HWND（幂等）。</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        Interop.FileDropReceiver.EnableTree(
            WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    /// <summary>显示右下角应用内通知，3.5 秒后自动消失（连发时重置计时）。</summary>
    /// <param name="message">要展示的文本。</param>
    /// <remarks>
    /// 事件来自 <see cref="ToastService"/> 的广播。⚠️ 广播线程不保证是 UI 线程
    /// （ViewModel 侧都是 UI 线程调用，但契约上不承诺），经 DispatcherQueue 切回 UI 线程。
    /// </remarks>
    private void ShowToast(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ToastText.Text = message;
            ToastPanel.Visibility = Visibility.Visible;

            _toastTimer ??= DispatcherQueue.CreateTimer();
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(3500);
            _toastTimer.IsRepeating = false;
            _toastTimer.Tick += OnToastTimerTick;
            _toastTimer.Start();
        });
    }

    private void OnToastTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Tick -= OnToastTimerTick;
        sender.Stop();
        ToastPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>在指定 Tag 的顶层菜单项之后插入分隔线。</summary>
    /// <param name="tag">锚点菜单项标签。</param>
    private void InsertSeparatorAfter(string tag)
    {
        for (var i = 0; i < NavView.MenuItems.Count; i++)
        {
            if (NavView.MenuItems[i] is NavigationViewItem { Tag: string candidate } && candidate == tag)
            {
                NavView.MenuItems.Insert(i + 1, new NavigationViewItemSeparator());
                return;
            }
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            _navigation.Navigate(NavFrame, tag);
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        // 顶层模块之间没有"上一步"关系，所以这个按钮实际上不会出现
        // （IsBackButtonVisible 绑的是 NavFrame.CanGoBack，恒为 false）。
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    /// <summary>唤起窗口并切到「调度日志」页（调度端气泡点击 / <c>--goto-log</c>，D18）。</summary>
    public void ShowRunsLog()
    {
        Program.LogGotoLog("ShowRunsLog：开始执行唤起。");
        BringToFront();

        _ = SelectMenu(NavigationService.RunsTag);

        // 无论"新切过去"还是"本来就在这一页"都要重载：前者页面会拿缓存/上次读到的归档
        // 直接显示，后者连导航都不会发生 —— 两种情况用户看到的都是旧数据。
        ReloadCurrentPage();
        Program.LogGotoLog("ShowRunsLog：已激活窗口并切换到调度日志页。");
    }

    /// <summary>
    /// 唤起窗口并落到指定位置（系统通知点击 / <c>--goto-startup</c>，D74）。
    /// </summary>
    /// <param name="target">跨进程定位令牌，取值见 <see cref="UiNavigationTarget"/>。</param>
    /// <remarks>
    /// <para>
    /// 令牌 → 菜单项走 <see cref="UiTargetNavigation"/>；未知令牌只前置窗口、不切页
    /// （宁可停在原处，也不要把用户带到不相干的位置）。
    /// </para>
    /// <para>
    /// 方法名保留 <c>ShowStartup</c>（历史命名，当初只服务「自启动项」来源页）——
    /// 现在令牌也可能是 <c>delay</c>（失效条目自 D81 起并入「延时启动」页），
    /// 但它做的事没变：前置窗口 + 选中一个菜单项。
    /// </para>
    /// <para>
    /// 🔴 <b>定位之后必须重载数据，两种情况都是</b>（2026-09-22 用户回报"点通知不刷新
    /// 延时启动的数据，需要手动刷新"）：
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>目标页就是当前页</b> —— 定位靠"给 <c>NavigationView.SelectedItem</c> 赋新值 →
    /// <c>SelectionChanged</c> → 换页"，而给同一个项重新赋值**不会触发事件**，
    /// 页面既不重建、<c>Loaded</c> 也不再触发，数据停在原样；
    /// </description></item>
    /// <item><description>
    /// <b>确实是切过去</b> —— 目标页读的是**启动时那份扫描缓存**（bug#7 的秒回优化），
    /// 而通知说的"有变化"恰恰是缓存里还没有的信息：不强制重载就看不见那条新增项，
    /// 用户会以为通知是假的。
    /// </description></item>
    /// </list>
    /// <para>
    /// 只负责导航，**不读请求文件** —— 那属于跨进程协议，由 <see cref="App"/> 处理。
    /// 本类保持"只做视图相关的事"（<c>design.md</c> 9.5）。
    /// </para>
    /// </remarks>
    public void ShowStartup(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        Program.LogGotoLog($"ShowStartup：开始执行唤起，目标令牌『{target}』。");
        BringToFront();

        var tag = UiTargetNavigation.TagFor(target);
        if (tag is null)
        {
            Program.LogGotoLog($"ShowStartup：未知定位令牌『{target}』，仅前置窗口。");
            return;
        }

        var switched = SelectMenu(tag);
        ReloadCurrentPage();
        Program.LogGotoLog($"ShowStartup：{(switched ? "已切换到" : "已是")}『{tag}』页，并已要求该页重载数据。");
    }

    /// <summary>
    /// 选中带指定 Tag 的菜单项（选中即触发 <c>SelectionChanged</c> → 导航）。
    /// </summary>
    /// <param name="tag">菜单项 Tag。</param>
    /// <returns>是否**发生了一次切换**（<see langword="false"/> = 菜单里没有它，或当前已停在那一项上）。</returns>
    /// <remarks>
    /// <para>
    /// 🔴 **必须递归**：「自启动项」下的四个来源页是**子菜单项**
    /// （<c>NavigationViewItem.MenuItems</c>），只扫顶层会永远找不到，
    /// 表现成"点了查看没反应"。
    /// </para>
    /// <para>
    /// 返回值现在只用于日志（重载是无条件做的），但保留它是因为"有没有真的换页"
    /// 是排查唤起问题时第一个要看的分支 —— 让调用方不必再去猜。
    /// </para>
    /// </remarks>
    private bool SelectMenu(string tag)
    {
        var item = FindMenuItem(NavView.MenuItems, tag);
        if (item is null)
        {
            return false;
        }

        // 按 Tag 而不是按引用比较：菜单项可能被重建（分隔线是运行时插进去的），
        // 而"当前停在哪个页面"这件事由 Tag 唯一确定。
        if (NavView.SelectedItem is NavigationViewItem { Tag: string current } && current == tag)
        {
            return false;
        }

        NavView.SelectedItem = item;
        return true;
    }

    /// <summary>要求当前显示的页面重读自己的数据（页面没实现 <see cref="IReloadablePage"/> 时什么也不做）。</summary>
    private void ReloadCurrentPage()
    {
        if (NavFrame.Content is IReloadablePage page)
        {
            page.Reload();
        }
    }

    /// <summary>在菜单树里按 Tag 深度优先找一个菜单项。</summary>
    private static NavigationViewItem? FindMenuItem(IList<object> items, string tag)
    {
        foreach (var entry in items)
        {
            if (entry is NavigationViewItem item)
            {
                if (item.Tag is string candidate && candidate == tag)
                {
                    return item;
                }

                if (FindMenuItem(item.MenuItems, tag) is { } nested)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    /// <summary>仅把已有窗口带到前台（普通启动撞单实例互斥时的唤起语义，不切页）。</summary>
    public void ShowForeground()
    {
        Program.LogGotoLog("ShowForeground：前置已有管理端窗口。");
        BringToFront();
    }

    /// <summary>
    /// 把窗口恢复并带到前台。🔴 Windows 前台锁会静默拒绝后台进程直接抢前台
    /// （SetForegroundWindow 返回 false 只闪任务栏），所以失败时走
    /// 最小化→还原路径 —— SW_RESTORE 走的是系统允许的激活路径，能真正弹到最前。
    /// </summary>
    private void BringToFront()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }
        else if (!SetForegroundWindow(hwnd))
        {
            _ = ShowWindow(hwnd, SwMinimize);
            _ = ShowWindow(hwnd, SwRestore);
        }

        _ = SetForegroundWindow(hwnd);
        Activate();
    }

    private const int SwRestore = 9;
    private const int SwMinimize = 6;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);
}
