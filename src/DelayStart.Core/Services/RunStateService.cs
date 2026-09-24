using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Serialization;

namespace DelayStart.Core.Services;

/// <summary>
/// 运行状态的读写与归档（§2.2 / D19 / FR-5.12 / FR-5.13）。
/// </summary>
/// <remarks>
/// <para>
/// 调度端写、管理端读，双方只通过文件系统交汇 —— 这是 D19 的核心取舍：
/// 用一次文件写入换掉整套跨进程协议，代价是几十毫秒的可见延迟，收益是零 IPC 依赖、
/// 崩溃后天然留现场（E9）。
/// </para>
/// <para>
/// 归档清理按 <c>runId</c> 的字符串序（<c>yyyyMMdd-HHmmss</c> 恰好字典序等于时间序），
/// 不读文件内容，因此清理动作的开销与运行次数无关。
/// </para>
/// </remarks>
public sealed class RunStateService : IRunStateStore
{
    /// <summary>归档保留份数上限（FR-5.13 / FR-8.3）。</summary>
    public const int MaxArchivedRuns = 30;

    private const string RunIdFormat = "yyyyMMdd-HHmmss";
    private const string ArchiveSearchPattern = "*.json";

    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>构造运行状态服务。</summary>
    /// <param name="paths">路径解析服务。</param>
    /// <param name="log">日志接收端。</param>
    public RunStateService(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _log = log;
    }

    /// <inheritdoc />
    public string CurrentStateFilePath => _paths.CurrentRunFilePath;

    /// <inheritdoc />
    public string ArchiveRoot => _paths.SchedulerArchiveRoot;

    /// <summary>按本地时间生成运行标识，格式 <c>yyyyMMdd-HHmmss</c>，同时用作归档文件名。</summary>
    /// <param name="localTime">本次运行的开始时间。</param>
    /// <returns>运行标识。</returns>
    public static string CreateRunId(DateTimeOffset localTime)
        => localTime.ToString(RunIdFormat, CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public RunRecord? ReadCurrent()
    {
        string? json;
        try
        {
            json = AtomicFileWriter.ReadAllTextOrNull(CurrentStateFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, $"读取实时状态失败，按「无调度进行中」处理：{CurrentStateFilePath}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, JsonContext.Default.RunRecord);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // 实时状态是**易失**数据：读不懂就当没有。它会随下一次调度被整体重写，
            // 不像 config.json 那样承载着不可再生的信息，因此不值得打断调用方。
            _log.Warn(ex, $"实时状态文件无法解析，按「无调度进行中」处理：{CurrentStateFilePath}");
            return null;
        }
    }

    /// <inheritdoc />
    public void WriteCurrent(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(record, JsonContext.Default.RunRecord);
        AtomicFileWriter.WriteAllText(CurrentStateFilePath, json);
    }

    /// <inheritdoc />
    public void Archive(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);

        _paths.EnsureCreated();

        var targetPath = _paths.GetRunFilePath(record.RunId);
        var json = JsonSerializer.Serialize(record, JsonContext.Default.RunRecord);
        AtomicFileWriter.WriteAllText(targetPath, json);

        TrimArchive();
    }

    /// <inheritdoc />
    public IReadOnlyList<RunRecord> ReadRecent(int maxCount)
    {
        if (maxCount <= 0 || !Directory.Exists(ArchiveRoot))
        {
            return [];
        }

        var results = new List<RunRecord>();

        // runId 是 yyyyMMdd-HHmmss，字典序降序 == 时间降序，无需读文件内容比较时间。
        var files = Directory.GetFiles(ArchiveRoot, ArchiveSearchPattern)
            .OrderByDescending(static path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .Take(maxCount);

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var record = JsonSerializer.Deserialize(json, JsonContext.Default.RunRecord);
                if (record is not null)
                {
                    results.Add(record);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 单份归档读不出来不该让整个调度日志页空掉（FR-1.4 的同一条原则）。
                _log.Warn(ex, $"运行归档无法读取，已跳过：{file}");
            }
        }

        return results;
    }

    private void TrimArchive()
    {
        var files = Directory.GetFiles(ArchiveRoot, ArchiveSearchPattern);
        if (files.Length <= MaxArchivedRuns)
        {
            return;
        }

        var expired = files
            .OrderByDescending(static path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .Skip(MaxArchivedRuns);

        foreach (var file in expired)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(ex, $"清理过期运行归档失败：{file}");
            }
        }
    }
}
