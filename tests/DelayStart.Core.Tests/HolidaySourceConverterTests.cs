using System.Globalization;
using System.Text;
using System.Text.Json;

using DelayStart.Core.Models;
using DelayStart.Core.Serialization;
using DelayStart.Core.Services;
using DelayStart.Management.Serialization;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="HolidaySourceConverter"/> 的单元测试（FR-15 / NFR-y）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这一层的价值在于**只有一条转换路径**：设置页的「从文件导入」与下载通道的写盘
/// 都走它。所以这里断言的不只是"能解析"，而是"用户从上游直接下载的那份
/// <c>2026.json</c> 落进本地后，判定结果与真实放假安排逐天一致"。
/// </para>
/// <para>
/// 最容易悄悄搞反的东西是 <c>isOffDay</c> 的极性（true = 放假 → <c>restDays</c>），
/// 以及"文件存在但 days 为空"这个**正常形态**（国务院 11 月才公布次年安排）。
/// 这两条各有独立用例。
/// </para>
/// </remarks>
public sealed class HolidaySourceConverterTests
{
    // ── 原始格式（holiday-cn） ───────────────────────────────────────────

    [Fact]
    public void SourceFormat_SplitsByIsOffDay()
    {
        // 交错排布：让"靠顺序切分"这种错误实现必然失败 —— 只有按 isOffDay 才可能对。
        var conversion = HolidaySourceConverter.Convert(Raw(2026, Interleaved2026()));

        Assert.Equal(HolidayConversionOutcome.Ok, conversion.Outcome);
        var document = Assert.IsType<HolidayCalendarDocument>(conversion.Document);

        Assert.Equal(
            RealCalendar2026.RestDays.OrderBy(static value => value, StringComparer.Ordinal),
            document.RestDays.OrderBy(static value => value, StringComparer.Ordinal));
        Assert.Equal(
            RealCalendar2026.Workdays.OrderBy(static value => value, StringComparer.Ordinal),
            document.Workdays.OrderBy(static value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void SourceFormat_ProducesCalendarMatchingReal2026()
    {
        // 端到端：上游文件 → 归一化文档 → 判定用日历。
        var conversion = HolidaySourceConverter.Convert(Raw(2026, Days2026()));
        var calendar = Assert.IsType<HolidayCalendarDocument>(conversion.Document).ToCalendar();

        Assert.Equal([2026], calendar.CoveredYears);
        Assert.True(calendar.IsWorkday(RealCalendar2026.MakeUpWorkSunday));  // 周日补班 → 工作日
        Assert.True(calendar.IsRestDay(RealCalendar2026.NationalDay));       // 周四法定假 → 休息日
        Assert.True(calendar.IsWorkday(RealCalendar2026.OrdinaryWednesday)); // 普通周三
        Assert.True(calendar.IsRestDay(RealCalendar2026.OrdinarySaturday));  // 普通周六
    }

    [Fact]
    public void SourceFormat_FillsMetadata()
    {
        var stamp = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var conversion = HolidaySourceConverter.Convert(
            Raw(2026, Days2026(), "https://www.gov.cn/zhengce/content/2026-11/01/content_1.htm"),
            expectedYear: 0,
            updatedAt: stamp);

        var document = Assert.IsType<HolidayCalendarDocument>(conversion.Document);

        Assert.Equal(2026, document.Year);
        Assert.Equal("CN", document.Region);
        Assert.Equal("holiday-cn", document.Source);
        Assert.Equal("国务院办公厅 2026 年放假安排", document.SourceLabel);
        Assert.Equal("https://www.gov.cn/zhengce/content/2026-11/01/content_1.htm", document.SourceRef);
        Assert.Equal(stamp.ToString("O", CultureInfo.InvariantCulture), document.UpdatedAt);
    }

    [Fact]
    public void SourceFormat_WithoutPapers_HasNoSourceRef()
    {
        var document = Assert.IsType<HolidayCalendarDocument>(
            HolidaySourceConverter.Convert(Raw(2026, Days2026())).Document);

        Assert.Null(document.SourceRef);
    }

    [Fact]
    public void SourceFormat_EmptyDays_ReportsNotPublished()
    {
        // 🔴 用户从上游直接下载次年文件时的**正常形态**：能下到、能解析、days 是空的。
        // 报成"文件坏了"会让他去重下或换文件，而正确答案是"等国务院公布"。
        var conversion = HolidaySourceConverter.Convert(Raw(2027, []));

        Assert.Equal(HolidayConversionOutcome.NotPublished, conversion.Outcome);
        Assert.Null(conversion.Document);
        Assert.Contains("尚未公布", conversion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFormat_TooFewDays_ReportsNotPublished()
    {
        var conversion = HolidaySourceConverter.Convert(Raw(2027, [("2027-01-01", true), ("2027-10-01", true)]));

        Assert.Equal(HolidayConversionOutcome.NotPublished, conversion.Outcome);
        Assert.Contains("2 条记录", conversion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFormat_WithoutYear_ReportsUnrecognized()
    {
        // 原始格式允许 year 缺失（源生成反序列化给 0）；导入时没有期望年份可回退，
        // 只能拒收 —— 年份决定文件落到 {year}.json，猜错就是写错地方。
        var conversion = HolidaySourceConverter.Convert(Raw(0, Days2026()));

        Assert.Equal(HolidayConversionOutcome.Unrecognized, conversion.Outcome);
        Assert.Contains("year", conversion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFormat_UsesExpectedYearWhenFileOmitsIt()
    {
        // 下载通道的用法：年份由请求方给定，不依赖文件里的 year。
        var conversion = HolidaySourceConverter.Convert(Raw(0, Days2026()), expectedYear: 2026);

        Assert.Equal(HolidayConversionOutcome.Ok, conversion.Outcome);
        Assert.Equal(2026, Assert.IsType<HolidayCalendarDocument>(conversion.Document).Year);
    }

    [Fact]
    public void SourceFormat_YearMismatchWithExpectedYear_ReportsInvalid()
    {
        var conversion = HolidaySourceConverter.Convert(Raw(2026, Days2026()), expectedYear: 2027);

        Assert.Equal(HolidayConversionOutcome.Invalid, conversion.Outcome);
        Assert.Contains("2026", conversion.Message, StringComparison.Ordinal);
        Assert.Contains("2027", conversion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFormat_SkipsBlankDates()
    {
        var days = Days2026().Concat([(string.Empty, true), ("   ", false)]);

        var conversion = HolidaySourceConverter.Convert(Raw(2026, days));

        Assert.Equal(HolidayConversionOutcome.Ok, conversion.Outcome);
        var document = Assert.IsType<HolidayCalendarDocument>(conversion.Document);
        Assert.DoesNotContain(document.RestDays, static value => string.IsNullOrWhiteSpace(value));
        Assert.DoesNotContain(document.Workdays, static value => string.IsNullOrWhiteSpace(value));
    }

    [Fact]
    public void SourceFormat_ToleratesWhitespaceAroundDates()
    {
        var conversion = HolidaySourceConverter.Convert(Raw(2026, [(" 2026-01-01 ", true), .. Days2026()]));

        var document = Assert.IsType<HolidayCalendarDocument>(conversion.Document);
        Assert.Contains("2026-01-01", document.RestDays);
    }

    // ── 归一化格式（本程序导出） ─────────────────────────────────────────

    [Fact]
    public void NormalizedFormat_RoundTrips()
    {
        var source = ValidDocument(2026);
        var json = JsonSerializer.Serialize(source, HolidayJsonContext.Default.HolidayCalendarDocument);

        var conversion = HolidaySourceConverter.Convert(json);
        var document = Assert.IsType<HolidayCalendarDocument>(conversion.Document);

        Assert.Equal(HolidayConversionOutcome.Ok, conversion.Outcome);
        Assert.Equal(source.Year, document.Year);
        Assert.Equal(source.Region, document.Region);
        Assert.Equal(source.SourceLabel, document.SourceLabel);
        Assert.Equal(source.UpdatedAt, document.UpdatedAt);
        Assert.Equal(source.Workdays, document.Workdays);
        Assert.Equal(source.RestDays, document.RestDays);
    }

    [Fact]
    public void NormalizedFormat_YearMismatchWithExpectedYear_ReportsInvalid()
    {
        var json = JsonSerializer.Serialize(ValidDocument(2026), HolidayJsonContext.Default.HolidayCalendarDocument);

        var conversion = HolidaySourceConverter.Convert(json, expectedYear: 2027);

        Assert.Equal(HolidayConversionOutcome.Invalid, conversion.Outcome);
    }

    [Fact]
    public void NormalizedFormat_TooFewEntries_ReportsInvalid()
    {
        var document = ValidDocument(2026);
        document.Workdays = ["2026-03-01"];
        document.RestDays = ["2026-03-02"];
        var json = JsonSerializer.Serialize(document, HolidayJsonContext.Default.HolidayCalendarDocument);

        var conversion = HolidaySourceConverter.Convert(json);

        Assert.Equal(HolidayConversionOutcome.Invalid, conversion.Outcome);
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"{HolidayCalendarStore.MinimumEntryCount}"),
            conversion.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizedFormat_ExplicitNullCollections_ReportsInvalid()
    {
        // 手工把 workdays 改成 null（不是 []）—— 源生成反序列化会绕过属性默认值。
        var json = """{ "year": 2026, "workdays": null, "restDays": null }""";

        var conversion = HolidaySourceConverter.Convert(json);

        Assert.Equal(HolidayConversionOutcome.Invalid, conversion.Outcome);
        Assert.Null(conversion.Document);
    }

    // ── 认不出来的东西 ───────────────────────────────────────────────────

    [Fact]
    public void UnknownObjectShape_ReportsUnrecognized()
    {
        var conversion = HolidaySourceConverter.Convert("""{ "foo": 1, "bar": [] }""");

        Assert.Equal(HolidayConversionOutcome.Unrecognized, conversion.Outcome);
        Assert.Contains("认不出", conversion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJson_ReportsUnrecognized()
    {
        var conversion = HolidaySourceConverter.Convert("""{ "year": 2026, "days": [ """);

        Assert.Equal(HolidayConversionOutcome.Unrecognized, conversion.Outcome);
        Assert.Contains("JSON", conversion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlErrorPage_ReportsUnrecognized()
    {
        // CDN 被拦或 404 页面时就是这个形态 —— 它必须是"换下一个地址再试"而不是崩溃。
        var conversion = HolidaySourceConverter.Convert("<!DOCTYPE html><html><body>404</body></html>");

        Assert.Equal(HolidayConversionOutcome.Unrecognized, conversion.Outcome);
    }

    [Fact]
    public void NonObjectRoot_ReportsUnrecognized()
    {
        var conversion = HolidaySourceConverter.Convert("[1, 2, 3]");

        Assert.Equal(HolidayConversionOutcome.Unrecognized, conversion.Outcome);
        Assert.Contains("对象", conversion.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void EmptyText_ReportsUnrecognized(string json)
    {
        var conversion = HolidaySourceConverter.Convert(json);

        Assert.Equal(HolidayConversionOutcome.Unrecognized, conversion.Outcome);
        Assert.Contains("空", conversion.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeysAreMatchedCaseInsensitively()
    {
        // 判别式若是大小写敏感的，就会出现"能反序列化但认不出格式"的错位（会被拒收）。
        // ⚠️ 只把**键名**大写：整个 JSON 走 ToUpperInvariant 会把 true/false 也变成 TRUE/FALSE，
        // 那已经不是合法 JSON 了，测到的是"解析失败"而不是大小写不敏感。
        var json = Raw(2026, Days2026())
            .Replace("\"year\"", "\"YEAR\"", StringComparison.Ordinal)
            .Replace("\"papers\"", "\"PAPERS\"", StringComparison.Ordinal)
            .Replace("\"days\"", "\"DAYS\"", StringComparison.Ordinal)
            .Replace("\"name\"", "\"NAME\"", StringComparison.Ordinal)
            .Replace("\"date\"", "\"DATE\"", StringComparison.Ordinal)
            .Replace("\"isOffDay\"", "\"ISOFFDAY\"", StringComparison.Ordinal);

        var conversion = HolidaySourceConverter.Convert(json);

        Assert.Equal(HolidayConversionOutcome.Ok, conversion.Outcome);
        Assert.Equal(
            RealCalendar2026.RestDays.Length,
            Assert.IsType<HolidayCalendarDocument>(conversion.Document).RestDays.Count);
    }

    // ── 样本构造 ─────────────────────────────────────────────────────────

    /// <summary>按 holiday-cn 的 schema 手写 JSON（不能用我们的源生成上下文 —— 那是被测方的代码）。</summary>
    private static string Raw(int year, IEnumerable<(string Date, bool IsOffDay)> days, string? paper = null)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{{ \"year\": {year}, \"papers\": [");
        if (paper is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\"{paper}\"");
        }

        builder.Append("], \"days\": [");

        var first = true;
        foreach (var (date, isOffDay) in days)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(CultureInfo.InvariantCulture, $"{{ \"name\": \"节日\", \"date\": \"{date}\", \"isOffDay\": {(isOffDay ? "true" : "false")} }}");
            first = false;
        }

        builder.Append("] }");
        return builder.ToString();
    }

    private static IEnumerable<(string Date, bool IsOffDay)> Days2026()
        => RealCalendar2026.RestDays.Select(static date => (date, true))
            .Concat(RealCalendar2026.Workdays.Select(static date => (date, false)));

    /// <summary>休息日与补班日交错，用来证明切分依据是 <c>isOffDay</c> 而不是数组顺序。</summary>
    private static IEnumerable<(string Date, bool IsOffDay)> Interleaved2026()
    {
        for (var index = 0; index < RealCalendar2026.RestDays.Length; index++)
        {
            yield return (RealCalendar2026.RestDays[index], true);
            if (index < RealCalendar2026.Workdays.Length)
            {
                yield return (RealCalendar2026.Workdays[index], false);
            }
        }
    }

    private static HolidayCalendarDocument ValidDocument(int year)
    {
        var workdays = new List<string>();
        var restDays = new List<string>();
        for (var index = 0; index < HolidayCalendarStore.MinimumEntryCount; index++)
        {
            (index % 2 == 0 ? workdays : restDays).Add(
                string.Create(CultureInfo.InvariantCulture, $"{year}-03-{index + 1:00}"));
        }

        return new HolidayCalendarDocument
        {
            Year = year,
            Region = "CN",
            Source = "holiday-cn",
            SourceLabel = string.Create(CultureInfo.InvariantCulture, $"国务院办公厅 {year} 年放假安排"),
            SourceRef = "https://www.gov.cn/",
            UpdatedAt = "2026-09-23T00:00:00.0000000+00:00",
            Workdays = workdays,
            RestDays = restDays,
        };
    }
}
