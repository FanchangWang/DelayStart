using System.Runtime.InteropServices;

using DelayStart.App.Services;

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
/// （<c>coding-standards.md</c> 12：code-behind 只放视图相关逻辑）。
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;
    private readonly ShellNavigator _shellNavigator;
    private readonly ThemeService _themeService;

    /// <summary>构造主窗口。</summary>
    /// <param name="navigation">导航服务，由容器注入。</param>
    /// <param name="shellNavigator">跨页导航器，由容器注入；页面用它跳菜单。</param>
    /// <param name="handles">窗口句柄提供者。文件选择器 / 拖放等 WinRT 互操作要它。</param>
    /// <param name="themeService">主题服务。读取配置里的主题偏好并在切换时联动。</param>
    public MainWindow(
        NavigationService navigation,
        ShellNavigator shellNavigator,
        WindowHandleProvider handles,
        ThemeService themeService)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(shellNavigator);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(themeService);

        _navigation = navigation;
        _shellNavigator = shellNavigator;
        _themeService = themeService;

        InitializeComponent();

        // FR-9 主题设置：启动即应用配置里的主题偏好；设置页切换时实时跟随。
        _themeService.Load();
        RootGrid.RequestedTheme = ThemeService.ToElementTheme(_themeService.Current);
        _themeService.ThemeChanged += theme =>
            RootGrid.RequestedTheme = ThemeService.ToElementTheme(theme);

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

    /// <summary>唤起窗口并切到「运行日志」页（调度端气泡点击 / <c>--goto-log</c>，D18）。</summary>
    public void ShowRunsLog()
    {
        Program.LogGotoLog("ShowRunsLog：开始执行唤起。");
        BringToFront();

        // 选中即触发 SelectionChanged → 导航；先激活再切页，视觉上是"弹出并落在日志页"。
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem { Tag: "runs" } runsItem)
            {
                NavView.SelectedItem = runsItem;
                break;
            }
        }

        Program.LogGotoLog("ShowRunsLog：已激活窗口并切换到运行日志页。");
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
