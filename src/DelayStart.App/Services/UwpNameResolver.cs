using DelayStart.Core.Models;

namespace DelayStart.App.Services;

/// <summary>
/// UWP 显示名的最后一道兜底（2026-09-20 批复 2）。
/// </summary>
/// <remarks>
/// <para>
/// Management 层的 SHLoadIndirectString 链路对"系统拆分包"解析不出来 —— 实测
/// Windows Terminal / Dev Home / Command Palette 的资源在 MRT Core 管理的拆分
/// 语言包里，SHLoadIndirectString 返回 0x80073B17，残留 "ms-resource:*" 或包名前缀。
/// </para>
/// <para>
/// 这里改用 WinRT 包目录 API 取**系统已解析好**的名字（AppListEntry.DisplayInfo，
/// 与开始菜单同源）。WinRT 投影只在 App 工程（TFM 带 Windows SDK 版本号）可用，
/// Management 的 TFM 是裸 <c>net10.0-windows</c>，所以兜底放 App 层、扫描后处理。
/// </para>
/// </remarks>
internal static class UwpNameResolver
{
    /// <summary>判断 UWP 条目名是否仍是"没解析出来"的状态。</summary>
    /// <param name="name">当前显示名。</param>
    /// <param name="packageFamilyName">包族名（条目的 <c>SourceDetail</c>）。</param>
    public static bool LooksUnresolved(string name, string packageFamilyName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        if (name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Management 解析全失败时兜底出的"包名前缀"（如 Microsoft.WindowsTerminal）。
        var underscore = packageFamilyName.IndexOf('_', StringComparison.Ordinal);
        var namePart = underscore > 0 ? packageFamilyName[..underscore] : packageFamilyName;
        return string.Equals(name, namePart, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取该包族在开始菜单里显示的名字；失败返回 <see langword="null"/>（保留原名）。</summary>
    /// <param name="packageFamilyName">包族名。</param>
    /// <returns>解析出的本地化名；查不到或出错为 <see langword="null"/>。</returns>
    public static async Task<string?> TryResolveAsync(string packageFamilyName)
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (var package in manager.FindPackages(packageFamilyName))
            {
                var listEntries = await package.GetAppListEntriesAsync().AsTask().ConfigureAwait(false);
                foreach (var listEntry in listEntries)
                {
                    var displayName = listEntry.DisplayInfo.DisplayName;
                    if (!string.IsNullOrWhiteSpace(displayName)
                        && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    {
                        return displayName;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            // 降级：系统应用被精简、包目录被锁、非当前用户包等都会走到这里，保留原名即可。
            return null;
        }

        return null;
    }
}
