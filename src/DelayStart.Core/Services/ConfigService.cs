using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Serialization;

namespace DelayStart.Core.Services;

/// <summary>
/// 配置的加载、规范化与原子保存（§2.2 / FR-4.8）。
/// </summary>
/// <remarks>
/// <para>
/// 加载策略只有两条，没有第三条：
/// </para>
/// <list type="number">
/// <item><description>文件**不存在** → 返回默认配置，不报错（首次运行的正常路径）。</description></item>
/// <item><description>文件**存在但读不了 / 解析不了 / 版本过高** → 抛 <see cref="StartupOperationException"/>，
/// 由调用方决定中止还是提示。损坏时先留一份副本再抛，绝不覆盖原文件（E10 / FR-12.3）。
/// 直接删掉或覆盖配置文件是不能接受的：里面存着"哪些系统项被接管了"，那是唯一的还原依据。</description></item>
/// </list>
/// <para>
/// 🔴 刻意**不做**"损坏时降级成默认配置"：那会让程序在"自己什么都不知道"的状态下继续工作 ——
/// 守卫安静地什么都不纠正、界面把接管项全显示成未接管、任何写操作都会覆盖掉仅存的副本。
/// 静默的破坏比明确的失败危险得多。
/// </para>
/// <para>
/// 只支持当前一种格式（v<see cref="AppConfig.CurrentVersion"/>，由本程序写出），不做旧格式迁移。
/// </para>
/// </remarks>
public sealed class ConfigService : IAppConfigStore
{
    private const string CorruptFileMarker = ".corrupt-";

    /// <summary>重试次数下界。</summary>
    public const int MinimumRetryCount = 0;

    /// <summary>
    /// 重试次数上界。与设置页 NumberBox 的 <c>Maximum</c> 一一对应 ——
    /// 两处各写一个数就会出现"界面显示 5、实际存着 8"的错位。
    /// </summary>
    public const int MaximumRetryCount = 5;

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Error(ex, $"读取配置文件失败，已拒绝加载：{ConfigFilePath}");
            throw new StartupOperationException(
                StartupFailureReason.AccessDenied,
                entryId: string.Empty,
                message: $"配置文件当前不可读：{ex.Message}",
                innerException: ex);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            // 文件不存在 = 首次运行，合法状态。
            return new AppConfig();
        }

        try
        {
            var config = Parse(json);
            Normalize(config);
            return config;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            // 先留副本再抛：原文件里是"哪些系统项被接管了"，是唯一的还原依据。
            _log.Error(ex, $"配置文件解析失败，已保留副本，已拒绝加载（E10）：{ConfigFilePath}");
            PreserveCorruptFile();
            throw new StartupOperationException(
                StartupFailureReason.ConfigCorrupted,
                entryId: string.Empty,
                message: $"配置文件损坏，已保留副本（{ConfigFilePath}）：{ex.Message}",
                innerException: ex);
        }
    }

    /// <inheritdoc />
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Normalize(config);

        try
        {
            _paths.EnsureCreated();
            var json = JsonSerializer.Serialize(config, JsonContext.Default.AppConfig);
            AtomicFileWriter.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException)
        {
            throw new StartupOperationException(
                StartupFailureReason.Unknown,
                entryId: string.Empty,
                message: $"保存配置文件失败：{ConfigFilePath}（{ex.Message}）",
                innerException: ex);
        }
    }

    /// <summary>
    /// 解析配置 JSON。只有一种受支持格式（v<see cref="AppConfig.CurrentVersion"/>，由本程序写出）。
    /// </summary>
    /// <remarks>
    /// 不做任何旧格式迁移：本项目没有需要兼容的历史配置，配置损坏或形状不符时由调用方
    /// 按损坏处理（保留原文件、不覆盖）。唯一保留的前向保护是：版本高于当前程序的配置
    /// **拒绝加载**，避免旧程序把新字段写丢。
    /// </remarks>
    private static AppConfig Parse(string json)
    {
        var config = JsonSerializer.Deserialize(json, JsonContext.Default.AppConfig)
            ?? throw new FormatException("配置文件内容无法解析为配置对象。");

        if (config.Version > AppConfig.CurrentVersion)
        {
            throw new StartupOperationException(
                StartupFailureReason.ConfigVersionUnsupported,
                entryId: string.Empty,
                message: $"配置版本 v{config.Version} 高于本程序支持的 v{AppConfig.CurrentVersion}，"
                    + "为避免写坏配置已拒绝加载。请升级程序后再试。");
        }

        return config;
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
        NormalizeCycles(config);

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
            item.ScheduleCycleId = string.IsNullOrWhiteSpace(item.ScheduleCycleId)
                ? BuiltinCycleIds.Everyday
                : item.ScheduleCycleId.Trim();
        }
    }

    /// <summary>
    /// 周期表的规范化（FR-15.7 / FR-15.20）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 刻意**不删除**任何一条周期定义，哪怕它已经过期或 <c>Days</c> 被手改成 0：
    /// 删掉等于替用户做决定，而且会连带让引用它的条目失去本来还保留着的意图。
    /// 无效定义的处理交给 <see cref="ScheduleCycleResolver"/>（按引用失效兜底为「每天」）。
    /// </para>
    /// </remarks>
    private static void NormalizeCycles(AppConfig config)
    {
        if (config.Cycles is null)
        {
            config.Cycles = [];
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cycle in config.Cycles)
        {
            if (cycle is null)
            {
                continue;
            }

            cycle.Id ??= string.Empty;
            cycle.Name ??= string.Empty;

            // 越界位清零（FR-15.7）：0x80 这类脏位在判定里没有意义，留着会污染未来的扩展。
            cycle.Days = WeekdaySets.Sanitize(cycle.Days);
            cycle.Id = cycle.Id.Trim();

            if (string.IsNullOrWhiteSpace(cycle.Id))
            {
                cycle.Id = NewCycleId(seen);
            }

            if (seen.Add(cycle.Id))
            {
                continue;
            }

            // 重复 id：后续条目的解析永远取不到自己，等于被悄悄屏蔽 —— 重发一个 id 比留着好。
            cycle.Id = NewCycleId(seen);
        }
    }

    /// <summary>生成一个未被占用的自定义周期 id（<c>c-</c> + 8 位十六进制）。</summary>
    private static string NewCycleId(HashSet<string> seen)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            // 十六进制比 int.ToString("x8") 好在：不走任何文化相关的格式化，
            // 也避免 CA1305 那类"换台机器就变样"的格式串风险。
            var bytes = new byte[4];
            Random.Shared.NextBytes(bytes);
            var candidate = BuiltinCycleIds.CustomPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
            if (seen.Add(candidate))
            {
                return candidate;
            }
        }

        // 到了这里说明配置已经坏到难以理喻；给一个唯一的 Guid 也比空 id 好。
        return BuiltinCycleIds.CustomPrefix + Guid.NewGuid().ToString("n");
    }

    private static void NormalizeSettings(Settings settings)
    {
        settings.DelayPresets = NormalizePresets(settings.DelayPresets);

        // 🔴 重试次数**两端都要夹**（B7）。原先只夹下界，手改成 1000000 就是"失败一百万次"：
        // 每个失败都要走一遍降权链 / 计划任务调用，一轮下来能把登录后的几分钟全占死，
        // 而用户看到的表现只是"登录后卡很久"。
        // 上界与设置页 NumberBox 的 `Maximum="5"` **保持一致**（唯一来源，不要各写一个数）：
        // 留"余量"只会制造"界面显示 5、配置实际是 8"这种显示与实际不符的状态。
        settings.RetryCount = Math.Clamp(settings.RetryCount, MinimumRetryCount, MaximumRetryCount);

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

        // 守卫档位（D74）：两道都要 —— 枚举未知值回默认模式；间隔不在白名单内回默认档位。
        // 越界的间隔会直接变成计划任务的重复周期，而 0 或负数的周期让任务行为不可预测
        //（可能瞬间反复触发），所以这里不能只做"看起来合理"的检查。
        if (settings.GuardMode is not (GuardMode.Disabled or GuardMode.OnceAfterLogin or GuardMode.Periodic))
        {
            settings.GuardMode = GuardMode.OnceAfterLogin;
        }

        settings.GuardMinutes = GuardSchedulePlan.NormalizeMinutes(settings.GuardMinutes);

        // 通知策略（D80）：枚举未知值回默认档。与 Theme / GuardMode 同理 ——
        // 枚举强转不抛异常，手改配置写个 7 进来不会报错，只会让收尾判定走到一个
        // 既不是"总是"也不是"从不"的分支，行为变得不可预期。
        if (settings.NotifyMode is not (NotifyMode.FailuresOnly or NotifyMode.Always or NotifyMode.Never))
        {
            settings.NotifyMode = NotifyMode.FailuresOnly;
        }

        if (settings.GuardNotifyMode is not (GuardNotifyMode.OnChange or GuardNotifyMode.Never))
        {
            settings.GuardNotifyMode = GuardNotifyMode.OnChange;
        }
    }

    private static int[] NormalizePresets(int[]? presets)
    {
        if (presets is null || presets.Length == 0)
        {
            return [0, 5, 10, 15, 20, 30, 60];
        }

        var normalized = presets
            .Where(static value => value >= 0)
            .Distinct()
            .Order()
            .ToArray();

        return normalized.Length == 0 ? [0, 5, 10, 15, 20, 30, 60] : normalized;
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
