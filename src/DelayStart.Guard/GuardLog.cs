using DelayStart.Core.Abstractions;
using DelayStart.Core.Logging;
using DelayStart.Core.Services;

namespace DelayStart.Guard;

/// <summary>
/// 守卫的日志入口（D74）：<c>%LOCALAPPDATA%\DelayStart\logs\guard.log</c>。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **为什么守卫必须写日志**：它是周期任务，绝大多数运行是静默的 ——
/// "跑了但没做任何事"和"根本没跑"在用户眼里完全一样。一次巡检至少留一行汇总
/// （扫描 N 项 / 纠正 M 项 / 新增 K 项 / 孤儿 J 项），否则这个功能完全不可审计。
/// </para>
/// <para>
/// 用静态入口而不是构造函数注入：本进程只有一条直线流程（自检 → 巡检 → 通报），
/// 而通报链路上的若干处（系统通知失败、来源失败）需要"随手记一笔"，
/// 为此把 <c>ILogSink</c> 一路透传下去只会让签名变长。守卫是单进程、单次运行、无并发，
/// 静态实例不会带来生命周期问题。
/// </para>
/// </remarks>
internal static class GuardLog
{
    private static ILogSink? _sink;

    /// <summary>日志接收端（首次访问时按默认路径建立）。</summary>
    public static ILogSink Sink => _sink ??= CreateSink();

    /// <summary>记一条信息。</summary>
    /// <param name="message">内容。</param>
    public static void Info(string message) => Sink.Info(message);

    /// <summary>记一条警告。</summary>
    /// <param name="message">内容。</param>
    public static void Warn(string message) => Sink.Warn(message);

    /// <summary>记一条警告（带异常栈）。</summary>
    /// <param name="exception">异常。</param>
    /// <param name="message">内容。</param>
    public static void Warn(Exception exception, string message) => Sink.Warn(exception, message);

    /// <summary>记一条错误（带异常栈）。</summary>
    /// <param name="exception">异常。</param>
    /// <param name="message">内容。</param>
    public static void Error(Exception exception, string message) => Sink.Error(exception, message);

    /// <summary>记一条错误。</summary>
    /// <param name="message">内容。</param>
    public static void Error(string message) => Sink.Error(message);

    private static FileLogger CreateSink()
    {
        var paths = new PathService();
        paths.EnsureCreated();

        return new FileLogger(paths.GuardLogPath, "Guard", SystemClock.Instance);
    }
}
