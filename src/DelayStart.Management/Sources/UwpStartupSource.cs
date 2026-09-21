using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Interop;
using DelayStart.Management.Services;

using Microsoft.Win32;

namespace DelayStart.Management.Sources;

/// <summary>
/// UWP / MSIX 应用的自启动任务来源（FR-1 / <c>docs/pitfalls.md</c> 1.5）。
/// </summary>
/// <remarks>
/// <para>
/// UWP 的自启动状态不在 <c>Run</c> 键里，而在 AppModel 体系下：
/// </para>
/// <code>
/// HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\
///     AppModel\SystemAppData\&lt;PackageFamilyName&gt;\&lt;TaskId&gt;\State   (DWORD)
/// </code>
/// <para>
/// <c>State = 2</c> 为启用、<c>0</c> 为禁用，两个值之外按启用处理（不猜未知语义）。
/// </para>
/// <para>
/// 关于 FR-1.8 的显示名解析（2026-09-19 批复 10 补全）：SplashScreen AppName →
/// <c>SHLoadIndirectString</c> → Repository\Packages 反查 DisplayName → PFN 前缀兜底。
/// </para>
/// </remarks>
public sealed class UwpStartupSource : IStartupSource
{
    private const string SystemAppDataSubKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private const string StateValueName = "State";
    private const int StateEnabled = 2;
    private const int StateDisabled = 0;

    private readonly ILogSink _log;

    /// <summary>构造 UWP 来源。</summary>
    /// <param name="log">日志接收端。</param>
    public UwpStartupSource(ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public StartupSource Kind => StartupSource.Uwp;

    /// <inheritdoc />
    public StartupScope Scope => StartupScope.None;

    /// <inheritdoc />
    public string DisplayName => "应用商店应用";

    /// <inheritdoc />
    /// <remarks>UWP 的 <c>State</c> 写在 HKCU 下，操作自己的用户级注册表不需要提权。</remarks>
    public bool RequiresElevation => false;

    /// <inheritdoc />
    public IReadOnlyList<StartupEntry> Scan(IReadOnlySet<string> takenOverKeys)
    {
        ArgumentNullException.ThrowIfNull(takenOverKeys);

        var entries = new List<StartupEntry>();

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var root = baseKey.OpenSubKey(SystemAppDataSubKey, writable: false);

        if (root is null)
        {
            _log.Info("未找到 UWP 的 SystemAppData 键，跳过该来源");
            return entries;
        }

        foreach (var packageFamilyName in root.GetSubKeyNames())
        {
            try
            {
                CollectPackageEntries(root, packageFamilyName, takenOverKeys, entries);
            }
            catch (Exception ex)
            {
                // FR-1.4：单个包读取失败不影响其余包。
                _log.Warn(ex, $"读取 UWP 包『{packageFamilyName}』的自启动任务失败，已跳过（FR-1.4）");
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public void Disable(StartupEntry entry) => WriteState(entry, StateDisabled);

    /// <inheritdoc />
    public void Enable(StartupEntry entry) => WriteState(entry, StateEnabled);

    private void CollectPackageEntries(
        RegistryKey root,
        string packageFamilyName,
        IReadOnlySet<string> takenOverKeys,
        List<StartupEntry> entries)
    {
        using var packageKey = root.OpenSubKey(packageFamilyName, writable: false);
        if (packageKey is null)
        {
            return;
        }

        foreach (var taskId in packageKey.GetSubKeyNames())
        {
            if (string.IsNullOrWhiteSpace(taskId) || taskId.Equals("SplashScreen", StringComparison.OrdinalIgnoreCase))
            {
                // SplashScreen 只是资源节点，不是任务。
                continue;
            }

            using var taskKey = packageKey.OpenSubKey(taskId, writable: false);
            if (taskKey?.GetValue(StateValueName) is not int state)
            {
                // 没有 State 值的子键不表示自启动状态（可能是别的元数据节点）。
                continue;
            }

            // 🔴 TaskId ≠ AppId（D41 真机修复）：AUMID 的 '!' 后段必须是清单里
            // <Application Id="…"> 的 Id，而这里拿到的是 <StartupTask TaskId="…">。
            // 实测 SnipDo：TaskId=PantherBarTask、Application Id=App —— 用 TaskId 拼出的
            // AUMID 外壳解析不了，explorer 会退回打开"文档"目录。
            var appId = UwpAppIdResolver.ResolveAppId(packageFamilyName, taskId, _log);
            var aumid = $"{packageFamilyName}!{appId}";
            var id = ItemKeyBuilder.Build(Kind, Scope, taskId);

            entries.Add(new StartupEntry
            {
                Id = id,
                Name = ResolveDisplayName(root, packageFamilyName, aumid),
                // 机制 4 / pitfalls.md 三：UWP 的"路径"就是 AUMID。
                // D28 = A 决定用 `explorer.exe shell:AppsFolder\<AUMID>` 激活它。
                Path = aumid,
                Arguments = string.Empty,
                Source = Kind,
                Scope = Scope,
                // 机制 4：source_key 是 TaskId，PackageFamilyName 走 source_detail。
                SourceKey = taskId,
                SourceDetail = packageFamilyName,
                // 只有明确等于 0 才算禁用；未知取值按启用处理，不猜。
                IsEnabled = state != StateDisabled,
                // UWP 应用没有可检查的独立文件路径（目标在 WindowsApps 下且受保护），不判失效。
                IsMissing = false,
                IsProtected = false,
                IsTakenOver = takenOverKeys.Contains(id),
            });
        }
    }

    /// <summary>
    /// 解析 UWP 应用的显示名（FR-1.8）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 解析顺序（2026-09-20 批复 2 再补全）：
    /// </para>
    /// <list type="number">
    /// <item><description><c>SplashScreen\&lt;AUMID&gt;\AppName</c> → <c>SHLoadIndirectString</c>。</description></item>
    /// <item><description><c>AppModel\Repository\Packages\&lt;PackageFullName&gt;</c> 的
    /// <c>DisplayName</c>。可能是纯文本、<c>@{包?资源}</c>、或**裸 <c>ms-resource:</c> 引用**
    /// （实测 DevHome / Terminal / CommandPalette 是裸引用）—— 裸引用先用包全名补上
    /// <c>@{…}</c> 包装再交给 <c>SHLoadIndirectString</c>。</description></item>
    /// <item><description>PFN 下划线前缀兜底。到这一步仍解析不出的（系统拆分包，
    /// 资源在 MRT Core 语言包里）由 App 层的 WinRT 兜底接管。</description></item>
    /// </list>
    /// </remarks>
    private static string ResolveDisplayName(RegistryKey root, string packageFamilyName, string aumid)
    {
        var resolved = ResolveCandidate(ReadAppName(root, packageFamilyName, aumid));
        if (resolved.Length > 0)
        {
            return resolved;
        }

        resolved = ResolveCandidate(ReadRepositoryDisplayName(packageFamilyName));
        if (resolved.Length > 0)
        {
            return resolved;
        }

        // 兜底：PFN 形如 `Microsoft.WindowsCalculator_8wekyb3d8bbwe`，
        // 下划线前那一段才是人类能读的部分。
        var underscore = packageFamilyName.IndexOf('_', StringComparison.Ordinal);
        return underscore > 0 ? packageFamilyName[..underscore] : packageFamilyName;
    }

    /// <summary>
    /// 把一个"显示名候选"归一成可读文本；解析不出返回空串（继续走下一个候选）。
    /// </summary>
    /// <remarks>
    /// 解析结果仍以 <c>ms-resource:</c> 开头的（资源真的不在当前包的 PRI 里）也判为失败，
    /// 不能把 "ms-resource:AppName" 这种半成品显示给用户。
    /// </remarks>
    private static string ResolveCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        // 裸 ms-resource 引用没有包名上下文，无法补包装 —— 本分支直接判失败。
        if (candidate.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (candidate[0] != '@')
        {
            return candidate; // 纯文本，本身就是可读名。
        }

        return Shlwapi.TryLoadIndirectString(candidate, out var resolved)
            && !resolved.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                ? resolved
                : string.Empty;
    }

    private static string ReadAppName(RegistryKey root, string packageFamilyName, string aumid)
    {
        try
        {
            using var splash = root.OpenSubKey($@"{packageFamilyName}\SplashScreen\{aumid}", writable: false);
            return splash?.GetValue("AppName")?.ToString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Repository\Packages 键路径（HKCU 下，与 SystemAppData 同一体系）。</summary>
    private const string RepositoryPackagesSubKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>
    /// 从 <c>Repository\Packages</c> 反查包显示名。
    /// </summary>
    /// <remarks>
    /// PackageFamilyName 只含名字与发布商哈希，而 Repository 的键是 **PackageFullName**
    /// （名字_版本_架构_资源ID_哈希）。用"名字前缀"匹配：PFN 去掉末段哈希后，
    /// FullName 一定以 <c>名字_</c> 开头 —— 命中任意一个已注册版本即可读 DisplayName。
    /// </remarks>
    private static string ReadRepositoryDisplayName(string packageFamilyName)
    {
        try
        {
            var hashIndex = packageFamilyName.LastIndexOf('_');
            if (hashIndex <= 0)
            {
                return string.Empty;
            }

            var prefix = packageFamilyName[..(hashIndex + 1)]; // "名字_"

            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var packages = baseKey.OpenSubKey(RepositoryPackagesSubKey, writable: false);
            if (packages is null)
            {
                return string.Empty;
            }

            foreach (var fullName in packages.GetSubKeyNames())
            {
                if (!fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var packageKey = packages.OpenSubKey(fullName, writable: false);
                var displayName = packageKey?.GetValue("DisplayName")?.ToString();
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    // 批复 2：裸 ms-resource 引用（实测 DevHome / Terminal / CommandPalette）
                    // 缺少 SHLoadIndirectString 要求的 @{包全名?资源} 包装，这里补上。
                    return NormalizeRepositoryName(displayName, fullName);
                }

                // 部分应用把可读名放在 App\Capabilities 的 ApplicationName（社区验证过的备选位）。
                using var capabilities = packageKey?.OpenSubKey(@"App\Capabilities", writable: false);
                var appName = capabilities?.GetValue("ApplicationName")?.ToString();
                if (!string.IsNullOrWhiteSpace(appName))
                {
                    return appName;
                }
            }

            return string.Empty;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>Repository 里的裸 ms-resource 引用补上 @{包全名?…} 包装；其余原样返回。</summary>
    private static string NormalizeRepositoryName(string value, string packageFullName)
        => value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            ? $"@{{{packageFullName}?{value}}}"
            : value;

    private static void WriteState(StartupEntry entry, int state)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

            // PackageFamilyName 存在 SourceDetail 里（机制 4），TaskId 在 SourceKey 里。
            var subKeyPath = $@"{SystemAppDataSubKey}\{entry.SourceDetail}\{entry.SourceKey}";
            using var key = baseKey.OpenSubKey(subKeyPath, writable: true)
                ?? throw new StartupOperationException(
                    StartupFailureReason.ScheduledTaskFailed,
                    entry.Id,
                    $"UWP 自启动项已不存在，无法写入状态：{subKeyPath}");

            key.SetValue(StateValueName, state, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is not StartupOperationException)
        {
            throw new StartupOperationException(
                StartupFailureReason.AccessDenied,
                entry.Id,
                $"设置 UWP 应用『{entry.Name}』的自启动状态失败：{ex.Message}",
                ex);
        }
    }
}
