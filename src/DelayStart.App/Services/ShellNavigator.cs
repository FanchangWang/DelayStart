using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Services;

/// <summary>
/// 页面可被导航定位的标记：带参数的页面（同一 Page 类服务多个菜单项）实现它，
/// 让 <see cref="NavigationService"/> 知道"当前显示的是哪个入口"。
/// </summary>
/// <remarks>
/// 「自启动项」的四个来源、「系统启动项」的四个分区共用一个 Page 类。
/// 不做这个标记的话，导航服务"同类型不重建"的判断会把"注册表 → 计划任务"
/// 的切换吞掉（类型相同直接返回）。
/// </remarks>
public interface INavigationTarget
{
    /// <summary>当前页面实例对应的导航标签。</summary>
    string? NavigationTag { get; set; }
}

/// <summary>
/// 页面 → 外壳的跨页导航（总览页的计数 chip、分区链接跳到对应菜单）。
/// </summary>
/// <remarks>
/// <para>
/// 页面拿不到 <c>NavFrame</c> / <see cref="NavigationView"/>（它们在 MainWindow 里），
/// 又不该把窗口引用层层下传。单例 + 构造签名可见依赖，与 <see cref="WindowHandleProvider"/> 同一思路。
/// </para>
/// <para>
/// 跳转经 <see cref="NavigationView.SelectedItem"/> 走：菜单高亮与页面切换由同一条
/// SelectionChanged 路径驱动，不会出现"页面换了、菜单还停在原地"的错位。
/// </para>
/// </remarks>
public sealed class ShellNavigator
{
    private readonly NavigationService _navigation;
    private Frame? _frame;
    private NavigationView? _navView;

    /// <summary>构造外壳导航器。</summary>
    /// <param name="navigation">导航映射服务。</param>
    public ShellNavigator(NavigationService navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        _navigation = navigation;
    }

    /// <summary>由主窗口登记外壳控件。重复登记以最后一次为准。</summary>
    /// <param name="frame">内容框架。</param>
    /// <param name="navView">导航视图。</param>
    public void Attach(Frame frame, NavigationView navView)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(navView);

        _frame = frame;
        _navView = navView;
    }

    /// <summary>跳到指定导航标签：选中对应菜单项（SelectionChanged 完成页面切换）。</summary>
    /// <param name="tag">导航标签。</param>
    /// <remarks>
    /// 菜单里找不到该标签（或外壳尚未登记）时直接切页面：跳转功能不应因为
    /// 菜单结构的调整而失效，最多损失菜单高亮。
    /// </remarks>
    public void Navigate(string tag)
    {
        var item = FindItem(_navView?.MenuItems, tag);
        if (_navView is not null && item is not null)
        {
            _navView.SelectedItem = item;
            return;
        }

        if (_frame is not null)
        {
            _navigation.Navigate(_frame, tag);
        }
    }

    private static NavigationViewItem? FindItem(IEnumerable<object>? items, string tag)    {
        if (items is null)
        {
            return null;
        }

        foreach (var item in items)
        {
            if (item is NavigationViewItem entry)
            {
                if (entry.Tag as string == tag)
                {
                    return entry;
                }

                // 子项在 MenuItems 里（嵌套容器），递归找一层层展开的树。
                var nested = FindItem(entry.MenuItems, tag);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
