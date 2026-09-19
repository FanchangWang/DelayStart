using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace DelayStart.App;

/// <summary>
/// 管理端的应用对象。
/// </summary>
/// <remarks>
/// 🔴 <see cref="IServiceProvider"/> 由构造函数注入，**不是**静态单例。
/// 静态定位器会让"界面用了哪些服务"从类型签名里消失，而且容器生命周期
/// （随进程退出释放）就没有明确的持有者了。
/// </remarks>
public partial class App : Application
{
    private readonly IServiceProvider _services;

    private Window? _window;

    /// <summary>构造应用对象。</summary>
    /// <param name="services">共享容器，由 <see cref="Program"/> 构建一次后传入。</param>
    public App(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        InitializeComponent();
    }

    /// <summary>
    /// 应用启动时调用；解析主窗口并激活。
    /// </summary>
    /// <param name="args">启动参数。</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = _services.GetRequiredService<MainWindow>();
        _window.Activate();
    }
}
