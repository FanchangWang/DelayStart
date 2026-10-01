using System.ComponentModel;
using System.Diagnostics;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Launch;

/// <summary>
/// 枚举某个进程名下**当前正在运行**的进程，并取它们的可执行文件路径
/// （D138：调度端与管理端「单条启动」共用同一份）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么在 Core 而不是各自实现</b>：<see cref="DuplicateLaunchPolicy"/> 的判定输入是
/// <c>RunningProcessInfo</c> 列表，那份纯逻辑住在 Core；而**收集**这份输入的实现原先是
/// 调度端私有的静态方法。管理端「启动」按钮要用同一套"目标已在跑 ⇒ 跳过"判定时，
/// 就会面临"抄一份"或"下沉"两个选择 —— 抄一份正是 D128 之前
/// <c>DeElevatedProcessLauncher</c> 落到调度端私有的原因，而那份拷贝后来漂移了。
/// </para>
/// <para>
/// 🔴 <b>只比较可执行文件路径，不比进程名</b>：同一个 exe 装在两个目录、
/// 或者被复制成不同名字，它们都该被当成"同一个目标"。而进程名相同、路径不同的两个
/// 进程（<c>chrome.exe</c> 装在两处）不该互相跳过。
/// </para>
/// <para>
/// 取路径可能失败（权限不足 / 进程已退出 / 32 位进程在 64 位下），
/// 那种情况返回 <see langword="null"/> 并**跳过它** —— 拿不到路径就无从判断，
/// 而误判成"已在跑"会让一个正常程序永远启动不了。
/// </para>
/// </remarks>
public static class RunningProcessProbe
{
    /// <summary>枚举指定进程名下正在运行的进程。</summary>
    /// <param name="processName">进程名（不含扩展名）。</param>
    /// <returns>进程快照；<paramref name="processName"/> 为空时返回空列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processName"/> 为 <see langword="null"/>。</exception>
    public static IReadOnlyList<RunningProcessInfo> Collect(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);

        if (string.IsNullOrWhiteSpace(processName))
        {
            return [];
        }

        var snapshot = new List<RunningProcessInfo>();

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                string? modulePath = null;

                try
                {
                    modulePath = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is Win32Exception
                    or InvalidOperationException
                    or NotSupportedException)
                {
                    // 取不到路径就跳过这一条 —— 见类型注释里那句"误判成已在跑更糟"。
                }

                snapshot.Add(new RunningProcessInfo(process.ProcessName, modulePath));
            }
        }

        return snapshot;
    }

    /// <summary>某条目对应的目标是否已经在运行。</summary>
    /// <param name="item">要判定的条目。</param>
    /// <param name="log">日志接收端；可为 <see langword="null"/>（判定失败时就没有地方可记）。</param>
    /// <param name="reason">不通过时的中文原因，进 toast 与日志。</param>
    /// <returns>应当跳过为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 判定失败（枚举进程抛异常）时返回 <see langword="false"/>：宁可重复启动一次，
    /// 也不要让一个正常程序永远启动不了。方向与 D87/D90 一致。
    /// </remarks>
    public static bool IsAlreadyRunning(
        DelayedItem item,
        ILogSink? log,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(item);
        reason = string.Empty;

        var target = LaunchTargetResolver.Resolve(item);
        if (target is null)
        {
            return false;
        }

        IReadOnlyList<RunningProcessInfo> processes;
        try
        {
            processes = Collect(target.ProcessName);
        }
        catch (Exception ex)
        {
            log?.Warn(ex, $"『{item.Name}』防双启动判定失败，按未启动处理（宁可重复启动一次）");
            return false;
        }

        if (!DuplicateLaunchPolicy.Decide(target, processes).Skip)
        {
            return false;
        }

        reason = "程序已在运行，未重复启动";
        return true;
    }
}
