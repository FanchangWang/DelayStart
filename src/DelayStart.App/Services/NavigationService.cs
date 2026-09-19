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
    /// <summary>「总览」模块的导航标签。</summary>
    public const string OverviewTag = "overview";

    /// <summary>「延时启动」模块的导航标签。</summary>
    public const string DelayTag = "delay";

    /// <summary>「自启动项」父级菜单的导航标签（点父项 = 全部来源视图）。</summary>
    public const string ItemsTag = "items";

    /// <summary>「系统启动项」父级菜单的导航标签（点父项 = 服务分区）。</summary>
    public const string SystemTag = "system";

    /// <summary>「自启动项 · 注册表」的导航标签（UI v2：来源即入口）。</summary>
    public const string ItemsRegistryTag = "items-registry";

    /// <summary>「自启动项 · 启动文件夹」的导航标签。</summary>
    public const string ItemsFolderTag = "items-folder";

    /// <summary>「自启动项 · 计划任务」的导航标签。</summary>
    public const string ItemsTaskTag = "items-task";

    /// <summary>「自启动项 · UWP Apps」的导航标签。</summary>
    public const string ItemsUwpTag = "items-uwp";

    /// <summary>「系统启动项 · 服务」的导航标签。</summary>
    public const string SysServicesTag = "sys-services";

    /// <summary>「系统启动项 · 驱动」的导航标签。</summary>
    public const string SysDriversTag = "sys-drivers";

    /// <summary>「系统启动项 · Winlogon」的导航标签。</summary>
    public const string SysWinlogonTag = "sys-winlogon";

    /// <summary>「系统启动项 · 组策略」的导航标签（D2：替换原「登录脚本」分区）。</summary>
    public const string SysGpoTag = "sys-gpo";

    /// <summary>「运行日志」模块的导航标签（FR-8）。</summary>
    public const string RunsTag = "runs";

    /// <summary>「设置」模块的导航标签（FR-9）。</summary>
    public const string SettingsTag = "settings";

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
            [OverviewTag] = typeof(Views.OverviewPage),
            [DelayTag] = typeof(Views.DelayPage),
            [ItemsTag] = typeof(Views.ItemsPage),
            [ItemsRegistryTag] = typeof(Views.ItemsPage),
            [ItemsFolderTag] = typeof(Views.ItemsPage),
            [ItemsTaskTag] = typeof(Views.ItemsPage),
            [ItemsUwpTag] = typeof(Views.ItemsPage),
            [SystemTag] = typeof(Views.SystemPage),
            [SysServicesTag] = typeof(Views.SystemPage),
            [SysDriversTag] = typeof(Views.SystemPage),
            [SysWinlogonTag] = typeof(Views.SystemPage),
            [SysGpoTag] = typeof(Views.SystemPage),
            [RunsTag] = typeof(Views.RunsPage),
            [SettingsTag] = typeof(Views.SettingsPage),
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
    /// <para>
    /// 未知标签**静默返回**而不是抛异常：导航标签来自 XAML，属于"配置写错"，
    /// 让程序在切换菜单时崩溃的代价远大于少显示一个页面。
    /// </para>
    /// <para>
    /// 同类型页面的去重靠 <see cref="INavigationTarget.NavigationTag"/>：四个来源共用
    /// <see cref="Views.ItemsPage"/>、四个分区共用 <see cref="Views.SystemPage"/>，
    /// 只比较类型会把「注册表 → 计划任务」的切换吞掉。
    /// </para>
    /// </remarks>
    public void Navigate(Frame frame, string? tag)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (tag is null || !_pages.TryGetValue(tag, out var pageType))
        {
            return;
        }

        if (frame.Content?.GetType() == pageType
            && !typeof(INavigationTarget).IsAssignableFrom(pageType))
        {
            // 同一个页面重复点选时不要重建：重建会丢掉页内滚动位置。
            return;
        }

        if (frame.Content?.GetType() == pageType
            && frame.Content is INavigationTarget current
            && current.NavigationTag == tag)
        {
            // 带参数页面的"同入口重复点选"，同样不重建。
            return;
        }

        var page = _services.GetRequiredService(pageType);
        if (page is INavigationTarget target)
        {
            target.NavigationTag = tag;
        }

        frame.Content = page;
    }
}
