using DelayStart.App.Dialogs;
using DelayStart.App.Services;
using DelayStart.App.ViewModels;
using DelayStart.Core.Models;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.ApplicationModel.DataTransfer;

namespace DelayStart.App.Views;

/// <summary>
/// 「自启动项」来源页（UI v2：四个来源共用本页，<c>NavigationTag</c> 决定来源）。
/// </summary>
/// <remarks>
/// 实现 <see cref="IReloadablePage"/>：守卫的"新增自启动项"通报会定位到某个来源页，
/// 而"点通知时用户正好就在那一页"是最常见的情形 —— 那时导航不会发生（同一个菜单项
/// 重新赋值不触发 <c>SelectionChanged</c>），必须由外壳调用 <see cref="Reload"/>。
/// 重载走**强制重扫**而不是读缓存：缓存快照里本来就还没有那条新增项。
/// </remarks>
public sealed partial class ItemsPage : Page, INavigationTarget, IReloadablePage
{
    private readonly WindowHandleProvider _handles;
    private readonly IconProvider _icons;
    private readonly CycleCatalogService _cycles;

    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    /// <param name="handles">主窗口句柄提供者（编辑器选文件用）。</param>
    /// <param name="icons">图标提取服务（编辑器 UWP 入口用，D46）。</param>
    /// <param name="cycles">周期目录服务（编辑器「调度周期」一节用，FR-15）。</param>
    public ItemsPage(
        ItemsViewModel viewModel,
        WindowHandleProvider handles,
        IconProvider icons,
        CycleCatalogService cycles)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(cycles);

        ViewModel = viewModel;
        _handles = handles;
        _icons = icons;
        _cycles = cycles;

        InitializeComponent();

        // 读缓存秒回，但仍挂 Loaded：让窗口先画出来，再灌列表。
        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public ItemsViewModel ViewModel { get; }

    /// <inheritdoc />
    /// <remarks>
    /// 🔴 切标签（注册表 → 计划任务 …）也走刷新路径，但**不是**靠这一句，
    /// 更不能只靠 <c>Loaded</c> —— 两个都不够：
    /// <list type="number">
    /// <item><description><b>当前</b>：<c>ItemsPage</c> 与 <c>ItemsViewModel</c> 都是
    /// <c>AddTransient</c>，每次导航都是新实例，<c>_hasLoaded</c> 恒为 <see langword="false"/>
    /// ⇒ 下面这个分支**一次都走不到**，真正干活的是新实例的 <c>Loaded</c>。</description></item>
    /// <item><description><b>若哪天改成单例</b>：<c>frame.Content</c> 赋同一个实例不保证
    /// <c>Loaded</c> 再触发，那时这条分支就是唯一的刷新入口。</description></item>
    /// </list>
    /// 🔴 <b>而这两个来源都不够</b>：漏掉刷新时的症状是"标签切了、标题变了、
    /// 列表还是上一个来源的"（2026-10-02 用户实测）。漏掉筛选时的症状更隐蔽 ——
    /// 见 <see cref="ItemsViewModel"/> 里 <c>SourceFilterPolicy</c> 的说明（D140）。
    /// </remarks>
    public string? NavigationTag
    {
        get => field;
        set
        {
            var changed = field != value;
            field = value;
            ApplyTag(value);

            // 🔴 只在"已经加载过一次"之后才由标签驱动刷新：首次导航时外壳会先设标签、
            // 再把页面挂上去，紧跟着的 Loaded 会刷一次 —— 两次叠加没有意义。
            if (changed && _hasLoaded)
            {
                RequestRefresh();
            }
        }
    }

    /// <summary>本页是否已经加载过至少一次（用来区分"首次导航"与"切标签"）。</summary>
    private bool _hasLoaded;

    /// <summary>把导航标签翻译成来源筛选与页标题。</summary>
    private void ApplyTag(string? tag) => ViewModel.SourceFilter = tag switch
    {
        NavigationService.ItemsRegistryTag => ApplyTitle(StartupSource.Registry, "自启动项 · 注册表"),
        NavigationService.ItemsFolderTag => ApplyTitle(StartupSource.StartupFolder, "自启动项 · 启动文件夹"),
        NavigationService.ItemsTaskTag => ApplyTitle(StartupSource.ScheduledTask, "自启动项 · 计划任务"),
        NavigationService.ItemsUwpTag => ApplyTitle(StartupSource.Uwp, "自启动项 · UWP Apps"),
        _ => null,
    };

    private StartupSource ApplyTitle(StartupSource source, string title)
    {
        PageTitleText.Text = title;
        return source;
    }

    /// <summary>
    /// 进入本页时触发加载。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>这里原来有一句 <c>Loaded -= OnLoaded;</c>，是"切页不刷新"的根因之一</b>
    /// （2026-10-02 用户实测：在外部加/删注册表项后，重新进入本页表格没刷新）。
    /// 它当初的理由是「只加载一次，免得守卫和 <see cref="IReloadablePage.Reload"/> 都叠加」。
    /// 但那个理由不成立 —— <see cref="IReloadablePage.Reload"/> 与本页的
    /// <c>Loaded</c> 本来就走同一个 <see cref="ScanCacheService"/>，重复调用是幂等的
    /// （指纹相同 ⇒ 一个集合通知都不发）。所以现在**不摘绑**。
    /// <para>
    /// 🔴 <b>更正一处我自己写错的陈述</b>（2026-10-02）：本方法原先的注释写着
    /// 「页面注册成单例，同一个实例被反复 <c>frame.Content = page</c> 复用，
    /// 于是第二次进页 <c>Loaded</c> 没有处理器了」。<b>前半句是错的</b> ——
    /// <c>ServiceRegistration</c> 里是 <c>AddTransient&lt;ItemsPage&gt;()</c>，
    /// 每次导航都是**全新实例**。后半句的**结论**碰巧仍成立（自解绑之后第二次进页
    /// 确实没有处理器），但当时给出的机制是错的 —— 而按错的机制去推，就会推出
    /// 「切标签必须靠 <see cref="NavigationTag"/> 补一次刷新」这种在当前注册下
    /// **永远走不到**的分支（见下）。
    /// </para>
    /// </remarks>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hasLoaded = true;
        RequestRefresh();
    }

    /// <inheritdoc />
    public void Reload() => RequestRefresh();

    /// <summary>
    /// 请求一次"强制重扫当前来源"。
    /// </summary>
    /// <remarks>
    /// 🔴 三个入口（进页 / 切标签 / 外壳定位）统一走这里，且一律用
    /// <c>RefreshCommand</c> 而不是 <c>LoadCommand</c>：后者命中缓存就直接返回，
    /// 而"系统里多了/少了一条"（用户在外部改注册表、卸载器清了启动文件夹）
    /// 恰恰是缓存里**没有**、也没有任何东西会把它标记成过期的信息。
    /// <para>
    /// 重复调用是安全的：加载是"先摆缓存、后台重扫"两段式，且
    /// <c>AsyncRelayCommand</c> 默认不允许并发执行，飞行中 <c>CanExecute</c> 为假。
    /// 指纹相同 ⇒ 一个集合通知都不发，所以无变化的重扫对界面完全不可见。
    /// </para>
    /// </remarks>
    private void RequestRefresh()
    {
        if (ViewModel.RefreshCommand.CanExecute(null))
        {
            ViewModel.RefreshCommand.Execute(null);
        }
    }

    // ── 行操作 ───────────────────────────────────────────────────────────

    /// <summary>「查看」：打开条目对应的注册表位置 / 文件目录 / 任务计划。</summary>
    private void OnViewRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StartupEntryRow row })
        {
            return;
        }

        try
        {
            OpenLocation(row.Entry);
        }
        catch (Exception ex)
        {
            _ = ShowMessageAsync("未能打开位置", ex.Message);
        }
    }

    /// <summary>按来源打开对应位置。</summary>
    /// <remarks>
    /// <para>
    /// 注册表：regedit 支持 <c>LastKey</c> 定位（写 Applets\Regedit\LastKey 再启动），
    /// 同时把键路径放进剪贴板兜底 —— regedit 已在运行时不会重新定位，剪贴板仍可用。
    /// </para>
    /// <para>
    /// 启动文件夹：explorer /select 直达并选中 .lnk；计划任务：打开任务计划程序；
    /// UWP：打开系统「启动应用」设置页。
    /// </para>
    /// </remarks>
    private static void OpenLocation(StartupEntry entry)
    {
        switch (entry.Source)
        {
            case StartupSource.Registry:
            {
                var hive = entry.Scope switch
                {
                    StartupScope.Hklm => "HKEY_LOCAL_MACHINE",
                    StartupScope.HklmWow => "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node",
                    _ => "HKEY_CURRENT_USER",
                };
                var keyPath = $@"计算机\{hive}\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

                var clipboard = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                clipboard.SetText(keyPath);
                Clipboard.SetContent(clipboard);

                try
                {
                    using var applets = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", writable: true);
                    applets?.SetValue("LastKey", keyPath);
                }
                catch
                {
                    // 写不进 LastKey 不影响流程：剪贴板里已有键路径。
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("regedit.exe")
                {
                    UseShellExecute = true,
                });
                break;
            }

            case StartupSource.StartupFolder:
            {
                var folder = entry.Scope == StartupScope.UserFolder
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Startup)
                    : Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                var target = System.IO.Path.Combine(folder, entry.SourceKey);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{target}\"",
                    UseShellExecute = true,
                });
                break;
            }

            case StartupSource.ScheduledTask:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskschd.msc")
                {
                    UseShellExecute = true,
                });
                break;

            case StartupSource.Uwp:
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps")
                {
                    UseShellExecute = true,
                });
                break;
        }
    }

    /// <summary>打开延时配置编辑器，确认后接管该条目。</summary>
    private async void OnDelayRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StartupEntryRow row })
        {
            return;
        }

        var dialog = new DelayEditorDialog(row.Entry, ViewModel.DelayPresets, ViewModel.DefaultPreset, _cycles)
        {
            XamlRoot = XamlRoot,
        };

        // 二次确认（自定义延时）走弹窗内确认面板：确认后 Hide() 的结果值是 None。
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && !dialog.DelayConfirmed)
        {
            return;
        }

        var options = new TakeoverOptions
        {
            DelaySeconds = dialog.DelaySeconds,
            RunAsAdmin = dialog.RunAsAdmin,
            Arguments = string.IsNullOrWhiteSpace(dialog.Arguments) ? null : dialog.Arguments,
        };

        var outcome = ViewModel.Takeover(row.Entry, options, row);
        if (!outcome.Succeeded)
        {
            await ShowMessageAsync(
                "未能加入延时启动",
                outcome.Message ?? "未给出具体原因，详情见调度日志。");
        }
    }

    /// <summary>「移出延时」（bug#4）：恢复系统项并删除配置，行就地变回原状态。</summary>
    private async void OnReleaseRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StartupEntryRow row })
        {
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"移出「{row.Name}」的延时启动？",
            Content = "该程序的原始自启动项将恢复为你接管前的状态。下次登录时它会按系统原本的方式启动，不再受本程序控制。",
            PrimaryButtonText = "移除并恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // 🔴 同 DelayPage：Release 的意外异常不允许冲出 async void（会把进程带崩）。
        TakeoverOutcome outcome;
        try
        {
            outcome = ViewModel.Release(row);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                "未能移出延时启动",
                $"移除过程发生意外错误：{ex.Message}\n\n该条目仍保持接管状态 —— 可稍后重试，详情见调度日志。");
            return;
        }

        if (!outcome.Succeeded)
        {
            await ShowMessageAsync(
                "未能移出延时启动",
                $"{outcome.Message}\n\n该条目仍保持接管状态 —— 可以稍后重试，详情见调度日志。");
        }
    }

    /// <summary>「禁用」：纯禁用（StartupApproved 写标记），不接管（D3）。</summary>
    private async void OnDisableRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StartupEntryRow row })
        {
            return;
        }

        if (!ViewModel.SetEntryEnabled(row, enabled: false))
        {
            await ShowMessageAsync("未能禁用", "写入软禁用标记失败，详情见调度日志。该项不会被删除，可重试。");
        }
    }

    /// <summary>「启用」：删除 StartupApproved 标记。</summary>
    private async void OnEnableRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StartupEntryRow row })
        {
            return;
        }

        if (!ViewModel.SetEntryEnabled(row, enabled: true))
        {
            await ShowMessageAsync("未能启用", "删除软禁用标记失败，详情见调度日志。可重试。");
        }
    }

    /// <summary>「编辑」已接管项：延时 / 参数 / 工作目录。</summary>
    private async void OnEditRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StartupEntryRow row })
        {
            return;
        }

        DelayedItem item;
        try
        {
            item = ViewModel.GetItemFor(row);
        }
        catch (InvalidOperationException ex)
        {
            await ShowMessageAsync("未能打开编辑器", ex.Message);
            return;
        }

        var dialog = new DelayEditorDialog(item, ViewModel.DelayPresets, ViewModel.DefaultPreset, _handles, _icons, _cycles)
        {
            XamlRoot = XamlRoot,
        };

        // 二次确认（自定义延时）走弹窗内确认面板：确认后 Hide() 的结果值是 None。
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && !dialog.DelayConfirmed)
        {
            return;
        }

        if (!ViewModel.ApplyEdit(row, dialog.ToValues()))
        {
            await ShowMessageAsync("未能保存修改", "配置中找不到该条目，请先刷新本页。");
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }
}
