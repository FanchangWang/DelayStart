using DelayStart.App.Dialogs;
using DelayStart.App.ViewModels;
using DelayStart.Core.Models;
using DelayStart.Management.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「自启动项」页（<c>design-spec.md</c> 页面 2）。本页只读系统里已经存在的自启动项。
/// </summary>
public sealed partial class ItemsPage : Page
{
    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public ItemsPage(ItemsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        // FR-1.2：进入即扫描一次。挂在 Loaded 而不是构造函数里，是为了让窗口先出现 ——
        // 扫描要读三个注册表 hive、两个启动文件夹、计划任务库与 UWP，首屏不该等它。
        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public ItemsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 页面对象与 Loaded 是一对一的（每次导航新建），但仍显式退订：
        // x:Load / 重入导航都可能让它触发第二次，那会变成一次多余的完整扫描。
        Loaded -= OnLoaded;

        if (ViewModel.RefreshCommand.CanExecute(null))
        {
            ViewModel.RefreshCommand.Execute(null);
        }
    }

    /// <summary>打开延时配置编辑器，确认后接管该条目。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    /// <remarks>
    /// 放在 code-behind 而不是 ViewModel 的 <c>RelayCommand</c>：弹窗需要 <c>XamlRoot</c>，
    /// 那是视图的概念；ViewModel 只负责"接管"这个动作本身（<c>coding-standards.md</c> 12）。
    /// </remarks>
    private async void OnDelayRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StartupEntryRow row })
        {
            return;
        }

        var dialog = new DelayEditorDialog(row.Entry, ViewModel.DelayPresets, ViewModel.MaxDelaySeconds)
        {
            // ContentDialog 必须挂到窗口的 XamlRoot 上，否则 ShowAsync 直接抛异常。
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var options = new TakeoverOptions
        {
            DelaySeconds = dialog.DelaySeconds,
            RunAsAdmin = dialog.RunAsAdmin,

            // 留空 = 沿用原自启动项自带的参数（FR-4.5）。传空串会被当成"显式指定无参数"。
            Arguments = string.IsNullOrWhiteSpace(dialog.Arguments) ? null : dialog.Arguments,
        };

        var outcome = ViewModel.Takeover(row.Entry, options);

        if (outcome.Succeeded)
        {
            // 重新扫描：该条目此刻应显示为「已接管」，列表副标题的计数也要跟着变。
            if (ViewModel.RefreshCommand.CanExecute(null))
            {
                ViewModel.RefreshCommand.Execute(null);
            }

            return;
        }

        // TakeoverOutcome.Message 理论上是非空的，但它是外部契约，界面层不能假设 ——
        // 兜底成一句用户能看懂的话，而不是把 null 显示成空白弹窗。
        await ShowFailureAsync(outcome.Message ?? "未给出具体原因，详情见运行日志。");
    }

    /// <summary>接管失败时给出可操作的提示。</summary>
    /// <param name="message">失败原因。</param>
    /// <returns>提示关闭后的 <see cref="Task"/>。</returns>
    /// <remarks>
    /// 这里不用 <c>ConfigureAwait</c>：<c>ContentDialog.ShowAsync</c> 返回的是
    /// <c>IAsyncOperation&lt;T&gt;</c>（WinRT 异步操作），它本身不提供 <c>ConfigureAwait</c>，
    /// 而 await 它默认就回到原 UI 上下文，正是我们要的。
    /// </remarks>
    private async Task ShowFailureAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "未能加入延时启动",
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }
}
