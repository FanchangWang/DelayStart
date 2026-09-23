using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Serialization;
using DelayStart.Core.Services;

namespace DelayStart.Management.Serialization;

/// <summary>
/// 一份节假日 JSON 被识别并转换后的结局。
/// </summary>
/// <remarks>
/// 与 <see cref="DelayStart.Management.Services.HolidayUpdateOutcome"/> 分开：
/// 那个是"一次网络更新"的结局（含超时、地址不可用），这个是"一份内容"的结局。
/// 导入没有网络那几种失败，硬塞进同一个枚举就得在导入侧写一堆不可能的 case。
/// </remarks>
public enum HolidayConversionOutcome
{
    /// <summary>转换成了一份**已通过校验**的归一化文档。</summary>
    Ok,

    /// <summary>结构认得，但里面一条记录都没有 —— 上游还没公布该年安排。</summary>
    NotPublished,

    /// <summary>既不是原始格式，也不是归一化格式。</summary>
    Unrecognized,

    /// <summary>格式认得，但内容不合格（年份不符 / 日期格式坏 / 有记录但可用条目太少）。</summary>
    Invalid,
}

/// <summary>
/// 转换结果。
/// </summary>
/// <param name="Outcome">结局分类。</param>
/// <param name="Document"><see cref="HolidayConversionOutcome.Ok"/> 时的归一化文档；其他结局为 <see langword="null"/>。</param>
/// <param name="Message">中文说明，直接进日志、设置页错误条或 toast。</param>
public sealed record HolidayConversionResult(
    HolidayConversionOutcome Outcome,
    HolidayCalendarDocument? Document,
    string Message);

/// <summary>
/// 把一份节假日 JSON 归一化成 <see cref="HolidayCalendarDocument"/>（第三方 schema 的**唯一**入口）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **这是同一条路径的两个消费者**：设置页的「从文件导入」与「重新下载」。
/// 抽出来的理由很实在 —— 用户手上那份 <c>2026.json</c> 就是从
/// <c>NateScarlet/holiday-cn</c> 直接下载的原始文件；要求他先自己转成归一化格式，
/// 等于把这一步推回给用户，而"离线机器用 U 盘传一份数据"这件事本身就说明他不该再被要求做转换。
/// </para>
/// <para>
/// 两种入参格式**自动识别**，不看扩展名也不看文件名：
/// </para>
/// <list type="bullet">
///   <item><b>原始格式</b>（holiday-cn）：顶层有 <c>days</c> 数组，每项 <c>{ name, date, isOffDay }</c>。</item>
///   <item><b>归一化格式</b>（本程序导出）：顶层有 <c>workdays</c> / <c>restDays</c>。</item>
/// </list>
/// <para>
/// 判据是 <c>days</c> 的存在与否：归一化格式**没有**这个字段，原始格式**必有**这个字段，
/// 所以它是一个干净的判别式，不需要试探性反序列化。
/// </para>
/// <para>
/// 🔴 转换**不抛异常**，所有失败都变成一条中文消息：导入侧要把消息直接显示给用户，
/// 抛异常会让"文件不对"这种日常情况变成崩溃日志。
/// </para>
/// </remarks>
public static class HolidaySourceConverter
{
    /// <summary>数据源家族标识，写进归一化文档的 <c>source</c> 字段。</summary>
    private const string SourceFamily = "holiday-cn";

    /// <summary>与源文件的宽松度对齐：允许注释与尾逗号，用户手改过的文件也收。</summary>
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// 识别并按需转换一份节假日 JSON。
    /// </summary>
    /// <param name="json">文件内容。</param>
    /// <param name="expectedYear">
    /// 期望的年份。传 <c>0</c>（默认）表示"年份完全以文件内容为准"，这是**导入**的用法
    /// （文件名不可信，见 <c>SettingsViewModel.ImportHolidayFile</c>）。
    /// 下载通道传具体年份，用来交叉校验镜像给的是不是被请求的那一年。
    /// </param>
    /// <param name="updatedAt">
    /// 归一化文档的 <c>updatedAt</c>。为 <see langword="null"/> 时取当前 UTC 时间。
    /// 开放这个参数只为一件事：让测试能断言确定值。
    /// </param>
    /// <returns>转换结果；<see cref="HolidayConversionResult.Document"/> 非空时必定已过 <see cref="HolidayCalendarStore.TryValidate"/>。</returns>
    public static HolidayConversionResult Convert(string json, int expectedYear = 0, DateTimeOffset? updatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new HolidayConversionResult(HolidayConversionOutcome.Unrecognized, null, "文件是空的。");
        }

        JsonDocument root;
        try
        {
            root = JsonDocument.Parse(json, ParseOptions);
        }
        catch (JsonException ex)
        {
            return new HolidayConversionResult(
                HolidayConversionOutcome.Unrecognized,
                null,
                $"不是合法的 JSON：{ex.Message}");
        }

        using (root)
        {
            var element = root.RootElement;
            if (element.ValueKind != JsonValueKind.Object)
            {
                return new HolidayConversionResult(
                    HolidayConversionOutcome.Unrecognized,
                    null,
                    "顶层必须是一个 JSON 对象。");
            }

            if (TryGetProperty(element, "days", out _))
            {
                return FromSource(json, expectedYear, updatedAt);
            }

            if (TryGetProperty(element, "workdays", out _) || TryGetProperty(element, "restDays", out _))
            {
                return FromNormalized(json, expectedYear);
            }

            return new HolidayConversionResult(
                HolidayConversionOutcome.Unrecognized,
                null,
                "认不出这是节假日数据：既没有原始格式的 days，也没有本程序归一化格式的 workdays / restDays。");
        }
    }

    /// <summary>holiday-cn 原始格式 → 归一化文档。</summary>
    private static HolidayConversionResult FromSource(string json, int expectedYear, DateTimeOffset? updatedAt)
    {
        HolidaySource? source;
        try
        {
            source = JsonSerializer.Deserialize(json, HolidaySourceJsonContext.Default.HolidaySource);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            return new HolidayConversionResult(
                HolidayConversionOutcome.Unrecognized,
                null,
                $"原始格式解析失败：{ex.Message}");
        }

        if (source is null)
        {
            return new HolidayConversionResult(HolidayConversionOutcome.Unrecognized, null, "原始格式解析结果为空。");
        }

        var year = expectedYear != 0 ? expectedYear : source.Year;
        if (year == 0)
        {
            return new HolidayConversionResult(
                HolidayConversionOutcome.Unrecognized,
                null,
                "文件里没有 year 字段，无法确定这是哪一年的数据。");
        }

        if (source.Year != 0 && expectedYear != 0 && source.Year != expectedYear)
        {
            return new HolidayConversionResult(
                HolidayConversionOutcome.Invalid,
                null,
                $"文件里的年份 {source.Year} 与请求年份 {expectedYear} 不一致。");
        }

        var days = source.Days ?? [];
        if (days.Count < HolidayCalendarStore.MinimumEntryCount)
        {
            // 🔴 这一条就是用户从上游直接下载文件时的正常形态：国务院通常 11 月才公布次年安排，
            // 那之前仓库里 {次年}.json 是存在的、能下载的、days 是空的。这不是"文件坏了"，
            // 是"还没到时候"，两者的用户动作完全不同（等 vs 换一份文件）。
            return new HolidayConversionResult(
                HolidayConversionOutcome.NotPublished,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"该年放假安排尚未公布（文件里只有 {days.Count} 条记录，少于 {HolidayCalendarStore.MinimumEntryCount} 条）。"));
        }

        // isOffDay 的极性是这里唯一容易搞反的东西：true = 放假 → restDays；
        // false = 调休上班 → workdays。反过来会让整年判定左右颠倒，所以测试里逐个日期断言。
        var workdays = new List<string>();
        var restDays = new List<string>();
        foreach (var day in days)
        {
            if (string.IsNullOrWhiteSpace(day.Date))
            {
                continue;
            }

            (day.IsOffDay ? restDays : workdays).Add(day.Date.Trim());
        }

        return Finish(new HolidayCalendarDocument
        {
            Year = year,
            Region = "CN",
            Source = SourceFamily,
            SourceLabel = BuildSourceLabel(year),
            SourceRef = source.Papers is { Count: > 0 } papers ? papers[0] : null,
            UpdatedAt = (updatedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
            Workdays = workdays,
            RestDays = restDays,
        });
    }

    /// <summary>归一化格式：已经是我们自己的 schema，反序列化后校验即可。</summary>
    private static HolidayConversionResult FromNormalized(string json, int expectedYear)
    {
        HolidayCalendarDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, HolidayJsonContext.Default.HolidayCalendarDocument);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            return new HolidayConversionResult(
                HolidayConversionOutcome.Unrecognized,
                null,
                $"归一化格式解析失败：{ex.Message}");
        }

        if (document is null)
        {
            return new HolidayConversionResult(HolidayConversionOutcome.Unrecognized, null, "归一化格式解析结果为空。");
        }

        if (expectedYear != 0 && document.Year != expectedYear)
        {
            return new HolidayConversionResult(
                HolidayConversionOutcome.Invalid,
                null,
                $"文件里的年份 {document.Year} 与请求年份 {expectedYear} 不一致。");
        }

        return Finish(document);
    }

    /// <summary>统一出口：过校验器，通过才算 Ok。</summary>
    /// <remarks>
    /// 校验只有这一道，与写盘前的 <see cref="HolidayCalendarStore.Write"/> 用的是同一个判定 ——
    /// 在这里说"可以"，到了写盘再说"不可以"，用户就会看到自相矛盾的提示。
    /// </remarks>
    private static HolidayConversionResult Finish(HolidayCalendarDocument document)
    {
        var work = document.Workdays ?? [];
        var rest = document.RestDays ?? [];

        if (!HolidayCalendarStore.TryValidate(document, out var reason))
        {
            return new HolidayConversionResult(
                HolidayConversionOutcome.Invalid,
                null,
                $"{document.Year} 年数据不合格：{reason}");
        }

        return new HolidayConversionResult(
            HolidayConversionOutcome.Ok,
            document,
            string.Create(
                CultureInfo.InvariantCulture,
                $"已识别 {document.Year} 年数据：{rest.Count} 个放假日 / {work.Count} 个调休补班日。"));
    }

    /// <summary>
    /// 大小写不敏感地取属性。
    /// </summary>
    /// <remarks>
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> 是**大小写敏感**的，
    /// 而两个反序列化上下文都开了 <c>PropertyNameCaseInsensitive</c> ——
    /// 用敏感版做判别式，会出现"能解析但认不出"的错位：文件被当成不认识的格式拒收。
    /// </remarks>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string BuildSourceLabel(int year)
        => string.Create(CultureInfo.InvariantCulture, $"国务院办公厅 {year} 年放假安排");
}
