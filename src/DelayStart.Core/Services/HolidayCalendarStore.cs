using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Models;
using DelayStart.Core.Serialization;

namespace DelayStart.Core.Services;

/// <summary>
/// 读取法定日历时发现的坏文件。
/// </summary>
/// <param name="FilePath">坏文件完整路径。</param>
/// <param name="Reason">中文原因，直接进日志。</param>
public sealed record HolidayFileIssue(string FilePath, string Reason);

/// <summary>
/// 一次读取的结果：日历本体 + 坏文件清单。
/// </summary>
/// <param name="Calendar">合并后的日历；一份可读数据都没有时是 <see cref="HolidayCalendar.Empty"/>。</param>
/// <param name="Issues">解析失败或校验不通过的文件。🔴 有值时调用方必须让用户看见。</param>
public sealed record HolidayCalendarLoadResult(HolidayCalendar Calendar, IReadOnlyList<HolidayFileIssue> Issues);

/// <summary>
/// 法定日历文件的读写（落 <c>%LOCALAPPDATA%\DelayStart\holidays\{year}.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 挂在 Core 而不是某一端：管理端写、调度端读，两边必须认同一个文件布局。
/// 路径一律来自 <see cref="PathService"/>（NFR-6.7 / D23 禁止硬编码）。
/// </para>
/// <para>
/// 🔴 **读失败**返回 <see cref="HolidayCalendar.Empty"/> + 问题清单，**绝不抛异常**：
/// 调度端在登录链路上调用它，任何一次抛异常都会变成"今天什么都没启动"这种最坏结果（§4.3 / E-x5）。
/// </para>
/// <para>
/// 一次读取把目录里的**全部年份**合并成一份日历：每年只有一个小 JSON，
/// 合并成本可忽略，而"今天跨年 belonging 到谁"这种边界情况直接消失。
/// </para>
/// </remarks>
public static class HolidayCalendarStore
{
    /// <summary>写盘的最低条目数。正常年份 30 条以上，低于它必然是空壳或脏数据（E-x6）。</summary>
    public const int MinimumEntryCount = 20;

    private const int MinYear = 2000;
    private const int MaxYear = 2999;

    /// <summary>
    /// 读取全部年份的数据并合并。
    /// </summary>
    /// <param name="paths">路径服务（<see cref="PathService.HolidaysRoot"/>）。</param>
    /// <returns>日历与坏文件清单；目录不存在时得到空日历且清单为空（首次运行的正常路径）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> 为 <see langword="null"/>。</exception>
    public static HolidayCalendarLoadResult Load(PathService paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var root = paths.HolidaysRoot;
        if (!Directory.Exists(root))
        {
            return new HolidayCalendarLoadResult(HolidayCalendar.Empty, []);
        }

        var workdays = new List<DateOnly>();
        var restDays = new List<DateOnly>();
        var issues = new List<HolidayFileIssue>();

        foreach (var file in Directory.EnumerateFiles(root, "*.json").OrderBy(static p => p, StringComparer.Ordinal))
        {
            DocumentAndIssue result;
            try
            {
                result = Read(paths, file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                issues.Add(new HolidayFileIssue(file, $"读取失败：{ex.Message}"));
                continue;
            }

            if (result.Issue is not null)
            {
                issues.Add(result.Issue);
                continue;
            }

            var document = result.Document!;
            workdays.AddRange(Parse(document.Workdays));
            restDays.AddRange(Parse(document.RestDays));
        }

        return new HolidayCalendarLoadResult(HolidayCalendar.Create(workdays, restDays), issues);
    }

    /// <summary>
    /// 校验一份待落盘的数据。写盘前**必须**先过这一道：拿空壳数据覆盖好数据是
    /// "下载失败的代价远大于它的收益"的典型。
    /// </summary>
    /// <param name="document">待校验的数据。</param>
    /// <returns>校验通过为 <see langword="true"/>。</returns>
    public static bool IsValid(HolidayCalendarDocument document)
        => TryValidate(document, out _);

    /// <summary>
    /// 校验并返回不通过的原因。
    /// </summary>
    /// <param name="document">待校验的数据。</param>
    /// <param name="reason">中文原因；校验通过时为 <see langword="null"/>。</param>
    /// <returns>校验通过为 <see langword="true"/>。</returns>
    public static bool TryValidate(HolidayCalendarDocument document, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Year is < MinYear or > MaxYear)
        {
            reason = $"年份 {document.Year} 不在合法区间 {MinYear}-{MaxYear}。";
            return false;
        }

        // 关键：源文件明明存在却是空 days（国务院还没公布次年安排时就是常态），
        // 此时 HTTP 状态码是 200 —— 靠状态码判断"有没有数据"是错的，必须数条目。
        // 源生成反序列化在遇到 "workdays": null 这类显式 null 时会赋 null，绕过属性默认值。
        var work = document.Workdays ?? [];
        var rest = document.RestDays ?? [];
        var total = work.Count + rest.Count;
        if (total < MinimumEntryCount)
        {
            reason = $"条目数 {total} 少于 {MinimumEntryCount}，视为尚未公布或数据不完整。";
            return false;
        }

        var parsed = work.Count(IsIsoDate) + rest.Count(IsIsoDate);
        if (parsed < MinimumEntryCount)
        {
            reason = $"可解析日期 {parsed} 少于 {MinimumEntryCount}，日期格式应为 {HolidayCalendarDocument.DateFormat}。";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// 原子写入某一年份的数据文件。
    /// </summary>
    /// <param name="paths">路径服务。</param>
    /// <param name="document">已通过 <see cref="IsValid"/> 的数据。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">数据未通过校验。</exception>
    public static void Write(PathService paths, HolidayCalendarDocument document)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(document);

        if (!TryValidate(document, out var reason))
        {
            throw new ArgumentException($"法定日历数据不合法，已拒绝写盘：{reason}", nameof(document));
        }

        var json = JsonSerializer.Serialize(document, HolidayJsonContext.Default.HolidayCalendarDocument);
        AtomicFileWriter.WriteAllText(paths.GetHolidayFilePath(document.Year), json);
    }

    /// <summary>
    /// 删除某一年份的数据文件（"回滚到没有数据"的唯一途径；失败不抛）。
    /// </summary>
    /// <param name="paths">路径服务。</param>
    /// <param name="year">年份。</param>
    /// <returns>删除成功为 <see langword="true"/>；文件不存在或删除失败为 <see langword="false"/>。</returns>
    public static bool Delete(PathService paths, int year)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            var path = paths.GetHolidayFilePath(year);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly record struct DocumentAndIssue(HolidayCalendarDocument? Document, HolidayFileIssue? Issue);

    private static DocumentAndIssue Read(PathService paths, string file)
    {
        var json = AtomicFileWriter.ReadAllTextOrNull(file);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DocumentAndIssue(null, new HolidayFileIssue(file, "文件为空。"));
        }

        HolidayCalendarDocument document;
        try
        {
            document = JsonSerializer.Deserialize(json, HolidayJsonContext.Default.HolidayCalendarDocument)
                ?? new HolidayCalendarDocument();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            return new DocumentAndIssue(null, new HolidayFileIssue(file, $"JSON 解析失败：{ex.Message}"));
        }

        var fileName = Path.GetFileNameWithoutExtension(file);
        if (!int.TryParse(fileName, out var yearFromName) || yearFromName != document.Year)
        {
            return new DocumentAndIssue(
                null,
                new HolidayFileIssue(file, $"文件名年份 {fileName} 与内容 year={document.Year} 不一致。"));
        }

        return new DocumentAndIssue(document, null);
    }

    private static IEnumerable<DateOnly> Parse(List<string>? values)
    {
        if (values is null)
        {
            yield break;
        }

        foreach (var value in values)
        {
            if (DateOnly.TryParseExact(
                    value,
                    HolidayCalendarDocument.DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                yield return date;
            }
        }
    }

    private static bool IsIsoDate(string value)
        => DateOnly.TryParseExact(
            value,
            HolidayCalendarDocument.DateFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
}
