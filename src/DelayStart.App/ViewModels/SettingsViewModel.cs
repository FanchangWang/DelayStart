using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;

using Microsoft.UI.Xaml.Controls;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 设置页 ViewModel（FR-9）。所有修改立即落盘（原子写），不需要「应用」按钮 ——
/// 与设计稿「设置改动即时生效，改完就走」的交互一致。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppConfigStore _configStore;

    /// <summary>构造设置页 ViewModel。</summary>
    /// <param name="configStore">配置读写端。</param>
    public SettingsViewModel(IAppConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);

        _configStore = configStore;
    }

    /// <summary>延时预设值的可编辑文本（逗号分隔，FR-9.2）。</summary>
    [ObservableProperty]
    public partial string DelayPresetsText { get; set; } = string.Empty;

    /// <summary>单条目延时上限秒数；0 = 不限制（FR-9.3）。</summary>
    [ObservableProperty]
    public partial string MaxDelayText { get; set; } = string.Empty;

    /// <summary>完成时的通知策略（FR-9.7）。下拉框下标与 <see cref="NotifyMode"/> 枚举值一致。</summary>
    [ObservableProperty]
    public partial int NotifyModeIndex { get; set; }

    /// <summary>同一次运行内的重试次数文本（FR-9.6）。</summary>
    [ObservableProperty]
    public partial string RetryCountText { get; set; } = string.Empty;

    /// <summary>全部启动完成后托盘图标保留秒数（FR-9.5）。</summary>
    [ObservableProperty]
    public partial string TrayKeepText { get; set; } = string.Empty;

    /// <summary>是否显示托盘进度图标（FR-9.8）。</summary>
    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; } = true;

    /// <summary>普通用户条目优先降权启动（FR-9.9）。</summary>
    [ObservableProperty]
    public partial bool PreferDeElevatedLaunch { get; set; } = true;

    /// <summary>降权启动失败时回退为直接启动（FR-9.9）。</summary>
    [ObservableProperty]
    public partial bool FallbackOnDeElevationFailure { get; set; } = true;

    /// <summary>页面底部状态（保存结果 / 校验错误）。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary><see cref="StatusText"/> 变化时联动三个派生属性。</summary>
    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StatusSeverity));
    }

    /// <summary>状态是否为错误（决定 InfoBar 颜色）。</summary>
    public bool HasError => StatusText.StartsWith('✗');

    /// <summary>状态条是否可见。</summary>
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>状态条的严重级别（XAML 不能直接算，放这里）。</summary>
    public InfoBarSeverity StatusSeverity => HasError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    /// <summary>通知策略下拉框的选项。</summary>
    public ObservableCollection<string> NotifyModes { get; } = ["仅失败时通知", "总是通知", "从不通知"];

    /// <summary>加载当前设置。页面进入时调用。</summary>
    public void Load()
    {
        var settings = _configStore.Load().Settings;

        DelayPresetsText = string.Join(", ", settings.DelayPresets);
        MaxDelayText = settings.MaxDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        NotifyModeIndex = (int)settings.NotifyMode;
        RetryCountText = settings.RetryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TrayKeepText = settings.TrayKeepSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowTrayIcon = settings.ShowTrayIcon;
        PreferDeElevatedLaunch = settings.PreferDeElevatedLaunch;
        FallbackOnDeElevationFailure = settings.FallbackOnDeElevationFailure;

        StatusText = string.Empty;
    }

    /// <summary>保存全部设置。任何一项不合法就整体不保存（避免半新半旧的配置）。</summary>
    /// <remarks>
    /// 🔴 校验失败**不写文件**：设置项之间没有依赖，但"改了上限、预设没改"的半保存状态
    /// 会让用户搞不清哪些生效了。要么全存，要么一条不存。
    /// </remarks>
    [RelayCommand]
    private void Save()
    {
        if (!TryParsePresets(DelayPresetsText, out var presets, out var error))
        {
            Fail($"延时预设：{error}");
            return;
        }

        if (!TryParseInt(MaxDelayText, 0, int.MaxValue, "延时上限", out var maxDelay, out error))
        {
            Fail(error);
            return;
        }

        if (!TryParseInt(RetryCountText, 0, 5, "重试次数", out var retryCount, out error))
        {
            Fail(error);
            return;
        }

        if (!TryParseInt(TrayKeepText, 0, 3600, "托盘保留秒数", out var trayKeep, out error))
        {
            Fail(error);
            return;
        }

        var config = _configStore.Load();
        config.Settings.DelayPresets = presets;
        config.Settings.MaxDelaySeconds = maxDelay;
        config.Settings.NotifyMode = (NotifyMode)NotifyModeIndex;
        config.Settings.RetryCount = retryCount;
        config.Settings.TrayKeepSeconds = trayKeep;
        config.Settings.ShowTrayIcon = ShowTrayIcon;
        config.Settings.PreferDeElevatedLaunch = PreferDeElevatedLaunch;
        config.Settings.FallbackOnDeElevationFailure = FallbackOnDeElevationFailure;

        try
        {
            _configStore.Save(config);
        }
        catch (StartupOperationException ex)
        {
            Fail(ex.Message);
            return;
        }

        StatusText = "✓ 已保存，下次登录调度时生效";
    }

    /// <summary>打开配置文件所在目录。</summary>
    [RelayCommand]
    private void OpenConfigFolder()
    {
        var directory = System.IO.Path.GetDirectoryName(_configStore.ConfigFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        // explorer 是独立进程，返回值与本程序无关；CA1806 用 discard 显式表达。
        _ = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private void Fail(string message)
    {
        StatusText = $"✗ {message}";
    }

    /// <summary>解析逗号分隔的预设值：允许空（保留空集）、升序去重（FR-9.2 的存储约定）。</summary>
    private static bool TryParsePresets(string text, out int[] presets, out string error)
    {
        presets = [];
        error = string.Empty;

        var tokens = text.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return true; // 清空 = 不显示快选按钮，是合法状态
        }

        var values = new List<int>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!int.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 0)
            {
                error = $"「{token}」不是非负整数";
                return false;
            }

            values.Add(value);
        }

        presets = [.. values.Distinct().OrderBy(static value => value)];
        return true;
    }

    private static bool TryParseInt(string text, int min, int max, string label, out int value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (!int.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            error = $"{label}：「{text}」不是整数";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"{label}必须在 {min} 到 {max} 之间";
            return false;
        }

        return true;
    }
}
