using System.Text;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Logging;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="FileLogger"/> 的单元测试：行格式、滚动与"日志写不进去不许把业务带崩"
/// （design.md 7.7 / §8.2 / NFR-2.2）。
/// </summary>
/// <remarks>
/// <para>
/// 落点是 <see cref="TempDirectory"/>。🔴 **不触碰真实 <c>%LOCALAPPDATA%\DelayStart\logs</c>**：
/// 那里面是用户机器上真机排查的现场，测试往里追加行等于污染证据。
/// </para>
/// <para>
/// 阈值经构造参数注入（<c>maxBytes</c> / <c>retainedFileCount</c>），因此滚动路径
/// **可以**用 10 字节这样的小阈值真实走一遍，不必造 2 MB 垃圾数据，也不必为可测性改生产代码。
/// 注意滚动的判定发生在**追加之前**（现有长度 ≥ 上限就滚），所以测试阈值必须小于单行长度，
/// "写一行滚一次"的时序才对得上。
/// </para>
/// </remarks>
public sealed class FileLoggerTests : IDisposable
{
    private const string Source = "Scheduler";
    private const string FileName = "scheduler.log";

    /// <summary>小于任何一行的阈值：保证每次追加前都判定为"已超限"。</summary>
    private const long TinyThreshold = 10;

    private readonly TempDirectory _temp = new();
    private readonly FakeClock _clock = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Write_信息级_写出时间级别来源消息四段式()
    {
        // Arrange
        var path = LogPath();
        var logger = Create(path);

        // Act
        logger.Write(LogLevel.Info, "正在启动 微信（延时 30s）");

        // Assert：design.md 7.7 规定的 `时间 [级别] [来源] 消息`，级别用三字母标记。
        Assert.Equal(
            $"2026-09-19 08:41:12.000 [INF] [{Source}] 正在启动 微信（延时 30s）{Environment.NewLine}",
            File.ReadAllText(path));
    }

    [Fact]
    public void Write_警告级_级别标记为WRN()
    {
        var path = LogPath();

        Create(path).Write(LogLevel.Warn, "条目读取失败：注册表");

        Assert.Contains(" [WRN] ", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_错误级_级别标记为ERR()
    {
        var path = LogPath();

        Create(path).Write(LogLevel.Error, "启动失败");

        Assert.Contains(" [ERR] ", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Write_未知级别_按错误级落盘且不丢行()
    {
        // 只有三档级别，但枚举可以被强转出一个第四值。丢行比标错更糟。
        var path = LogPath();

        Create(path).Write((LogLevel)99, "未知级别");

        var line = ReadFirstLine(path);
        Assert.Contains(" [ERR] ", line, StringComparison.Ordinal);
        Assert.Contains("未知级别", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_带异常_异常全文另起一行追加在消息之后()
    {
        // 只拼 ex.Message 会丢栈，而栈才是排错依据（ILogSink 的备注）。
        var path = LogPath();
        var logger = Create(path);

        logger.Write(LogLevel.Error, new InvalidOperationException("创建进程失败"), "启动失败");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("启动失败", lines[0], StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: 创建进程失败", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_连续多次_按顺序追加且每条独占一行()
    {
        var path = LogPath();
        var logger = Create(path);

        logger.Write(LogLevel.Info, "第一条");
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        logger.Write(LogLevel.Warn, "第二条");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("第一条", lines[0], StringComparison.Ordinal);

        // 时间戳取自 IClock 而不是 DateTime.Now —— 否则这两行的时间无从断言。
        Assert.StartsWith("2026-09-19 08:41:12.250 ", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("第二条", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_单次写入_文件按UTF8无BOM落盘()
    {
        // 编码统一约定为 UTF-8 无 BOM（design.md 9.2）。带 BOM 会让第一行的时间戳前
        // 多出三个不可见字节，按行读的工具看到的是乱码开头。
        var path = LogPath();

        Create(path).Write(LogLevel.Info, "中文消息");

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3, "文件不该是空的");
        Assert.NotEqual<byte>(0xEF, bytes[0]);
        Assert.NotEqual<byte>(0xBB, bytes[1]);
        Assert.NotEqual<byte>(0xBF, bytes[2]);
        Assert.Equal("2026-09-19 08:41:12.000 [INF] [Scheduler] 中文消息", ReadFirstLine(path));
    }

    [Fact]
    public void Write_日志目录不存在_自动创建目录并落盘()
    {
        // 首次启动时 logs\ 还不存在，日志写不出去等于"整轮调度没有现场"。
        var path = _temp.Combine(Path.Combine("logs", "nested", FileName));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));

        Create(path).Write(LogLevel.Info, "首次写入");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Write_文件达到上限_当前文件滚动为第一份历史()
    {
        // Arrange
        var path = LogPath();
        var logger = Create(path, maxBytes: TinyThreshold);

        logger.Write(LogLevel.Info, "第一行");
        Assert.False(File.Exists(RolledPath(1)), "第一次写入前文件还不存在，不该滚");

        // Act
        logger.Write(LogLevel.Info, "第二行");

        // Assert：当前文件只含新行，历史文件承载旧行。
        Assert.True(File.Exists(RolledPath(1)));
        AssertMessage("第二行", path);
        AssertMessage("第一行", RolledPath(1));
    }

    [Fact]
    public void Write_连续滚动_按保留份数轮换历史文件编号()
    {
        // Arrange：保留 2 份历史 ⇒ 编号只到 .2。
        var path = LogPath();
        var logger = Create(path, maxBytes: TinyThreshold);

        // Act：除第一行外每写一行就滚一次。
        for (var index = 1; index <= 4; index++)
        {
            logger.Write(LogLevel.Info, $"第{index}行");
        }

        // Assert
        Assert.True(File.Exists(RolledPath(1)));
        Assert.True(File.Exists(RolledPath(2)));
        Assert.False(File.Exists(RolledPath(3)), "保留份数之外不许再留历史文件");

        // 编号越大内容越旧：第 1 行已在第 4 次写入时被最旧的清理挤掉。
        AssertMessage("第2行", RolledPath(2));
        AssertMessage("第3行", RolledPath(1));
        AssertMessage("第4行", path);
    }

    [Fact]
    public void Write_保留份数为一_历史文件编号不超过一()
    {
        var path = LogPath();
        var logger = Create(path, maxBytes: TinyThreshold, retainedFileCount: 1);

        logger.Write(LogLevel.Info, "第一行");
        logger.Write(LogLevel.Info, "第二行");
        logger.Write(LogLevel.Info, "第三行");

        Assert.True(File.Exists(RolledPath(1)));
        Assert.False(File.Exists(RolledPath(2)));
        AssertMessage("第二行", RolledPath(1));
    }

    [Fact]
    public void Write_保留份数传零或负数_按一份历史处理()
    {
        // 构造时 Math.Max 兜底：传 0 若被当成"不留历史"，一次滚动就会把日志清空。
        var path = LogPath();
        var logger = Create(path, maxBytes: TinyThreshold, retainedFileCount: 0);

        logger.Write(LogLevel.Info, "第一行");
        logger.Write(LogLevel.Info, "第二行");

        Assert.True(File.Exists(RolledPath(1)));
        Assert.False(File.Exists(RolledPath(2)));
    }

    [Fact]
    public void 构造_文件路径为空白_抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FileLogger("   ", Source, _clock));
    }

    [Fact]
    public void 构造_来源标记为空白_抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new FileLogger(LogPath(), "  ", _clock));
    }

    [Fact]
    public void 构造_时钟为null_抛ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new FileLogger(LogPath(), Source, null!));
    }

    [Fact]
    public void 写入_父路径被同名普通文件占住_不抛异常()
    {
        // 🔴 日志自身写不进去，绝不能反过来让业务崩溃（FileLogger 的 catch 注释）。
        // 用"父路径是一个普通文件"制造确定的写失败 —— 比改目录 ACL 稳定，
        // 且不碰系统全局状态（单元测试禁止触碰真实 ACL）。
        var blocker = _temp.Combine("not-a-directory");
        File.WriteAllText(blocker, "占位");
        var path = Path.Combine(blocker, FileName);
        var logger = Create(path);

        var exception = Record.Exception(() => logger.Write(LogLevel.Error, "写不进去也得活着返回"));

        Assert.Null(exception);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void 写入_发生滚动之后_后续写入仍继续落在当前文件()
    {
        // 降级不是"整个实例报废"：滚动后必须还能继续记。
        var path = LogPath();
        var logger = Create(path, maxBytes: TinyThreshold);

        logger.Write(LogLevel.Info, "第一行");
        logger.Write(LogLevel.Info, "第二行");
        logger.Write(LogLevel.Info, "第三行");

        AssertMessage("第三行", path);
        AssertMessage("第二行", RolledPath(1));
        AssertMessage("第一行", RolledPath(2));
    }

    [Fact]
    public void 默认阈值_为两兆字节且默认保留两份历史()
    {
        // design.md 7.7「单文件 2MB 轮转」，保留份数在 §8.2 定的也是 2。
        Assert.Equal(2 * 1024 * 1024, FileLogger.DefaultMaxBytes);
        Assert.Equal(2, FileLogger.DefaultRetainedFileCount);
    }

    private FileLogger Create(
        string path,
        long maxBytes = FileLogger.DefaultMaxBytes,
        int retainedFileCount = FileLogger.DefaultRetainedFileCount)
        => new(path, Source, _clock, maxBytes, retainedFileCount);

    private string LogPath() => _temp.Combine(FileName);

    private string RolledPath(int index) => _temp.Combine($"scheduler.{index}.log");

    private static string ReadFirstLine(string path)
        => File.ReadAllLines(path)[0];

    /// <summary>断言文件首行以指定消息结尾（行首是时间戳/级别/来源，这里只比消息部分）。</summary>
    private static void AssertMessage(string message, string path)
        => Assert.EndsWith($"[{Source}] {message}", ReadFirstLine(path), StringComparison.Ordinal);
}