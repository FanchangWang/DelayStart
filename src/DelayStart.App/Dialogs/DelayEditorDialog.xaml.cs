using System.Globalization;

using DelayStart.App.Services;
using DelayStart.App.ViewModels;
using DelayStart.Core.Models;
using DelayStart.Management.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Storage.Pickers;

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
/// </remarks>
public sealed partial class DelayEditorDialog : ContentDialog
{
    /// <summary>自定义延时数字框的上限（无设置上限时的兜底：7 天）。</summary>
    private const int FallbackMaxDelay = 604800;

    private readonly int _maxDelaySeconds;
    private readonly WindowHandleProvider? _handles;

    private bool _suppressSync;
    private int _delaySeconds;
    private string _targetPath = string.Empty;
    private bool _nameWasAutoFilled;

    /// <summary>构造「加入系统项」形态：目标程序由扫描到的自启动项绑定，不可更改。</summary>
    /// <param name="entry">要接管的系统自启动项。</param>
    /// <param name="presets">延时预设值（秒），来自 <c>Settings.DelayPresets</c>。</param>
    /// <param name="maxDelaySeconds">单条目延时上限；<c>0</c> 表示不限制。</param>
    public DelayEditorDialog(StartupEntry entry, int[] presets, int maxDelaySeconds)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _maxDelaySeconds = maxDelaySeconds;

        InitializeComponent();
        InitializeCommon(presets);

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
        SetDelay(DelayedItem.DefaultDelaySeconds);
        SetIdentity(false);
    }

    /// <summary>构造「编辑已有条目」形态：系统项的目标程序只读，手动项可更换。</summary>
    /// <param name="item">要编辑的配置条目。</param>
    /// <param name="presets">延时预设值（秒）。</param>
    /// <param name="maxDelaySeconds">单条目延时上限；<c>0</c> 表示不限制。</param>
    /// <param name="handles">主窗口句柄提供者，手动形态选文件时需要。</param>
    public DelayEditorDialog(
        DelayedItem item,
        int[] presets,
        int maxDelaySeconds,
        WindowHandleProvider handles)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(handles);

        _maxDelaySeconds = maxDelaySeconds;
        _handles = handles;

        InitializeComponent();
        InitializeCommon(presets);

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
        }

        ArgumentsBox.Text = item.Arguments;
        SetDelay(item.DelaySeconds);
        SetIdentity(item.RunAsAdmin);
    }

    /// <summary>构造「手动添加」形态：目标程序由用户选择。</summary>
    /// <param name="presets">延时预设值（秒）。</param>
    /// <param name="maxDelaySeconds">单条目延时上限；<c>0</c> 表示不限制。</param>
    /// <param name="handles">主窗口句柄提供者，选文件时需要。</param>
    public DelayEditorDialog(int[] presets, int maxDelaySeconds, WindowHandleProvider handles)
    {
        ArgumentNullException.ThrowIfNull(handles);

        _maxDelaySeconds = maxDelaySeconds;
        _handles = handles;

        InitializeComponent();
        InitializeCommon(presets);

        Title = "手动添加延时启动";
        PrimaryButtonText = "加入延时启动";
        SubtitleText.Text = "选择一个程序，由本程序在登录后延时启动";

        // ⚠️ 设计稿原文是"拖入文件或点击选择"。当前**只实现了点击选择** ——
        // 提权进程的拖放（R10）尚未在本期落地，与其留一个"拖进去没反应"的控件，
        // 不如不承诺它。拖放可用后再把这句改回原文。
        TargetHintText.Text = "点击选择程序";

        ImpactHeaderText.Text = "④ 系统影响";
        InfoTypeText.Text = "手动添加 · 不属于系统自启动项";
        InfoWriteText.Text = @"%APPDATA%\DelayStart\config.json";
        InfoImpactText.Text = "不改动注册表、启动文件夹、计划任务；移除时直接删除本条配置，无残留。";

        UsePickTarget();
        SetDelay(DelayedItem.DefaultDelaySeconds);
        SetIdentity(false);
    }

    /// <summary>最终选择的延时秒数。</summary>
    public int DelaySeconds => _delaySeconds;

    /// <summary>最终选择的启动身份。</summary>
    public bool RunAsAdmin => IdentityChoices.SelectedIndex == 1;

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

    /// <summary>四种形态共用的初始化：延时预设、上限、身份提示。</summary>
    /// <param name="presets">延时预设值（秒）。</param>
    private void InitializeCommon(int[] presets)
    {
        CustomDelayBox.Maximum = _maxDelaySeconds > 0 ? _maxDelaySeconds : FallbackMaxDelay;
        FillDelayPresets(presets);
    }

    private void FillDelayPresets(int[] presets)
    {
        var usable = presets is { Length: > 0 } ? presets : [0, 10, 30, 60, 120];

        foreach (var preset in usable)
        {
            DelayChoices.Items.Add(new RadioButton
            {
                Content = DisplayText.DelayOf(preset),
                Tag = preset,
            });
        }
    }

    /// <summary>切到"目标程序只读"形态。</summary>
    private void UseReadOnlyTarget()
    {
        ReadOnlyTargetBorder.Visibility = Visibility.Visible;
        PickEmptyBorder.Visibility = Visibility.Collapsed;
        PickFilledBorder.Visibility = Visibility.Collapsed;
        NameBox.Visibility = Visibility.Collapsed;
        WorkingDirBox.Visibility = Visibility.Collapsed;

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

    /// <summary>打开文件选择器选一个程序。</summary>
    /// <returns>选中的文件路径；用户取消时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 🔴 unpackaged 的 WinUI 3 必须 <c>InitializeWithWindow</c>，否则 <c>PickSingleFileAsync</c>
    /// 直接抛异常。这是「手动添加」的**主路径**，拖放（R10）只是增强 ——
    /// 按 <c>architecture.md</c> 第九节，这条路径必须 100% 可用。
    /// </remarks>
    private async Task<string?> PickProgramAsync()
    {
        if (_handles is null || _handles.Handle == 0)
        {
            return null;
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _handles.Handle);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        foreach (var extension in new[] { ".exe", ".lnk", ".bat", ".cmd", ".msi" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void OnPickFile(object sender, RoutedEventArgs e)
    {
        var path = await PickProgramAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ApplyPickedFile(path, fillName: true, fillWorkingDirectory: true);
    }

    private void OnDelayPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSync || DelayChoices.SelectedItem is not RadioButton { Tag: int seconds })
        {
            return;
        }

        SetCustomDelayValue(seconds);
        _delaySeconds = seconds;
        UpdateSummary();
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

        // 自定义值不再等于任何预设 → 取消预设的选中态，避免"选了 30 秒却显示 45"的矛盾。
        SyncDelayPresetSelection();

        UpdateSummary();
    }

    private void OnIdentityChanged(object sender, SelectionChangedEventArgs e) => UpdateIdentityHint();

    /// <summary>把延时设为指定值，同步预设选中态与自定义输入框。</summary>
    /// <param name="seconds">延时秒数。</param>
    private void SetDelay(int seconds)
    {
        _delaySeconds = seconds;
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

    /// <summary>让预设单选组反映当前延时值；无匹配时清空选中。</summary>
    private void SyncDelayPresetSelection()
    {
        var index = -1;
        for (var i = 0; i < DelayChoices.Items.Count; i++)
        {
            if (DelayChoices.Items[i] is RadioButton { Tag: int preset } && preset == _delaySeconds)
            {
                index = i;
                break;
            }
        }

        _suppressSync = true;
        DelayChoices.SelectedIndex = index;
        _suppressSync = false;
    }

    /// <summary>设置启动身份并刷新提示文案。</summary>
    /// <param name="runAsAdmin">是否以管理员身份启动。</param>
    private void SetIdentity(bool runAsAdmin)
    {
        IdentityChoices.SelectedIndex = runAsAdmin ? 1 : 0;
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

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 校验是同步的，不需要 GetDeferral —— 取了 deferral 就必须保证每条路径都 Complete，
        // 在这里只会增加"某条分支忘记 Complete 导致弹窗卡死"的风险。

        if (IsPickTarget && string.IsNullOrWhiteSpace(_targetPath))
        {
            ValidationBar.Title = "还没有选择程序";
            ValidationBar.Message = "请点击上方区域选择一个程序（.exe / .lnk / .bat / .cmd / .msi）。";
            ValidationBar.IsOpen = true;
            args.Cancel = true;
            return;
        }

        if (_delaySeconds < 0 || (_maxDelaySeconds > 0 && _delaySeconds > _maxDelaySeconds))
        {
            ValidationBar.Title = "延时值不合法";
            ValidationBar.Message = _maxDelaySeconds > 0
                ? $"延时必须在 0 到 {_maxDelaySeconds.ToString(CultureInfo.InvariantCulture)} 秒之间（上限可在「设置」里调整）。"
                : "延时不能为负数。";
            ValidationBar.IsOpen = true;
            args.Cancel = true;
        }
    }
}
