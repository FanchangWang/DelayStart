using DelayStart.App.Services;
using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「调度日志」页（FR-8）。
/// </summary>
/// <remarks>
/// 实现 <see cref="IReloadablePage"/>：调度端的通知点击（<c>--goto-log</c>）落点就是本页，
/// 而"点通知时本页已经开着"时导航不会发生，必须由外壳调用 <see cref="Reload"/> 重读归档。
/// </remarks>
public sealed partial class RunsPage : Page, IReloadablePage
{
    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public RunsPage(RunsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public RunsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 🔴 **不摘绑**（2026-10-02）：调度端每跑完一轮就多一份归档，回到本页必须能看到。
        // 论证见 ItemsPage.OnLoaded。
        Reload();
    }

    /// <inheritdoc />
    public void Reload() => _ = ViewModel.LoadAsync();
}
