using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DelayStart.App.Services;
using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 预设延时列表的一行（FR-9.2，2026-09-19 用户批复的新编辑形态）。
/// </summary>
/// <remarks>
/// 左侧选中状态（选中 = 默认）+ 中间时间文本 + 右侧删除图标；
/// 整行是不可变快照 —— 任何增删改都整表重建，避免行内状态与配置半同步。
/// </remarks>
public sealed class PresetRow
{
    /// <summary>构造一行。</summary>
    /// <param name="seconds">延时秒数。</param>
    /// <param name="isDefault">是否为默认预设（列表中处于「选中」状态的那一项）。</param>
    /// <param name="canDelete">当前能否删除（列表只剩一项时不允许）。</param>
    public PresetRow(int seconds, bool isDefault, bool canDelete)
    {
        Seconds = seconds;
        IsDefault = isDefault;
        CanDelete = canDelete;
        Display = DisplayText.DelayOf(seconds);
    }

    /// <summary>延时秒数。</summary>
    public int Seconds { get; }

    /// <summary>是否为默认预设。</summary>
    public bool IsDefault { get; }

    /// <summary>能否删除（只剩一项时为 <see langword="false"/>）。</summary>
    public bool CanDelete { get; }

    /// <summary>时间文案（立即 / n 秒 / n 分 n 秒）。</summary>
    public string Display { get; }
}

/// <summary>
/// 设置页 ViewModel（FR-9，2026-09-19 用户批复重做）：
/// 所有操作**立即落盘生效**，没有「保存」按钮；改某项就存某项，失败只在状态条提示该项。
/// </summary>
/// <remarks>
/// 本轮批复的关键语义：
/// ① 降权两开关移除 —— 普通条目一律降权启动，失败记日志，绝不回退提权；
/// ② 托盘两设置移除 —— 托盘只属调度端，生命周期跟随通知；
/// ③ 新增主题（跟随系统 / 浅色 / 深色），切换即时生效；
/// ④ 预设延时改为列表编辑：左选中（=默认）+ 中时间 + 右删除，删默认时默认前移（无前则后移），
///    只剩一项不允许删除；支持输入秒数添加。
/// </remarks>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppConfigStore _configStore;
    private readonly ThemeService _theme;
    private readonly ToastService _toast;

    /// <summary>加载期守卫：Load 期间对可观察属性的回灌不得触发落盘。</summary>
    private bool _loading;

    /// <summary>构造设置页 ViewModel。</summary>
    /// <param name="configStore">配置读写端。</param>
    /// <param name="theme">主题服务（切主题即时落盘并广播到主窗口）。</param>
    /// <param name="toast">应用内通知（2026-09-21 批复：添加成功改走右下角自动消失的通知）。</param>
    public SettingsViewModel(IAppConfigStore configStore, ThemeService theme, ToastService toast)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(toast);

        _configStore = configStore;
        _theme = theme;
        _toast = toast;
    }

    /// <summary>预设延时列表（选中项 = 默认）。</summary>
    public ObservableCollection<PresetRow> Presets { get; } = [];

    /// <summary>「添加」输入框的秒数（NumberBox 双向绑定；NaN 表示未填）。</summary>
    [ObservableProperty]
    public partial double NewPresetValue { get; set; } = double.NaN;

    /// <summary>重试次数（0–5），NumberBox 的数值形式。</summary>
    [ObservableProperty]
    public partial double RetryCount { get; set; }

    /// <summary>通知策略下拉框下标，与 <see cref="NotifyMode"/> 枚举值一致。</summary>
    [ObservableProperty]
    public partial int NotifyModeIndex { get; set; }

    /// <summary>主题下拉框下标，与 <see cref="ThemePreference"/> 枚举值一致。</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary>页面底部状态条的错误文案（2026-09-21 批复：状态条只报错；成功类提示走右下角通知）。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>状态条是否可见。现在只有错误才会置文案。</summary>
    /// <remarks>
    /// 🔴 必须是 <c>[ObservableProperty]</c> 的 <see cref="StatusText"/> + 这里的通知，
    /// 而不是把可见性做成独立字段：x:Bind OneWay 要求路径上有通知源，
    /// get-only 计算属性会被 XamlCompiler 拒绝（增量 pass-2 下按错误处理）。
    /// </remarks>
    public bool HasError => StatusText.Length > 0;

    /// <summary>通知策略下拉框的选项。</summary>
    // UI v2（2026-09-21 批复）：完成通知的载体从右下角气泡改为进度面板，
    // 文案点明「面板」，避免用户以为还是气泡。枚举取值与下标对应关系不变。
    public ObservableCollection<string> NotifyModes { get; } = ["仅失败时弹出面板", "总是弹出面板", "从不弹出面板"];

    /// <summary>主题下拉框的选项。</summary>
    public ObservableCollection<string> ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];

    /// <summary>加载当前设置。页面进入时调用。</summary>
    public void Load()
    {
        _loading = true;
        try
        {
            var settings = _configStore.Load().Settings;

            RebuildPresets(settings);
            RetryCount = Math.Clamp(settings.RetryCount, 0, 5);
            NotifyModeIndex = (int)settings.NotifyMode;
            ThemeIndex = (int)settings.Theme;
            NewPresetValue = double.NaN;
            StatusText = string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>通知策略被用户改变：立即落盘。</summary>
    /// <param name="index">下拉框下标。</param>
    public void SetNotifyMode(int index)
    {
        if (_loading || index < 0)
        {
            return;
        }

        Persist(settings => settings.NotifyMode = (NotifyMode)index);
    }

    /// <summary>重试次数被用户改变：立即落盘（收敛到 0–5）。</summary>
    /// <param name="value">输入值。</param>
    public void SetRetryCount(double value)
    {
        if (_loading || double.IsNaN(value))
        {
            return;
        }

        Persist(settings => settings.RetryCount = (int)Math.Clamp(value, 0, 5));
    }

    /// <summary>主题被用户改变：经 <see cref="ThemeService"/> 落盘并广播。</summary>
    /// <param name="index">下拉框下标。</param>
    public void SetTheme(int index)
    {
        if (_loading || index < 0)
        {
            return;
        }

        try
        {
            _theme.Set((ThemePreference)index);
            StatusText = string.Empty;
        }
        catch (StartupOperationException ex)
        {
            Fail($"主题保存失败：{ex.Message}");
        }
    }

    /// <summary>把某个预设设为默认：立即落盘。</summary>
    /// <param name="row">选中的行。</param>
    public void SetDefaultPreset(PresetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_loading)
        {
            return;
        }

        Persist(settings => settings.DefaultPreset = row.Seconds);
        ReloadPresetsFromConfig();
    }

    /// <summary>删除一个预设。</summary>
    /// <param name="row">要删除的行。</param>
    /// <remarks>
    /// 用户批复的规则：删除的是默认时，默认**向前一个**（无前则向后一个）；
    /// 列表只剩一项时不允许删除（行上的删除按钮已禁用，这里再兜一层）。
    /// </remarks>
    public void DeletePreset(PresetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_loading)
        {
            return;
        }

        var settings = _configStore.Load().Settings;
        var list = settings.DelayPresets.ToList();
        if (list.Count <= 1 || !list.Contains(row.Seconds))
        {
            return;
        }

        var index = list.IndexOf(row.Seconds);
        list.RemoveAt(index);
        settings.DelayPresets = [.. list];

        if (settings.DefaultPreset == row.Seconds)
        {
            // 默认随删除前移；删的是第一项就后移（剩下的列表此时必然非空）。
            settings.DefaultPreset = list[Math.Max(index - 1, 0)];
        }

        SaveOrReport(settings);
        ReloadPresetsFromConfig();
    }

    /// <summary>按输入框里的秒数添加一个预设。</summary>
    public void AddPreset()
    {
        if (_loading)
        {
            return;
        }

        if (double.IsNaN(NewPresetValue))
        {
            Fail("先填一个以秒计的延时，再点「添加」");
            return;
        }

        var seconds = (int)Math.Round(NewPresetValue);

        var settings = _configStore.Load().Settings;
        if (seconds < 0)
        {
            Fail("延时不能是负数");
            return;
        }

        if (settings.DelayPresets.Contains(seconds))
        {
            // 2026-09-21 批复：重复添加走右下角通知（与成功提示同一通道），不占状态条。
            _toast.Show($"「{DisplayText.DelayOf(seconds)}」已在列表里，无需重复添加");
            NewPresetValue = double.NaN;
            return;
        }

        Persist(settings => settings.DelayPresets = [.. settings.DelayPresets.Append(seconds).Order()]);
        NewPresetValue = double.NaN;
        ReloadPresetsFromConfig();

        // 2026-09-21 批复：添加成功改走右下角自动消失的应用内通知（不再占用状态条）。
        _toast.Show($"已添加预设延时 {DisplayText.DelayOf(seconds)}");
    }

    /// <summary>统一落盘：读最新配置 → 局部改 → 原子写。失败转状态条。</summary>
    /// <param name="mutate">对 <c>settings</c> 节点的局部修改。</param>
    private void Persist(Action<Settings> mutate)
    {
        var config = _configStore.Load();
        mutate(config.Settings);
        SaveOrReport(config.Settings);
    }

    private void SaveOrReport(Settings settings)
    {
        var config = _configStore.Load();
        config.Settings = settings;

        try
        {
            _configStore.Save(config);
            StatusText = string.Empty;
        }
        catch (StartupOperationException ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>按配置现状重建预设行（整表快照，见 <see cref="PresetRow"/>）。</summary>
    private void ReloadPresetsFromConfig() => RebuildPresets(_configStore.Load().Settings);

    private void RebuildPresets(Settings settings)
    {
        var presets = settings.DelayPresets;
        Presets.Clear();
        foreach (var seconds in presets)
        {
            Presets.Add(new PresetRow(
                seconds,
                isDefault: seconds == settings.DefaultPreset,
                canDelete: presets.Length > 1));
        }
    }

    private void Fail(string message)
    {
        StatusText = message;
    }
}
