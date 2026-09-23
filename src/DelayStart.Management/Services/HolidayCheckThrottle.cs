using System.Globalization;

namespace DelayStart.Management.Services;

/// <summary>
/// 一次自动检查的结局分类。
/// </summary>
/// <remarks>
/// 🔴 只分两类，因为**判据只有这两个分支**：值得尽快再试 / 不必再试。
/// 这里刻意不复用 <see cref="HolidayUpdateOutcome"/> 的四档 ——
/// 「尚未公布」对用户要区分（提示他等 11 月），但对节流而言与"已更新"完全等价：
/// 三个地址拉到的都是同一份空内容，一小时后再拉一遍还是空的。
/// </remarks>
public enum HolidayCheckOutcome
{
    /// <summary>拿到了可用数据，或上游明确回复「尚未公布」—— 两者都不需要再试。</summary>
    Succeeded,

    /// <summary>网络或内容出了问题 —— 换个时间很可能就好了，要尽快重试。</summary>
    Failed,
}

/// <summary>
/// 「上一次自动检查」的记录：什么时候查的、结果如何、说了什么。
/// </summary>
/// <param name="At">这次尝试的时间（UTC）。</param>
/// <param name="Outcome">结局分类。</param>
/// <param name="Message">给用户看的一句话。</param>
/// <remarks>
/// <para>
/// 落盘成**三行纯文本**（时间 / ok 或 fail / 文案），不是 JSON：
/// 这份文件要在启动路径上读写，用 <c>JsonDocument</c> 之外的任何反序列化器都会给
/// AOT / 裁剪引入新的注意事项，而它只有三个字段、只被本程序读写。
/// </para>
/// <para>
/// 🔴 **读取必须向后兼容**：旧版本（2026-09-23 之前）写的是**单行 ISO 时间戳**，没有结局行。
/// 这种记录按 <see cref="HolidayCheckOutcome.Failed"/> 处理 —— 宁可多查一次，
/// 也不要让升级上来的机器把"上次那次失败"当成"上周查过了"再静默等 7 天。
/// </para>
/// </remarks>
public readonly record struct HolidayCheckRecord(DateTimeOffset At, HolidayCheckOutcome Outcome, string Message)
{
    private const string SucceededFlag = "ok";

    private const string FailedFlag = "fail";

    /// <summary>序列化成三行文本。</summary>
    /// <returns>可写入文件的文本。</returns>
    /// <remarks>文案里的换行压成空格：文件格式是"一行一个字段"，多行文案会把解析带偏。</remarks>
    public string Format() => string.Join(
        '\n',
        At.ToString("O", CultureInfo.InvariantCulture),
        Outcome == HolidayCheckOutcome.Succeeded ? SucceededFlag : FailedFlag,
        Message.Replace('\r', ' ').Replace('\n', ' ').Trim());

    /// <summary>
    /// 解析记录。
    /// </summary>
    /// <param name="text">文件内容。</param>
    /// <param name="record">解析结果。</param>
    /// <returns>能解析出至少一个合法时间戳时为 <see langword="true"/>。</returns>
    public static bool TryParse(string? text, out HolidayCheckRecord record)
    {
        record = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        if (!DateTimeOffset.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return false;
        }

        // 没有结局行（旧格式 / 写了一半）→ 按失败处理：下一次启动就重试。
        var outcome = HolidayCheckOutcome.Failed;
        var messageStart = 1;

        if (lines.Length > 1)
        {
            var flag = lines[1].Trim();
            if (flag.Equals(SucceededFlag, StringComparison.OrdinalIgnoreCase))
            {
                outcome = HolidayCheckOutcome.Succeeded;
                messageStart = 2;
            }
            else if (flag.Equals(FailedFlag, StringComparison.OrdinalIgnoreCase))
            {
                messageStart = 2;
            }
        }

        var parts = new List<string>(lines.Length);
        for (var i = messageStart; i < lines.Length; i++)
        {
            var part = lines[i].Trim();
            if (part.Length > 0)
            {
                parts.Add(part);
            }
        }

        record = new HolidayCheckRecord(at, outcome, string.Join(" ", parts));
        return true;
    }
}

/// <summary>
/// 「现在该不该再去查一次节假日数据」的判据（FR-15 / design.md FR-15「管理端呈现」）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **节流的对象是"重复的无效开销"，不是"用户想要的功能"。** 2026-09-23 真机暴露的
/// 第一版把这两件事混了：它在**发请求之前**先落时间戳，于是首次安装那次失败
/// （国内访问 raw.githubusercontent.com 超时 —— 本机实测 15 秒超时，两个 jsdelivr 镜像
/// 1.1 / 1.5 秒就返回 200）把用户锁在 7 天之外，而且**界面上一个字都没有**：
/// 用户看到的是"开关是开的，但它什么也没做"。
/// </para>
/// <para>
/// 现在的规则按**结局**分开：拿到数据（或上游明说没公布）才配享 7 天安静期；
/// 失败只安静一小时 —— 足以避开"反复启动反复打网络"，又不至于让一次网络抖动
/// 否决一个星期的功能。
/// </para>
/// </remarks>
public static class HolidayCheckThrottle
{
    /// <summary>成功之后的安静期。</summary>
    /// <remarks>7 天的依据：官方数据一年只更新几次（每年约 11 月公布次年安排），
    /// 而"当年数据已经就绪"之后本来就不会再联网。</remarks>
    public static readonly TimeSpan SuccessInterval = TimeSpan.FromDays(7);

    /// <summary>失败之后的安静期。</summary>
    /// <remarks>一小时 —— 比一次会话长，比"一天开三次机器"短。</remarks>
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// 现在是否到期。
    /// </summary>
    /// <param name="record">上一次的记录；从没查过时为 <see langword="null"/>。</param>
    /// <param name="now">当前时间（UTC）。</param>
    /// <param name="nextAttemptAt">最早可以再试的时间。</param>
    /// <returns>该查时为 <see langword="true"/>。</returns>
    public static bool IsDue(HolidayCheckRecord? record, DateTimeOffset now, out DateTimeOffset nextAttemptAt)
    {
        if (record is not { } last)
        {
            nextAttemptAt = now;
            return true;
        }

        var interval = last.Outcome == HolidayCheckOutcome.Succeeded ? SuccessInterval : FailureRetryInterval;
        nextAttemptAt = last.At + interval;
        return now >= nextAttemptAt;
    }

    /// <summary>
    /// 把一组更新结果归成节流用的两类。
    /// </summary>
    /// <param name="results">这一次的逐年结果。</param>
    /// <returns>只要有一年没成，就算失败（下次早点再试）。</returns>
    public static HolidayCheckOutcome OutcomeOf(IReadOnlyList<HolidayUpdateResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        foreach (var result in results)
        {
            if (result.Outcome is HolidayUpdateOutcome.NetworkFailure or HolidayUpdateOutcome.InvalidContent)
            {
                return HolidayCheckOutcome.Failed;
            }
        }

        return HolidayCheckOutcome.Succeeded;
    }
}
