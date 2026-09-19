using DelayStart.App.Services;
using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DelayStart.App.Views;

/// <summary>
/// 「总览」页（UI v2，PowerToys 分节布局，<c>docs/ui-mockup-v2.html</c>）。
/// </summary>
/// <remarks>
/// ⚠️ <see cref="ToggleSwitch.Toggled"/> 的回灌问题：绑定 <c>IsOn</c> 随
/// <see cref="OverviewViewModel.IsTaskRegistered"/> 更新时也会触发 Toggled。
/// 判据与「延时启动」页的条目开关一致 —— 开关当前值等于 ViewModel 已记录值即为回灌，
/// 直接忽略；命令执行期间的连点由 ViewModel 的 <c>IsTogglingTask</c> 守卫挡掉。
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

    private void OnTaskToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        // 回灌：开关当前值与 ViewModel 已记录值一致，说明这次 Toggled 是绑定更新造成的。
        if (toggle.IsOn == ViewModel.IsTaskRegistered)
        {
            return;
        }

        if (ViewModel.ToggleTaskCommand.CanExecute(null))
        {
            ViewModel.ToggleTaskCommand.Execute(null);
        }
    }
}
