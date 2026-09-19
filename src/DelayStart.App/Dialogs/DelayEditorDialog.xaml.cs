using DelayStart.App.Interop;
using DelayStart.App.Services;
using DelayStart.App.ViewModels;
using DelayStart.Core.Models;
using DelayStart.Management.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DelayStart.App.Dialogs;

/// <summary>
/// 延时配置编辑器（<c>design-spec.md</c> 三之二）。4 种进入方式共用这一个弹窗，
/// 靠"这条记录有没有系统来源"分叉成 3 种形态。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **文案与编号一律取自设计稿**，不在这里临场发挥：用户判断"我这次操作会不会搞坏系统"
/// 靠的就是第 ④ 块那三行，写得含糊等于没写。
/// </para>
/// <para>
/// 各文本控件在构造函数里直接赋值而不用 <c>x:Bind</c>：这些值在弹窗显示前一次性确定、
/// 之后只有"活摘要"会变，绑定带来的复杂度大于收益。
/// </para>
/// <para>
/// ① 里的三块目标程序面板（只读卡片 / 未选择 / 已选择）按形态切可见性，
/// 而不是做三个弹窗 —— ②③④ 与底部摘要是四种进入方式完全共享的部分。
/// </para>
/// <para>
/// 2026-09-19 用户批复：**单条目延时上限已移除**（自定义值只受 7 天的输入兜底约束）；
/// 手动编辑自定义延时后提交需要**二次确认** —— 弹窗内的确认面板，点「确认使用」才真正提交。
/// bug 批复：延时预设与启动身份均为**胶囊**形态；文件选择走 Win32 对话框（提权进程里
/// WinRT 选择器打不开）；弹窗打开后对主窗口子树放行 UIPI 拖放消息。
/// </para>
/// </remarks>
public sealed partial class DelayEditorDialog : ContentDialog
{
    /// <summary>自定义延时数字框的输入兜底上限：7 天（上限配置已移除，只挡明显的误输入）。</summary>
    private const int FallbackMaxDelay = 604800;

    private readonly WindowHandleProvider? _handles;

    private bool _suppressSync;
    private int _delaySeconds;
    private string _targetPath = string.Empty;
    private bool _nameWasAutoFilled;

    /// <summary>用户是否手动改过自定义延时（选预设不算）。</summary>
    private bool _manualDelayEdit;

    /// <summary>自定义延时是否已经过确认面板确认。</summary>
    private bool _manualDelayConfirmed;

    /// <summary>构造「加入系统项」形态：目标程序由扫描到的自启动项绑定，不可更改。</summary>
    /// <param name="entry">要接管的系统自启动项。</param>
    /// <param name="presets">延时预设值（秒），来自 <c>Settings.DelayPresets</c>。</param>
    /// <param name="defaultPreset">默认预设（秒）—— 接管时预选的延时。</param>
    public DelayEditorDialog(StartupEntry entry, int[] presets, int defaultPreset)
    {
        ArgumentNullException.ThrowIfNull(entry);

        InitializeComponent();
        InitializeCommon(presets, defaultPreset);

        Title = "加入延时启动";
        PrimaryButtonText = "加入延时启动";
        SubtitleText.Text = $"来自「{DisplayText.SourceOf(entry.Source)}」，加入后原自启动项将被软禁用";
        TargetHintText.Text = "由系统自启动项绑定，不可更改";

        TargetNameText.Text = entry.Name;
        TargetPathText.Text = string.IsNullOrWhiteSpace(entry.Path) ? entry.SourceKey : entry.Path;
        TargetSourceText.Text = $"{DisplayText.SourceOf(entry.Source)} · {DisplayText.ScopeOf(entry.Scope)}";
        ArgumentsBox.Text = entry.Arguments;

        ImpactHeaderText.Text = "④ 接管对象";
        InfoTypeText.Text = $"系统自启动项 · 来自「{DisplayText.SourceOf(entry.Source)}」";
        InfoWriteText.Text = @"%APPDATA%\DelayStart\config.json（另在 StartupApproved 写入软禁用标记）";
        InfoImpactText.Text = "原自启动项将被软禁用：不删除注册表值、不移动文件，随时可完整恢复。";

        UseReadOnlyTarget();
        SetDelay(defaultPreset);
        SetIdentity(false);
    }

    /// <summary>构造「编辑已有条目」形态：系统项的目标程序只读，手动项可更换。</summary>
    /// <param name="item">要编辑的配置条目。</param>
    /// <param name="presets">延时预设值（秒）。</param>
    /// <param name="defaultPreset">默认预设（秒）；编辑形态下预选条目现有延时。</param>
    /// <param name="handles">主窗口句柄提供者，手动形态选文件时需要。</param>
    public DelayEditorDialog(
        DelayedItem item,
        int[] presets,
        int defaultPreset,
        WindowHandleProvider handles)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(handles);

        _handles = handles;

        InitializeComponent();
        InitializeCommon(presets, defaultPreset);

        var manual = item.IsManual;

        Title = "编辑延时设置";
        PrimaryButtonText = "保存修改";

        if (manual)
        {
            SubtitleText.Text = "手动添加的条目，不关联任何系统自启动项";
            TargetHintText.Text = "可更换目标程序";

            ImpactHeaderText.Text = "④ 系统影响";
            InfoTypeText.Text = "手动添加 · 不属于系统自启动项";
            InfoWriteText.Text = @"%APPDATA%\DelayStart\config.json";
            InfoImpactText.Text = "不改动注册表、启动文件夹、计划任务；移除时直接删除本条配置，无残留。";

            UsePickTarget();
            ApplyPickedFile(item.Path, fillName: true, fillWorkingDirectory: true);
            NameBox.Text = item.Name;
            WorkingDirBox.Text = item.WorkingDirectory;
            _nameWasAutoFilled = false;
        }
        else
        {
            SubtitleText.Text = "修改已接管条目，原自启动项保持软禁用";
            TargetHintText.Text = "由系统自启动项绑定，不可更改";

            TargetNameText.Text = item.Name;
            TargetPathText.Text = item.Path;
            TargetSourceText.Text = $"{DisplayText.SourceOf(item.Source)} · {DisplayText.ScopeOf(item.Scope)}";

            ImpactHeaderText.Text = "④ 接管对象";
            InfoTypeText.Text = $"系统自启动项 · 来自「{DisplayText.SourceOf(item.Source)}」";
            InfoWriteText.Text = @"%APPDATA%\DelayStart\config.json";
            InfoImpactText.Text = "原自启动项保持软禁用状态，本次修改不会改变它在系统中的状态。";

            UseReadOnlyTarget();
            WorkingDirBox.Text = item.WorkingDirectory;
        }

        ArgumentsBox.Text = item.Arguments;
        SetDelay(item.DelaySeconds);
        SetIdentity(item.RunAsAdmin);
    }

    /// <summary>构造「手动添加」形态：目标程序由用户选择。</summary>
    /// <param name="presets">延时预设值（秒）。</param>
    /// <param name="defaultPreset">默认预设（秒）—— 打开时预选的延时。</param>
    /// <param name="handles">主窗口句柄提供者，选文件时需要。</param>
    public DelayEditorDialog(int[] presets, int defaultPreset, WindowHandleProvider handles)
    {
        ArgumentNullException.ThrowIfNull(handles);

        _handles = handles;

        InitializeComponent();
        InitializeCommon(presets, defaultPreset);

        Title = "手动添加延时启动";
        PrimaryButtonText = "加入延时启动";
        SubtitleText.Text = "选择一个程序，由本程序在登录后延时启动";

        // 选择程序的主路径是 Win32 通用对话框（提权可用）；拖放是增强，
        // 弹窗 Opened 后对主窗口子树放行 UIPI 拖放消息（见 OnDialogOpened）。
        TargetHintText.Text = "拖入文件或点击选择";

        ImpactHeaderText.Text = "④ 系统影响";
        InfoTypeText.Text = "手动添加 · 不属于系统自启动项";
        InfoWriteText.Text = @"%APPDATA%\DelayStart\config.json";
        InfoImpactText.Text = "不改动注册表、启动文件夹、计划任务；移除时直接删除本条配置，无残留。";

        UsePickTarget();
        SetDelay(defaultPreset);
        SetIdentity(false);
    }

    /// <summary>最终选择的延时秒数。</summary>
    public int DelaySeconds => _delaySeconds;

    /// <summary>
    /// 自定义延时是否已经过确认面板确认。
    /// </summary>
    /// <remarks>
    /// 二次确认走弹窗内的确认面板（ContentDialog 之上不能叠第二个 ContentDialog），
    /// 确认后用 <c>Hide()</c> 关闭 —— 结果值为 <see cref="ContentDialogResult.None"/>，
    /// 调用方必须用「Primary 或本属性为真」判定提交。
    /// </remarks>
    public bool DelayConfirmed => _manualDelayConfirmed;

    /// <summary>最终选择的启动身份。</summary>
    public bool RunAsAdmin
    {
        get
        {
            foreach (var child in IdentityChoices.Children)
            {
                if (child is ToggleButton { Tag: "admin" } pill && pill.IsChecked == true)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>命令行参数；留空表示沿用原自启动项自带的参数（FR-4.5）。</summary>
    public string Arguments => ArgumentsBox.Text.Trim();

    /// <summary>显示名。只读形态下恒为空 —— 系统条目的名字由系统项决定，不参与编辑。</summary>
    public string ItemName => NameBox.Text.Trim();

    /// <summary>目标程序路径。只读形态下为系统项自带的路径。</summary>
    public string TargetPath => _targetPath;

    /// <summary>工作目录；留空表示用程序所在目录。</summary>
    public string WorkingDirectory => WorkingDirBox.Text.Trim();

    /// <summary>是否处于"目标程序由用户选择 / 可更换"的形态。</summary>
    public bool IsPickTarget => PickFilledBorder.Visibility == Visibility.Visible
        || PickEmptyBorder.Visibility == Visibility.Visible;

    /// <summary>把弹窗当前内容收成一份编辑值，供 <c>ConfigEditService</c> 使用。</summary>
    /// <returns>编辑值。</returns>
    public DelayItemValues ToValues() => new()
    {
        DelaySeconds = _delaySeconds,
        RunAsAdmin = RunAsAdmin,
        Arguments = Arguments,
        Name = ItemName,
        Path = TargetPath,
        WorkingDirectory = WorkingDirectory,
    };

    /// <summary>四种形态共用的初始化：延时预设、身份胶囊、输入兜底、拖放接收。</summary>
    /// <param name="presets">延时预设值（秒）。</param>
    /// <param name="defaultPreset">默认预设（秒），用于无值时的预选兜底。</param>
    private void InitializeCommon(int[] presets, int defaultPreset)
    {
        CustomDelayBox.Maximum = FallbackMaxDelay;
        FillDelayPresets(presets, defaultPreset);
        BuildIdentityPills();

        // 批复 4：拖放走经典 WM_DROPFILES（FileDropReceiver 子类化接收），
        // 弹窗打开时对弹层 HWND 再补一轮启用；收到文件路径后填进目标程序。
        Opened += OnDialogOpened;
        Closed += OnDialogClosed;
        FileDropReceiver.FilesDropped += OnFilesDropped;
    }

    /// <summary>弹窗已打开：此时弹层 HWND 已创建，放行 UIPI 白名单 + 启用经典拖放。</summary>
    private void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_handles is not null)
        {
            UipiMessageFilter.AllowDragDropForTree(_handles.Handle);
            FileDropReceiver.EnableTree(_handles.Handle);
        }
    }

    /// <summary>弹窗关闭：退订全局拖放广播，避免残留订阅。</summary>
    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        FileDropReceiver.FilesDropped -= OnFilesDropped;
    }

    /// <summary>经典拖放收到文件（批复 4）：取第一个受支持的填进目标程序。</summary>
    private void OnFilesDropped(string[] paths)
    {
        // 只读形态（目标程序由系统项绑定）不接受更换 —— 静默忽略。
        if (!IsPickTarget || paths.Length == 0)
        {
            return;
        }

        var path = Array.Find(paths, static candidate => IsSupportedProgram(candidate));
        if (string.IsNullOrEmpty(path))
        {
            ShowValidation("不支持的文件类型", "只接受 .exe / .lnk / .bat / .cmd / .msi。");
            return;
        }

        ApplyPickedFile(path, fillName: true, fillWorkingDirectory: true);
    }

    /// <summary>填充延时预设胶囊（一行多个，选中 = 当前延时）。</summary>
    private void FillDelayPresets(int[] presets, int defaultPreset)
    {
        var usable = presets is { Length: > 0 } ? presets : [0, 10, 30, 60, 120];
        if (!usable.Contains(defaultPreset))
        {
            defaultPreset = usable[0];
        }

        foreach (var preset in usable)
        {
            DelayChoices.Items.Add(
                CreatePill(DisplayText.DelayOf(preset), preset, preset == defaultPreset, OnDelayPillChecked));
        }
    }

    /// <summary>填充启动身份胶囊（普通身份 / 管理员，一行两个）。</summary>
    private void BuildIdentityPills()
    {
        IdentityChoices.Children.Add(
            CreatePill("普通身份", "normal", isChecked: false, OnIdentityNormalChecked));
        IdentityChoices.Children.Add(
            CreatePill("管理员", "admin", isChecked: false, OnIdentityAdminChecked));
    }

    /// <summary>造一个胶囊按钮。</summary>
    /// <remarks>
    /// 先设 <c>IsChecked</c> 再订阅 <c>Checked</c>：初始选中态不应触发"用户选择"的逻辑。
    /// Margin 统一右 8 下 8 —— 与身份胶囊 / 设置页胶囊保持一致的固定间距（二轮 bug 批复 4）。
    /// </remarks>
    private static ToggleButton CreatePill(string text, object tag, bool isChecked, RoutedEventHandler onChecked)
    {
        var pill = new ToggleButton
        {
            Content = text,
            Tag = tag,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 8),
            IsChecked = isChecked,
        };

        pill.Checked += onChecked;
        return pill;
    }

    /// <summary>把 <paramref name="source"/> 以外的胶囊全部弹起（单选语义）。</summary>
    private void MakeExclusive(System.Collections.IEnumerable pills, ToggleButton source)
    {
        _suppressSync = true;
        foreach (var pill in pills)
        {
            if (pill is ToggleButton button && !ReferenceEquals(button, source))
            {
                button.IsChecked = false;
            }
        }

        _suppressSync = false;
    }

    /// <summary>切到"目标程序只读"形态。</summary>
    /// <remarks>
    /// 🔴 工作目录**保持可编辑**（bug#5）：系统项的路径不可改，但"从哪个目录启动"是
    /// 启动质量的一部分（有些程序依赖工作目录找配置），接管时同样需要能填。
    /// </remarks>
    private void UseReadOnlyTarget()
    {
        ReadOnlyTargetBorder.Visibility = Visibility.Visible;
        PickEmptyBorder.Visibility = Visibility.Collapsed;
        PickFilledBorder.Visibility = Visibility.Collapsed;
        NameBox.Visibility = Visibility.Collapsed;
        WorkingDirBox.Visibility = Visibility.Visible;

        // 只读形态下 TargetPath 由系统项决定，不允许用户改。
        _targetPath = TargetPathText.Text;
    }

    /// <summary>切到"目标程序可选 / 可更换"形态。</summary>
    private void UsePickTarget()
    {
        ReadOnlyTargetBorder.Visibility = Visibility.Collapsed;
        PickEmptyBorder.Visibility = Visibility.Visible;
        PickFilledBorder.Visibility = Visibility.Collapsed;
        NameBox.Visibility = Visibility.Visible;
        WorkingDirBox.Visibility = Visibility.Visible;
    }

    /// <summary>记录一次文件选择，并把名称 / 工作目录填成合理默认。</summary>
    /// <param name="path">选中的文件路径。</param>
    /// <param name="fillName">是否覆盖名称框（用户改过就不覆盖）。</param>
    /// <param name="fillWorkingDirectory">是否覆盖工作目录框。</param>
    private void ApplyPickedFile(string path, bool fillName, bool fillWorkingDirectory)
    {
        _targetPath = path;

        PickedNameText.Text = System.IO.Path.GetFileNameWithoutExtension(path);
        PickedPathText.Text = path;
        PickEmptyBorder.Visibility = Visibility.Collapsed;
        PickFilledBorder.Visibility = Visibility.Visible;

        if (fillName && (_nameWasAutoFilled || string.IsNullOrWhiteSpace(NameBox.Text)))
        {
            NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(path);
            _nameWasAutoFilled = true;
        }

        if (fillWorkingDirectory && string.IsNullOrWhiteSpace(WorkingDirBox.Text))
        {
            WorkingDirBox.Text = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        }

        UpdateSummary();
    }

    /// <summary>打开文件选择对话框选一个程序。</summary>
    /// <returns>选中的文件路径；用户取消时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 🔴 WinRT 的 <c>FileOpenPicker</c> 在提权进程里打不开（选择器 broker 拒绝高完整性
    /// 令牌，表现为"点击选择程序没反应"—— 2026-09-19 用户实测）。改用 Win32 的
    /// <see cref="Win32FilePicker"/>（记事本等系统提权程序用的同一套对话框）。
    /// 这是「手动添加」的**主路径**，拖放只是增强 —— 按 <c>architecture.md</c> 第九节，
    /// 这条路径必须 100% 可用。对话框自己泵模态消息循环，同步调用即可。
    /// </remarks>
    private Task<string?> PickProgramAsync()
    {
        if (_handles is null || _handles.Handle == 0)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(Win32FilePicker.PickFile(
            _handles.Handle,
            "选择延时启动的程序",
            PickerExtensions));
    }

    private async void OnPickFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickProgramAsync();
            if (string.IsNullOrEmpty(path))
            {
                // 用户取消选择是正常路径；但 Handle 未登记时也走这里 —— 把这种情况
                // 显式说出来（bug#1：此前"点击没反应"就是它被静默吞掉）。
                if (_handles is null || _handles.Handle == 0)
                {
                    ShowValidation("无法打开文件选择器", "主窗口句柄尚未就绪，请关闭弹窗后重试；或直接把文件拖入本区域。");
                }

                return;
            }

            ApplyPickedFile(path, fillName: true, fillWorkingDirectory: true);
        }
        catch (Exception ex)
        {
            // 文件选择器失败（权限 / COM 状态异常）不再静默：弹窗内直接给出原因，
            // 拖放路径仍可用 —— 弹窗打开时已对主窗口子树放行 UIPI 拖放消息。
            ShowValidation("文件选择器打开失败", $"{ex.Message}\n\n也可以直接把文件拖入上方区域。");
        }
    }

    private static bool IsSupportedProgram(string path) =>
        Array.Exists(PickerExtensions, extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>与文件选择器一致的扩展名白名单（单一事实来源：两处必须同步改）。</summary>
    private static readonly string[] PickerExtensions = [".exe", ".lnk", ".bat", ".cmd", ".msi"];

    private void ShowValidation(string title, string message)
    {
        ValidationBar.Title = title;
        ValidationBar.Message = message;
        ValidationBar.IsOpen = true;

        // 提示条在内容区顶部：内容超高时把视口带回顶部，保证看得见（二轮 bug 批复 5）。
        ValidationBar.StartBringIntoView();
    }

    /// <summary>某个延时胶囊被选中。</summary>
    private void OnDelayPillChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSync || sender is not ToggleButton { Tag: int seconds } pill)
        {
            return;
        }

        MakeExclusive(DelayChoices.Items, pill);
        SetCustomDelayValue(seconds);
        _delaySeconds = seconds;

        // 选预设不算手动编辑：不需要二次确认。
        _manualDelayEdit = false;
        UpdateSummary();
    }

    private void OnIdentityNormalChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSync || sender is not ToggleButton pill)
        {
            return;
        }

        MakeExclusive(IdentityChoices.Children, pill);
        UpdateIdentityHint();
    }

    private void OnIdentityAdminChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSync || sender is not ToggleButton pill)
        {
            return;
        }

        MakeExclusive(IdentityChoices.Children, pill);
        UpdateIdentityHint();
    }

    private void OnCustomDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSync)
        {
            return;
        }

        if (double.IsNaN(args.NewValue))
        {
            // 用户清空输入框时的中间态：保留上一次的值，等他敲完。
            return;
        }

        _delaySeconds = (int)Math.Clamp(args.NewValue, 0, CustomDelayBox.Maximum);

        // 自定义值不再等于任何预设 → 弹起全部胶囊，避免"选了 30 秒却显示 45"的矛盾。
        SyncDelayPresetSelection();

        // 用户批复 6：手动编辑自定义延时后提交需要二次确认。
        _manualDelayEdit = true;
        _manualDelayConfirmed = false;

        UpdateSummary();
    }

    /// <summary>把延时设为指定值，同步胶囊选中态与自定义输入框。</summary>
    /// <param name="seconds">延时秒数。</param>
    private void SetDelay(int seconds)
    {
        _delaySeconds = seconds;
        _manualDelayEdit = false;
        _manualDelayConfirmed = false;
        SetCustomDelayValue(seconds);
        SyncDelayPresetSelection();
        UpdateSummary();
    }

    private void SetCustomDelayValue(int seconds)
    {
        _suppressSync = true;
        CustomDelayBox.Value = seconds;
        _suppressSync = false;
    }

    /// <summary>让延时胶囊组反映当前延时值；无匹配时全部弹起。</summary>
    private void SyncDelayPresetSelection()
    {
        _suppressSync = true;
        foreach (var item in DelayChoices.Items)
        {
            if (item is ToggleButton { Tag: int preset } pill)
            {
                pill.IsChecked = preset == _delaySeconds;
            }
        }

        _suppressSync = false;
    }

    /// <summary>设置启动身份并刷新提示文案。</summary>
    /// <param name="runAsAdmin">是否以管理员身份启动。</param>
    private void SetIdentity(bool runAsAdmin)
    {
        _suppressSync = true;
        foreach (var child in IdentityChoices.Children)
        {
            if (child is ToggleButton { Tag: string tag } pill)
            {
                pill.IsChecked = runAsAdmin ? tag == "admin" : tag == "normal";
            }
        }

        _suppressSync = false;

        UpdateIdentityHint();
    }

    private void UpdateIdentityHint()
    {
        IdentityHintText.Text = RunAsAdmin
            ? "由调度器在最高权限下启动，不会弹 UAC 确认框。"
            : "以当前登录用户身份启动，拖拽、剪贴板、文件权限都正常。";
    }

    private void UpdateSummary()
    {
        // 活摘要是设计稿刻意要求的：用户不用回去逐项核对就能确认最终结果。
        // 它显示在弹窗底部之外，因此这里同步写进 PrimaryButton 的相邻区域 ——
        // ContentDialog 没有"底部摘要槽"，放在内容末尾是最接近设计意图的位置。
        SummaryText.Text = $"将在 {DisplayText.DelayOf(_delaySeconds)} 以 {DisplayText.IdentityOf(RunAsAdmin)} 身份启动";
    }

    /// <summary>确认面板上的「确认使用」：确认后按已确认收尾。</summary>
    private void OnConfirmDelayAccepted(object sender, RoutedEventArgs e)
    {
        _manualDelayConfirmed = true;
        ConfirmOverlay.Visibility = Visibility.Collapsed;

        // Hide() 的结果值是 None；调用方用 DelayConfirmed 属性区分"确认提交"与"取消"。
        Hide();
    }

    /// <summary>确认面板上的「返回修改」：回到编辑状态。</summary>
    private void OnConfirmDelayRejected(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 校验是同步的，不需要 GetDeferral —— 取了 deferral 就必须保证每条路径都 Complete，
        // 在这里只会增加"某条分支忘记 Complete 导致弹窗卡死"的风险。

        if (IsPickTarget && string.IsNullOrWhiteSpace(_targetPath))
        {
            ShowValidation(
                "还没有选择程序",
                "请拖入或点击上方区域选择一个程序（.exe / .lnk / .bat / .cmd / .msi）。");
            args.Cancel = true;
            return;
        }

        if (_delaySeconds < 0)
        {
            ShowValidation("延时值不合法", "延时不能为负数。");
            args.Cancel = true;
            return;
        }

        // 用户批复 6：手动编辑的自定义延时在提交前需要二次确认。
        if (_manualDelayEdit && !_manualDelayConfirmed)
        {
            ConfirmText.Text = $"将使用自定义延时 {DisplayText.DelayOf(_delaySeconds)}（不在预设列表里）。确定采用吗？";
            ConfirmOverlay.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
    }
}
