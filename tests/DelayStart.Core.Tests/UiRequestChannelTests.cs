using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Services;
using DelayStart.Core.Tests.Fakes;
using DelayStart.Management.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="UiRequestChannel"/> 的单元测试：一次性请求的写入、读后即删与令牌往返
/// （D74 / D82 / FR-8.4）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>读即删</b>是这条通道最要紧的时序（D82）：请求文件是一次性的，不删的话之后
/// 任何一次普通唤起都会重复跳到同一个位置 —— 表现为"点了查看之后每次开管理端都自己跳走"。
/// 覆盖写的语义同样要钉住：不带来源的 <c>--goto-log</c>（D18）写的是"看日志"这个更新的意图，
/// 必须盖掉可能残留的定位请求。
/// </para>
/// <para>
/// 令牌白名单映射（<c>App.Services.UiTargetNavigation.TagFor</c>）在 App 层，
/// 而测试项目**不引用 App**（docs/design.md 1.3）。因此这里只覆盖 Management + Core 侧：
/// 令牌能原样过通道。翻译成界面标签那一层不在本文件范围内。
/// </para>
/// </remarks>
public sealed class UiRequestChannelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeLogSink _log = new();
    private readonly PathService _paths;
    private readonly UiRequestChannel _channel;

    /// <summary>搭好一套隔离在临时目录里的定位请求通道。</summary>
    public UiRequestChannelTests()
    {
        _paths = new PathService(_temp.Combine("local"), _temp.Combine("config"), _temp.Path);
        _channel = new UiRequestChannel(_paths, _log);
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Write后Consume_读到目标令牌且请求文件已被删除()
    {
        // Act
        _channel.Write(UiNavigationTarget.Registry);
        Assert.True(File.Exists(_paths.UiRequestFilePath), "写入后请求文件应存在");

        var token = _channel.Consume();

        // Assert
        Assert.Equal(UiNavigationTarget.Registry, token);
        Assert.False(File.Exists(_paths.UiRequestFilePath), "🔴 读后即删（D82）");
    }

    [Fact]
    public void Consume_无请求文件_返回null且不记警告()
    {
        // 首次唤起是常态，不是异常：不该在守卫/管理端日志里刷 Warn。
        Assert.Null(_channel.Consume());
        Assert.Equal(0, _log.Count);
    }

    [Fact]
    public void Consume_同一请求连续读两次_第二次返回null()
    {
        // 一次性语义的直接体现：第二遍读不到，说明第一次确实消费掉了。
        _channel.Write(UiNavigationTarget.RunsLog);

        Assert.Equal(UiNavigationTarget.RunsLog, _channel.Consume());
        Assert.Null(_channel.Consume());
    }

    [Fact]
    public void Write_连续写两次_后写的令牌覆盖先写的()
    {
        // 覆盖写：残留的旧定位请求必须被更新的意图盖掉。
        _channel.Write(UiNavigationTarget.Registry);
        _channel.Write(UiNavigationTarget.RunsLog);

        Assert.Equal(UiNavigationTarget.RunsLog, _channel.Consume());
    }

    [Fact]
    public void Write_目标令牌为空白_抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _channel.Write("   "));
    }

    [Fact]
    public void 构造_路径服务为null_抛ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new UiRequestChannel(null!, _log));
    }

    [Fact]
    public void 构造_日志接收端为null_抛ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new UiRequestChannel(_paths, null!));
    }

    [Fact]
    public void Consume_请求文件内容不可解析_返回null并删除文件且记警告()
    {
        // 🔴 坏掉的请求文件留在那儿只会让后续每次唤起都白读一遍，所以**解析失败也要删**。
        WriteRequestFile("{ 这不是合法 JSON");

        var token = _channel.Consume();

        Assert.Null(token);
        Assert.False(File.Exists(_paths.UiRequestFilePath), "解析失败也必须删掉请求文件");
        Assert.True(_log.Contains(LogLevel.Warn, "UI 定位请求解析失败"), "解析失败必须留 Warn");
        Assert.True(_log.HasExceptionAt(LogLevel.Warn), "必须走带异常的重载");
    }

    [Fact]
    public void Consume_请求文件内容为空_返回null且不记警告()
    {
        // 空文件读出来是空串，走 IsNullOrWhiteSpace 的早退分支。
        WriteRequestFile(string.Empty);

        Assert.Null(_channel.Consume());
        Assert.Equal(0, _log.Count);
    }

    [Fact]
    public void Consume_请求文件内容为空白_返回null且文件仍留在原处()
    {
        // ⚠️ 记录**当前实现**的行为：空白内容走的是 IsNullOrWhiteSpace 早退，
        // 而早退发生在 Discard 之前，所以文件不会被删（只有解析失败那条路径才删）。
        // 源码注释写的是"无论解析成败都要删"—— 空白这一档与之有偏差，
        // 本批只报告不改生产代码；这里钉住现状，改这条路径时测试一定会拦下。
        WriteRequestFile("   ");

        Assert.Null(_channel.Consume());
        Assert.True(File.Exists(_paths.UiRequestFilePath));
        Assert.Equal(0, _log.Count);
    }

    [Fact]
    public void Consume_请求文件内容为空白_后续再写再读仍然正常()
    {
        // 残留的空文件不许把通道带进坏状态：下一次写入必须能恢复正常。
        WriteRequestFile("   ");
        Assert.Null(_channel.Consume());

        _channel.Write(UiNavigationTarget.Delay);

        Assert.Equal(UiNavigationTarget.Delay, _channel.Consume());
    }

    [Fact]
    public void Discard_请求文件不存在但目录已存在_静默返回()
    {
        Directory.CreateDirectory(_paths.LocalRoot);

        var exception = Record.Exception(() => _channel.Discard());

        Assert.Null(exception);
        Assert.Equal(0, _log.Count);
    }

    [Fact]
    public void Discard_目录尚未创建_记一条警告但不抛()
    {
        // ⚠️ 记录**当前实现**的行为：File.Delete 在父目录不存在时抛
        // DirectoryNotFoundException，它是 IOException 的子类，于是被"删不掉不致命"
        // 的 catch 收走并记一条 Warn。方法注释写的是"不存在时静默返回"，与此有偏差
        // （首次运行、用户清过 Local 目录时会刷出无意义的 Warn）。本批只报告不改生产代码。
        Assert.False(Directory.Exists(_paths.LocalRoot));

        var exception = Record.Exception(() => _channel.Discard());

        Assert.Null(exception);
        Assert.True(_log.Contains(LogLevel.Warn, "删除 UI 定位请求文件失败"));
    }

    [Fact]
    public void Discard_存在待处理请求_删掉后Consume读不到()
    {
        _channel.Write(UiNavigationTarget.Uwp);

        _channel.Discard();

        Assert.False(File.Exists(_paths.UiRequestFilePath));
        Assert.Null(_channel.Consume());
    }

    [Fact]
    public void 令牌白名单_注册表来源页_原样往返()
        => AssertRoundTrips(UiNavigationTarget.Registry, "registry");

    [Fact]
    public void 令牌白名单_启动文件夹来源页_原样往返()
        => AssertRoundTrips(UiNavigationTarget.StartupFolder, "startup-folder");

    [Fact]
    public void 令牌白名单_计划任务来源页_原样往返()
        => AssertRoundTrips(UiNavigationTarget.ScheduledTask, "scheduled-task");

    [Fact]
    public void 令牌白名单_UWP来源页_原样往返()
        => AssertRoundTrips(UiNavigationTarget.Uwp, "uwp");

    [Fact]
    public void 令牌白名单_延时启动页_原样往返()
        => AssertRoundTrips(UiNavigationTarget.Delay, "delay");

    [Fact]
    public void 令牌白名单_失效条目位置_原样往返()
        => AssertRoundTrips(UiNavigationTarget.Stale, "stale");

    [Fact]
    public void 令牌白名单_调度日志页_原样往返()
        => AssertRoundTrips(UiNavigationTarget.RunsLog, "runs-log");

    [Fact]
    public void 令牌字面量_跨进程契约不得漂移()
    {
        // 🔴 这些字符串是**跨进程契约**（守卫写、管理端读）。拼错一个连字符的表现是
        // "点了查看没反应"——与"通知根本没发出来"在用户眼里一模一样，几乎无法从现场倒推。
        // 常量集中一处还不够，得把**字面量本身**也钉住。
        Assert.Equal("registry", UiNavigationTarget.Registry);
        Assert.Equal("startup-folder", UiNavigationTarget.StartupFolder);
        Assert.Equal("scheduled-task", UiNavigationTarget.ScheduledTask);
        Assert.Equal("uwp", UiNavigationTarget.Uwp);
        Assert.Equal("delay", UiNavigationTarget.Delay);
        Assert.Equal("stale", UiNavigationTarget.Stale);
        Assert.Equal("runs-log", UiNavigationTarget.RunsLog);
    }

    [Fact]
    public void 令牌字面量_互不相同()
    {
        // 令牌撞车会让两个不同意图落到同一页，且不会有任何报错。
        var all = new[]
        {
            UiNavigationTarget.Registry,
            UiNavigationTarget.StartupFolder,
            UiNavigationTarget.ScheduledTask,
            UiNavigationTarget.Uwp,
            UiNavigationTarget.Delay,
            UiNavigationTarget.Stale,
            UiNavigationTarget.RunsLog,
        };

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Consume_未知令牌_原样透传给调用方()
    {
        // 通道不做白名单校验：翻译成界面标签是 App 层 TagFor 的职责（未知值返回 null）。
        // 旧版本写入方发来的新令牌必须能安全穿过旧版通道。
        WriteRequestFile("""{"target":"future-token"}""");

        Assert.Equal("future-token", _channel.Consume());
    }

    private void AssertRoundTrips(string token, string expectedLiteral)
    {
        Assert.Equal(expectedLiteral, token);

        _channel.Write(token);

        Assert.Equal(token, _channel.Consume());
        Assert.Equal(0, _log.Count);
    }

    private void WriteRequestFile(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.UiRequestFilePath)!);
        File.WriteAllText(_paths.UiRequestFilePath, contents);
    }
}