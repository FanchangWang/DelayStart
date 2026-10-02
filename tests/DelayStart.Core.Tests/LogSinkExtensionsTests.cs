using DelayStart.Core.Abstractions;
using DelayStart.Core.Tests.Fakes;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="LogSinkExtensions"/> 的单元测试：五个便捷调用名各自映射到正确的级别与重载。
/// </summary>
/// <remarks>
/// 🔴 这层看着只是转发，但它承载一条真实约定（<see cref="ILogSink"/> 的备注）：
/// **带异常时必须走带 <see cref="Exception"/> 的重载** —— 只拼 <c>ex.Message</c> 会丢栈，
/// 而栈才是排错依据。所以每个方法的"异常对象有没有带上"都要单独断言，
/// 不能只看消息文本。
/// </remarks>
public sealed class LogSinkExtensionsTests
{
    private readonly FakeLogSink _log = new();

    [Fact]
    public void Info_写入信息级且不带异常()
    {
        // Act
        _log.Info("调度开始");

        // Assert：消息原样保留，且**没有**异常对象（无参重载不该凭空造一个）。
        Assert.True(_log.Contains(LogLevel.Info, "调度开始"));
        Assert.False(_log.HasExceptionAt(LogLevel.Info));
        Assert.Equal(1, _log.Count);
    }

    [Fact]
    public void Warn_写入警告级且不带异常()
    {
        // Act
        _log.Warn("配置项缺失，已用默认值");

        // Assert
        Assert.True(_log.Contains(LogLevel.Warn, "配置项缺失，已用默认值"));
        Assert.False(_log.HasExceptionAt(LogLevel.Warn));
        Assert.Equal(1, _log.Count);
    }

    [Fact]
    public void Warn_带异常_写入警告级并保留异常对象()
    {
        // Act
        _log.Warn(new IOException("配置文件被锁"), "读取配置失败");

        // Assert：级别是 Warn，消息带得上，**异常对象也带上了**（栈不能丢）。
        Assert.True(_log.Contains(LogLevel.Warn, "读取配置失败"));
        Assert.True(_log.HasExceptionAt(LogLevel.Warn), "🔴 带异常的重载必须把异常对象传下去");
    }

    [Fact]
    public void Error_写入错误级且不带异常()
    {
        // Act
        _log.Error("启动失败");

        // Assert
        Assert.True(_log.Contains(LogLevel.Error, "启动失败"));
        Assert.False(_log.HasExceptionAt(LogLevel.Error));
        Assert.Equal(1, _log.Count);
    }

    [Fact]
    public void Error_带异常_写入错误级并保留异常对象()
    {
        // Act
        _log.Error(new InvalidOperationException("创建进程失败"), "启动失败");

        // Assert
        Assert.True(_log.Contains(LogLevel.Error, "启动失败"));
        Assert.True(_log.HasExceptionAt(LogLevel.Error), "🔴 带异常的重载必须把异常对象传下去");
    }

    [Fact]
    public void 扩展方法_接收端为null_五个方法都抛ArgumentNullException()
    {
        // ArgumentNullException.ThrowIfNull(sink)：否则调用点会在 NullReferenceException 上炸，
        // 而栈里看不出是哪个便捷方法。
        ILogSink? nullSink = null;

        Assert.Throws<ArgumentNullException>(() => nullSink!.Info("消息"));
        Assert.Throws<ArgumentNullException>(() => nullSink!.Warn("消息"));
        Assert.Throws<ArgumentNullException>(() => nullSink!.Warn(new IOException("boom"), "消息"));
        Assert.Throws<ArgumentNullException>(() => nullSink!.Error("消息"));
        Assert.Throws<ArgumentNullException>(() => nullSink!.Error(new IOException("boom"), "消息"));
    }
}