using DelayStart.App.Services;
using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「总览」页（UI v3，2026-09-21 批复）。
/// </summary>
/// <remarks>
/// 2026-09-21 批复：开机调度任务从开关改为状态卡（缺失自动补建、失败给重试按钮），
/// 原先 <c>ToggleSwitch.Toggled</c> 的回灌守卫随之删除 —— 界面上已经没有开关了。
/// </remarks>
public sealed partial class OverviewPage : Page
{
    private readonly ShellNavigator _navigator;

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = ViewModel.LoadAsync();
    }

    private void OnGoRegistry(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.ItemsRegistryTag);

    private void OnGoFolder(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.ItemsFolderTag);

    private void OnGoTask(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.ItemsTaskTag);

    private void OnGoUwp(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.ItemsUwpTag);

    /// <summary>「手动添加」chip：手动条目只存在于延时列表，跳延时启动页。</summary>
    private void OnGoDelay(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.DelayTag);

    /// <summary>「查看运行日志 →」：跳运行日志页（2026-09-21 批复）。</summary>
    private void OnGoRuns(object sender, RoutedEventArgs e) => _navigator.Navigate(NavigationService.RunsTag);
}
