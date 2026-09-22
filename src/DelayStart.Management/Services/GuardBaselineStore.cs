using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Models;
using DelayStart.Management.Serialization;

namespace DelayStart.Management.Services;

/// <summary>
/// 守卫基线快照的读写（D74）。
/// </summary>
/// <remarks>
/// <para>
/// 落点 <c>%LOCALAPPDATA%\DelayStart\guard\baseline.json</c>，走
/// <see cref="AtomicFileWriter"/>（先写 <c>.tmp</c> 再替换），避免守卫被强杀时留下半截文件
/// —— 半截 JSON 会让下一次巡检把全部条目报成"新增"。
/// </para>
/// <para>
/// 读不到或解析失败一律返回 <see langword="null"/>（等价于"首次运行"）：
/// 差集测不出来只是少一次提示，而拿一份坏基线去测会制造满屏假"新增"。
/// </para>
/// </remarks>
public sealed class GuardBaselineStore
{
    private readonly PathService _paths;
    private readonly ILogSink _log;
    private readonly IClock _clock;

    /// <summary>构造基线存储。</summary>
    /// <param name="paths">路径服务。</param>
    /// <param name="log">日志接收端。</param>
    /// <param name="clock">时间源（给快照打时间戳）。</param>
    public GuardBaselineStore(PathService paths, ILogSink log, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _paths = paths;
        _log = log;
        _clock = clock;
    }

    /// <summary>读取基线。</summary>
    /// <returns>基线；不存在或不可用时为 <see langword="null"/>（视为首次运行）。</returns>
    public GuardBaseline? Read()
    {
        string? json;
        try
        {
            json = AtomicFileWriter.ReadAllTextOrNull(_paths.GuardBaselinePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, "读取守卫基线失败，本次按「首次运行」处理（不通报新增）");
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, GuardJsonContext.Default.GuardBaseline);
        }
        catch (JsonException ex)
        {
            _log.Warn(ex, "守卫基线解析失败，本次按「首次运行」处理并重建（不通报新增）");
            return null;
        }
    }

    /// <summary>把基线里的主键集合取出，供差集使用。</summary>
    /// <param name="baseline">基线；<see langword="null"/> 表示尚未建立。</param>
    /// <returns>主键集合；基线为 <see langword="null"/> 时为 <see langword="null"/>。</returns>
    public static IReadOnlySet<string>? ToIdSet(GuardBaseline? baseline)
        => baseline is null
            ? null
            : new HashSet<string>(
                baseline.Entries.Select(static entry => entry.Id),
                StringComparer.Ordinal);

    /// <summary>原子写入基线。</summary>
    /// <param name="entries">本次要记入的条目。</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> 为 <see langword="null"/>。</exception>
    public void Write(IReadOnlyList<StartupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var baseline = new GuardBaseline
        {
            Version = 1,
            CapturedAt = _clock.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Entries = [.. entries.Select(static entry => new GuardBaselineEntry
            {
                Id = entry.Id,
                Name = entry.Name,
                Source = entry.Source,
                Scope = entry.Scope,
                IsEnabled = entry.IsEnabled,
                IsMissing = entry.IsMissing,
            })],
        };

        try
        {
            var json = JsonSerializer.Serialize(baseline, GuardJsonContext.Default.GuardBaseline);
            AtomicFileWriter.WriteAllText(_paths.GuardBaselinePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去只影响"下一次能不能测出新增"，不该让整次巡检失败。
            _log.Warn(ex, "写入守卫基线失败，下次巡检将按「首次运行」处理");
        }
    }
}
