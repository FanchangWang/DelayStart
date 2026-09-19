using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「总览」页（<c>design-spec.md</c> 页面 1）。
/// </summary>
public sealed partial class OverviewPage : Page
{
    /// <summary>模拟时钟：100ms 一次 = 模拟时间 10 倍速推进（D37）。</summary>
    private readonly DispatcherTimer _simulationTimer;

    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public OverviewPage(OverviewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        // 先建表再订阅：lambda 里要解引用 _simulationTimer，别让可空分析报警。
        _simulationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _simulationTimer.Tick += (_, _) => _ = ViewModel.AdvanceSimulation();

        // 模拟结束时 ViewModel 会把 Simulating 置回 false，这里顺势停表。
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(OverviewViewModel.Simulating) && !ViewModel.Simulating)
            {
                _simulationTimer.Stop();
            }
        };

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public OverviewViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = ViewModel.LoadAsync();
    }
}
