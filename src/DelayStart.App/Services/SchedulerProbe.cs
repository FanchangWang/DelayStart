using System.Diagnostics;

using DelayStart.App.ViewModels;
using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.App.Services;

/// <summary>
/// 调度端的手动启动与单实例探测（A4.2）。
/// </summary>
/// <remarks>
/// <para>
/// 单独成一个服务而不是塞进 ViewModel：它要读配置、算计划摘要、起进程，
/// 这些都与界面无关；而且它需要 <see cref="IAppConfigStore"/> 与一个日志端，
/// 让 ViewModel 多背两个依赖不划算。
/// </para>
/// <para>
/// 🔴 单实例互斥体名必须与调度端**逐字一致**（<c>Local\DelayStart.Scheduler</c>）。
/// 两边各写一个字面量的话，改了一边就等于没有互斥 —— 而症状是"点了立即运行，
/// 调度器秒退，看起来像点了没反应"，不报任何错。
/// </para>
/// </remarks>
public sealed class SchedulerProbe
{
    /// <summary>调度端的单实例互斥名（必须与调度端一致）。</summary>
    public const string SingleInstanceMutexName = @"Local\DelayStart.Scheduler";

    /// <summary>确认框摘要里最多列几个条目名。</summary>
    private const int SummaryNameLimit = 3;

    private readonly IAppConfigStore _configStore;
    private readonly PathService _paths;
    private readonly IClock _clock;

    /// <summary>构造。</summary>
    /// <param name="configStore">配置读取端（要算"本轮会启动几项"）。</param>
    /// <param name="paths">路径服务（取调度端 exe 位置）。</param>
    /// <param name="clock">时间源（调度计划判定要"今天"）。</param>
    public SchedulerProbe(IAppConfigStore configStore, PathService paths, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(clock);

        _configStore = configStore;
        _paths = paths;
        _clock = clock;
    }

    /// <summary>调度端是否已经在跑（互斥体拿不到时答 <see langword="false"/>）。</summary>
    /// <returns>已在跑为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 这里<b>只</b>用 <c>Mutex(..., out createdNew)</c> 探测，**不启动它**：
    /// 探测用的那把锁必须立刻 Dispose，否则会挡住调度端自己那次启动 ——
    /// 而症状极其难懂："点了立即运行，什么都没发生，日志里也没有启动记录"。
    /// <para>
    /// 拿不到互斥体（权限 / 名字被占）时答 <see langword="false"/>：
    /// 拿不到不等于有人在跑，而"以为有人在跑却不启动"是更坏的方向 ——
    /// 用户点了没反应且没有任何提示。
    /// </para>
    /// </remarks>
    public static bool IsSchedulerRunning()
    {
        try
        {
            using var probe = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out var createdNew);
            return !createdNew;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 生成确认框摘要：本轮将启动几项（列前几个名称）+ 最晚延时。
    /// </summary>
    /// <remarks>
    /// 🔴 这个摘要不是客套。用户点的是一个会**真的启动程序**的按钮，而"启动了哪些"
    /// 决定了他要不要先把某些程序关掉（尤其是带锁文件的应用）。只写"确定要运行吗？"
    /// 等于让他在不知情的情况下替自己做决定。
    /// <para>
    /// 判定走 Core 的 <see cref="SchedulePlan"/>，与调度端同一份 ——
    /// 摘要说 3 项、实际启动 5 项，那比不给摘要更糟。
    /// </para>
    /// </remarks>
    public string DescribeLaunch()
    {
        AppConfig config;
        try
        {
            config = _configStore.Load();
        }
        catch (StartupOperationException ex)
        {
            return $"无法读取配置，未能算出本轮将启动哪些项：{ex.Message}";
        }

        var today = DateOnly.FromDateTime(_clock.Now.LocalDateTime);
        var calendar = TryReadCalendar(today.Year);
        var outcome = SchedulePlan.BuildWithSkipped(config.Items, config.Cycles, today, calendar);
        var planned = outcome.Entries;

        if (planned.Count == 0)
        {
            return outcome.SkippedToday.Count == 0
                ? "当前没有启用中的延时条目，本轮不会启动任何程序。"
                : $"本轮没有条目进入启动计划（{outcome.SkippedToday.Count} 项今天不在周期内）。";
        }

        var names = string.Join("、", planned.Take(SummaryNameLimit).Select(static entry => entry.Item.Name));
        var suffix = planned.Count > SummaryNameLimit ? $" 等 {planned.Count} 项" : $"（共 {planned.Count} 项）";
        var latest = planned.Max(static entry => entry.Item.DelaySeconds);

        return $"本次将启动 {suffix}：{names}。最晚一项的延时是 {DisplayText.DelayOf(latest)}。";
    }

    /// <summary>启动调度端（不等待它退出）。</summary>
    /// <returns>进程是否起来了。</returns>
    /// <remarks>
    /// 🔴 只判断"进程是否成功创建"，不判断它是否**正确接管**：那要等它抢到单实例互斥体，
    /// 由 <see cref="IsSchedulerRunning"/> 的短时轮询去看。这里判不了就只能如实说
    /// "启动失败"，不能假装成功。
    /// <para>
    /// 管理端已是提权进程（<c>app.manifest</c> requireAdministrator，D20），
    /// 直接 <c>Process.Start</c> 就会继承提权令牌 —— 调度端必须是 High（D82）。
    /// 刻意<b>不</b>走中转器：那一路会降权（shell 令牌 / CPWT / uiAccess），
    /// 调度端恰恰不能降权。
    /// </para>
    /// </remarks>
    public bool Launch()
    {
        var exe = _paths.SchedulerExecutablePath;

        if (!File.Exists(exe))
        {
            return false;
        }

        // 不给 WorkingDirectory：调度端自己的所有路径都经 PathService 从系统目录解析，
        // 不依赖当前目录。
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
        return true;
    }

    /// <summary>读法定日历；读不到就当没有（降级方向由 <c>ScheduleRulePolicy</c> 负责）。</summary>
    /// <remarks>
    /// 🔴 本地文件读取**不产生任何网络请求**（硬约束 8）：这里只读
    /// <c>{LocalRoot}\holidays\*.json</c>，取哪一年由文件内容决定。
    /// </remarks>
    private HolidayCalendar? TryReadCalendar(int year)
    {
        try
        {
            var loaded = HolidayCalendarStore.Load(_paths);
            return loaded.Calendar.Covers(new DateOnly(year, 1, 1)) ? loaded.Calendar : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
