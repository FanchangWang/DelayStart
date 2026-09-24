using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="HolidayCheckThrottle"/> 与 <see cref="HolidayCheckRecord"/> 的单元测试（FR-15 / §6.5）。
/// </summary>
/// <remarks>
/// <para>
/// 这一层值得单独测，是因为它承载了两次真机事故的修复。
/// 第一版"发请求之前先落时间戳"，于是**一次失败 = 静默 7 天**（本机实测：
/// raw.githubusercontent.com 稳定超时，两个 jsdelivr 镜像 1.1 / 1.5 秒返回 200，
/// 用户在界面上只看到"开关开着但什么都没发生"）。
/// </para>
/// <para>
/// 第二版只有"成功 / 失败"两档，于是第二种静默出现了：**下载成功过、文件后来在本地
/// 被改了名**，节流拿"成功"记账，自动检查再也不会去补（真机实测：记录里写着
/// <c>ok / 已获取 2026 年数据</c>，而目录里那份数据的文件名已经不是 <c>2026.json</c>，
/// 于是它被读盘校验拒掉、年份行显示"未下载"、用户看到"开关是开的，就是没反应"）。
/// 现在 <see cref="HolidayCheckOutcome.Updated"/> 在"本地没有可用数据"的前提下立即到期，
/// 这条边界就是 <see cref="UpdatedRecord_ButDataIsGone_IsDueNow"/> 钉住的东西。
/// </para>
/// <para>
/// 🔴 另一个必须钉死的是**向后兼容两代格式**：更早的机器上留着的是单行 ISO 时间戳
/// （没有结局行），它必须被当成"失败"处理；中间那代写的是 <c>ok</c> / <c>fail</c>，
/// 其中 <c>ok</c> 按"已获取"读（最坏只是多查一次，见 <see cref="LegacyOkFlag_IsNoLongerRecognized"/>）。
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
            HolidayCheckOutcome.Updated,
            "已获取 2026 年数据。");

        Assert.True(HolidayCheckRecord.TryParse(original.Format(), out var parsed));

        Assert.Equal(original.At, parsed.At);
        Assert.Equal(original.Outcome, parsed.Outcome);
        Assert.Equal(original.Message, parsed.Message);
    }

    [Fact]
    public void NotPublishedRecord_RoundTrips()
    {
        var original = new HolidayCheckRecord(Now, HolidayCheckOutcome.NotPublished, "2027 年安排尚未公布（每年约 11 月）。");

        Assert.True(HolidayCheckRecord.TryParse(original.Format(), out var parsed));

        Assert.Equal(HolidayCheckOutcome.NotPublished, parsed.Outcome);
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
        // 更早的版本写的就是这个：一行 ISO 时间戳，没有结局行。
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

    [Fact]
    public void LegacyOkFlag_IsNoLongerRecognized()
    {
        // D121：中间那代把「已获取」与「未公布」混写成 `ok` 的历史格式已不再兼容。
        // 读到不认识的值按「失败」处理 —— 下一次检查会重试。对本项目而言这没有代价：
        // 下一个版本要求先卸载旧版，记录文件不可能来自旧版本。
        const string Legacy = "2026-09-23T14:16:55.6005424+00:00\nok\n已获取 2026 年数据。";

        Assert.True(HolidayCheckRecord.TryParse(Legacy, out var parsed));

        Assert.Equal(HolidayCheckOutcome.Failed, parsed.Outcome);
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
    public void NotPublished_StaysQuietForSevenDays()
    {
        var record = new HolidayCheckRecord(Now, HolidayCheckOutcome.NotPublished, "2027 年安排尚未公布（每年约 11 月）。");

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
    public void UpdatedRecord_ButDataIsGone_IsDueNow()
    {
        // 🔴 2026-09-23 真机事故的回归用例。
        // 记录写着"已获取 2026 年数据"，可本地那份数据已经不在了（被改名 / 删掉 / 坏掉）——
        // 调用方只在"本地没有可用数据"时才会问节流，所以这个组合的含义就是数据丢了。
        // 此时若还按"成功 7 天"算了，用户会看到"开关是开的，却再也不补"。
        var record = new HolidayCheckRecord(Now, HolidayCheckOutcome.Updated, "已获取 2026 年数据。");

        Assert.True(HolidayCheckThrottle.IsDue(record, Now, out var next));
        Assert.Equal(Now, next);

        // 一分钟后就该重试 —— 不是 7 天后。
        Assert.True(HolidayCheckThrottle.IsDue(record, Now.AddMinutes(1), out _));
    }

    [Fact]
    public void FailureInterval_IsStrictlyShorterThanSuccessInterval()
    {
        // 这条是在防"哪天有人把两个常量改成同一个值"——那样失败就又变成静默 7 天了。
        Assert.True(HolidayCheckThrottle.FailureRetryInterval < HolidayCheckThrottle.SuccessInterval);
    }

    // ── 结果 → 节流分类 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(HolidayUpdateOutcome.Updated, HolidayCheckOutcome.Updated)]
    [InlineData(HolidayUpdateOutcome.NotPublished, HolidayCheckOutcome.NotPublished)]
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
    public void OutcomeOf_NotPublishedMixedWithUpdated_IsUpdated()
    {
        // 30 天窗口跨年时会出现"今年更新了、明年还没公布"：今年的数据确实落盘了，
        // 不能因为明年没公布就把这一轮记成"未公布"（那会让"数据不在本地"失去线索）。
        var results = new List<HolidayUpdateResult>
        {
            new(2026, HolidayUpdateOutcome.Updated, "已更新"),
            new(2027, HolidayUpdateOutcome.NotPublished, "尚未公布"),
        };

        Assert.Equal(HolidayCheckOutcome.Updated, HolidayCheckThrottle.OutcomeOf(results));
    }

    [Fact]
    public void OutcomeOf_EmptyList_IsNotPublished() =>
        // 没有待更新的年份 = 无事可做，安静 7 天；但它**不是**"本地已有数据"，
        // 所以不能被记成 Updated（那会让"数据不在"与"数据在"共用一个结局）。
        Assert.Equal(HolidayCheckOutcome.NotPublished, HolidayCheckThrottle.OutcomeOf([]));
}
