using System.Globalization;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Services;
using DelayStart.Management.Services;

using Microsoft.UI.Dispatching;

namespace DelayStart.App.Services;

/// <summary>
/// 管理端启动后的节假日数据自动检查（FR-15 / design.md FR-15「管理端呈现」）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **触发条件刻意收得很窄**，三条件缺一不可：
/// ① 开关打开（默认开）；② 当年数据不可用；③ 距上次检查已过节流期。
/// 于是正常机器上它一辈子只真正联网一次 —— 首次安装补齐当年数据；之后只有
/// 次年安排公布（每年约 11 月）而本地还没有时才再跑一次。
/// </para>
/// <para>
/// 🔴 **绝不抛异常、绝不弹窗**：它跑在启动路径上，任何失败都只是"今年仍然没有这份数据"，
/// 而那是可以接受的降级状态（法定两档退化为星期近似，界面上带 <c>≈</c>）。
/// 让一个可选的数据刷新把管理端拖住或打断，是明显不成比例的。
/// </para>
/// <para>
/// 🔴 **但"静默"不等于"悄悄"**（2026-09-23 真机反馈后的修正）。第一版把两件事一起做错了：
/// 失败也把时间戳留下（一次网络抖动 = 静默 7 天），而且全程不给界面任何信号 ——
/// 用户看到的是"开关开着、它什么也没做"，只能来问"是不是根本没实现"。
/// 现在：节流按结局分开（见 <see cref="HolidayCheckThrottle"/>），
/// 进度与结果一律上报到 <see cref="HolidayUpdateStatus"/>，成功时另发一条应用内通知。
/// </para>
/// </remarks>
public sealed class HolidayAutoCheckService
{
    private readonly IAppConfigStore _configStore;
    private readonly HolidayCalendarUpdateService _holidays;
    private readonly CycleCatalogService _cycles;
    private readonly PathService _paths;
    private readonly HolidayUpdateStatus _status;
    private readonly ToastService _toast;
    private readonly ILogSink _log;

    /// <summary>UI 线程调度器（构造在 UI 线程上）；解析不到时通知直接就地发出。</summary>
    private readonly DispatcherQueue? _dispatcher;

    /// <summary>单飞标志：>0 表示已有一轮在跑（并发进来的一律跳过）。</summary>
    /// <remarks>
    /// 用 <see cref="Interlocked"/> 而不是 <c>SemaphoreSlim</c>：这里要的语义是
    /// **"忙就跳过"**，不是"排队等待" —— 后进来的那一轮等前一轮跑完再打一遍网络毫无意义。
    /// 顺带避开 CA1001（可释放字段要求类型自身可释放，而一个进程级单例"谁来 Dispose"
    /// 本身就是个没有答案的问题）。
    /// </remarks>
    private int _running;

    /// <summary>构造自动检查服务。</summary>
    /// <param name="configStore">配置读写端（读开关）。</param>
    /// <param name="holidays">节假日抓取服务。</param>
    /// <param name="cycles">周期目录服务（下载成功后丢掉日历快照，让界面立刻用上新数据）。</param>
    /// <param name="paths">路径服务（检查记录文件）。</param>
    /// <param name="status">共享可见状态（进度与上次结果都往这里报）。</param>
    /// <param name="toast">应用内通知（后台补齐数据成功时说一声）。</param>
    /// <param name="log">日志接收端。</param>
    public HolidayAutoCheckService(
        IAppConfigStore configStore,
        HolidayCalendarUpdateService holidays,
        CycleCatalogService cycles,
        PathService paths,
        HolidayUpdateStatus status,
        ToastService toast,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(holidays);
        ArgumentNullException.ThrowIfNull(cycles);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(toast);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _holidays = holidays;
        _cycles = cycles;
        _paths = paths;
        _status = status;
        _toast = toast;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// 到期才检查一次。调用方可以 fire-and-forget（本方法不抛异常）。
    /// </summary>
    /// <returns>检查（或跳过）完成的 <see cref="Task"/>。</returns>
    public Task RunIfDueAsync() => RunAsync(ignoreInterval: false);

    /// <summary>
    /// 立刻检查一次，**不受节流限制**（但依然"本地有可用数据就不联网"）。
    /// </summary>
    /// <returns>检查完成的 <see cref="Task"/>。</returns>
    /// <remarks>
    /// 给"用户刚把开关打开"这个动作使用：打开开关是明确的人为意图，
    /// 他要的是"现在就生效"，而不是"等 7 天后的某个时刻"。这也是 NFR-x 允许的触发源 ——
    /// 用户动作，而不是定时器。
    /// </remarks>
    public Task RunNowAsync() => RunAsync(ignoreInterval: true);

    /// <summary>
    /// 「上次检查怎么样」的一句话说明（设置页「自动检查更新」卡片的副标题）。
    /// </summary>
    /// <returns>按当前状态最该说的那一句。</returns>
    /// <remarks>
    /// 🔴 三种状态各说各的，不能压成一句：**关着**（不会联网）/**已就绪**（不必联网）/
    /// **没数据**（联网过没有、结果如何）。用户问的"它到底跑没跑"，答案就在第三种里。
    /// </remarks>
    public string Describe()
    {
        if (!_configStore.Load().Settings.AutoCheckHolidayUpdates)
        {
            return "已关闭 —— 不会自动联网补数据；要补用上面的「补齐缺失年份」。";
        }

        var year = DateTime.Now.Year;
        if (_holidays.HasUsableData(year))
        {
            return $"{year} 年数据已就绪 —— 只有在数据缺失时才会自动联网。";
        }

        if (ReadRecord() is not { } last)
        {
            return $"{year} 年数据尚未下载 —— 当年数据缺失时会在启动后自动补一次。";
        }

        var at = last.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return last.Outcome switch
        {
            // 🔴 记录说"已获取"，可本地现在读不到它 —— 文件被改名 / 删除 / 损坏了。
            // 这是用户唯一能看到这条线索的地方（年份行只会说"未下载"），而它恰好解释了
            // "为什么开关是开的却一直没有动静"：见 HolidayCheckThrottle.IsDue 对 Updated 的处理。
            HolidayCheckOutcome.Updated =>
                $"上次自动检查（{at}）报告已获取 {year} 年数据，但本地现在读不到它（可能被改名或删除）—— 下次启动会自动重试。",
            HolidayCheckOutcome.NotPublished =>
                $"上次自动检查：{at} · {last.Message}（要立刻重来，点上面的「重新下载」）",
            _ => $"上次自动检查失败（{at}）：{last.Message} 稍后会自动重试。",
        };
    }

    private async Task RunAsync(bool ignoreInterval)
    {
        // 🔴 单飞：启动检查与"开关打开即跑一次"可能撞在一起。跑两轮没有意义，
        // 还白等一轮 15 秒 × 3 个地址的网络。忙的那一次直接返回（不排队）。
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!_configStore.Load().Settings.AutoCheckHolidayUpdates)
            {
                return;
            }

            var year = DateTime.Now.Year;

            // 本地已有可用数据 → 不联网（这正是"正常机器一辈子只跑一次"的落点），
            // 但也要把状态写清楚，否则用户没法区分"已就绪"与"坏了"。
            if (_holidays.HasUsableData(year))
            {
                _status.Report(Describe());
                return;
            }

            if (!ignoreInterval && !HolidayCheckThrottle.IsDue(ReadRecord(), DateTimeOffset.UtcNow, out var next))
            {
                _log.Info($"节假日数据自动检查跳过：距上次尝试不足最小间隔，{next.ToLocalTime():yyyy-MM-dd HH:mm} 之后才会再试。");
                _status.Report(Describe());
                return;
            }

            using (_status.Begin())
            {
                IReadOnlyList<HolidayUpdateResult> results;
                try
                {
                    results = await _holidays.UpdateAsync([year], force: false).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    // 服务本身把网络失败都转成结果对象了，走到这里说明是文件系统 / 取消这类意外。
                    _log.Error(ex, "节假日数据自动检查失败");
                    WriteRecord(new HolidayCheckRecord(DateTimeOffset.UtcNow, HolidayCheckOutcome.Failed, ex.Message));
                    _status.Report(Describe());
                    return;
                }

                var outcome = HolidayCheckThrottle.OutcomeOf(results);
                var message = Summarize(results);

                foreach (var result in results)
                {
                    _log.Info($"节假日数据自动检查（{result.Year}）：{result.Message}");
                }

                WriteRecord(new HolidayCheckRecord(DateTimeOffset.UtcNow, outcome, message));

                if (outcome == HolidayCheckOutcome.Updated)
                {
                    // 新数据要立刻对判定生效：丢掉日历快照（界面可能已经开着）。
                    _cycles.InvalidateCalendar();
                    Post(() => _toast.Show($"节假日数据已自动就绪：{message}"));
                }

                _status.Report(Describe());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error(ex, "节假日数据自动检查失败");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>把这一次的结果压成一句能进记录文件的话。</summary>
    private static string Summarize(IReadOnlyList<HolidayUpdateResult> results)
    {
        if (results.Count == 0)
        {
            return "没有需要更新的年份。";
        }

        // 只更新一年，所以拿第一条说话。条目数不在这里报 ——
        // 设置页的年份行本来就会写「33 个放假日 / 6 个调休补班日」，这里重复只是噪声。
        return results[0].Outcome switch
        {
            HolidayUpdateOutcome.Updated => $"已获取 {results[0].Year} 年数据。",
            HolidayUpdateOutcome.NotPublished => $"{results[0].Year} 年安排尚未公布（每年约 11 月）。",
            _ => results[0].Message,
        };
    }

    /// <summary>读检查记录；文件缺失 / 旧格式 / 读不出来一律返回 <see langword="null"/>。</summary>
    private HolidayCheckRecord? ReadRecord()
    {
        try
        {
            var path = _paths.HolidayCheckStampPath;
            if (!File.Exists(path))
            {
                return null;
            }

            return HolidayCheckRecord.TryParse(File.ReadAllText(path), out var record) ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>写检查记录（失败不影响流程 —— 只是下次会多查一遍）。</summary>
    private void WriteRecord(HolidayCheckRecord record)
    {
        try
        {
            Directory.CreateDirectory(_paths.HolidaysRoot);
            File.WriteAllText(_paths.HolidayCheckStampPath, record.Format());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"写节假日检查记录失败：{ex.Message}");
        }
    }

    /// <summary>在 UI 线程上执行（本方法会从线程池线程被调用，而通知面板是 UI）。</summary>
    private void Post(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }
}
