using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Services;

namespace DelayStart.App.Services;

/// <summary>
/// 界面侧的「调度周期」目录：读周期表、算引用数、增删改，外加一份本地法定日历快照（FR-15）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由与 <see cref="CycleInfoProvider"/> 不同：那个是**判定**（今天跳不跳、包含哪些天），
/// 这个是**编辑**（新建 / 改名 / 改星期 / 删除 / 数引用）。两者都要求"周期表与日历来自同一份快照"，
/// 但一个给列表页用、一个给设置页与编辑器用，混在一起会让列表页白白拿一堆写操作入口。
/// </para>
/// <para>
/// 🔴 写操作一律转交 <see cref="ConfigEditService"/>（Management 层），这里不做落盘 ——
/// 引用完整性校验（被引用不可删）与日志都在那边，界面层重复一遍只会分叉。
/// </para>
/// <para>
/// 🔴 法定日历**只读本地**（NFR-x）：管理端也不在这个服务里联网。下载是设置页里
/// 用户点「立即更新」时由抓取服务做的事。
/// </para>
/// </remarks>
public sealed class CycleCatalogService
{
    private readonly IAppConfigStore _configStore;
    private readonly ConfigEditService _editor;
    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>进程内日历快照。首次读取后不再重复读盘 —— 设置页与各弹窗共用同一份。</summary>
    private HolidayCalendar? _calendar;

    private bool _calendarLoaded;

    /// <summary>构造周期目录服务。</summary>
    /// <param name="configStore">配置读写端（取周期表）。</param>
    /// <param name="editor">条目 / 周期编辑服务（唯一的落盘点）。</param>
    /// <param name="paths">路径服务（法定日历落点）。</param>
    /// <param name="log">日志接收端。</param>
    public CycleCatalogService(
        IAppConfigStore configStore,
        ConfigEditService editor,
        PathService paths,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _editor = editor;
        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// 本地法定日历；不可用时为 <see langword="null"/>（法定两档退化为星期近似）。
    /// </summary>
    /// <remarks>
    /// 首次访问读盘一次，之后进程内复用。用户刚点完「立即更新」想立刻看到新数据时，
    /// 调用 <see cref="InvalidateCalendar"/> 丢弃快照。
    /// </remarks>
    public HolidayCalendar? Calendar
    {
        get
        {
            if (!_calendarLoaded)
            {
                _calendar = ReadCalendar();
                _calendarLoaded = true;
            }

            return _calendar;
        }
    }

    /// <summary>丢弃日历快照，下次访问重读（下载完成后调用）。</summary>
    public void InvalidateCalendar()
    {
        _calendar = null;
        _calendarLoaded = false;
    }

    /// <summary>读配置里的自定义周期表（内置 5 档不在其中，它们由代码定义）。</summary>
    /// <returns>自定义周期列表；配置读不出来时返回空表。</returns>
    public IReadOnlyList<ScheduleCycle> LoadCycles()
    {
        try
        {
            return _configStore.Load().Cycles;
        }
        catch (StartupOperationException ex)
        {
            // 配置版本过新 / 坏损：这里只影响"我的周期"那一组的显示，
            // 不该让整个弹窗或设置页炸掉 —— 页面自己有更显眼的错误通道。
            _log.Error(ex, "读取周期表失败");
            return [];
        }
    }

    /// <summary>造一个判定用的信息提供者（今天 + 同一份日历 + 同一份周期表）。</summary>
    /// <param name="cycles">周期表；为 <see langword="null"/> 时现读一次。</param>
    /// <returns>信息提供者。</returns>
    /// <remarks>
    /// 「今天」在这里现取一次并**冻进提供者**：列表页与编辑器都要"本次刷新的今天"
    /// 这一个值，同屏两处各取一次时间会在午夜前后分叉（FR-15.5 的同一思路）。
    /// </remarks>
    public CycleInfoProvider CreateProvider(IReadOnlyList<ScheduleCycle>? cycles = null)
        => new(cycles ?? LoadCycles(), Calendar, DateOnly.FromDateTime(DateTime.Now));

    /// <summary>某个周期被多少个延时条目引用（FR-15.14 的置灰依据）。</summary>
    /// <param name="cycleId">周期 id。</param>
    /// <returns>引用它的条目数。</returns>
    public int CountReferences(string cycleId)
    {
        if (string.IsNullOrWhiteSpace(cycleId))
        {
            return 0;
        }

        try
        {
            return ScheduleCycleResolver.CountReferences(cycleId, _configStore.Load().Items);
        }
        catch (StartupOperationException ex)
        {
            _log.Error(ex, "统计周期引用数失败");
            return 0;
        }
    }

    /// <summary>除某个 id 之外的自定义周期名 —— 新建 / 改名面板做重名即时校验要用。</summary>
    /// <param name="exceptCycleId">要排除的周期 id（改名时是自己）；新建时传 <see langword="null"/>。</param>
    /// <returns>现有自定义周期的名称。</returns>
    /// <remarks>
    /// 内置五档的名称**不在这里返回**：它们属于"固有占用"，判据（<c>CycleNames.IsTaken</c>）
    /// 自己会算上，调用方不必也不该重复拼一份 —— 拼两份就会有漏一处的那天。
    /// </remarks>
    public IReadOnlyList<string> CustomCycleNames(string? exceptCycleId = null)
    {
        var names = new List<string>();
        foreach (var cycle in LoadCycles())
        {
            if (cycle is not null && !string.Equals(cycle.Id, exceptCycleId, StringComparison.Ordinal))
            {
                names.Add(cycle.Name);
            }
        }

        return names;
    }

    /// <summary>新建一个自定义周期（FR-15.10）。</summary>
    /// <param name="name">周期名。</param>
    /// <param name="days">星期集合。</param>
    /// <returns>建好的周期。</returns>
    /// <exception cref="ArgumentException">名为空或一天都没选（FR-15.20）。</exception>
    public ScheduleCycle Create(string name, WeekdaySet days) => _editor.AddCycle(name, days);

    /// <summary>修改一个自定义周期（FR-15.12：引用它的条目同步生效）。</summary>
    /// <param name="cycleId">周期 id。</param>
    /// <param name="name">新名称。</param>
    /// <param name="days">新的星期集合。</param>
    /// <returns>是否找到并修改。</returns>
    /// <exception cref="ArgumentException">名为空或一天都没选。</exception>
    public bool Update(string cycleId, string name, WeekdaySet days) => _editor.UpdateCycle(cycleId, name, days);

    /// <summary>删除一个自定义周期（FR-15.14：被引用时抛 <see cref="InvalidOperationException"/>）。</summary>
    /// <param name="cycleId">周期 id。</param>
    /// <returns>是否找到并删除。</returns>
    public bool Delete(string cycleId) => _editor.DeleteCycle(cycleId);

    private HolidayCalendar? ReadCalendar()
    {
        try
        {
            var result = HolidayCalendarStore.Load(_paths);
            foreach (var issue in result.Issues)
            {
                _log.Warn($"法定日历文件不可用：{issue.FilePath} —— {issue.Reason}");
            }

            return result.Calendar;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取法定日历失败，法定两档按星期规律近似判定");
            return null;
        }
    }
}
