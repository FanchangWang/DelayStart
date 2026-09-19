using DelayStart.Core.Abstractions;

namespace DelayStart.Core.Tests.Fakes;

/// <summary>
/// 把日志记在内存里的测试替身，供断言"是否记了该记的日志"。
/// </summary>
internal sealed class FakeLogSink : ILogSink
{
    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

    /// <summary>已记录的条目数。</summary>
    public int Count => _entries.Count;

    /// <inheritdoc />
    public void Write(LogLevel level, string message) => _entries.Add((level, message, null));

    /// <inheritdoc />
    public void Write(LogLevel level, Exception exception, string message)
        => _entries.Add((level, message, exception));

    /// <summary>判断是否存在某级别、消息中包含指定片段的日志。</summary>
    /// <param name="level">日志级别。</param>
    /// <param name="fragment">消息片段。</param>
    /// <returns>命中则返回 <see langword="true"/>。</returns>
    public bool Contains(LogLevel level, string fragment)
        => _entries.Any(entry =>
            entry.Level == level
            && entry.Message.Contains(fragment, StringComparison.Ordinal));

    /// <summary>判断某级别下是否有随附异常对象的日志（用于确认异常没被丢掉栈）。</summary>
    /// <param name="level">日志级别。</param>
    /// <returns>存在则返回 <see langword="true"/>。</returns>
    public bool HasExceptionAt(LogLevel level)
        => _entries.Any(entry => entry.Level == level && entry.Exception is not null);
}
