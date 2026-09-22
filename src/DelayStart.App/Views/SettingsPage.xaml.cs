using DelayStart.App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DelayStart.App.Views;

/// <summary>
/// 「设置」页（FR-9，UI v2.2 重做）。所有操作即时落盘，没有保存按钮。
/// </summary>
/// <remarks>
/// 预设胶囊的行内控件（选中 / 删除 / 添加）走 code-behind 事件而不是命令：
/// 行是整表重建的快照，事件带上 <see cref="PresetRow"/> 的 DataContext 最直接。
/// </remarks>
public sealed partial class SettingsPage : Page
{
    /// <summary>
    /// 初始化守卫：x:Bind 初始化 / <c>ViewModel.Load()</c> 回灌时会触发一次
    /// SelectionChanged / ValueChanged —— 那些事件**不能落盘**，否则每次进设置页
    /// 都会把主题（以及通知策略）写回第 0 项并广播（2026-09-19 用户实测的 bug：
    /// 手动切浅色后重新进设置页就变回跟随系统）。真正的用户输入只会发生在这之后。
    /// </summary>
    private bool _initialized;

    /// <summary>构造页面。</summary>
    /// <param name="viewModel">本页的 ViewModel，由容器注入。</param>
    public SettingsPage(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public SettingsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ViewModel.Load();
        _initialized = true;
    }

    /// <summary>某个预设被选为默认。</summary>
    /// <remarks>
    /// 🔴 胶囊是 <see cref="ToggleButton"/>，**没有原生单选语义** —— 用户实测能同时
    /// 选中多个（二轮 bug 批复 2）。这里手工互斥：选中一个就遍历可视树弹起其余胶囊。
    /// </remarks>
    private void OnPresetChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton source)
        {
            return;
        }

        if (source.DataContext is PresetRow { IsDefault: false } row)
        {
            ViewModel.SetDefaultPreset(row);
        }

        UncheckOtherPills(PresetPills, source);
    }

    /// <summary>默认胶囊被再次点击弹起：默认值必须始终存在，弹回去。</summary>
    private void OnPresetUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: PresetRow { IsDefault: true } } pill)
        {
            pill.IsChecked = true;
        }
    }

    /// <summary>遍历可视树，把 <paramref name="source"/> 以外的胶囊全部弹起。</summary>
    /// <remarks>
    /// 🔴 跳过 <c>DataContext.IsDefault == true</c> 的胶囊：SetDefaultPreset 触发集合重建，
    /// 新容器的生成可能落后于本方法执行 —— 不加守卫会把"新默认胶囊"弹起，
    /// 进而触发 Unchecked→Checked 回环。按 DataContext 判定与容器代次无关，天然幂等。
    /// </remarks>
    private static void UncheckOtherPills(DependencyObject parent, ToggleButton source)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ToggleButton button
                && !ReferenceEquals(button, source)
                && button.DataContext is not PresetRow { IsDefault: true })
            {
                button.IsChecked = false;
            }

            UncheckOtherPills(child, source);
        }
    }

    /// <summary>点击删除按钮。删除前弹 <see cref="ContentDialog"/> 二次确认（2026-09-21 批复）。</summary>
    /// <remarks>
    /// 确认弹窗说明「使用该值的条目不受影响」—— 预设只是编辑器的快选默认值，
    /// 与已存在的条目无引用关系，用户不必因怕误伤而犹豫。
    /// 2026-09-21 二次批复：垃圾桶回退为嵌套 Button（裸 TextBlock 方案触发 XamlCompiler
    /// pass-1 静默崩溃，且悬停变色难以两主题兼顾），确认弹窗逻辑保留。
    /// </remarks>
    private async void OnPresetDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PresetRow row })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除预设延时",
            Content = $"确定删除「{row.Display}」吗？使用该值的条目不受影响，仅从快选列表移除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeletePreset(row);
        }
    }

    /// <summary>添加一个预设。</summary>
    private void OnPresetAdd(object sender, RoutedEventArgs e) => ViewModel.AddPreset();

    /// <summary>通知策略被改变。⚠️ 初始化 / 回灌事件在这里被守卫挡掉（不落盘）。</summary>
    private void OnNotifyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        if (sender is ComboBox { SelectedIndex: var index })
        {
            ViewModel.SetNotifyMode(index);
        }
    }

    /// <summary>守卫通知策略被改变（D80）。⚠️ 初始化 / 回灌事件在这里被守卫挡掉（不落盘）。</summary>
    /// <remarks>
    /// 与「调度 · 通知策略」是两个独立的卡片、两个独立的枚举，各自落盘各自的字段 ——
    /// 唯一相同的是这套"初始化期不落盘"的处理（<see cref="_initialized"/>）。
    /// </remarks>
    private void OnGuardNotifyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        if (sender is ComboBox { SelectedIndex: var index })
        {
            ViewModel.SetGuardNotifyMode(index);
        }
    }

    /// <summary>重试次数被改变。⚠️ 初始化 / 回灌事件在这里被守卫挡掉（不落盘）。</summary>
    private void OnRetryChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initialized)
        {
            return;
        }

        ViewModel.SetRetryCount(args.NewValue);
    }

    /// <summary>主题被改变：即时切换全局外观。⚠️ 初始化 / 回灌事件在这里被守卫挡掉（不落盘）。</summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        if (sender is ComboBox { SelectedIndex: var index })
        {
            ViewModel.SetTheme(index);
        }
    }
}
