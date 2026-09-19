using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;

using Microsoft.UI.Xaml;

namespace DelayStart.App.Services;

/// <summary>
/// 管理端主题服务（FR-9 主题设置，2026-09-19 用户批复新增）。
/// </summary>
/// <remarks>
/// 主题存 <c>config.json</c> 的 <c>settings.theme</c>（跟随系统 / 浅色 / 深色），
/// 切换即时落盘并广播 —— <see cref="MainWindow"/> 订阅后设置根元素
/// <c>RequestedTheme</c>，WinUI 的主题资源会随之下刷，无需重启。
/// </remarks>
public sealed class ThemeService
{
    private readonly IAppConfigStore _configStore;

    /// <summary>当前主题偏好。构造后由 <see cref="Load"/> 从配置读入。</summary>
    public ThemePreference Current { get; private set; } = ThemePreference.FollowSystem;

    /// <summary>主题被用户改变时触发（初始加载不触发）。</summary>
    public event Action<ThemePreference>? ThemeChanged;

    /// <summary>构造主题服务。</summary>
    /// <param name="configStore">配置读写端。</param>
    public ThemeService(IAppConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);

        _configStore = configStore;
    }

    /// <summary>从配置载入当前主题。主窗口构造时调用。</summary>
    public void Load()
    {
        Current = _configStore.Load().Settings.Theme;
    }

    /// <summary>切换主题：立即落盘并广播；失败时抛出，由设置页转成状态条错误。</summary>
    /// <param name="theme">目标主题。</param>
    public void Set(ThemePreference theme)
    {
        if (theme == Current)
        {
            return;
        }

        var config = _configStore.Load();
        config.Settings.Theme = theme;
        _configStore.Save(config);

        Current = theme;
        ThemeChanged?.Invoke(theme);
    }

    /// <summary>映射到 WinUI 的 <see cref="ElementTheme"/>（跟随系统 = Default）。</summary>
    /// <param name="theme">主题偏好。</param>
    /// <returns>根元素应取的主题值。</returns>
    public static ElementTheme ToElementTheme(ThemePreference theme) => theme switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
