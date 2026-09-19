namespace DelayStart.Core.Abstractions;

/// <summary>
/// 日志级别。
/// </summary>
/// <remarks>
/// 只保留三档，刻意不提供 <c>Debug</c>/<c>Trace</c>：本项目的日志面向"事后排查真机问题"，
/// 而正式发布版不可能打开一个能刷爆 2 MB 上限的冗长级别。需要调试细节时用调试器，
/// 不要让日志成为需要开关的东西。
/// </remarks>
public enum LogLevel
{
    /// <summary>正常流程里程碑。</summary>
    Info,

    /// <summary>可继续但需要人看一眼：单条目读取失败、降权回退等（FR-1.4 / E6）。</summary>
    Warn,

    /// <summary>当前操作失败。</summary>
    Error,
}
