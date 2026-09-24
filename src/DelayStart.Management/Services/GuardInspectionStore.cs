using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Services;
using DelayStart.Management.Models;
using DelayStart.Management.Serialization;

namespace DelayStart.Management.Services;

/// <summary>
/// 守卫巡检归档的写入、读取与滚动清理（D116）。
/// </summary>
/// <remarks>
/// <para>
/// 落点 <c>%LOCALAPPDATA%\DelayStart\guard\inspections\{yyyyMMdd-HHmmss}.json</c>，
/// 每次巡检一份（内容就是 <see cref="GuardRunReport"/> 原样序列化，与运行归档存
/// <see cref="DelayStart.Core.Models.RunRecord"/> 同一思路）。文件名取巡检完成时刻 —— 与运行归档的
/// <c>runId</c> 同一语义：字典序即时间序，读取与清理都不用碰文件内容。
/// </para>
/// <para>
/// 这份归档是守卫日志页与总览「上次守卫巡检」卡的**结构化**数据源（D1=A 批复）：
/// 此前条目明细只存在于 guard.log 的计数行之外 —— 纠正了什么、新增了什么，只在
/// 系统通知里出现过（超过 3 个还折成"等 N 项"），巡检一旦结束就无处可查。
/// 文本日志（guard.log）保留双轨（D4）：一行汇总给人扫一眼，归档给界面查明细。
/// </para>
/// <para>
/// 🔴 归档是**旁路能力**：写入失败只记 Warn，绝不阻塞巡检本身 —— 守卫的主职是
/// 纠正写回，日志页查不到一次巡检是损失，但一次纠正没执行是事故。
/// </para>
/// </remarks>
public sealed class GuardInspectionStore
{
    /// <summary>归档保留份数上限（与运行归档同口径，FR-8.3 的 30 份）。</summary>
    public const int MaxRetainedInspections = 30;

    private const string ArchiveSearchPattern = "*.json";

    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>构造巡检归档存储。</summary>
    /// <param name="paths">路径服务。</param>
    /// <param name="log">日志接收端。</param>
    public GuardInspectionStore(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _log = log;
    }

    /// <summary>归档一次巡检结果（守卫进程在巡检完成后调用）。</summary>
    /// <param name="report">本次巡检结果（<see cref="GuardRunReport.CompletedAt"/> 用作文件名）。</param>
    public void Write(GuardRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.GuardDisabled)
        {
            // 守卫关闭的那次"运行"没有执行任何巡检，归档它只会制造一条空记录。
            return;
        }

        var fileName = report.CompletedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var targetPath = Path.Combine(_paths.GuardInspectionsRoot, $"{fileName}.json");

        try
        {
            var json = JsonSerializer.Serialize(report, GuardJsonContext.Default.GuardRunReport);
            AtomicFileWriter.WriteAllText(targetPath, json);
            TrimArchive();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, $"写入守卫巡检归档失败（不影响本次巡检，guard.log 仍有汇总行）：{targetPath}");
        }
    }

    /// <summary>按完成时间倒序读取最近若干份巡检归档，供守卫日志页与总览卡使用。</summary>
    /// <param name="maxCount">最多读取数量。</param>
    /// <returns>倒序排列（最新在前）的巡检结果集合；无归档时为空集合。</returns>
    public IReadOnlyList<GuardRunReport> ReadRecent(int maxCount)
    {
        if (maxCount <= 0 || !Directory.Exists(_paths.GuardInspectionsRoot))
        {
            return [];
        }

        var results = new List<GuardRunReport>();

        // 文件名是 yyyyMMdd-HHmmss，字典序降序 == 时间降序，无需读文件内容比较时间。
        var files = Directory.GetFiles(_paths.GuardInspectionsRoot, ArchiveSearchPattern)
            .OrderByDescending(static path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .Take(maxCount);

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var report = JsonSerializer.Deserialize(json, GuardJsonContext.Default.GuardRunReport);
                if (report is not null)
                {
                    results.Add(report);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // 单份归档读不出来不该让整个守卫日志页空掉（FR-1.4 的同一条原则）。
                _log.Warn(ex, $"守卫巡检归档无法读取，已跳过：{file}");
            }
        }

        return results;
    }

    /// <summary>清理超出上限的旧归档（写入后调用；与 <c>RunStateService.TrimArchive</c> 同构）。</summary>
    private void TrimArchive()
    {
        var files = Directory.GetFiles(_paths.GuardInspectionsRoot, ArchiveSearchPattern);
        if (files.Length <= MaxRetainedInspections)
        {
            return;
        }

        var expired = files
            .OrderByDescending(static path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .Skip(MaxRetainedInspections);

        foreach (var file in expired)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(ex, $"清理过期守卫巡检归档失败：{file}");
            }
        }
    }
}
