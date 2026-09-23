using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="HolidayCheckThrottle"/> 与 <see cref="HolidayCheckRecord"/> 的单元测试（FR-15 / §6.5）。
/// </summary>
/// <remarks>
/// <para>
/// 这一层值得单独测，是因为它承载了一次真机事故的修复：第一版"发请求之前先落时间戳"，
/// 于是**一次失败 = 静默 7 天**（本机实测：raw.githubusercontent.com 稳定超时，
/// 两个 jsdelivr 镜像 1.1 / 1.5 秒返回 200，用户在界面上只看到"开关开着但什么都没发生"）。
/// 现在的规则按结局分开，这两条边界（成功的 7 天、失败的 1 小时）就是被断言的对象。
/// </para>
/// <para>
/// 🔴 另一个必须钉死的是**向后兼容**：升级上来的机器上留着的是旧版本的
/// 单行 ISO 时间戳（没有结局行）。它必须被当成"失败"处理 —— 否则那些机器会继续
/// 把上次那次失败当成"刚查过"，再静默等一个星期。
/// </para>
/// </remarks>
public sealed class HolidayCheckThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    // ── 记录的序列化 ─────────────────────────────────────────────────────

    [Fact]
    public void Record_RoundTrips()
    {
        var original = new HolidayCheckRecord(
            Now,
            HolidayCheckOutcome.Succeeded,
            "已获取 2026 年数据。");

        Assert.True(HolidayCheckRecord.TryParse(original.Format(), out var parsed));

        Assert.Equal(original.At, parsed.At);
        Assert.Equal(original.Outcome, parsed.Outcome);
        Assert.Equal(original.Message, parsed.Message);
    }

    [Fact]
    public void FailedRecord_RoundTrips()
    {
        var original = new HolidayCheckRecord(Now, HolidayCheckOutcome.Failed, "请求超时（15 秒）：raw.githubusercontent.com");

        Assert.True(HolidayCheckRecord.TryParse(original.Format(), out var parsed));

        Assert.Equal(HolidayCheckOutcome.Failed, parsed.Outcome);
        Assert.Equal(original.Message, parsed.Message);
    }

    [Fact]
    public void MultiLineMessage_IsFlattenedSoItCannotBreakParsing()
    {
        // 设置页把多条失败拼成多行文案，而记录文件是"一行一个字段"——
        // 不压平的话，第二行会被当成结局行读，记录直接错位。
        var original = new HolidayCheckRecord(Now, HolidayCheckOutcome.Failed, "2026 年：超时\n2027 年：内容不合法");

        Assert.True(HolidayCheckRecord.TryParse(original.Format(), out var parsed));

        Assert.DoesNotContain('\n', parsed.Message);
        Assert.Contains("2027 年：内容不合法", parsed.Message, StringComparison.Ordinal);
    }

    // ── 向后兼容与坏数据 ─────────────────────────────────────────────────

    [Fact]
    public void LegacySingleLineTimestamp_IsTreatedAsFailed()
    {
        // 旧版本（2026-09-23 之前）写的就是这个：一行 ISO 时间戳，没有结局行。
        const string Legacy = "2026-09-23T13:10:26.5914414+00:00";

        Assert.True(HolidayCheckRecord.TryParse(Legacy, out var parsed));

        Assert.Equal(HolidayCheckOutcome.Failed, parsed.Outcome);
        Assert.Equal(string.Empty, parsed.Message);

        // 用带精度的比较：真实文件里的 ISO 串带 7 位小数（.5914414），
        // 而"这个时间点被读出来了"才是要断言的事，不必把小数位也钉死。
        Assert.Equal(
            new DateTimeOffset(2026, 9, 23, 13, 10, 26, TimeSpan.Zero),
            parsed.At,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void LegacyTimestamp_RetriesAfterOneHourNotSevenDays()
    {
        // 升级路径的完整意图：旧记录按"失败"算，所以一小时后就该重试 ——
        // 而不是被当作"上周查过了"再等 7 天。
        Assert.True(HolidayCheckRecord.TryParse("2026-09-23T11:30:00+00:00", out var parsed));

        Assert.False(HolidayCheckThrottle.IsDue(parsed, Now, out _));

        var oneHourLater = Now.AddHours(1);
        Assert.True(HolidayCheckThrottle.IsDue(parsed, oneHourLater, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("这不是时间戳")]
    [InlineData("{\"at\":\"2026-09-23\"}")]
    public void UnparsableText_IsRejected(string? text) =>
        Assert.False(HolidayCheckRecord.TryParse(text, out _));

    // ── 节流判据 ─────────────────────────────────────────────────────────

    [Fact]
    public void NoRecord_IsAlwaysDue()
    {
        Assert.True(HolidayCheckThrottle.IsDue(null, Now, out var next));

        // 从没查过 → 现在就该查（不能报一个"未来"的重试时间出去）。
        Assert.Equal(Now, next);
    }

    [Fact]
    public void Success_StaysQuietForSevenDays()
    {
        var record = new HolidayCheckRecord(Now, HolidayCheckOutcome.Succeeded, "已获取 2026 年数据。");

        Assert.False(HolidayCheckThrottle.IsDue(record, Now.AddDays(7).AddMinutes(-1), out var next));
        Assert.Equal(Now + HolidayCheckThrottle.SuccessInterval, next);

        Assert.True(HolidayCheckThrottle.IsDue(record, Now.AddDays(7), out _));
    }

    [Fact]
    public void Failure_RetriesAfterOneHour()
    {
        var record = new HolidayCheckRecord(Now, HolidayCheckOutcome.Failed, "全部下载地址均不可用。");

        Assert.False(HolidayCheckThrottle.IsDue(record, Now.AddMinutes(30), out var next));
        Assert.Equal(Now + HolidayCheckThrottle.FailureRetryInterval, next);

        Assert.True(HolidayCheckThrottle.IsDue(record, Now.AddHours(1), out _));
    }

    [Fact]
    public void FailureInterval_IsStrictlyShorterThanSuccessInterval()
    {
        // 这条是在防"哪天有人把两个常量改成同一个值"——那样失败就又变成静默 7 天了。
        Assert.True(HolidayCheckThrottle.FailureRetryInterval < HolidayCheckThrottle.SuccessInterval);
    }

    // ── 结果 → 节流分类 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(HolidayUpdateOutcome.Updated, HolidayCheckOutcome.Succeeded)]
    [InlineData(HolidayUpdateOutcome.NotPublished, HolidayCheckOutcome.Succeeded)]
    [InlineData(HolidayUpdateOutcome.NetworkFailure, HolidayCheckOutcome.Failed)]
    [InlineData(HolidayUpdateOutcome.InvalidContent, HolidayCheckOutcome.Failed)]
    public void OutcomeOf_SingleYear(HolidayUpdateOutcome updateOutcome, HolidayCheckOutcome expected) =>
        Assert.Equal(expected, HolidayCheckThrottle.OutcomeOf([new HolidayUpdateResult(2026, updateOutcome, "说明")]));

    [Fact]
    public void OutcomeOf_AnyFailureMakesTheWholeRunFailed()
    {
        // 只要有一年没成，就该早点再试 —— 拿"最好的一年"当结论会让坏的那年永远补不上。
        var results = new List<HolidayUpdateResult>
        {
            new(2026, HolidayUpdateOutcome.Updated, "已更新"),
            new(2027, HolidayUpdateOutcome.NetworkFailure, "超时"),
        };

        Assert.Equal(HolidayCheckOutcome.Failed, HolidayCheckThrottle.OutcomeOf(results));
    }

    [Fact]
    public void OutcomeOf_EmptyList_IsSucceeded() =>
        Assert.Equal(HolidayCheckOutcome.Succeeded, HolidayCheckThrottle.OutcomeOf([]));
}
