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
/// 预设延时与周期两节的行内控件（设为默认 / 删除 / 添加 / 编辑）走 code-behind 事件
/// 而不是命令：行是整表重建的快照，事件带上行的 DataContext 最直接。
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

    /// <summary>某一行的「设为默认」。</summary>
    /// <remarks>
    /// 🔴 原先"点整行 = 设为默认"的互斥逻辑（遍历可视树弹起其余行）已随形态一起删掉：
    /// 默认值本来就只有一份、写在配置里，界面每次都是按配置整表重建 ——
    /// 手工互斥是在补控件没有的能力，现在由按钮直接表达，这层补丁就是负债。
    /// </remarks>
    private void OnPresetSetDefault(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PresetRow row })
        {
            ViewModel.SetDefaultPreset(row);
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

    /// <summary>
    /// 「＋ 新建延时」：弹窗输入秒数（2026-09-23 批复 23；批复 24 起挂在子内容第一条上）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 校验失败时**不关窗**（<c>args.Cancel = true</c>）并把原因写在弹窗内的 InfoBar 上：
    /// 弹窗是模态的，页面底部状态条与右下角通知都在它后面 —— 报在那两处等于没报。
    /// </para>
    /// <para>
    /// 面板内容用代码拼而不单开一个 XAML：只有一个数字框加一条错误提示，
    /// 为它立一个 <c>ContentDialog</c> 子类，收益不抵"又多一处要同步的版式"。
    /// </para>
    /// </remarks>
    private async void OnPresetAddDialog(object sender, RoutedEventArgs e)
    {
        var box = new NumberBox
        {
            Header = "延时（秒）",
            Minimum = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = double.NaN,
        };

        var error = new InfoBar
        {
            IsClosable = false,
            IsOpen = false,
            Severity = InfoBarSeverity.Error,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(CaptionText("编辑器快选按钮里会多出这一项，接管条目时自动填入。"));
        panel.Children.Add(box);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新建预设延时",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (double.IsNaN(box.Value))
            {
                error.Message = "先填一个以秒计的延时。";
                error.IsOpen = true;
                args.Cancel = true;
                return;
            }

            if (ViewModel.AddPreset((int)Math.Round(box.Value)) is { } reason)
            {
                error.Message = reason;
                error.IsOpen = true;
                args.Cancel = true;
            }
        };

        await dialog.ShowAsync();
    }

    // ── 两节的展开 / 收起（2026-09-23 批复 23；批复 24 起整行都可点）──────────────

    /// <summary>「延时」节的展开 / 收起。</summary>
    /// <remarks>
    /// 🔴 三角**不再是 ToggleButton**（批复 24）：它现在只是一枚跟着 ViewModel 变的图标，
    /// 展开 / 收起的唯一入口是整行那个透明按钮。这样也就不需要"把按钮的选中态回写对齐"
    /// 那一步 —— 真值只有 <c>ViewModel.IsDelaySectionExpanded</c> 一个。
    /// </remarks>
    private void OnPresetSectionToggle(object sender, RoutedEventArgs e) => ViewModel.ToggleDelaySection();

    /// <summary>「周期」节的展开 / 收起。</summary>
    private void OnCycleSectionToggle(object sender, RoutedEventArgs e) => ViewModel.ToggleCycleSection();

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
