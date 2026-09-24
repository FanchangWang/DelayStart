using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「关于」页（2026-09-24 批复 28）。
/// </summary>
/// <remarks>
/// <para>
/// 页面上的动作只有三类：打开某个数据目录、复制诊断信息、打开项目主页。
/// 它们都是"点一下立刻完成"，没有需要回灌控件的初始值 —— 所以这一页**没有**
/// 设置页那种 <c>_initialized</c> 守卫（那里是为了防"初始化事件被当成用户输入落盘"）。
/// </para>
/// <para>
/// 但失败通道是照搬的：错误写 ViewModel 的 <c>StatusText</c>，页面底部一条
/// <see cref="InfoBar"/> 显示。列在卡片里的路径不显示在别处，报错位置也就只有一个。
/// </para>
/// </remarks>
public sealed partial class AboutPage : Page
{
    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public AboutPage(AboutViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public AboutViewModel ViewModel { get; }

    /// <summary>
    /// 「打开」某一项数据目录。
    /// </summary>
    /// <remarks>
    /// 路径**从行的 <c>Tag</c> 上取**，而不是在每条 <c>Click</c> 里各写一遍方法：
    /// <c>Tag="{x:Bind ViewModel.Xxx, Mode=OneTime}"</c> 是**编译期**绑定，
    /// 不经过（在 <c>SettingsCard.Content</c> 里本就不可靠的）继承链 ——
    /// 这也是设置页行内按钮用的同一手法。
    /// </remarks>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            ViewModel.OpenFolder(path);
        }
    }

    /// <summary>把页面上的信息复制成一段纯文本。</summary>
    private void OnCopyDiagnostics(object sender, RoutedEventArgs e) => ViewModel.CopyDiagnostics();

    /// <summary>用默认浏览器打开项目主页。</summary>
    private void OnOpenProjectPage(object sender, RoutedEventArgs e) => ViewModel.OpenProjectPage();

    /// <summary>用默认浏览器打开一个开源库的项目主页（URL 从行的 <c>Tag</c> 上取，同 <see cref="OnOpenFolder"/>）。</summary>
    private void OnOpenLibrary(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            ViewModel.OpenUrl(url, "开源库主页");
        }
    }

    /// <summary>用默认浏览器打开节假日数据源（holiday-cn）的项目主页。</summary>
    private void OnOpenHolidaySource(object sender, RoutedEventArgs e) =>
        ViewModel.OpenUrl(ViewModel.HolidaySourceUrl, "节假日数据源主页");
}
