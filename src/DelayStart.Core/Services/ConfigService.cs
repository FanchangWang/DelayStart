using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Serialization;

namespace DelayStart.Core.Services;

/// <summary>
/// 配置的加载、校验、迁移与原子保存（§2.2 / FR-4.8 / FR-12）。
/// </summary>
/// <remarks>
/// <para>
/// 三条容错策略，按"用户损失最小"排序：
/// </para>
/// <list type="number">
/// <item><description>文件不存在 → 用默认配置，不报错（首次运行的正常路径）。</description></item>
/// <item><description>解析失败 → **保留坏文件副本**再重建默认配置（E10 / FR-12.3）。
/// 直接删掉用户的配置是不能接受的：里面存着"哪些系统项被接管了"。</description></item>
/// <item><description>版本高于本程序 → **拒绝加载**并抛异常。硬解析未知结构会把配置写坏，
/// 而写坏之后连还原路径都没了。</description></item>
/// </list>
/// </remarks>
public sealed class ConfigService : IAppConfigStore
{
    private const string CorruptFileMarker = ".corrupt-";

    private readonly PathService _paths;
    private readonly ILogSink _log;
    private readonly IClock _clock;

    /// <summary>构造配置服务。</summary>
    /// <param name="paths">路径解析服务。</param>
    /// <param name="log">日志接收端。</param>
    /// <param name="clock">时间源，用于给损坏文件副本打时间戳。</param>
    public ConfigService(PathService paths, ILogSink log, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _paths = paths;
        _log = log;
        _clock = clock;
    }

    /// <inheritdoc />
    public string ConfigFilePath => _paths.ConfigFilePath;

    /// <inheritdoc />
    public AppConfig Load()
    {
        _paths.EnsureCreated();

        string? json;
        try
        {
            json = AtomicFileWriter.ReadAllTextOrNull(ConfigFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, $"读取配置文件失败，本次使用默认配置：{ConfigFilePath}");
            return new AppConfig();
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppConfig();
        }

        AppConfig config;
        try
        {
            config = Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            _log.Error(ex, $"配置文件解析失败，已保留副本并重建默认配置（E10）：{ConfigFilePath}");
            PreserveCorruptFile();
            return new AppConfig();
        }

        Normalize(config);
        return config;
    }

    /// <inheritdoc />
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Normalize(config);
        _paths.EnsureCreated();

        var json = JsonSerializer.Serialize(config, JsonContext.Default.AppConfig);
        AtomicFileWriter.WriteAllText(ConfigFilePath, json);
    }

    private AppConfig Parse(string json)
    {
        var version = ReadVersion(json);

        if (version > AppConfig.CurrentVersion)
        {
            throw new StartupOperationException(
                StartupFailureReason.ConfigVersionUnsupported,
                entryId: string.Empty,
                message: $"配置版本 v{version} 高于本程序支持的 v{AppConfig.CurrentVersion}，"
                    + "为避免写坏配置已拒绝加载。请升级程序后再试。");
        }

        if (version < AppConfig.CurrentVersion)
        {
            return MigrateFromV1(json);
        }

        return JsonSerializer.Deserialize(json, JsonContext.Default.AppConfig) ?? new AppConfig();
    }

    /// <summary>
    /// 读取配置版本号。
    /// </summary>
    /// <remarks>
    /// 找不到 <c>version</c> 字段时返回 1：demo 时代的 v1 配置没有稳定写版本号，
    /// 而 v2 由本程序写出、**必然**带 version。因此"没有版本号"等价于 v1。
    /// </remarks>
    private static int ReadVersion(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind is not JsonValueKind.Object)
        {
            throw new FormatException("配置文件根节点不是 JSON 对象。");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("version", StringComparison.OrdinalIgnoreCase)
                && property.Value.TryGetInt32(out var version))
            {
                return version;
            }
        }

        return 1;
    }

    /// <summary>
    /// v1（demo 格式）→ v2 迁移（FR-12.1）。
    /// </summary>
    /// <remarks>
    /// 补齐的三项：<c>scope</c>、<c>enabled</c>、<c>originalState</c>。
    /// <para>
    /// <c>originalState</c> 取 <see cref="OriginalState.WasEnabled"/> = <see langword="true"/>。
    /// ⚠️ 早先此处取 <c>false</c>，理由是"能被接管的条目在接管时通常已被软禁用" ——
    /// <b>那是把因果搞反了</b>：接管后处于禁用状态是**我们刚写的标记**造成的，
    /// 而 <c>OriginalState</c> 要回答的是"接管**之前**是什么样"。v1 条目在接管前是正常
    /// 自启动的，记成 <c>false</c> 会让它们「移出延时启动」后仍然不启动，用户无从知道原因。
    /// </para>
    /// </remarks>
    private AppConfig MigrateFromV1(string json)
    {
        var legacy = JsonSerializer.Deserialize(json, JsonContext.Default.LegacyConfigV1)
            ?? new LegacyConfigV1();

        var config = new AppConfig { Version = AppConfig.CurrentVersion };
        foreach (var legacyItem in legacy.Items)
        {
            config.Items.Add(ConvertLegacyItem(legacyItem));
        }

        _log.Info($"配置已从 v1 迁移到 v{AppConfig.CurrentVersion}，共 {config.Items.Count} 个条目（FR-12.1）");
        return config;
    }

    private static DelayedItem ConvertLegacyItem(LegacyItemV1 legacy)
    {
        var source = ParseLegacySource(legacy.Source);

        return new DelayedItem
        {
            Id = legacy.Id,
            Name = legacy.Name,
            Path = legacy.Path,
            Arguments = legacy.Args,
            DelaySeconds = legacy.DelaySeconds,
            SortOrder = legacy.SortOrder,
            RunAsAdmin = legacy.RunAsAdmin,
            Enabled = true,
            Source = source,
            Scope = InferLegacyScope(source, legacy.SourceDetail),
            SourceKey = legacy.SourceKeyName,
            SourceDetail = legacy.SourceDetail,
            // v1 条目记不下接管前的状态。按"原本会自启动"处理 —— 取 false 会让这些历史条目
            // 在「移出延时启动」后仍然不启动，而用户无从知道原因。
            OriginalState = new OriginalState { WasEnabled = true },
        };
    }

    private static StartupSource ParseLegacySource(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "registry" => StartupSource.Registry,
            "startup_folder" => StartupSource.StartupFolder,
            "scheduled_task" => StartupSource.ScheduledTask,
            "uwp" => StartupSource.Uwp,
            _ => StartupSource.Manual,
        };

    /// <summary>
    /// 从 v1 的位置描述字符串推断 <see cref="StartupScope"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 **这是迁移期的一次性推断，不是运行时逻辑。** 推断结果会固化进配置文件，
    /// 此后一切判断都读枚举值。运行期用字符串猜 hive 是坑 5 明令禁止的
    /// （结果只能靠字符串猜，写错还不报错）。这里必须推断，因为 v1 根本没存 scope；
    /// 所幸只有这一次，而且推断失败也只是退化为 <see cref="StartupScope.None"/>。
    /// </remarks>
    private static StartupScope InferLegacyScope(StartupSource source, string sourceDetail)
    {
        if (string.IsNullOrWhiteSpace(sourceDetail))
        {
            return StartupScope.None;
        }

        if (source is StartupSource.Registry)
        {
            // v1 的 SourceDetail 形如 "HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
            // 或 "HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"。
            if (sourceDetail.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase))
            {
                return StartupScope.HklmWow;
            }

            if (sourceDetail.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
            {
                return StartupScope.Hklm;
            }

            if (sourceDetail.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase))
            {
                return StartupScope.Hkcu;
            }

            return StartupScope.None;
        }

        if (source is StartupSource.StartupFolder)
        {
            // v1 的 SourceDetail 是 "用户启动文件夹" / "系统启动文件夹"。
            var isSystem = sourceDetail.Contains("系统", StringComparison.Ordinal)
                || sourceDetail.Contains("Common", StringComparison.OrdinalIgnoreCase);

            return isSystem ? StartupScope.SystemFolder : StartupScope.UserFolder;
        }

        return StartupScope.None;
    }

    /// <summary>
    /// 把配置里明显非法的值收拢到合法区间。
    /// </summary>
    /// <remarks>
    /// 刻意**保守**：只处理"不处理就一定出问题"的值（空集合、负数、缺子对象），
    /// 不做风格性改写。用户手改配置是他的权利，程序不该悄悄覆盖他的意图。
    /// </remarks>
    private static void Normalize(AppConfig config)
    {
        config.Version = AppConfig.CurrentVersion;

        // 源生成反序列化在遇到 "items": null 这类显式 null 时会赋 null，绕过属性默认值。
        if (config.Items is null)
        {
            config.Items = [];
        }

        if (config.Settings is null)
        {
            config.Settings = new Settings();
        }

        NormalizeSettings(config.Settings);

        foreach (var item in config.Items)
        {
            item.Id ??= string.Empty;
            item.Name ??= string.Empty;
            item.Path ??= string.Empty;
            item.Arguments ??= string.Empty;
            item.WorkingDirectory ??= string.Empty;
            item.SourceKey ??= string.Empty;
            item.SourceDetail ??= string.Empty;
            item.OriginalState ??= new OriginalState();
        }
    }

    private static void NormalizeSettings(Settings settings)
    {
        settings.DelayPresets = NormalizePresets(settings.DelayPresets);

        if (settings.RetryCount < 0)
        {
            settings.RetryCount = 0;
        }

        // 默认预设必须真的是列表成员：手改配置删掉它时兜底到第一项。
        if (!settings.DelayPresets.Contains(settings.DefaultPreset))
        {
            settings.DefaultPreset = settings.DelayPresets[0];
        }

        // 手改配置出现未知枚举值时回跟随系统（枚举强转不抛异常，只能显式校验）。
        if (settings.Theme is not (ThemePreference.FollowSystem or ThemePreference.Light or ThemePreference.Dark))
        {
            settings.Theme = ThemePreference.FollowSystem;
        }
    }

    private static int[] NormalizePresets(int[]? presets)
    {
        if (presets is null || presets.Length == 0)
        {
            return [0, 10, 30, 60, 120];
        }

        var normalized = presets
            .Where(static value => value >= 0)
            .Distinct()
            .Order()
            .ToArray();

        return normalized.Length == 0 ? [0, 10, 30, 60, 120] : normalized;
    }

    private void PreserveCorruptFile()
    {
        try
        {
            var stamp = _clock.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var backupPath = ConfigFilePath + CorruptFileMarker + stamp;

            if (File.Exists(ConfigFilePath))
            {
                File.Copy(ConfigFilePath, backupPath, overwrite: true);
                _log.Warn($"损坏的配置文件已保留为：{backupPath}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, "保留损坏配置文件的副本失败，原文件保持不动");
        }
    }
}
