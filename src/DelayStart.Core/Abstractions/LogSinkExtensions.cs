namespace DelayStart.Core.Abstractions;

/// <summary>
/// <see cref="ILogSink"/> 的便捷调用名。
/// </summary>
/// <remarks>
/// 调用点写 <c>_log.Warn(ex, "…")</c> 比 <c>_log.Write(LogLevel.Warn, ex, "…")</c> 读起来
/// 更像在记日志而不是在调一个通用写入接口。这些方法刻意做成扩展方法而不是接口成员，
/// 理由见 <see cref="ILogSink"/> 的备注。
/// </remarks>
public static class LogSinkExtensions
{
    /// <summary>记录一条信息级日志。</summary>
    /// <param name="sink">日志接收端。</param>
    /// <param name="message">消息文本，中文。</param>
    public static void Info(this ILogSink sink, string message)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write(LogLevel.Info, message);
    }

    /// <summary>记录一条警告级日志。</summary>
    /// <param name="sink">日志接收端。</param>
    /// <param name="message">消息文本，中文。</param>
    public static void Warn(this ILogSink sink, string message)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write(LogLevel.Warn, message);
    }

    /// <summary>记录一条带异常的警告级日志。</summary>
    /// <param name="sink">日志接收端。</param>
    /// <param name="exception">异常对象，用于保留完整栈信息。</param>
    /// <param name="message">消息文本，中文。</param>
    public static void Warn(this ILogSink sink, Exception exception, string message)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write(LogLevel.Warn, exception, message);
    }

    /// <summary>记录一条错误级日志。</summary>
    /// <param name="sink">日志接收端。</param>
    /// <param name="message">消息文本，中文。</param>
    public static void Error(this ILogSink sink, string message)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write(LogLevel.Error, message);
    }

    /// <summary>记录一条带异常的错误级日志。</summary>
    /// <param name="sink">日志接收端。</param>
    /// <param name="exception">异常对象，用于保留完整栈信息。</param>
    /// <param name="message">消息文本，中文。</param>
    public static void Error(this ILogSink sink, Exception exception, string message)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Write(LogLevel.Error, exception, message);
    }
}
