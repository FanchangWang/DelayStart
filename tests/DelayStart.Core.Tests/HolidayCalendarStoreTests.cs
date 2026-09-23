using DelayStart.Core.Models;
using DelayStart.Core.Serialization;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="HolidayCalendar"/> 与 <see cref="HolidayCalendarStore"/> 的单元测试（NFR-y 三层容错）。
/// </summary>
/// <remarks>
/// <para>
/// 这一层是"离线可判定"的最后一道保障：文件缺失、文件损坏、条目为空三种情形
/// 都必须**不抛异常**地降级 —— 调度端在登录链路上读它，抛一次异常就是"今天什么都没启动"。
/// </para>
/// </remarks>
public sealed class HolidayCalendarStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    private PathService Paths => new(_temp.Combine("local"), _temp.Combine("config"));

    private static HolidayCalendarDocument Document(int year, int entries = 20)
    {
        var workdays = new List<string>();
        var restDays = new List<string>();
        for (var index = 0; index < entries; index++)
        {
            (index % 2 == 0 ? workdays : restDays).Add($"2026-01-{(index % 28) + 1:00}");
        }

        return new HolidayCalendarDocument
        {
            Year = year,
            Region = "CN",
            Source = "holiday-cn",
            Workdays = workdays,
            RestDays = restDays,
        };
    }

    [Fact]
    public void Load_NoDirectory_ReturnsEmptyCalendarWithoutIssues()
    {
        var result = HolidayCalendarStore.Load(Paths);

        Assert.True(result.Calendar.IsEmpty);
        Assert.Empty(result.Issues);   // 首次运行不是故障，不该吓唬任何人。
    }

    [Fact]
    public void WriteThenLoad_RoundTrips()
    {
        var paths = Paths;
        HolidayCalendarStore.Write(paths, Document(2026));

        var result = HolidayCalendarStore.Load(paths);

        Assert.Empty(result.Issues);
        Assert.True(result.Calendar.Covers(new DateOnly(2026, 1, 5)));
        Assert.True(result.Calendar.IsWorkday(new DateOnly(2026, 1, 1)));
        Assert.True(result.Calendar.IsRestDay(new DateOnly(2026, 1, 2)));
    }

    [Fact]
    public void Write_TooFewEntries_IsRejected()
    {
        // E-x6 的核心：源文件明明存在但 days 是空数组（国务院还没公布次年安排）。
        // 没有这道门槛，一次"成功的下载"会把旧数据覆盖成空壳。
        var document = Document(2026, entries: 3);

        Assert.False(HolidayCalendarStore.IsValid(document));

        var thrown = Assert.Throws<ArgumentException>(() => HolidayCalendarStore.Write(Paths, document));
        Assert.Contains("条目数", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_UnparsableDates_IsRejectedEvenWhenEnoughEntries()
    {
        // 条目数够（10 条有效 + 20 条坏格式 = 30），但可解析的只有 10 条 ——
        // 必须被"日期格式"这道检查拦下，而不是先被条目数糊弄过去。
        var document = Document(2026, entries: 20);
        document.RestDays = Enumerable.Range(1, 20).Select(static index => $"2026/{index:00}/01").ToList();

        Assert.False(HolidayCalendarStore.IsValid(document));
        Assert.False(HolidayCalendarStore.TryValidate(document, out var reason));
        Assert.Contains("日期格式", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsIssueButKeepsGoing()
    {
        var paths = Paths;
        HolidayCalendarStore.Write(paths, Document(2026));
        File.WriteAllText(paths.GetHolidayFilePath(2026), "{ \"year\": 2026, \"days\": [");

        var result = HolidayCalendarStore.Load(paths);

        var issue = Assert.Single(result.Issues);
        Assert.Contains("JSON 解析失败", issue.Reason, StringComparison.Ordinal);
        Assert.True(result.Calendar.IsEmpty);
    }

    [Fact]
    public void Load_FileNameYearMismatch_IsReportedAsIssue()
    {
        var paths = Paths;
        Directory.CreateDirectory(paths.HolidaysRoot);
        var twenty = string.Join(
            ",",
            Enumerable.Range(1, 20).Select(static index => $"\"2030-01-{index:00}\""));
        File.WriteAllText(
            paths.GetHolidayFilePath(2026),
            "{ \"year\": 2030, \"region\": \"CN\", \"workdays\": [" + twenty + "], \"restDays\": [] }");

        var result = HolidayCalendarStore.Load(paths);

        var issue = Assert.Single(result.Issues);
        Assert.Contains("不一致", issue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CorruptFileDoesNotBlockOtherYears()
    {
        var paths = Paths;
        HolidayCalendarStore.Write(paths, Document(2026));
        File.WriteAllText(paths.GetHolidayFilePath(2027), "不是 JSON");

        var result = HolidayCalendarStore.Load(paths);

        Assert.Single(result.Issues);
        Assert.True(result.Calendar.Covers(new DateOnly(2026, 3, 3)));
        Assert.False(result.Calendar.Covers(new DateOnly(2027, 3, 3)));
    }

    [Fact]
    public void Delete_RemovesYearData()
    {
        var paths = Paths;
        HolidayCalendarStore.Write(paths, Document(2026));

        Assert.True(HolidayCalendarStore.Delete(paths, 2026));
        Assert.False(HolidayCalendarStore.Delete(paths, 2026));   // 幂等
        Assert.False(HolidayCalendarStore.Delete(paths, 1999));
    }

    [Fact]
    public void Calendar_AskedAboutYearWithoutData_ThrowsRatherThanGuessing()
    {
        // 未被覆盖的年份：判定权属于调用方（降级），日历本身**不猜** ——
        // 返回 false 会被误读成"这天不上班"，从而得出完全相反的结论。
        var calendar = RealCalendar2026.Create();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => calendar.IsWorkday(new DateOnly(2027, 1, 4)));
    }

    [Fact]
    public void Document_ToCalendar_SkipsUnparsableDates()
    {
        var document = new HolidayCalendarDocument
        {
            Year = 2026,
            Workdays = ["2026-01-05", "garbage"],
            RestDays = [.. RealCalendar2026.RestDays],
        };

        var calendar = document.ToCalendar();

        // 一条坏数据不该让整年失效。
        Assert.True(calendar.IsWorkday(new DateOnly(2026, 1, 5)));
        Assert.True(calendar.IsRestDay(new DateOnly(2026, 10, 1)));
    }
}
