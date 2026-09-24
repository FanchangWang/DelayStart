using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「守卫日志」页（D115 引入 / D116 改版）：按次分组的守卫巡检记录，最新在前。
/// </summary>
/// <remarks>
/// 不是通知落点（<see cref="DelayStart.App.Services.IReloadablePage"/> 是给调度端通知点击用的），
/// 所以只挂 <c>Loaded</c> 一次性加载；重进页面 = 容器给一套新实例，数据自然重读。
/// </remarks>
public sealed partial class GuardRunsPage : Page
{
    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public GuardRunsPage(GuardRunsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public GuardRunsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = ViewModel.LoadAsync();
    }
}
