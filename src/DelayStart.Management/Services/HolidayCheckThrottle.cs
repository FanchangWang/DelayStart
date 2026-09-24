using System.Globalization;

namespace DelayStart.Management.Services;

/// <summary>
/// 一次自动检查的结局分类。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **分三档而不是两档**（2026-09-23 第二次真机反馈后的修正）。
/// 第一版把「已获取」与「上游未公布」合并成一个 <c>Succeeded</c>，于是节流回答不了一个
/// 关键问题：**记录说数据已经写好了，可现在它并不在**。那种状态下"成功"的 7 天安静期
/// 就是彻底的静默 —— 用户看到开关是开的、本地却没有数据，而且再没有人会去补，
/// 表现与"功能根本没实现"完全一样（真机上就是这么发生的：下载确实成功过，
/// 那份文件后来在本地被改了名，于是自动检查按"成功"记账、直到 7 天后才再试）。
/// </para>
/// <para>
/// 判据与 <see cref="HolidayUpdateOutcome"/> 的四档不是一一对应：
/// 「尚未公布」对用户要单独提示（等 11 月），对节流则等价于"不必马上再试" ——
/// 三个地址拉到的都是同一份空内容，一小时后再拉还是空的。
/// </para>
/// </remarks>
public enum HolidayCheckOutcome
{
    /// <summary>拿到了数据并写进本地（或本地已有可用数据、连网都不必）。</summary>
    Updated,

    /// <summary>上游明确回复「尚未公布」—— 等下一个窗口，不必马上再试。</summary>
    NotPublished,

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
/// 落盘成**三行纯文本**（时间 / 结局 / 文案），不是 JSON：
/// 这份文件要在启动路径上读写，用 <c>JsonDocument</c> 之外的任何反序列化器都会给
/// AOT / 裁剪引入新的注意事项，而它只有三个字段、只被本程序读写。
/// </para>
/// <para>
/// 🔴 **读取必须向后兼容两代格式**：
/// ① 最初（2026-09-23 之前）写的是**单行 ISO 时间戳**，没有结局行 —— 按"失败"处理，
/// 宁可多查一次，也不要让升级上来的机器把"上次那次失败"当成"上周查过了"再静默 7 天；
/// ② 中间那代的结局行是 <c>ok</c> / <c>fail</c>，其中 <c>ok</c> 把「已获取」与
/// 「未公布」混在一起 —— 按「已获取」读（理由见 <see cref="TryParse"/>）。
/// </para>
/// </remarks>
public readonly record struct HolidayCheckRecord(DateTimeOffset At, HolidayCheckOutcome Outcome, string Message)
{
    /// <summary>拿到数据并落盘。</summary>
    private const string UpdatedFlag = "updated";

    /// <summary>上游明确说还没公布。</summary>
    private const string NotPublishedFlag = "nopublish";

    private const string FailedFlag = "fail";

    /// <summary>序列化成三行文本。</summary>
    /// <returns>可写入文件的文本。</returns>
    /// <remarks>文案里的换行压成空格：文件格式是"一行一个字段"，多行文案会把解析带偏。</remarks>
    public string Format() => string.Join(
        '\n',
        At.ToString("O", CultureInfo.InvariantCulture),
        Outcome switch
        {
            HolidayCheckOutcome.Updated => UpdatedFlag,
            HolidayCheckOutcome.NotPublished => NotPublishedFlag,
            _ => FailedFlag,
        },
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

        // 没有结局行（更早的格式 / 写了一半）→ 按失败处理：下一次启动就重试。
        var outcome = HolidayCheckOutcome.Failed;
        var messageStart = 1;

        if (lines.Length > 1)
        {
            var flag = lines[1].Trim();
            if (flag.Equals(UpdatedFlag, StringComparison.OrdinalIgnoreCase))
            {
                outcome = HolidayCheckOutcome.Updated;
                messageStart = 2;
            }
            else if (flag.Equals(NotPublishedFlag, StringComparison.OrdinalIgnoreCase))
            {
                outcome = HolidayCheckOutcome.NotPublished;
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
/// 第二版按结局分开，却仍然只有"成功 / 失败"两档 —— 于是第二种静默出现了：
/// **下载成功过、那份文件后来在本地被改名（或被删、被损坏）**，节流拿"成功"记账，
/// 自动检查再也不会去补，界面上的年份行则显示"未下载"。现在三档：
/// 拿到数据（<see cref="HolidayCheckOutcome.Updated"/>）本来就不该再联网，
/// 一旦发现本地没有可用数据就立即重来；上游明说未公布
/// （<see cref="HolidayCheckOutcome.NotPublished"/>）安静 7 天；失败只安静一小时 ——
/// 足以避开"反复启动反复打网络"，又不至于让一次网络抖动否决一个星期的功能。
/// </para>
/// </remarks>
public static class HolidayCheckThrottle
{
    /// <summary>上一次**有了结论**（拿到数据 / 上游未公布）之后的安静期。</summary>
    /// <remarks>
    /// 7 天的依据：官方数据一年只更新几次（每年约 11 月公布次年安排），
    /// 而"当年数据已经就绪"之后本来就不会再联网。实际生效的是「未公布」那一支 ——
    /// 「已获取」会因为本地数据必然还在而在调用点就被拦下（见 <see cref="IsDue"/>）。
    /// </remarks>
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

        var interval = last.Outcome switch
        {
            HolidayCheckOutcome.Failed => FailureRetryInterval,
            HolidayCheckOutcome.NotPublished => SuccessInterval,

            // 🔴 Updated 走到这里，说明**"记录说数据已写盘，可现在它不在"**：
            // 调用方只在本地没有可用数据时才问节流（有数据那一步就 return 了）。
            // 文件被改名 / 被删 / 坏了都属于这种状态 —— 它不是"刚查过"，
            // 没有等 7 天的理由；唯一能自愈的动作就是立刻再拉一次。
            _ => TimeSpan.Zero,
        };

        nextAttemptAt = last.At + interval;
        return now >= nextAttemptAt;
    }

    /// <summary>
    /// 把一组更新结果归成节流用的三档。
    /// </summary>
    /// <param name="results">这一次的逐年结果。</param>
    /// <returns>只要有一年没成，就算失败（下次早点再试）。</returns>
    public static HolidayCheckOutcome OutcomeOf(IReadOnlyList<HolidayUpdateResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var anyUpdated = false;
        foreach (var result in results)
        {
            if (result.Outcome is HolidayUpdateOutcome.NetworkFailure or HolidayUpdateOutcome.InvalidContent)
            {
                return HolidayCheckOutcome.Failed;
            }

            anyUpdated |= result.Outcome == HolidayUpdateOutcome.Updated;
        }

        // 一年都没更新（全部"尚未公布"，或压根没有待更新年份）→ 安静期，
        // 但**不是** Updated：本地仍然没有数据，哪天文件出现了也不该被这句话误判成"已就绪"。
        return anyUpdated ? HolidayCheckOutcome.Updated : HolidayCheckOutcome.NotPublished;
    }
}
