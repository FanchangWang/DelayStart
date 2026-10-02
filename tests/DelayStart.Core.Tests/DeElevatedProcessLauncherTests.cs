using System.Reflection;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="DeElevatedProcessLauncher"/> 中"临时目录清理"分支的单元测试（硬约束 7：失败必须可见）。
/// </summary>
/// <remarks>
/// <para>
/// 该方法没有公开入口：它只在 UIAccess 降权启动链内部被调用，而那条链要真令牌、
/// 真中转器 exe 与真进程 —— 单元测试禁止触碰进程（<c>Agents.md</c> 硬约束），
/// 因此这里用反射直接调那一个私有方法，只锁"清理失败可见、且不影响启动结果"这一条。
/// </para>
/// <para>
/// "删不掉"靠**独占打开**（<see cref="FileShare.None"/>）制造：Windows 上
/// <c>Directory.Delete(recursive: true)</c> 会因目录内文件被独占而抛
/// <see cref="IOException"/>。每个失败用例都额外断言目录仍在，杜绝恒真的假绿。
/// </para>
/// </remarks>
public sealed class DeElevatedProcessLauncherTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();

    /// <summary>反射调用私有的 <c>TryDeleteDirectory</c>（返回即"没有抛出"）。</summary>
    private static void TryDeleteDirectory(ILogSink log, string itemName, string path)
    {
        var method = typeof(DeElevatedProcessLauncher)
            .GetMethod("TryDeleteDirectory", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(DeElevatedProcessLauncher).FullName, "TryDeleteDirectory");

        method.Invoke(new DeElevatedProcessLauncher(log), [itemName, path]);
    }

    /// <summary>建一个含 job.json 的中转作业临时目录。</summary>
    private string CreateBrokerTempDir()
    {
        var path = _temp.Combine("broker");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "job.json"), "{}");
        return path;
    }

    [Fact]
    public void TryDeleteDirectory_NormalDirectory_RemovesItAndLogsNothing()
    {
        var log = new FakeLogSink();
        var path = CreateBrokerTempDir();

        TryDeleteDirectory(log, "微信", path);

        Assert.False(Directory.Exists(path));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void TryDeleteDirectory_DeleteFails_LogsWarningWithExceptionAndDoesNotThrow()
    {
        var log = new FakeLogSink();
        var path = CreateBrokerTempDir();
        using var locked = new FileStream(
            Path.Combine(path, "job.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var exception = Record.Exception(() => TryDeleteDirectory(log, "微信", path));

        Assert.Null(exception);                                   // fail-open：绝不抛给降权启动链
        Assert.True(Directory.Exists(path));                      // 确实没删掉 —— 真失败，不是走过场
        Assert.True(log.Contains(LogLevel.Warn, "『微信』"));
        Assert.True(log.Contains(LogLevel.Warn, path));
        Assert.True(log.HasExceptionAt(LogLevel.Warn));           // 异常对象没被丢掉（带栈）
    }

    [Fact]
    public void TryDeleteDirectory_MissingDirectory_IsQuietAndDoesNotThrow()
    {
        var log = new FakeLogSink();
        var path = _temp.Combine("never-created");

        var exception = Record.Exception(() => TryDeleteDirectory(log, "微信", path));

        Assert.Null(exception);
        Assert.Equal(0, log.Count);                               // 目录本来就没有，不是故障
    }
}
