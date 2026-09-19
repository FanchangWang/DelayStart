using DelayStart.App.Services;
using DelayStart.App.ViewModels;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App;

/// <summary>
/// 主窗口：标题栏 + 左侧导航 + 内容框架 + 底部调度任务状态条。
/// </summary>
/// <remarks>
/// 导航壳的职责只有两件：把菜单项映射到页面、显示调度任务状态。
/// 页面内容一律在各自的 Page + ViewModel 里，本类不碰业务
/// （<c>coding-standards.md</c> 12：code-behind 只放视图相关逻辑）。
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;

    /// <summary>构造主窗口。</summary>
    /// <param name="viewModel">外壳 ViewModel，由容器注入。</param>
    /// <param name="navigation">导航服务，由容器注入。</param>
    /// <param name="handles">窗口句柄提供者。文件选择器 / 拖放等 WinRT 互操作要它。</param>
    public MainWindow(ShellViewModel viewModel, NavigationService navigation, WindowHandleProvider handles)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(handles);

        ViewModel = viewModel;
        _navigation = navigation;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 登记主窗口句柄：unpackaged 的文件选择器必须经 InitializeWithWindow 挂到这个
        // 句柄上，否则 PickSingleFileAsync 在运行时直接抛异常。此刻 AppWindow 已可用。
        handles.Set(WinRT.Interop.WindowNative.GetWindowHandle(this));

        ViewModel.RefreshTaskStatus();

        // ⚠️ 顺序：必须在 InitializeComponent 之后设 SelectedItem ——
        // 写在 XAML 的 IsSelected 上会让 SelectionChanged 在 NavFrame 还没构造出来时触发。
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>外壳 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public ShellViewModel ViewModel { get; }

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
        // 保留分支是为了将来出现二级页面（如"查看来源详情"）时不必回头改。
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }
}
