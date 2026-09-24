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
/// 页面可以在被跨进程通知定位到时重读自己的数据。
/// </summary>
/// <remarks>
/// <para>
/// 存在的原因是一个真实的失败（2026-09-22 用户回报"点通知不刷新延时启动的数据，
/// 需要手动刷新"）。跨进程定位走"给 <c>NavigationView.SelectedItem</c> 赋新值 →
/// <c>SelectionChanged</c> → 换页"，两种情况都会留下旧数据：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>目标页就是当前页</b> —— 给同一个项重新赋值不会再触发事件，页面既不重建、
/// <c>Loaded</c> 也不再触发。用户看到的是"窗口弹到前面了，列表还是旧的"。
/// </description></item>
/// <item><description>
/// <b>确实切了过去</b> —— 自启动项各页读的是启动时那一份扫描缓存（bug#7 的秒回优化），
/// 而通知说的"有变化"恰恰是缓存里还没有的信息。
/// </description></item>
/// </list>
/// <para>
/// 只给"会被通知定位到"的页面实现：<c>延时启动</c>（失效条目）、
/// 四个来源共用的 <c>自启动项</c>（新增条目）、<c>调度日志</c>（调度端通知）。
/// </para>
/// </remarks>
public interface IReloadablePage
{
    /// <summary>重新读取本页数据（在 UI 线程上调用）。</summary>
    /// <remarks>
    /// 实现应当**异步发起、立即返回**（命令式重载），不要在这里同步等待扫描 ——
    /// 它是在处理一次唤起信号，卡住 UI 线程会让"点了通知窗口反而不响应"。
    /// </remarks>
    void Reload();
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
