using DelayStart.Core.Abstractions;
using DelayStart.Core.Logging;
using DelayStart.Core.Services;

namespace DelayStart.Scheduler;

/// <summary>
/// 调度端入口点。
/// </summary>
/// <remarks>
/// <para>
/// <b>技术选型（D24 批复 B，2026-09-19）：纯 Win32，不引用任何 UI 框架。</b>
/// 托盘图标用 <c>Shell_NotifyIcon</c>，点击弹出的面板用自绘无边框窗口，全部走 P/Invoke。
/// </para>
/// <para>
/// 进程被计划任务在登录时以交互用户身份启动（<c>LogonType=Interactive</c> +
/// <c>RunLevel=Highest</c>，🔴 禁止 SYSTEM —— NFR-6.8）。
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>单实例互斥名。重复启动（用户手动双击 + 计划任务并发）时后者静默退出。</summary>
    private const string SingleInstanceMutexName = @"Local\DelayStart.Scheduler";

    /// <summary>
    /// 调度端主入口。
    /// </summary>
    /// <returns>进程退出码。<c>0</c> 表示本次调度流程正常结束。</returns>
    [STAThread]
    private static int Main()
    {
        using var mutex = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            return 0; // 已有调度在跑：直接退出，不弹任何东西
        }

        var paths = new PathService();
        paths.EnsureCreated();

        var log = new FileLogger(
            paths.SchedulerLogPath,
            "Scheduler",
            SystemClock.Instance);

        var engine = new SchedulerEngine(
            new ConfigService(paths, log, SystemClock.Instance),
            new RunStateService(paths, log),
            new DeElevatedProcessLauncher(log),
            paths,
            log);

        try
        {
            return engine.Run();
        }
        catch (Exception ex)
        {
            // 兜底：任何未捕获异常都不得让调度端带着弹窗崩溃（登录瞬间无人值守）。
            log.Error(ex, "调度引擎发生未处理异常。");
            return 1;
        }
    }
}
