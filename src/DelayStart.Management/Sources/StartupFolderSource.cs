using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

namespace DelayStart.Management.Sources;

/// <summary>
/// 启动文件夹来源（FR-1 / <c>api-analysis.md</c> 1.3）。按 <see cref="StartupScope"/> 实例化两次：
/// <see cref="StartupScope.UserFolder"/>（<c>%APPDATA%</c>）与 <see cref="StartupScope.SystemFolder"/>（<c>%PROGRAMDATA%</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **绝不移动、绝不删除文件夹里的任何文件**。禁用状态同样只由
/// <c>StartupApproved\StartupFolder</c> 标记表达（机制 4）——
/// demo 曾额外维护 <c>backup\{user|system}\</c> 物理备份目录，本方案**刻意不做**：
/// 多一份文件副本就多一处可能与真实状态不一致的地方，而标记法本身已完全可逆。
/// </para>
/// <para>
/// <c>.lnk</c> 的真实目标通过 <see cref="IShellLinkResolver"/> 解析（FR-1.7 / D9）；
/// 解析失败时**回退显示快捷方式本身**而不标失效 —— 让用户看到一个真实的 <c>.lnk</c>，
/// 好过看到一个被误判为"已失效"的可用程序（FR-1.4 的宽容原则）。
/// </para>
/// </remarks>
public sealed class StartupFolderSource : IStartupSource
{
    private static readonly string[] ShortcutExtensions = [".lnk", ".url"];

    private readonly IShellLinkResolver _resolver;
    private readonly IClock _clock;
    private readonly ILogSink _log;

    /// <summary>构造一个启动文件夹来源实例。</summary>
    /// <param name="scope">必须是 <see cref="StartupScope.UserFolder"/> 或 <see cref="StartupScope.SystemFolder"/>。</param>
    /// <param name="resolver">快捷方式解析器。</param>
    /// <param name="clock">时间源，仅用于写禁用标记的时间戳。</param>
    /// <param name="log">日志接收端。</param>
    public StartupFolderSource(StartupScope scope, IShellLinkResolver resolver, IClock clock, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        if (scope is not (StartupScope.UserFolder or StartupScope.SystemFolder))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                "启动文件夹来源只支持 UserFolder / SystemFolder 两种作用域。");
        }

        Scope = scope;
        _resolver = resolver;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public StartupSource Kind => StartupSource.StartupFolder;

    /// <inheritdoc />
    public StartupScope Scope { get; }

    /// <inheritdoc />
    public string DisplayName => Scope == StartupScope.UserFolder ? "当前用户" : "所有用户";

    /// <inheritdoc />
    public bool RequiresElevation => Scope == StartupScope.SystemFolder;

    /// <summary>本实例扫描的文件夹路径。</summary>
    public string FolderPath => Environment.GetFolderPath(
        Scope == StartupScope.UserFolder
            ? Environment.SpecialFolder.Startup
            : Environment.SpecialFolder.CommonStartup);

    /// <inheritdoc />
    public IReadOnlyList<StartupEntry> Scan(IReadOnlySet<string> takenOverKeys)
    {
        ArgumentNullException.ThrowIfNull(takenOverKeys);

        var entries = new List<StartupEntry>();
        var folder = FolderPath;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _log.Info($"启动文件夹不存在，跳过：{folder}");
            return entries;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder)
                .Where(static file => ShortcutExtensions.Contains(
                    Path.GetExtension(file),
                    StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, $"枚举启动文件夹失败：{folder}");
            throw;
        }

        foreach (var file in files)
        {
            try
            {
                entries.Add(BuildEntry(file, takenOverKeys));
            }
            catch (Exception ex)
            {
                // FR-1.4：单条失败不影响其余。
                _log.Warn(ex, $"读取启动文件夹项『{Path.GetFileName(file)}』失败，已跳过（FR-1.4）");
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public void Disable(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StartupApprovedStore.Disable(Kind, Scope, entry.SourceKey, _clock.Now);
    }

    /// <inheritdoc />
    public void Enable(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StartupApprovedStore.Enable(Kind, Scope, entry.SourceKey);
    }

    private StartupEntry BuildEntry(string filePath, IReadOnlySet<string> takenOverKeys)
    {
        var fileName = Path.GetFileName(filePath);
        var id = ItemKeyBuilder.Build(Kind, Scope, fileName);

        var target = _resolver.Resolve(filePath);
        var targetPath = target?.Path ?? string.Empty;

        // 解析成功才用真实目标；失败则回退为快捷方式自身，用户至少能看到文件名对应的实体。
        var effectivePath = string.IsNullOrWhiteSpace(targetPath) ? filePath : targetPath;

        return new StartupEntry
        {
            Id = id,
            // FR-1.7：显示名取文件名去扩展名（「微信」而不是「微信.lnk」）。
            Name = Path.GetFileNameWithoutExtension(fileName),
            Path = effectivePath,
            Arguments = target?.Arguments ?? string.Empty,
            Source = Kind,
            Scope = Scope,
            // 机制 4 要求 source_key 存**含扩展名的文件名**，因为标记就是以这个名字写入的。
            SourceKey = fileName,
            SourceDetail = Scope == StartupScope.UserFolder ? "用户启动文件夹" : "系统启动文件夹",
            IsEnabled = !StartupApprovedStore.IsDisabled(Kind, Scope, fileName),
            // 只有解析成功时才敢判失效：解析失败说明我们不知道它指向哪儿。
            IsMissing = !string.IsNullOrWhiteSpace(targetPath)
                && Path.IsPathFullyQualified(targetPath)
                && !File.Exists(targetPath),
            IsProtected = Scope == StartupScope.SystemFolder,
            IsTakenOver = takenOverKeys.Contains(id),
        };
    }
}
