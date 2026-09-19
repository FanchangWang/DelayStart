using DelayStart.App.ViewModels;
using DelayStart.Core.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.Dialogs;

/// <summary>
/// 延时配置编辑器（<c>design-spec.md</c> 三之二）。本批是「加入系统项」形态。
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
/// </remarks>
public sealed partial class DelayEditorDialog : ContentDialog
{
    /// <summary>自定义延时数字框的上限（无设置上限时的兜底：7 天）。</summary>
    private const int FallbackMaxDelay = 604800;

    private readonly int _maxDelaySeconds;
    private bool _suppressSync;
    private int _delaySeconds;

    /// <summary>构造「加入系统项」形态的编辑器。</summary>
    /// <param name="entry">要接管的系统自启动项。</param>
    /// <param name="presets">延时预设值（秒），来自 <c>Settings.DelayPresets</c>。</param>
    /// <param name="maxDelaySeconds">单条目延时上限；<c>0</c> 表示不限制。</param>
    public DelayEditorDialog(StartupEntry entry, int[] presets, int maxDelaySeconds)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _maxDelaySeconds = maxDelaySeconds;

        InitializeComponent();

        Title = "加入延时启动";
        PrimaryButtonText = "加入延时启动";
        SubtitleText.Text = $"来自「{DisplayText.SourceOf(entry.Source)}」，加入后原自启动项将被软禁用";
        TargetHintText.Text = "由系统自启动项绑定，不可更改";

        TargetNameText.Text = entry.Name;
        TargetPathText.Text = string.IsNullOrWhiteSpace(entry.Path) ? entry.SourceKey : entry.Path;
        TargetSourceText.Text = $"{DisplayText.SourceOf(entry.Source)} · {DisplayText.ScopeOf(entry.Scope)}";
        ArgumentsBox.Text = entry.Arguments;

        InfoTypeText.Text = $"系统自启动项 · 来自「{DisplayText.SourceOf(entry.Source)}」";
        InfoWriteText.Text = @"%APPDATA%\DelayStart\config.json（另在 StartupApproved 写入软禁用标记）";
        InfoImpactText.Text = "原自启动项将被软禁用：不删除注册表值、不移动文件，随时可完整恢复。";

        CustomDelayBox.Maximum = maxDelaySeconds > 0 ? maxDelaySeconds : FallbackMaxDelay;

        FillDelayPresets(presets);
        IdentityChoices.SelectedIndex = 0;

        UpdateSummary();
    }

    /// <summary>最终选择的延时秒数。</summary>
    public int DelaySeconds => _delaySeconds;

    /// <summary>最终选择的启动身份。</summary>
    public bool RunAsAdmin => IdentityChoices.SelectedIndex == 1;

    /// <summary>命令行参数；留空表示沿用原自启动项自带的参数（FR-4.5）。</summary>
    public string Arguments => ArgumentsBox.Text.Trim();

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

        // 默认落在 30 秒：它是设计稿里的默认值，也是大多数"开机不着急"程序的合理档位。
        var defaultIndex = Array.IndexOf(usable, DelayedItem.DefaultDelaySeconds);
        DelayChoices.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;
    }

    private void OnDelayPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSync || DelayChoices.SelectedItem is not RadioButton { Tag: int seconds })
        {
            return;
        }

        _delaySeconds = seconds;

        _suppressSync = true;
        CustomDelayBox.Value = seconds;
        _suppressSync = false;

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
        var matchesPreset = DelayChoices.SelectedItem is RadioButton { Tag: int preset } && preset == _delaySeconds;

        _suppressSync = true;
        if (!matchesPreset)
        {
            DelayChoices.SelectedItem = null;
        }

        _suppressSync = false;

        UpdateSummary();
    }

    private void OnIdentityChanged(object sender, SelectionChangedEventArgs e)
    {
        IdentityHintText.Text = RunAsAdmin
            ? "由调度器在最高权限下启动，不会弹 UAC 确认框。"
            : "以当前登录用户身份启动，拖拽、剪贴板、文件权限都正常。";

        UpdateSummary();
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
        if (_delaySeconds < 0 || (_maxDelaySeconds > 0 && _delaySeconds > _maxDelaySeconds))
        {
            ValidationBar.Message = _maxDelaySeconds > 0
                ? $"延时必须在 0 到 {_maxDelaySeconds} 秒之间（上限可在「设置」里调整）。"
                : "延时不能为负数。";
            ValidationBar.IsOpen = true;
            args.Cancel = true;
        }
    }
}
