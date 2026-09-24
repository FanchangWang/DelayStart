using DelayStart.App.Services;
using DelayStart.App.ViewModels;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DelayStart.App.Views;

/// <summary>
/// 「总览」页（UI v3，2026-09-21 批复；D117 分节合并为「后台任务」+「最近记录」；
/// D118 来源计数合并为「启动项」——蓝 = 接管（延时页）、灰 = 系统全部（自启动项页））。
/// </summary>
/// <remarks>
/// 2026-09-21 批复：开机调度任务从开关改为状态卡（缺失自动补建、失败给重试按钮），
/// 原先 <c>ToggleSwitch.Toggled</c> 的回灌守卫随之删除 —— 界面上已经没有开关了。
/// </remarks>
public sealed partial class OverviewPage : Page
{
    private readonly ShellNavigator _navigator;

    /// <summary>
    /// 初始化守卫：x:Bind 初始化 / <c>LoadAsync</c> 回灌档位时会触发一次
    /// <c>SelectionChanged</c> —— 那一**不能落盘**（否则每次进总览页都会重存一遍档位）。
    /// 与设置页 <c>SettingsPage._initialized</c> 同款做法。
    /// </summary>
    private bool _initialized;

    /// <summary>
    /// 启动项数字悬停用的手型光标（静态缓存，进程内共用一份；
    /// <c>InputSystemCursor</c> 实现了 <c>IDisposable</c>，但不 Dispose —— 页面可能反复进出）。
    /// </summary>
    private static readonly InputCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    /// <param name="navigator">跨页导航器：计数 chip 跳到对应菜单项。</param>
    public OverviewPage(OverviewViewModel viewModel, ShellNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(navigator);

        ViewModel = viewModel;
        _navigator = navigator;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public OverviewViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ViewModel.LoadAsync();
        _initialized = true;
    }

    /// <summary>「接管」点击（蓝数字，D118）：跳「延时启动」页。</summary>
    private void OnGoDelay(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.DelayTag);

    /// <summary>
    /// 启动项数字悬停：光标换手型（tooltip 由 XAML 的 <c>ToolTipService.ToolTip</c> 提供）。
    /// <c>ProtectedCursor</c> 是 protected 成员，设在本页（悬停元素的祖先）上即对子树生效，
    /// 离开时置回 <see langword="null"/> 恢复默认箭头（2026-09-24 D118 微调）。
    /// </summary>
    private void OnLinkPointerEntered(object sender, PointerRoutedEventArgs e) => ProtectedCursor = HandCursor;

    private void OnLinkPointerExited(object sender, PointerRoutedEventArgs e) => ProtectedCursor = null;

    /// <summary>灰数字点击（D118）：跳对应来源的「自启动项」页。</summary>
    private void OnGoRegistry(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.ItemsRegistryTag);

    private void OnGoFolder(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.ItemsFolderTag);

    private void OnGoTask(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.ItemsTaskTag);

    private void OnGoUwp(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.ItemsUwpTag);

    /// <summary>「查看调度日志 →」：跳调度日志页（2026-09-21 批复）。</summary>
    private void OnGoRuns(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.RunsTag);

    /// <summary>「查看守卫日志 →」：跳守卫日志页（D115）。</summary>
    private void OnGoGuardRuns(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.GuardRunsTag);

    /// <summary>守卫档位被用户改变：立即落盘并同步计划任务。⚠️ 回灌事件在这里被挡掉。</summary>
    private void OnGuardModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        if (sender is ComboBox { SelectedIndex: var index })
        {
            ViewModel.SetGuardIndex(index);
        }
    }
}
