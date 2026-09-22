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

    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    /// <param name="handles">主窗口句柄提供者（编辑器选文件用）。</param>
    /// <param name="icons">图标提取服务（编辑器 UWP 入口用，D46）。</param>
    public ItemsPage(ItemsViewModel viewModel, WindowHandleProvider handles, IconProvider icons)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(icons);

        ViewModel = viewModel;
        _handles = handles;
        _icons = icons;

        InitializeComponent();

        // 读缓存秒回，但仍挂 Loaded：让窗口先画出来，再灌列表。
        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public ItemsViewModel ViewModel { get; }

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 读缓存秒回，但仍挂 Loaded：让窗口先画出来，再灌列表。
        // 只加载一次；后续重载由外壳经 IReloadablePage.Reload 触发。
        Loaded -= OnLoaded;

        if (ViewModel.LoadCommand.CanExecute(null))
        {
            ViewModel.LoadCommand.Execute(null);
        }
    }

    /// <inheritdoc />
    public void Reload()
    {
        // 用「刷新本页」那条命令（强制重扫当前来源），不用 LoadCommand：
        // 后者命中缓存就直接返回，而"系统里多了/少了一条"恰恰是缓存里没有的信息。
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

        var dialog = new DelayEditorDialog(row.Entry, ViewModel.DelayPresets, ViewModel.DefaultPreset)
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
                outcome.Message ?? "未给出具体原因，详情见运行日志。");
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
                $"移除过程发生意外错误：{ex.Message}\n\n该条目仍保持接管状态 —— 可稍后重试，详情见运行日志。");
            return;
        }

        if (!outcome.Succeeded)
        {
            await ShowMessageAsync(
                "未能移出延时启动",
                $"{outcome.Message}\n\n该条目仍保持接管状态 —— 可以稍后重试，详情见运行日志。");
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
            await ShowMessageAsync("未能禁用", "写入软禁用标记失败，详情见运行日志。该项不会被删除，可重试。");
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
            await ShowMessageAsync("未能启用", "删除软禁用标记失败，详情见运行日志。可重试。");
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

        var dialog = new DelayEditorDialog(item, ViewModel.DelayPresets, ViewModel.DefaultPreset, _handles, _icons)
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
