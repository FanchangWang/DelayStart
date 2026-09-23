using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

using DelayStart.Core.Serialization;
using DelayStart.Core.Services;
using DelayStart.Management.Serialization;

namespace DelayStart.Management.Services;

/// <summary>
/// 一次年份更新的结果。
/// </summary>
/// <param name="Year">年份。</param>
/// <param name="Outcome">结局分类。</param>
/// <param name="Message">中文说明，直接进日志或设置页。</param>
public sealed record HolidayUpdateResult(int Year, HolidayUpdateOutcome Outcome, string Message);

/// <summary>
/// 更新某一年节假日数据的结局。
/// </summary>
/// <remarks>
/// 分这么细不是强迫症：每一种结局对应的用户动作都不一样 ——
/// 「尚未公布」只能等，「网络失败」可以重试或导入文件，「已更新」什么都不用做。
/// 把它们压成一个 bool，用户就只能看到"失败"，然后不知道该怎么办。
/// </remarks>
public enum HolidayUpdateOutcome
{
    /// <summary>已拉取并写入本地（或覆盖为同一份内容）。</summary>
    Updated,

    /// <summary>源上有这份文件，但内容是空的 —— 官方还没公布（典型：11 月之前拉次年）。</summary>
    NotPublished,

    /// <summary>三个下载地址都不可用（超时 / 代理拦截 / DNS 失败）。</summary>
    NetworkFailure,

    /// <summary>拿到了东西但不是能用的数据（JSON 坏 / 结构不符 / 条目太少）。</summary>
    InvalidContent,
}

/// <summary>
/// 节假日数据的抓取与归一化落盘（管理端职责）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **只有这里允许出现网络代码**（NFR-x）。调度端与守卫端必须离线可判定：
/// 它们是登录即跑的进程，判定输入只能来自本地磁盘上的东西。
/// 管理端相反 —— 它是人看着用的界面，网络失败可以当场告诉用户。
/// </para>
/// <para>
/// 地址依次降级（2026-09-23 用户指定）：GitHub 原始文件 → fastly.jsdelivr → cdn.jsdelivr。
/// 国内环境下 raw.githubusercontent.com 经常被拦或有极高的丢包率，
/// 所以它是"首选"而不是"唯一"；两个 CDN 是同一份数据的不同镜像，互为备份。
/// </para>
/// <para>
/// 🔴 本类**只负责"取"**，不负责"转" —— 归一化是
/// <see cref="HolidaySourceConverter"/> 的职责，设置页的「从文件导入」用的是同一个它。
/// 转的地方有两处而实现有一份，这是刻意的：用户在离线机器上导入的那份文件，
/// 和本类从上游拉的是**同一个 URL 上的同一份内容**。
/// </para>
/// <para>
/// 写盘前必过校验：**旧数据绝不被坏的新数据覆盖**（保留可用数据 &gt; 数据新鲜）。
/// 空 days（官方未发布）是这条规则最主要的适用情形。
/// </para>
/// </remarks>
public sealed class HolidayCalendarUpdateService
{
    /// <summary>holiday-cn 的三个候选地址，按顺序尝试（用户 2026-09-23 指定）。</summary>
    private static readonly string[] SourceUrls =
    [
        "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{0}.json",
        "https://fastly.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{0}.json",
        "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{0}.json",
    ];

    private const int TimeoutSeconds = 15;

    private readonly HttpClient _http;
    private readonly PathService _paths;

    /// <summary>构造更新服务。</summary>
    /// <param name="paths">路径服务（决定本地落点）。</param>
    /// <param name="http">HTTP 客户端；为 <see langword="null"/> 时用进程级默认实例。</param>
    public HolidayCalendarUpdateService(PathService paths, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _http = http ?? DefaultHttpClient.Instance;
    }

    /// <summary>
    /// 更新若干年份的数据，逐个独立处理（哪一年失败不影响其他年份）。
    /// </summary>
    /// <param name="years">待更新的年份。</param>
    /// <param name="force">
    /// 是否覆盖本地已有的数据。默认 <see langword="false"/>：
    /// 本地已有合法数据且来源相同的情形下跳过，避免每次打开设置页都白跑一趟网络。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐年的结果，顺序与入参一致。</returns>
    public async Task<IReadOnlyList<HolidayUpdateResult>> UpdateAsync(
        IEnumerable<int> years,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(years);

        var results = new List<HolidayUpdateResult>();
        foreach (var year in years)
        {
            results.Add(await UpdateYearAsync(year, force, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// 更新一个年份。
    /// </summary>
    /// <param name="year">年份。</param>
    /// <param name="force">是否覆盖本地已有数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该年的结果。</returns>
    public async Task<HolidayUpdateResult> UpdateYearAsync(
        int year,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (year < 2000 || year > 2999)
        {
            return new HolidayUpdateResult(year, HolidayUpdateOutcome.InvalidContent, $"年份 {year} 不在合法区间。");
        }

        if (!force && HasUsableData(year))
        {
            return new HolidayUpdateResult(year, HolidayUpdateOutcome.Updated, "本地已有该年数据，未重复下载。");
        }

        HolidayUpdateResult? lastFailure = null;
        foreach (var template in SourceUrls)
        {
            var url = string.Format(CultureInfo.InvariantCulture, template, year);

            string? json;
            try
            {
                json = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 外部没取消，那就是我们自己按 TimeoutSeconds 到点了。
                // 🔴 **必须 continue，不能 return**（2026-09-23 真机修的 bug）：
                // 原实现把超时当成终局，于是"降级链"在最常见的失败形态上根本不生效 ——
                // 本机实测 raw.githubusercontent.com 稳定 15 秒超时，而两个 jsdelivr 镜像
                // 1.1 / 1.5 秒就返回 200。首个地址超时直接 return，等于用户永远拿不到数据、
                // 却以为程序"没反应"。
                // 超时与"主动取消"要分开报告：前者可以去下一个地址，后者必须立刻退出。
                lastFailure = new HolidayUpdateResult(
                    year,
                    HolidayUpdateOutcome.NetworkFailure,
                    $"请求超时（{TimeoutSeconds} 秒）：{HostOf(url)}");
                continue;
            }
            catch (HttpRequestException ex)
            {
                lastFailure = new HolidayUpdateResult(
                    year,
                    HolidayUpdateOutcome.InvalidContent,
                    $"从 {HostOf(url)} 读取失败：{ex.Message}");
                continue;
            }

            if (json is null)
            {
                lastFailure = new HolidayUpdateResult(
                    year,
                    HolidayUpdateOutcome.NetworkFailure,
                    $"{HostOf(url)} 返回空内容。");
                continue;
            }

            var conversion = HolidaySourceConverter.Convert(json, expectedYear: year);

            if (conversion.Outcome == HolidayConversionOutcome.Ok)
            {
                return Write(year, conversion.Document!, url);
            }

            if (conversion.Outcome == HolidayConversionOutcome.Unrecognized)
            {
                // 认不出结构 —— CDN 拦截时返回的 HTML 错误页正好落在这里。
                // 这属于"传输层拿到的东西不对"，换下一个地址再试是有意义的。
                lastFailure = new HolidayUpdateResult(year, HolidayUpdateOutcome.InvalidContent, conversion.Message);
                continue;
            }

            // 尚未公布 / 内容不合格：三个地址是同一份内容，再试没有意义，直接把结局交出去。
            return new HolidayUpdateResult(
                year,
                conversion.Outcome == HolidayConversionOutcome.NotPublished
                    ? HolidayUpdateOutcome.NotPublished
                    : HolidayUpdateOutcome.InvalidContent,
                conversion.Message);
        }

        return lastFailure
            ?? new HolidayUpdateResult(year, HolidayUpdateOutcome.NetworkFailure, "全部下载地址均不可用。");
    }

    /// <summary>
    /// 把已通过校验的文档落盘。
    /// </summary>
    /// <param name="year">年份。</param>
    /// <param name="document">来自 <see cref="HolidaySourceConverter"/> 的归一化文档。</param>
    /// <param name="url">命中的地址（只用于日志与提示）。</param>
    /// <returns>该年的结果。</returns>
    private HolidayUpdateResult Write(int year, HolidayCalendarDocument document, string url)
    {
        try
        {
            HolidayCalendarStore.Write(_paths, document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new HolidayUpdateResult(
                year,
                HolidayUpdateOutcome.InvalidContent,
                $"写入本地数据失败：{ex.Message}");
        }

        return new HolidayUpdateResult(
            year,
            HolidayUpdateOutcome.Updated,
            string.Create(
                CultureInfo.InvariantCulture,
                $"已更新 {year} 年数据：{document.RestDays.Count} 个放假日 / {document.Workdays.Count} 个调休补班日（来源 {HostOf(url)}）。"));
    }

    /// <summary>取一次正文；返回 <see langword="null"/> 表示拿到空内容。正文的识别与转换交给 <see cref="HolidaySourceConverter"/>。</summary>
    private async Task<string?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? null : json;
    }

    /// <summary>
    /// 本地是否已有合法数据（不校验内容新鲜度，只问"能不能用"）。
    /// </summary>
    /// <param name="year">年份。</param>
    /// <returns>存在且通过校验时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 公开出来是给**启动期的自动检查**用的（<c>HolidayAutoCheckService</c>）：
    /// "当年数据能不能用"必须是同一份判据。各写一份的话，会出现自动检查认定"没有数据、
    /// 该联网"而下载端认定"有数据、跳过下载"这种自相矛盾的状态 ——
    /// 表现出来就是"每次启动都提示要下载、点了一无所获"。
    /// </remarks>
    public bool HasUsableData(int year)
    {
        var path = _paths.GetHolidayFilePath(year);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            var existing = JsonSerializer.Deserialize(json, HolidayJsonContext.Default.HolidayCalendarDocument);
            return existing is not null && HolidayCalendarStore.IsValid(existing);
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string HostOf(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    /// <summary>
    /// 进程级共享的 HTTP 客户端。
    /// </summary>
    /// <remarks>
    /// 静态复用的理由不是"节省一个对象"，而是**避免 TIME_WAIT 端口耗尽**：
    /// 每次 new 一个 <see cref="HttpClient"/> 会留下处于 TIME_WAIT 的连接，
    /// 短时间里连续请求几次就把端口池吃光（这是 HTTP 客户端的经典坑）。
    /// </remarks>
    private static class DefaultHttpClient
    {
        public static HttpClient Instance { get; } = Create();

        private static HttpClient Create()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DelayStart", "0.1"));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }
    }
}
