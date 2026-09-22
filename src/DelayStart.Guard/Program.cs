using System.Globalization;
using System.Text;

using DelayStart.Core.Launch;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Models;
using DelayStart.Management.Services;
using DelayStart.Management.Sources;

namespace DelayStart.Guard;

/// <summary>
/// 守卫入口（D74）。由计划任务 <c>\DelayStartGuard</c> 拉起，跑完一次巡检即退出。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>唯一合法入口是计划任务</b>（<c>RunLevel = Highest</c>）。手动双击 exe 时进程以
/// 未提权令牌启动，这里会检测出来并静默退出（D78，2026-09-22 用户批复）——
/// 否则用户会得到第二条不受管理的巡检路径，而它读不到 HKLM 项、也改不动别人的计划任务，
/// 表现是"守卫生效了一半"，比完全不生效更难查。
/// </para>
/// <para>
/// 流程见 <c>docs/design.md</c> 11.2：入口自检 → 读配置 → 扫描 → 纠正 →
/// 新增 / 失效检测 → 更新基线 → 有变化才发系统通知，否则静默退出。
/// </para>
/// <para>
/// 🔴 <b>通报之后不等待任何人</b>（D79）：通知由系统持有，点击由 Shell 经
/// <c>delaystart:</c> 协议转给管理端。守卫发完就退出 —— 不再有"弹框挂着等 60 秒"
/// 那段历史，也就不会出现"上一轮还没收尾、下一轮已被 IgnoreNew 挡掉"。
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>单实例互斥名（与调度端 / 管理端同款做法）。</summary>
    private const string SingleInstanceMutexName = @"Local\DelayStart.Guard";

    /// <summary>守卫主入口。</summary>
    /// <returns>进程退出码：0 正常；1 巡检未完成。</returns>
    [STAThread]
    private static int Main()
    {
        // ── 第 0 步：入口自检 ────────────────────────────────────────────────
        // 顺序有讲究：先互斥再查提权。反过来会有一个很吵的场景 ——
        // 用户连点几次双击，每次都先写一行"未提权"日志。
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            GuardLog.Info("已有守卫实例在运行（单实例互斥命中），本次直接退出。");
            return 0;
        }

        if (!ElevationCheck.IsElevated())
        {
            GuardLog.Error("守卫未以管理员身份运行（疑似手动双击启动），自动退出。");
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += static (_, e) => GuardLog.Error(
            e.ExceptionObject as Exception ?? new InvalidOperationException("非 Exception 的未捕获错误"),
            "守卫发生未捕获异常");

        var paths = new PathService();
        paths.EnsureCreated();

        var log = GuardLog.Sink;
        var clock = SystemClock.Instance;
        var configStore = new ConfigService(paths, log, clock);

        // 来源实例走 Management 的统一工厂 —— 与我们一起的是管理端看到的那七个，
        // 尤其是 HklmWow（32 位视图），漏掉它就看不见 32 位项被写回。
        var sources = StartupSourceFactory.Create(new ShellLinkResolver(log), clock, log);
        var scanner = new ScanService(sources, configStore, log);
        var baselineStore = new GuardBaselineStore(paths, log, clock);
        var guard = new GuardService(scanner, configStore, baselineStore, sources, log);

        GuardRunReport report;
        try
        {
            report = guard.RunOnce();
        }
        catch (Exception ex)
        {
            GuardLog.Error(ex, "守卫巡检未完成");
            return 1;
        }

        if (report.GuardDisabled)
        {
            GuardLog.Info("守卫已关闭（GuardMode=Disabled），本次未执行巡检。");
            return 0;
        }

        WriteSummary(report);

        if (!report.HasNotifications)
        {
            // 静默退出：没有任何变化时不打扰用户，这是守卫的常态路径。
            return 0;
        }

        if (report.NotifyMode is GuardNotifyMode.Never)
        {
            // 通知策略（D80）：巡检与纠正照做，只是不打扰。这一行必须落盘 ——
            // 否则"策略为从不通知"与"本轮根本没跑"在日志里长得一模一样。
            GuardLog.Info("本轮有变化，但通知策略为「从不通知」，已跳过通报。");
            return 0;
        }

        Notify(report);
        return 0;
    }

    /// <summary>
    /// 写一行巡检汇总（每次运行都写）。
    /// </summary>
    /// <remarks>
    /// 这一行是守卫**唯一的可审计痕迹** —— 没有它，"跑了但没变化"与"根本没跑"无从区分。
    /// </remarks>
    private static void WriteSummary(GuardRunReport report)
    {
        var failedSources = report.Failures.Count == 0
            ? string.Empty
            : $" · 来源失败 {report.Failures.Count}（列表不完整）";

        GuardLog.Info(
            $"巡检完成：扫描 {report.ScannedCount} 项"
            + $" · 纠正 {report.Corrections.Count}（失败 {report.CorrectionFailureCount}）"
            + $" · 新增 {report.NewItems.Count}"
            + $" · 失效 {report.StaleItems.Count}{failedSources}");

        foreach (var failure in report.Failures)
        {
            GuardLog.Warn($"来源『{failure.DisplayName}』本次扫描失败：{failure.Message}");
        }
    }

    /// <summary>发系统通知（点击落到最该看的那个页面）。</summary>
    /// <param name="report">本次巡检结果。</param>
    private static void Notify(GuardRunReport report)
    {
        var paths = new PathService();
        var registration = new ShellRegistrationService(paths, GuardLog.Sink);

        // 先确保系统侧身份就绪（开始菜单快捷方式的 AUMID + delaystart 协议）。
        // 幂等且不抛；失败不影响下面的发送 —— 之前已经注册过的话照样能弹出来。
        _ = registration.EnsureRegistered();

        var target = ChooseTarget(report);
        var summary = BuildSummary(report);
        var detail = BuildDetail(report);

        if (GuardToast.TryShow(target, summary, detail))
        {
            GuardLog.Info($"已发送系统通知（点击落到『{target}』）。");
        }
    }

    /// <summary>第二行：计数概览。</summary>
    /// <param name="report">本次巡检结果。</param>
    /// <returns>形如 <c>新增 2 项 · 失效 1 项</c>。</returns>
    private static string BuildSummary(GuardRunReport report)
    {
        var parts = new List<string>(2);

        if (report.NewItems.Count > 0)
        {
            parts.Add($"新增 {Count(report.NewItems.Count)} 项");
        }

        if (report.StaleItems.Count > 0)
        {
            parts.Add($"失效 {Count(report.StaleItems.Count)} 项");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>第三行：具体条目名（超出折成"等 N 项"）。</summary>
    /// <param name="report">本次巡检结果。</param>
    /// <returns>形如 <c>新增：A、B　失效：C</c>；两组都空时为 <see cref="string.Empty"/>。</returns>
    private static string BuildDetail(GuardRunReport report)
    {
        var groups = new List<string>(2);

        var added = DescribeGroup("新增", report.NewItems.Select(static entry => entry.Name));
        if (added.Length > 0)
        {
            groups.Add(added);
        }

        var lost = DescribeGroup("失效", report.StaleItems.Select(static stale => stale.Item.Name));
        if (lost.Length > 0)
        {
            groups.Add(lost);
        }

        // 用全角空格分隔两组：Toast 正文是单行排版，半角空格在小字号下几乎看不出来。
        return string.Join('　', groups);
    }

    private static string DescribeGroup(string title, IEnumerable<string> names)
    {
        var all = names.ToList();
        if (all.Count == 0)
        {
            return string.Empty;
        }

        var shown = string.Join('、', all.Take(GuardToast.MaxNames));
        return all.Count > GuardToast.MaxNames
            ? $"{title}：{shown} 等 {Count(all.Count)} 项"
            : $"{title}：{shown}";
    }

    /// <summary>
    /// 选通知的落点：有新增就落到"第一个有新增的来源页"，否则落到「延时启动」页。
    /// </summary>
    /// <param name="report">本次巡检结果。</param>
    /// <returns>定位令牌，取值见 <see cref="UiNavigationTarget"/>。</returns>
    /// <remarks>
    /// <para>
    /// 新增项的展示顺序固定 注册表 &gt; 启动文件夹 &gt; 计划任务 &gt; UWP，
    /// 与「自启动项」各来源页在菜单里的顺序一致：用户看到的第一条就在列表最上方。
    /// </para>
    /// <para>
    /// 纯失效的情况落到「延时启动」页而不是某个来源页 —— 失效条目已经并入那一页
    /// （D81，2026-09-22 用户批复：「失效条目」不再是一个单独页面），
    /// 那里才是能对它们动手（删除 / 转为手动）的地方。
    /// </para>
    /// </remarks>
    private static string ChooseTarget(GuardRunReport report)
    {
        foreach (var source in new[]
                 {
                     StartupSource.Registry,
                     StartupSource.StartupFolder,
                     StartupSource.ScheduledTask,
                     StartupSource.Uwp,
                 })
        {
            if (report.NewItems.Any(entry => entry.Source == source))
            {
                return source switch
                {
                    StartupSource.Registry => UiNavigationTarget.Registry,
                    StartupSource.StartupFolder => UiNavigationTarget.StartupFolder,
                    StartupSource.ScheduledTask => UiNavigationTarget.ScheduledTask,
                    _ => UiNavigationTarget.Uwp,
                };
            }
        }

        return UiNavigationTarget.Delay;
    }

    private static string Count(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
