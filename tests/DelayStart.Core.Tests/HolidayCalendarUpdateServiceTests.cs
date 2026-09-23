using System.Net;
using System.Text;

using DelayStart.Core.Serialization;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="HolidayCalendarUpdateService"/> 的地址降级链测试（FR-15 / §6.5）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这一层被测的原因是 2026-09-23 的一次真机事故：首个地址
/// <c>raw.githubusercontent.com</c> 在国内稳稳定 15 秒超时（本机实测两个 jsdelivr 镜像
/// 1.1 / 1.5 秒就返回 200），而原实现把**超时当成终局**直接 <c>return</c> ——
/// 所谓"三地址依次降级"在最常见的失败形态上完全不生效：用户拿到的是
/// "开关是开的，但什么都没有"。
/// </para>
/// <para>
/// 用假的 <see cref="HttpMessageHandler"/> 而不是真网络：单测不能依赖外部服务
/// （那个超时要等 15 秒，而且它是不是超时取决于当天的网络）。
/// </para>
/// </remarks>
public sealed class HolidayCalendarUpdateServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    private PathService Paths => new(_temp.Combine("local"), _temp.Combine("config"));

    [Fact]
    public async Task FirstAddressTimingOut_FallsThroughToTheNextAddress()
    {
        var handler = new StubHandler(url => IsGitHub(url) ? null : Raw2026());
        var paths = Paths;
        var service = new HolidayCalendarUpdateService(paths, new HttpClient(handler));

        var result = await service.UpdateYearAsync(2026, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HolidayUpdateOutcome.Updated, result.Outcome);

        // 关键断言：超时之后**继续试了第二个地址**，而不是就此收手。
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("raw.githubusercontent.com", handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("fastly.jsdelivr.net", handler.Requests[1], StringComparison.Ordinal);

        // 数据真的落到了本地（"降级成功"不许只是返回值好看）。
        Assert.True(service.HasUsableData(2026));
    }

    [Fact]
    public async Task HtmlErrorPage_FallsThroughToTheNextAddress()
    {
        // CDN 被拦时常常返回 200 + 一页 HTML。它属于"拿到了东西但不是数据"，
        // 换下一个地址是有意义的 —— 与"三个地址是同一份内容"（尚未公布）要分开处理。
        var handler = new StubHandler(url => IsGitHub(url) ? "<html>Service Unavailable</html>" : Raw2026());
        var service = new HolidayCalendarUpdateService(Paths, new HttpClient(handler));

        var result = await service.UpdateYearAsync(2026, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HolidayUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AllAddressesTimingOut_ReportsNetworkFailure()
    {
        var handler = new StubHandler(_ => null);
        var service = new HolidayCalendarUpdateService(Paths, new HttpClient(handler));

        var result = await service.UpdateYearAsync(2026, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HolidayUpdateOutcome.NetworkFailure, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);   // 三个地址都试过了
        Assert.Contains("超时", result.Message, StringComparison.Ordinal);
        Assert.False(service.HasUsableData(2026));
    }

    [Fact]
    public async Task LocalDataPresent_SkipsNetworkEntirely()
    {
        // 自动检查在启动路径上天天问这个问题，所以"本地有数据就别联网"必须是硬行为。
        var paths = Paths;
        HolidayCalendarStore.Write(paths, Document());

        var handler = new StubHandler(_ => Raw2026());
        var service = new HolidayCalendarUpdateService(paths, new HttpClient(handler));

        var result = await service.UpdateYearAsync(2026, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HolidayUpdateOutcome.Updated, result.Outcome);
        Assert.Empty(handler.Requests);
        Assert.Contains("未重复下载", result.Message, StringComparison.Ordinal);
    }

    private static bool IsGitHub(string url) => url.Contains("raw.githubusercontent.com", StringComparison.Ordinal);

    private static HolidayCalendarDocument Document() => new()
    {
        Year = 2026,
        Region = "CN",
        Source = "holiday-cn",
        Workdays = [.. RealCalendar2026.Workdays],
        RestDays = [.. RealCalendar2026.RestDays],
    };

    /// <summary>上游 2026.json 的等价内容（结构同 holiday-cn：顶层 year + days）。</summary>
    private static string Raw2026()
    {
        var builder = new StringBuilder();
        builder.Append("""{ "year": 2026, "days": [""");

        var first = true;
        foreach (var (date, isOffDay) in RealCalendar2026.RestDays
            .Select(static date => (Date: date, IsOffDay: true))
            .Concat(RealCalendar2026.Workdays.Select(static date => (Date: date, IsOffDay: false))))
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append(Day(date, isOffDay));
            first = false;
        }

        builder.Append("] }");
        return builder.ToString();
    }

    /// <summary>一条 <c>days</c> 记录。</summary>
    /// <remarks>
    /// 🔴 用 <c>$$"""…"""</c>（两个 <c>$</c>）而不是 <c>$"""…"""</c>：
    /// 单 <c>$</c> 下 <c>{</c> 一律开启插值，想写字面量花括号得靠 <c>$$</c> 把
    /// 插值定界符升格成 <c>{{ }}</c> —— 手写 JSON 时这一点最容易踩（CS9006）。
    /// </remarks>
    private static string Day(string date, bool isOffDay)
        => $$"""{"name":"节日","date":"{{date}}","isOffDay":{{(isOffDay ? "true" : "false")}}}""";

    /// <summary>
    /// 可编程的假 HTTP 处理器。
    /// </summary>
    /// <remarks>
    /// 应答函数返回 <see langword="null"/> 表示**模拟超时** ——
    /// 抛 <see cref="TaskCanceledException"/> 并且外层令牌没有取消，
    /// 正是 <c>FetchAsync</c> 内部那个"自己的 15 秒到点了"判别式要识别的形态。
    /// </remarks>
    private sealed class StubHandler(Func<string, string?> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            if (respond(url) is not { } body)
            {
                throw new TaskCanceledException("模拟 15 秒超时");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
