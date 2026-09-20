using DelayStart.App.Services;
using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「系统启动项」分区页（UI v2：服务 / 驱动 / Winlogon / 组策略 四个入口共用本页）。
/// 全部只读（FR-7）：本程序永远不会碰它们。
/// </summary>
public sealed partial class SystemPage : Page, INavigationTarget
{
    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public SystemPage(SystemViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public SystemViewModel ViewModel { get; }

    /// <inheritdoc />
    public string? NavigationTag
    {
        get => field;
        set
        {
            field = value;
            ApplyTag(value);
        }
    }

    /// <summary>把导航标签翻译成分区、页标题与分区描述。</summary>
    /// <remarks>
    /// 分区描述（2026-09-21 批复）：原常驻 InfoBar「本页全部只读」降级为标题下的一行描述，
    /// 文案按分区说明"这一页是什么 + 只读"；统计数字由 ViewModel.Subtitle 单独承担。
    /// </remarks>
    private void ApplyTag(string? tag)
    {
        ViewModel.Section = tag switch
        {
            NavigationService.SysServicesTag => ApplyTitle(
                "系统启动项 · 服务", SystemSection.Services,
                "随系统启动的 Windows 服务，只读展示，本程序不会改动它们。"),
            NavigationService.SysDriversTag => ApplyTitle(
                "系统启动项 · 驱动", SystemSection.Drivers,
                "随系统加载的内核驱动，默认只列出第三方驱动，只读展示。"),
            NavigationService.SysWinlogonTag => ApplyTitle(
                "系统启动项 · Winlogon", SystemSection.Winlogon,
                "Windows 登录环节加载的关键项（Shell、Userinit 等），只读展示。"),
            NavigationService.SysGpoTag => ApplyTitle(
                "系统启动项 · 组策略", SystemSection.GroupPolicy,
                "由组策略下发的登录与启动脚本，列表为空属正常现象，只读展示。"),
            _ => ViewModel.Section,
        };

        // 服务类列表的数据源随分区切换（服务 / 驱动各一个集合）——
        // 🔴 2026-09-20 修复：XAML 只能绑死一个集合，驱动分区此前显示的是空的服务集合（列表为空的真因）。
        ServiceList.ItemsSource = ViewModel.Section == SystemSection.Drivers
            ? ViewModel.Drivers
            : ViewModel.Services;

        // 批复 5：开关文案按分区切换（驱动分区 = "显示 Windows 内置驱动"）。
        BuiltinToggle.Content = ViewModel.Section == SystemSection.Drivers
            ? "显示 Windows 内置驱动"
            : "显示 Windows 内置服务";

        // 名值列表的数据源随分区切换（Winlogon / 组策略各一个集合）。
        ReadOnlyList.ItemsSource = ViewModel.Section == SystemSection.Winlogon
            ? ViewModel.WinlogonEntries
            : ViewModel.GroupPolicyEntries;
    }

    private SystemSection ApplyTitle(string title, SystemSection section, string description)
    {
        PageTitleText.Text = title;
        PageDescText.Text = description;
        return section;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = ViewModel.LoadAsync();
    }
}
