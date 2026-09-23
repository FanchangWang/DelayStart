using DelayStart.App.Controls;
using DelayStart.App.ViewModels;

using Microsoft.UI.Text;
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

        // 与 OnLoaded 里的「订阅共享状态」成对：离开页面就摘掉订阅，否则每进一次设置页
        // 都往单例上多挂一个处理器（瞬态 ViewModel 被单例长期引用 = 连锁着页面一起不释放）。
        Unloaded += OnUnloaded;
    }

    /// <summary>本页的 ViewModel，供 <c>x:Bind</c> 使用。</summary>
    public SettingsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ViewModel.Load();
        _initialized = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.DetachHolidayStatus();

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

    // ── 周期（FR-15.10–15.14，§6.4）─────────────────────────────────────────

    /// <summary>「＋ 新建周期」。</summary>
    private async void OnCycleAdd(object sender, RoutedEventArgs e) => await ShowCycleDialogAsync(null);

    /// <summary>某一行的「编辑」。</summary>
    private async void OnCycleEdit(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CycleRow row })
        {
            await ShowCycleDialogAsync(row);
        }
    }

    /// <summary>某一行的「删除」。被引用的行按钮已置灰，这里再兜一层并做二次确认。</summary>
    private async void OnCycleDelete(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CycleRow row })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除周期",
            Content = $"确定删除「{row.Display}」吗？该周期没有被任何条目使用。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DeleteCycle(row);
        }
    }

    /// <summary>
    /// 打开新建 / 编辑周期面板。
    /// </summary>
    /// <param name="existing">要编辑的行；为 <see langword="null"/> 时是新建。</param>
    /// <remarks>
    /// 🔴 设置页的宿主是真 <see cref="ContentDialog"/>（页面之上没有别的弹窗，
    /// 不存在"ContentDialog 套 ContentDialog"的问题）；延时编辑弹窗里那个是
    /// **同层 Overlay**。两处的面板内容是同一个控件类（<see cref="CycleEditorForm"/>），
    /// 所以视觉与交互完全一致，用户看不出区别。
    /// </remarks>
    private async Task ShowCycleDialogAsync(CycleRow? existing)
    {
        var form = new CycleEditorForm();
        if (existing is not null)
        {
            form.CycleName = existing.Name;
            form.Days = existing.Days;
        }

        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(new TextBlock
        {
            Text = existing is null ? "新建周期" : "编辑周期",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });

        // FR-15.16：编辑被引用的周期时，影响面写在副标题，不占面板正文。
        if (existing is { References: > 0 })
        {
            title.Children.Add(CaptionText($"{existing.References} 个条目正在使用它，保存后同步生效。"));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = form,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        // 「保存」永远可点：空着点下去才红字报错并保持面板不关（FR-15.20）。
        dialog.PrimaryButtonClick += (_, args) =>
        {
            // 查重名单取自落盘的那份配置，并排除正在编辑的这个周期自己（否则改星期会被自己挡住）。
            if (!form.Validate(ViewModel.OtherCycleNames(existing?.Id)))
            {
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (existing is null)
        {
            ViewModel.AddCycle(form.CycleName, form.Days);
        }
        else
        {
            ViewModel.UpdateCycle(existing.Id, form.CycleName, form.Days);
        }
    }

    /// <summary>造一段副标题文字（12px + 次要色）。</summary>
    private static TextBlock CaptionText(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };

        if (Application.Current?.Resources.TryGetValue("TextFillColorSecondaryBrush", out var brush) == true
            && brush is Brush secondary)
        {
            block.Foreground = secondary;
        }

        return block;
    }

    // ── 节假日数据（FR-15 / §6.5）──────────────────────────────────────────

    /// <summary>某一年份的「重新下载」：本地有数据也强制重拉。</summary>
    private async void OnHolidayUpdateYear(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.UpdateHolidaysAsync(force: true, onlyYear: row.Year);
        }
    }

    /// <summary>「补齐缺失年份」：只拉本地还没有合法数据的年份。</summary>
    private async void OnHolidayUpdateMissing(object sender, RoutedEventArgs e)
        => await ViewModel.UpdateHolidaysAsync(force: false);

    /// <summary>某一年份的「导出」：把本地数据另存为 JSON 文件。</summary>
    private void OnHolidayExportYear(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            ViewModel.ExportHolidayFile(row.Year);
        }
    }

    /// <summary>从行内按钮上取回它所属的那一行。</summary>
    /// <param name="sender">按钮。</param>
    /// <returns>行对象；取不到时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 🔴 优先读 <c>Tag</c>（XAML 里 <c>Tag="{x:Bind}"</c> 是**编译期**绑定，不经过继承链），
    /// <c>DataContext</c> 只作兜底。理由：这些按钮长在 <c>stc:SettingsCard</c> 的 Content 里，
    /// 而 Content 区的 DataContext 继承链不可靠 —— 卡片标题与描述显示正常**不能**作为
    /// 内容区 DataContext 正确的证据（那些走 x:Bind 编译期引用，与 DataContext 无关）。
    /// 取不到行的后果是静默 return，在用户眼里就是"这个按钮点了没反应"。
    /// </remarks>
    private static HolidayYearRow? RowOf(object sender)
        => sender is Button button
            ? button.Tag as HolidayYearRow ?? button.DataContext as HolidayYearRow
            : null;

    /// <summary>「选择文件」导入：年份取自文件内容，不是文件名。</summary>
    private void OnHolidayImport(object sender, RoutedEventArgs e) => ViewModel.ImportHolidayFile();

    /// <summary>「自动检查更新」开关被改变。⚠️ 初始化 / 回灌事件在这里被守卫挡掉（不落盘）。</summary>
    /// <remarks>
    /// 🔴 <c>async void</c> 是事件处理器的正确写法（没有调用方可以 await 它），
    /// 而这里**必须**能 await：打开开关会立刻跑一次检查，运行期间共享状态的忙标志要为真、
    /// 按钮要禁用；换成 fire-and-forget 的话，那一轮更新对界面来说等于不存在。
    /// </remarks>
    private async void OnHolidayAutoCheckToggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || sender is not ToggleSwitch toggle)
        {
            return;
        }

        await ViewModel.SetAutoCheckHolidayUpdatesAsync(toggle.IsOn);
    }

    /// <summary>在资源管理器里打开节假日数据目录（手工替换 / 校正数据用）。</summary>
    private void OnHolidayOpenFolder(object sender, RoutedEventArgs e) => ViewModel.OpenHolidayFolder();
}
