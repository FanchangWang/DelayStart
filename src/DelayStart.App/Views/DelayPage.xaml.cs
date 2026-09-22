using DelayStart.App.Dialogs;
using DelayStart.App.Services;
using DelayStart.App.ViewModels;
using DelayStart.Core.Models;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Views;

/// <summary>
/// 「延时启动」页（<c>design.md</c> 7.5）。数据来自 <c>config.json</c>。
/// </summary>
/// <remarks>
/// 实现 <see cref="IReloadablePage"/>：系统通知点击的落点之一是本页（失效条目，D81），
/// 而"点通知时本页已经开着"是最常见的情形 —— 那时导航不会发生，必须由外壳调用
/// <see cref="Reload"/> 才能让用户看到最新的失效状态。
/// </remarks>
public sealed partial class DelayPage : Page, IReloadablePage
{
    private readonly WindowHandleProvider _handles;
    private readonly IconProvider _icons;

    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    /// <param name="handles">主窗口句柄提供者，「手动添加」的文件选择器需要它。</param>
    /// <param name="icons">图标提取服务，「选择 UWP 应用」列表需要（D46）。</param>
    public DelayPage(DelayViewModel viewModel, WindowHandleProvider handles, IconProvider icons)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(icons);

        ViewModel = viewModel;
        _handles = handles;
        _icons = icons;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public DelayViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 首次进入只需要加载一次；后续重载由外壳经 IReloadablePage.Reload 触发。
        Loaded -= OnLoaded;
        RefreshData();
    }

    /// <inheritdoc />
    public void Reload() => RefreshData();

    /// <summary>
    /// 发起一次数据刷新。
    /// </summary>
    /// <remarks>
    /// 读配置是同步的（小文件），但失效判定要扫一次系统 —— 走命令让它异步。
    /// 命令自身有 <c>IsBusy</c> 防重入（见 <c>DelayViewModel.RefreshAsync</c>），
    /// 所以重复唤起不会叠加扫描。
    /// </remarks>
    private void RefreshData()
    {
        if (ViewModel.RefreshCommand.CanExecute(null))
        {
            ViewModel.RefreshCommand.Execute(null);
        }
    }

    /// <summary>打开「手动添加」形态的编辑器。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    /// <remarks>
    /// 手动添加与「接管」是两条不同的路：接管会把系统里已有的自启动项软禁用，
    /// 手动添加只往 <c>config.json</c> 里加一条记录，**不碰系统任何设置**（FR-3.4）。
    /// 编辑器第 ④ 块的文案负责把这件事说清楚。
    /// </remarks>
    private async void OnAddManualRequested(object sender, RoutedEventArgs e)
    {
        var dialog = new DelayEditorDialog(ViewModel.DelayPresets, ViewModel.DefaultPreset, _handles, _icons)
        {
            XamlRoot = XamlRoot,
        };

        // 二次确认（自定义延时）走弹窗内确认面板：确认后 Hide() 的结果值是 None，
        // 所以这里必须同时接受 Primary 与 DelayConfirmed。
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && !dialog.DelayConfirmed)
        {
            return;
        }

        try
        {
            ViewModel.AddManual(dialog.ToValues());
        }
        catch (StartupOperationException ex)
        {
            await ShowFailureAsync("未能添加延时启动", ex.Message);
        }
    }

    /// <summary>打开「编辑」形态的编辑器。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    private async void OnEditRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DelayRow row })
        {
            return;
        }

        var dialog = new DelayEditorDialog(row.Item, ViewModel.DelayPresets, ViewModel.DefaultPreset, _handles, _icons)
        {
            XamlRoot = XamlRoot,
        };

        // 二次确认（自定义延时）走弹窗内确认面板：确认后 Hide() 的结果值是 None。
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && !dialog.DelayConfirmed)
        {
            return;
        }

        try
        {
            ViewModel.ApplyEdit(row, dialog.ToValues());
        }
        catch (StartupOperationException ex)
        {
            await ShowFailureAsync("未能保存修改", ex.Message);
        }
    }

    /// <summary>在同一延时组内上移一位。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveCore(sender, -1);

    /// <summary>在同一延时组内下移一位。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveCore(sender, 1);

    private void MoveCore(object sender, int delta)
    {
        if (sender is not Button { DataContext: DelayRow row })
        {
            return;
        }

        try
        {
            // 已经在组内端点上时服务层返回 false，不写文件也不刷新 —— 界面保持原样即可，
            // 没必要为了"什么都没发生"弹一个框。
            _ = ViewModel.Move(row, delta);
        }
        catch (StartupOperationException ex)
        {
            _ = ShowFailureAsync("未能调整顺序", ex.Message);
        }
    }

    /// <summary>切换条目级开关（FR-4.6）。</summary>
    /// <param name="sender">触发开关。</param>
    /// <param name="e">事件参数。</param>
    /// <remarks>
    /// 🔴 <b>必须挡掉"回灌造成的二次触发"</b>：局部更新后 VM 会把 <c>IsEnabled</c> 推给
    /// OneWay 绑定，开关同样会触发一次 <c>Toggled</c>。
    /// 判据是"开关的当前值是否等于行对象已记的状态" —— 相等说明这是回灌，不是用户点的。
    /// 少这个判断就会出现"点一下开关、配置被来回写两次"的抖动。
    /// </remarks>
    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: DelayRow row } toggle)
        {
            return;
        }

        if (row.IsEnabled == toggle.IsOn)
        {
            return;
        }

        try
        {
            ViewModel.SetEnabled(row, toggle.IsOn);
        }
        catch (StartupOperationException ex)
        {
            // 落盘失败：把开关弹回真实状态（属性值没变，重发通知让 OneWay 绑定重读）。
            row.RefreshEnabled();
            _ = ShowFailureAsync("未能切换启用状态", ex.Message);
        }
    }

    /// <summary>移除确认 + 执行。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    /// <remarks>
    /// 🔴 **两种变体文案不能合并成一句**：手动条目与系统条目被移除后对系统的影响完全不同
    /// （前者不动系统、后者恢复原自启动）。写成一句"确定要移除吗"等于让用户赌。
    /// 文案取自 UI 原型评审定稿的 <c>unlink()</c>。
    /// </remarks>
    private async void OnRemoveRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DelayRow row })
        {
            return;
        }

        var manual = row.Item.IsManual;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"移除「{row.Item.Name}」的延时启动？",
            Content = manual
                ? "该条目是手动添加的，不关联任何系统自启动项。移除后只删除本程序里的这条配置，系统的任何设置都不会被改动。"
                : "该程序的原始自启动项将恢复为你接管前的状态。下次登录时它会按系统原本的方式启动，不再受本程序控制。",
            PrimaryButtonText = manual ? "移除条目" : "移除并恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // 🔴 Release 内部把可预期的失败折成 TakeoverOutcome，但意外异常（如配置读取
        // 失败、COM 调用出错）仍可能冲出来 —— async void 里未捕获异常会直接带崩进程
        // （2026-09-19 用户实测「删除一个条目时软件崩溃」）。这里统一兜住转成提示。
        TakeoverOutcome outcome;
        try
        {
            outcome = ViewModel.Release(row);
        }
        catch (Exception ex)
        {
            await ShowFailureAsync(
                "未能移出延时启动",
                $"移除过程发生意外错误：{ex.Message}\n\n该条目仍保持接管状态 —— 可稍后重试，详情见运行日志。");
            return;
        }

        if (outcome.Succeeded)
        {
            return;
        }

        var failure = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "未能移出延时启动",
            Content = $"{outcome.Message}\n\n该条目仍保持接管状态 —— 可以稍后重试，详情见运行日志。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };

        await failure.ShowAsync();
    }

    /// <summary>删除一个失效条目：只删配置，绝不动系统（D81）。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    /// <remarks>
    /// 🔴 与「移出延时」不是一回事，文案必须说清区别：那个会**恢复系统的原始自启动项**，
    /// 这个只把配置里这条记录删掉。失效行上之所以只剩「删除」，正是因为已经
    /// **没有可还原的系统项**（孤儿：系统项已被删除；或目标程序已不存在）——
    /// 所以这里绝不能复用「移除并恢复」的话术。
    /// </remarks>
    private async void OnDeleteRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DelayRow row })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"删除「{row.Item.Name}」？",
            Content = "该条目已经失效。此操作只删除本程序里的这条配置，系统的任何设置都不会被改动。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            ViewModel.Remove(row);
        }
        catch (StartupOperationException ex)
        {
            await ShowFailureAsync("未能删除条目", ex.Message);
        }
    }

    /// <summary>把一个失效条目转为手动条目（D81）。</summary>
    /// <param name="sender">触发按钮。</param>
    /// <param name="e">事件参数。</param>
    /// <remarks>
    /// 前提是目标程序还在：按钮显隐已由 <see cref="DelayRow.CanConvertToManual"/> 控制，
    /// <see cref="ConfigEditService.ConvertToManual"/> 落盘前还会再查一次文件
    /// （服务层不假设调用方守规矩）。转换**不可逆** —— 条目与系统来源彻底脱钩，
    /// 之后就只是一个普通的手动条目。
    /// </remarks>
    private async void OnConvertRequested(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DelayRow row })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"把「{row.Item.Name}」转为手动添加？",
            Content = "转换后该条目不再关联任何系统自启动项，只按你设置的延时与参数启动它；"
                + "此后它的名称、路径、参数都可以直接编辑。此操作不可撤销。",
            PrimaryButtonText = "转为手动",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            ViewModel.ConvertToManual(row);
        }
        catch (StartupOperationException ex)
        {
            await ShowFailureAsync("未能转为手动条目", ex.Message);
        }
    }

    /// <summary>统一的失败提示。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">失败原因。</param>
    /// <returns>提示关闭后的 <see cref="Task"/>。</returns>
    private async Task ShowFailureAsync(string title, string message)
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
