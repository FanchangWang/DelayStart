using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「延时启动」页（<c>design-spec.md</c> 页面 3）。数据来自 <c>config.json</c>。
/// </summary>
public sealed partial class DelayPage : Page
{
    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public DelayPage(DelayViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public DelayViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // 读配置是同步的（小文件），但失效判定要扫一次系统 —— 走命令让它异步。
        if (ViewModel.RefreshCommand.CanExecute(null))
        {
            ViewModel.RefreshCommand.Execute(null);
        }
    }

    /// <summary>移除确认 + 执行。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    /// <remarks>
    /// 🔴 **两种变体文案不能合并成一句**：手动条目与系统条目被移除后对系统的影响完全不同
    /// （前者不动系统、后者恢复原自启动）。写成一句"确定要移除吗"等于让用户赌。
    /// 文案取自 <c>docs/ui-mockup.html</c> 的 <c>unlink()</c>。
    /// </remarks>
    private async void OnRemoveRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DelayRow row })
        {
            return;
        }

        var manual = row.Item.IsManual;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"移除「{row.Item.Name}」的延时启动？",
            Content = manual
                ? "该条目是手动添加的，不关联任何系统自启动项。移除后只删除本程序里的这条配置，系统的任何设置都不会被改动。"
                : "该程序的原始自启动项将恢复为你接管前的状态。下次登录时它会按系统原本的方式启动，不再受本程序控制。",
            PrimaryButtonText = manual ? "移除条目" : "移除并恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var outcome = ViewModel.Release(row);
        if (outcome.Succeeded)
        {
            return;
        }

        var failure = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "未能移出延时启动",
            Content = $"{outcome.Message}\n\n该条目仍保持接管状态 —— 可以稍后重试，详情见运行日志。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };

        await failure.ShowAsync();
    }
}
