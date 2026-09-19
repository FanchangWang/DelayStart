using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Interop;

using Microsoft.Win32;

namespace DelayStart.Management.Sources;

/// <summary>
/// UWP / MSIX 应用的自启动任务来源（FR-1 / <c>api-analysis.md</c> 1.5）。
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
/// 🔴 关于 FR-1.8 的显示名解析，本实现覆盖其中的**第 1、2、4 步**（读
/// <c>SplashScreen\&lt;AUMID&gt;\AppName</c> → <c>SHLoadIndirectString</c> → 回退为
/// PackageFamilyName 下划线前的部分），**第 3 步（MrtCache 反查 PackageFullName）未实现**。
/// 理由见 <see cref="ResolveDisplayName"/> 的注释。
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

            var aumid = $"{packageFamilyName}!{taskId}";
            var id = ItemKeyBuilder.Build(Kind, Scope, taskId);

            entries.Add(new StartupEntry
            {
                Id = id,
                Name = ResolveDisplayName(root, packageFamilyName, aumid),
                // 机制 4 / api-analysis.md 1.5：UWP 的"路径"就是 AUMID。
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
    /// 实现第 1、2、4 步：读 <c>SplashScreen\&lt;AUMID&gt;\AppName</c> →
    /// <c>SHLoadIndirectString</c> → 回退为 PackageFamilyName 下划线前的部分。
    /// </para>
    /// <para>
    /// ⏳ **第 3 步（解析失败时去 MrtCache 反查 PackageFullName，拼成
    /// <c>@{PackageFullName?ms-resource://...}</c> 再解析）未实现**，属已知偏差。
    /// 判断依据：它需要额外解码 <c>resources.pri</c> 或改 TFM 引入 WinRT 的
    /// <c>PackageManager</c>，代码量与风险都明显高于收益，而第 4 步已给出可读兜底
    /// （用户看到的是 <c>Microsoft.WindowsCalculator</c> 这类包名前缀，而非乱码）。
    /// 该偏差已记入 <c>docs/build-and-test.md</c> 的 Phase 2 记录。
    /// </para>
    /// </remarks>
    private static string ResolveDisplayName(RegistryKey root, string packageFamilyName, string aumid)
    {
        var indirect = ReadAppName(root, packageFamilyName, aumid);
        if (Shlwapi.TryLoadIndirectString(indirect, out var resolved))
        {
            return resolved;
        }

        // 第 4 步兜底：PFN 形如 `Microsoft.WindowsCalculator_11.0.0.0_x64__8wekyb3d8bbwe`，
        // 下划线前那一段才是人类能读的部分。
        var underscore = packageFamilyName.IndexOf('_', StringComparison.Ordinal);
        return underscore > 0 ? packageFamilyName[..underscore] : packageFamilyName;
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
