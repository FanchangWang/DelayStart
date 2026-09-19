using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「设置」页（FR-9）。改动即时落盘，校验失败整体不保存。
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public SettingsPage(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public SettingsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ViewModel.Load();
    }

    /// <summary>保存全部设置；失败原因显示在底部状态条（级别随 ViewModel 联动）。</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveCommand.Execute(null);
    }
}
