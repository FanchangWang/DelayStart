namespace DelayStart.Core.Abstractions;

/// <summary>
/// 日志接收端抽象。通过构造函数注入，**禁止在类内部直接 new 具体日志实现**
/// （<c>docs/coding-standards.md</c> 第九节）。
/// </summary>
/// <remarks>
/// <para>
/// 接口只暴露两个 <c>Write</c> 重载，级别用 <see cref="LogLevel"/> 传递；
/// <c>Info</c> / <c>Warn</c> / <c>Error</c> 这些顺手的名字由
/// <see cref="LogSinkExtensions"/> 以扩展方法提供。
/// </para>
/// <para>
/// 这样拆分的两个理由：一是接口成员不必叫 <c>Error</c>（它在 VB 里是保留字，
/// <c>CA1716</c> 会拦下），二是**增删级别不必改动接口**——实现方只需处理
/// <see cref="LogLevel"/> 的分支。
/// </para>
/// <para>
/// ⚠️ 记录异常时**必须**用带 <see cref="Exception"/> 的重载：只拼 <c>ex.Message</c>
/// 会丢掉栈，而栈才是排错依据。
/// </para>
/// </remarks>
public interface ILogSink
{
    /// <summary>写入一条日志。</summary>
    /// <param name="level">日志级别。</param>
    /// <param name="message">消息文本，中文。</param>
    void Write(LogLevel level, string message);

    /// <summary>写入一条带异常的日志。</summary>
    /// <param name="level">日志级别。</param>
    /// <param name="exception">异常对象，用于保留完整栈信息。</param>
    /// <param name="message">消息文本，中文。</param>
    void Write(LogLevel level, Exception exception, string message);
}
