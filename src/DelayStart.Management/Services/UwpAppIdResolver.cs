using System.Collections.Concurrent;
using System.Xml.Linq;

using DelayStart.Core.Abstractions;

using Microsoft.Win32;

namespace DelayStart.Management.Services;

/// <summary>
/// UWP / MSIX 自启动任务 → 真实 AppId 的解析（D41，2026-09-20 真机修复）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>核心事实：注册表中的子键名不是 AppId。</b>
/// 自启动状态写在
/// <c>HKCU\…\AppModel\SystemAppData\&lt;PackageFamilyName&gt;\&lt;TaskId&gt;\State</c>，
/// 这里的 <c>&lt;TaskId&gt;</c> 是清单里 <c>&lt;StartupTask TaskId="…"&gt;</c> 的 Id；
/// 而 AUMID（<c>&lt;PFN&gt;!&lt;AppId&gt;</c>）的后半段必须是
/// <c>&lt;Application Id="…"&gt;</c> 的 Id。**两者经常不同**。
/// </para>
/// <para>
/// 本机实测（SnipDo / PantherBar）：
/// <c>&lt;Application Id="App"&gt;</c> 内含 <c>&lt;desktop:StartupTask TaskId="PantherBarTask"&gt;</c>
/// —— 按旧写法拼出的 AUMID 是 <c>…!PantherBarTask</c>（非法），交给外壳后解析不了，
/// explorer 退回打开"文档"目录，表现为"点了没反应、弹个资源管理器"。
/// 正确 AUMID = <c>JohannesTscholl.Pantherbar_3hp4skfxf5x2g!App</c>
/// （与 <c>Get-StartApps</c> 给出的 AppID 一致）。
/// </para>
/// <para>
/// 解析链：<c>Repository\Packages</c> 按名字前缀找到 <b>PackageFullName</b> → 读
/// <c>PackageRootFolder</c> → 读 <c>AppxManifest.xml</c> → 建立
/// <c>StartupTask.TaskId → Application.Id</c> 映射。
/// 全部走注册表 + 文件读取，**不碰 WinRT**（Management 是裸 <c>net10.0-windows</c>，无投影）。
/// </para>
/// </remarks>
internal static class UwpAppIdResolver
{
    private const string RepositoryPackagesSubKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private const string RootFolderValueName = "PackageRootFolder";

    private const string ManifestFileName = "AppxManifest.xml";

    private const string ApplicationsElement = "Applications";
    private const string ApplicationElement = "Application";
    private const string StartupTaskElement = "StartupTask";
    private const string IdAttribute = "Id";
    private const string TaskIdAttribute = "TaskId";

    /// <summary>
    /// 同一包的清单解析结果按 PackageFamilyName 缓存（扫描时每个自启动任务都会问一次）。
    /// 解析不出来也缓存空值，避免每次扫描都去读 <c>WindowsApps</c>。
    /// </summary>
    private static readonly ConcurrentDictionary<string, PackageApplications?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把 startupTask 的 TaskId 解析成真正的 Application Id（AUMID 的 <c>!</c> 后段）。
    /// </summary>
    /// <param name="packageFamilyName">包族名。</param>
    /// <param name="startupTaskId">注册表 <c>SystemAppData</c> 下的子键名（清单里的 StartupTask TaskId）。</param>
    /// <param name="log">日志。</param>
    /// <returns>
    /// 真实的 Application Id；解析不出来时**原样返回 <paramref name="startupTaskId"/>**
    /// （与修复前行为一致 —— 多数包两者同名，兜底不会更差）。
    /// </returns>
    public static string ResolveAppId(string packageFamilyName, string startupTaskId, ILogSink log)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
        {
            return startupTaskId;
        }

        if (string.IsNullOrWhiteSpace(startupTaskId))
        {
            return startupTaskId;
        }

        var applications = Cache.GetOrAdd(packageFamilyName, key => ReadPackageApplications(key, log));
        if (applications is null)
        {
            return startupTaskId;
        }

        // ① 精确匹配：该自启动任务挂在哪个 Application 下。
        if (applications.TaskIdToAppId.TryGetValue(startupTaskId, out var mapped))
        {
            return mapped;
        }

        // ② 包内只有一个 Application —— 无论 TaskId 是什么，只可能是它。
        if (!string.IsNullOrEmpty(applications.SingleAppId))
        {
            return applications.SingleAppId;
        }

        log.Warn($"UWP 包『{packageFamilyName}』的清单里找不到与自启动任务『{startupTaskId}』"
            + "对应的 Application，沿用 TaskId 作为 AppId（该条目可能启动不了）。");
        return startupTaskId;
    }

    /// <summary>定位包安装目录并解析清单；任一步失败返回 <see langword="null"/>。</summary>
    private static PackageApplications? ReadPackageApplications(string packageFamilyName, ILogSink log)
    {
        var hashIndex = packageFamilyName.LastIndexOf('_');
        if (hashIndex <= 0)
        {
            return null;
        }

        // PackageFamilyName = 名字_发布商哈希；FullName = 名字_版本_架构_资源ID_哈希。
        // FullName 一定以 "名字_" 开头 —— 与 UwpStartupSource.ReadRepositoryDisplayName 同一套前缀匹配。
        var prefix = packageFamilyName[..(hashIndex + 1)];

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var packages = baseKey.OpenSubKey(RepositoryPackagesSubKey, writable: false);
            if (packages is null)
            {
                return null;
            }

            foreach (var fullName in packages.GetSubKeyNames())
            {
                if (!fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var packageKey = packages.OpenSubKey(fullName, writable: false);
                var rootFolder = packageKey?.GetValue(RootFolderValueName)?.ToString();
                if (string.IsNullOrWhiteSpace(rootFolder))
                {
                    continue;
                }

                var manifestPath = Path.Combine(rootFolder, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var parsed = ParseManifest(manifestPath);
                if (parsed is not null)
                {
                    return parsed;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                     or UnauthorizedAccessException
                                     or IOException
                                     or System.Xml.XmlException)
        {
            // WindowsApps 目录读不到 / 清单损坏 —— 不阻断扫描，走 TaskId 兜底。
            log.Warn(ex, $"读取 UWP 包『{packageFamilyName}』的清单失败，AppId 解析将沿用 TaskId。");
            return null;
        }
    }

    /// <summary>
    /// 解析 <c>AppxManifest.xml</c>。元素一律按 <c>LocalName</c> 比较 ——
    /// 清单里前缀五花八门（<c>uap:</c> / <c>uap3:</c> / <c>uap5:</c> / <c>desktop:</c>），
    /// 按完整名匹配会漏掉大半。
    /// </summary>
    private static PackageApplications? ParseManifest(string manifestPath)
    {
        var document = XDocument.Load(manifestPath);
        if (document.Root is null)
        {
            return null;
        }

        var applications = document.Root
            .Elements()
            .Where(element => element.Name.LocalName == ApplicationsElement)
            .SelectMany(element => element.Elements())
            .Where(element => element.Name.LocalName == ApplicationElement)
            .ToList();

        if (applications.Count == 0)
        {
            return null;
        }

        var result = new PackageApplications();

        foreach (var application in applications)
        {
            var id = application.Attribute(IdAttribute)?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            // StartupTask 挂在 Application/Extensions 下（实测 SnipDo 就是这层）。
            foreach (var node in application.Descendants())
            {
                if (node.Name.LocalName != StartupTaskElement)
                {
                    continue;
                }

                var taskId = node.Attribute(TaskIdAttribute)?.Value;
                if (!string.IsNullOrWhiteSpace(taskId))
                {
                    result.TaskIdToAppId[taskId] = id;
                }
            }
        }

        if (applications.Count == 1)
        {
            result.SingleAppId = applications[0].Attribute(IdAttribute)?.Value ?? string.Empty;
        }

        return result;
    }

    /// <summary>一个包的清单解析结果。</summary>
    private sealed class PackageApplications
    {
        /// <summary><c>StartupTask.TaskId → Application.Id</c>。</summary>
        public Dictionary<string, string> TaskIdToAppId { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>包内只有一个 Application 时的那个 Id；不适用时为空串。</summary>
        public string SingleAppId { get; set; } = string.Empty;
    }
}
