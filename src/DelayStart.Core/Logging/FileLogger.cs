using System.Diagnostics;
using System.Globalization;
using System.Text;

using DelayStart.Core.Abstractions;

namespace DelayStart.Core.Logging;

/// <summary>
/// 按大小滚动的文本日志（§8.2）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意**不引入第三方日志库**：调度端是 NativeAOT 发布、体积目标是 6 MB
/// （NFR-1.2），为几十行写入逻辑背一个日志框架不划算，而且第三方库的 AOT 兼容性
/// 每升一次版都要重新验证。
/// </para>
/// <para>
/// 行格式：<c>2026-09-19 08:41:12.345 [INF] [Scheduler] 正在启动 微信（延时 30s）</c>。
/// 时间戳用 <see cref="IClock"/> 而非 <c>DateTime.Now</c>，以便测试可控。
/// </para>
/// <para>
/// 滚动策略：单文件超过 <see cref="DefaultMaxBytes"/> 时
/// <c>scheduler.log → scheduler.1.log → scheduler.2.log</c>，保留 <see cref="DefaultRetainedFileCount"/> 份历史。
/// </para>
/// </remarks>
public sealed class FileLogger : ILogSink
{
    /// <summary>单个日志文件的默认上限：2 MB。</summary>
    public const long DefaultMaxBytes = 2 * 1024 * 1024;

    /// <summary>默认保留的历史文件份数（不含当前文件）。</summary>
    public const int DefaultRetainedFileCount = 2;

    /// <summary>信息级别的行内标记。</summary>
    private const string InfoLevel = "INF";

    /// <summary>警告级别的行内标记。</summary>
    private const string WarnLevel = "WRN";

    /// <summary>错误级别的行内标记。</summary>
    private const string ErrorLevel = "ERR";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _filePath;
    private readonly string _source;
    private readonly IClock _clock;
    private readonly long _maxBytes;
    private readonly int _retainedFileCount;
    private readonly object _gate = new();

    /// <summary>构造文件日志。</summary>
    /// <param name="filePath">日志文件完整路径，目录不存在时自动创建。</param>
    /// <param name="source">日志来源标记，出现在每行的 <c>[来源]</c> 位置，如 <c>Scheduler</c> / <c>Manager</c>。</param>
    /// <param name="clock">时间源。</param>
    /// <param name="maxBytes">单文件大小上限，单位字节。</param>
    /// <param name="retainedFileCount">保留的历史文件份数。</param>
    public FileLogger(
        string filePath,
        string source,
        IClock clock,
        long maxBytes = DefaultMaxBytes,
        int retainedFileCount = DefaultRetainedFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(clock);

        _filePath = Path.GetFullPath(filePath);
        _source = source;
        _clock = clock;
        _maxBytes = maxBytes;
        _retainedFileCount = Math.Max(retainedFileCount, 1);
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string message) => Append(level, message, exception: null);

    /// <inheritdoc />
    public void Write(LogLevel level, Exception exception, string message)
        => Append(level, message, exception);

    private static string LevelToken(LogLevel level) => level switch
    {
        LogLevel.Info => InfoLevel,
        LogLevel.Warn => WarnLevel,
        LogLevel.Error => ErrorLevel,
        _ => ErrorLevel,
    };

    private void Append(LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder(message.Length + 64);
        builder.Append(_clock.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        builder.Append(" [").Append(LevelToken(level)).Append("] [").Append(_source).Append("] ").Append(message);

        if (exception is not null)
        {
            builder.Append(Environment.NewLine).Append(exception.ToString());
        }

        var line = builder.Append(Environment.NewLine).ToString();

        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RollIfNeeded();
                File.AppendAllText(_filePath, line, Utf8WithoutBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 🔴 日志自身写不进去，绝不能反过来让业务崩溃 —— 那会导致"日志系统自己出错"
                // 这种最难排查的组合。降级到调试输出，至少把原因留在调试器里。
                Debug.WriteLine($"[FileLogger] 写入日志失败：{_filePath} —— {ex.Message}");
            }
        }
    }

    private void RollIfNeeded()
    {
        if (!File.Exists(_filePath) || new FileInfo(_filePath).Length < _maxBytes)
        {
            return;
        }

        var oldest = BuildRolledPath(_retainedFileCount);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _retainedFileCount - 1; index >= 1; index--)
        {
            var source = BuildRolledPath(index);
            if (File.Exists(source))
            {
                File.Move(source, BuildRolledPath(index + 1), overwrite: true);
            }
        }

        File.Move(_filePath, BuildRolledPath(1), overwrite: true);
    }

    /// <summary>把 <c>scheduler.log</c> 的第 <paramref name="index"/> 份历史映射为 <c>scheduler.1.log</c>。</summary>
    /// <param name="index">历史序号，从 1 开始。</param>
    /// <returns>历史文件完整路径。</returns>
    private string BuildRolledPath(int index)
    {
        var directory = Path.GetDirectoryName(_filePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(_filePath);
        var extension = Path.GetExtension(_filePath);

        return Path.Combine(directory, $"{name}.{index}{extension}");
    }
}
