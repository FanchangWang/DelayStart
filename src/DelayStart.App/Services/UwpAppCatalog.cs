namespace DelayStart.App.Services;

/// <summary>
/// 一个"可以启动的 UWP 应用"（D46，2026-09-20 用户批复）。
/// </summary>
/// <param name="DisplayName">显示名（与开始菜单同源，已解析过 <c>ms-resource:</c>）。</param>
/// <param name="AppUserModelId">应用用户模型 ID，形如 <c>&lt;PackageFamilyName&gt;!&lt;ApplicationId&gt;</c>。</param>
/// <param name="PackageFamilyName">包族名。</param>
/// <remarks>
/// 🔴 <b>AUMID 的后半段必须是 <c>&lt;Application Id&gt;</c> 而不是 <c>&lt;StartupTask TaskId&gt;</c></b>
/// （D41 真机教训）。这里的 AUMID 直接来自 <c>AppListEntry.AppUserModelId</c>，是系统自己算好的——
/// 天然绕开了"注册表子键名 ≠ AppId"那个坑，不需要再去解析清单。
/// </remarks>
public sealed record UwpAppEntry(string DisplayName, string AppUserModelId, string PackageFamilyName);

/// <summary>
/// 枚举当前用户已安装、且**在开始菜单里能启动**的 UWP 应用（D46）。
/// </summary>
/// <remarks>
/// <para>
/// 判据是 <c>Package.GetAppListEntriesAsync()</c> 有没有条目（用户批复 D46-2：只列出有应用清单条目的包）：
/// 框架包、纯资源包、仅供后台任务的包都不会出现在列表里，正好是用户能在开始菜单里点开的那些。
/// </para>
/// <para>
/// 走 WinRT 投影（<c>PackageManager</c>）而非注册表：这是唯一能同时拿到**已解析显示名**与
/// **真实 AUMID**的来源。投影只在 App 工程可用（TFM 带 Windows SDK 版本号），
/// Management 是裸 <c>net10.0-windows</c> —— 与 <see cref="UwpNameResolver"/> 同一处境、同一解法。
/// </para>
/// <para>
/// 单个包取枚举失败（被精简 / 目录被锁 / 非当前用户包）只跳过它，不影响整张列表 ——
/// 选择器最忌讳"因为一个包装不上，所有应用都列不出来"。
/// </para>
/// </remarks>
internal static class UwpAppCatalog
{
    /// <summary>取当前用户可启动的 UWP 应用列表，按显示名排序。</summary>
    /// <returns>应用列表；包目录完全不可用时返回空列表（不抛异常）。</returns>
    public static async Task<IReadOnlyList<UwpAppEntry>> ListAsync()
    {
        var entries = new List<UwpAppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();

            // MSDN：userSecurityId 传空串表示**当前用户**。
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                var familyName = package.Id.FamilyName;

                try
                {
                    // 刻意只用 var 迭代、不写类型名：投影里 AppListEntry 不作为可命名类型暴露
                    // （UwpNameResolver 同样只用 var），写出类型名会 CS0234。
                    var listEntries = await package.GetAppListEntriesAsync().AsTask().ConfigureAwait(false);

                    foreach (var entry in listEntries)
                    {
                        var appUserModelId = entry.AppUserModelId;
                        if (string.IsNullOrWhiteSpace(appUserModelId) || !seen.Add(appUserModelId))
                        {
                            continue;
                        }

                        entries.Add(new UwpAppEntry(
                            ResolveDisplayName(entry.DisplayInfo.DisplayName, package.DisplayName, familyName),
                            appUserModelId,
                            familyName));
                    }
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    // 单个包取不到应用清单条目（被精简 / 目录被锁）——跳过它，不影响整张列表。
                    continue;
                }
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // 包目录整体不可用（非当前用户会话、策略限制等）—— 返回已收集到的部分。
        }

        return entries
            .OrderBy(static entry => entry.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 显示名三级回退：应用清单条目 → 包显示名 → 包族名的名字段。
    /// </summary>
    /// <param name="entryName">应用清单条目的显示名。</param>
    /// <param name="packageName">包显示名。</param>
    /// <param name="familyName">包族名。</param>
    /// <returns>可展示的名字。</returns>
    /// <remarks>
    /// 前两级都可能拿到未解析的 <c>ms-resource:</c>（拆分语言包的资源 MRT Core 解得开、
    /// 老 API 解不开）—— 用 <see cref="UwpNameResolver.LooksUnresolved"/> 判，别自己写判据。
    /// </remarks>
    private static string ResolveDisplayName(string? entryName, string? packageName, string familyName)
    {
        if (!UwpNameResolver.LooksUnresolved(entryName ?? string.Empty, familyName))
        {
            return entryName!;
        }

        if (!UwpNameResolver.LooksUnresolved(packageName ?? string.Empty, familyName))
        {
            return packageName!;
        }

        var underscore = familyName.IndexOf('_', StringComparison.Ordinal);
        return underscore > 0 ? familyName[..underscore] : familyName;
    }

    /// <summary>这些异常在"枚举已安装包"的场景下都是**可预期的局部失败**，跳过即可。</summary>
    private static bool IsRecoverable(Exception exception) => exception is ArgumentException
        or InvalidOperationException
        or UnauthorizedAccessException
        or System.Runtime.InteropServices.COMException;
}
