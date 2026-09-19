using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Services;

/// <summary>
/// 顶层模块之间的导航（<c>design-spec.md</c> 二、信息架构）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接用 <see cref="Frame.Navigate(Type)"/>：那样框架会用
/// <c>Activator.CreateInstance</c> 构造页面，只认无参构造函数 —— 而本项目的页面
/// 一律构造函数注入 ViewModel（<c>coding-standards.md</c> 12）。所以这里显式从容器解析页面，
/// 再赋给 <c>Frame.Content</c>。
/// </para>
/// <para>
/// 代价是不留导航历史（<see cref="Frame.CanGoBack"/> 恒为 false）。对六个并列的顶层模块来说
/// 这是对的 —— 它们之间没有"上一步/下一步"关系，标题栏的返回按钮因此不会出现。
/// </para>
/// <para>
/// 🔴 **可访问性是 <c>public</c>，不是 <c>internal</c>。** 它是 <see cref="MainWindow"/> 的
/// 构造函数参数，而 Window 子类必须 public（XamlCompiler 与生成的分部类都按 public 处理），
/// 参数类型比方法可见性低就是 CS0051。同一条也适用于 ViewModel 与页面 —— 它们都要被 XAML 或容器解析。
/// </para>
/// <para>
/// ⚠️ **只挂已经实现的模块。** Phase 0 删掉模板页时就定了这条：不预置死链
/// （<c>D25</c>）。总览 / 系统启动项 / 运行日志 / 设置四个模块随各自的页面一起加进来，
/// 所以 <c>MainWindow.xaml</c> 里的 <c>MenuItems</c> 会分阶段增长 —— 这是刻意的，
/// 不是遗漏。
/// </para>
/// </remarks>
public sealed class NavigationService
{
    /// <summary>「自启动项」模块的导航标签。</summary>
    public const string ItemsTag = "items";

    /// <summary>「延时启动」模块的导航标签。</summary>
    public const string DelayTag = "delay";

    private readonly IServiceProvider _services;
    private readonly Dictionary<string, Type> _pages;

    /// <summary>构造导航服务。</summary>
    /// <param name="services">容器，用于解析页面实例。</param>
    public NavigationService(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _pages = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            [ItemsTag] = typeof(Views.ItemsPage),
            [DelayTag] = typeof(Views.DelayPage),
        };
    }

    /// <summary>已接入导航的标签集合，供自检与测试使用。</summary>
    public IReadOnlyCollection<string> RegisteredTags => _pages.Keys;

    /// <summary>
    /// 切换到指定模块。
    /// </summary>
    /// <param name="frame">承载页面的框架。</param>
    /// <param name="tag">导航标签；未知标签或 <see langword="null"/> 时不做任何事。</param>
    /// <remarks>
    /// 未知标签**静默返回**而不是抛异常：导航标签来自 XAML，属于"配置写错"，
    /// 让程序在切换菜单时崩溃的代价远大于少显示一个页面。
    /// </remarks>
    public void Navigate(Frame frame, string? tag)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (tag is null || !_pages.TryGetValue(tag, out var pageType))
        {
            return;
        }

        // 同一个页面重复点选时不要重建：重建会丢掉页内滚动位置，而用户只是想"回到这一页"。
        if (frame.Content?.GetType() == pageType)
        {
            return;
        }

        frame.Content = _services.GetRequiredService(pageType);
    }
}
