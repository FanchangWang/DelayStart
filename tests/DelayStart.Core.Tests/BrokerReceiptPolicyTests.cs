using System.Text.Json;

using DelayStart.Core.Launch;
using DelayStart.Core.Serialization;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="BrokerReceiptPolicy"/>：UIAccess 降权链的<b>一次性回执令牌</b>（D70）。
/// </summary>
/// <remarks>
/// <para>
/// 这组用例钉的是一个<b>会静默发生</b>的假成功：结果文件在
/// <c>%TEMP%\DelayStart\broker\&lt;Guid&gt;</c> 下，<b>同用户的任何 Medium 进程</b>
/// 都能抢先落一份 <c>result.json</c>（<c>Ok=true</c>）。调度端此前只判文件存在 + 反序列化，
/// 于是这个从未运行过的 uiAccess 条目被<b>永久标记成"已启动"</b>，又因 D20「不提权回退」
/// 不再重试 —— 用户界面上一切正常，日志里也什么都看不到。
/// </para>
/// <para>
/// 修法是令牌：调度端写作业时带一枚随机令牌，中转器原样回写，读取时逐字节比对。
/// 🔴 刻意不用 ACL —— 同项目已定的教训是同用户进程的 DACL 能做的事一样多。
/// </para>
/// <para>
/// 判据下沉到纯逻辑（<see cref="BrokerReceiptPolicy"/>）以便单测；真正读文件的轮询要真文件，
/// 单元测试禁止触碰进程，那部分留在 <c>DeElevatedProcessLauncher</c>。
/// </para>
/// </remarks>
public sealed class BrokerReceiptPolicyTests
{
    /// <summary>本组用例使用的固定"期望令牌"（与真随机值无关，只用于比对）。</summary>
    private const string ExpectedToken = "9f1c2b7ae4d34c0f8b6a1d2e3f405162";

    [Fact]
    public void IsAuthentic_令牌不符但回执声称成功_判否不采信()
    {
        // Arrange：伪造者最想伪造的那一份 —— Ok=true、看起来完全正常，只有令牌对不上。
        var forged = new BrokerLaunchResult
        {
            Ok = true,
            ProcessId = 4321,
            Token = "00000000000000000000000000000000",
        };

        // Act
        var accepted = BrokerReceiptPolicy.IsAuthentic(forged, ExpectedToken);

        // Assert：🔴 核心回归 —— 不采信 ⇒ 调用方不会把它当成功，条目不会进"已启动"状态。
        Assert.False(accepted);
    }

    [Fact]
    public void IsAuthentic_令牌逐字节相同_判真()
    {
        // Arrange：真正的中转器原样回写作业里的令牌。
        var echoed = new BrokerLaunchResult
        {
            Ok = true,
            ProcessId = 1234,
            Token = ExpectedToken,
        };

        // Act / Assert
        Assert.True(BrokerReceiptPolicy.IsAuthentic(echoed, ExpectedToken));
    }

    [Fact]
    public void IsAuthentic_令牌相同且回执为成功_采信后可判为已启动()
    {
        // Arrange / Act：真回执被采信后，判定照常交给 BrokerResultPolicy。
        var echoed = new BrokerLaunchResult { Ok = true, ProcessId = 1234, Token = ExpectedToken };

        // Assert：采信 ⇒ 不产生失败原因 ⇒ 调用方按"已启动"继续（Success）。
        Assert.True(BrokerReceiptPolicy.IsAuthentic(echoed, ExpectedToken));
        Assert.Null(BrokerResultPolicy.FailureReason(echoed));
    }

    [Fact]
    public void IsAuthentic_回执缺少令牌字段_判否()
    {
        // Arrange：缺字段的反序列化结果就是默认空串（= 没带令牌的中转器或旧版本组合）。
        var noToken = new BrokerLaunchResult { Ok = true, ProcessId = 1234 };

        // Act / Assert
        Assert.Equal(string.Empty, noToken.Token);
        Assert.False(BrokerReceiptPolicy.IsAuthentic(noToken, ExpectedToken));
    }

    [Fact]
    public void IsAuthentic_期望令牌为空_判否()
    {
        // Arrange：即便回执也没带令牌（双方都空），也必须判否 ——
        // 否则"老版本组合"会静默通过校验，防伪造能力形同虚设。
        var noToken = new BrokerLaunchResult { Ok = true, ProcessId = 1234 };

        // Act / Assert
        Assert.False(BrokerReceiptPolicy.IsAuthentic(noToken, string.Empty));
    }

    [Fact]
    public void IsAuthentic_回执为null_判否()
    {
        // Arrange / Act / Assert：反序列化出 null（内容是字面量 "null"）不得被当成回执。
        Assert.False(BrokerReceiptPolicy.IsAuthentic(null, ExpectedToken));
    }

    [Fact]
    public void IsAuthentic_令牌仅大小写不同_判否()
    {
        // Arrange：逐字节比较 ⇒ 大小写不同即不符，不做任何"宽容归一"。
        var upper = new BrokerLaunchResult { Ok = true, Token = ExpectedToken.ToUpperInvariant() };

        // Act / Assert
        Assert.False(BrokerReceiptPolicy.IsAuthentic(upper, ExpectedToken.ToLowerInvariant()));
    }

    [Fact]
    public void NewToken_连续两次调用_取值不同()
    {
        // Act：钉住"令牌是每次新生成的随机值"——写成常量或可预测值，防伪造能力当场归零。
        var first = BrokerReceiptPolicy.NewToken();
        var second = BrokerReceiptPolicy.NewToken();

        // Assert
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NewToken_取值非空且为三十二位十六进制_与作业目录名同风格()
    {
        // Act
        var token = BrokerReceiptPolicy.NewToken();

        // Assert：Guid("N") = 32 位小写十六进制，无分隔符。
        Assert.Equal(32, token.Length);
        Assert.Matches("^[0-9a-f]{32}$", token);
    }

    [Fact]
    public void NewToken_生成的令牌_能被自己的回执校验通过()
    {
        // Arrange：模拟"调度端生成 → 中转器原样回写 → 调度端校验"的完整闭环。
        var token = BrokerReceiptPolicy.NewToken();
        var echoed = new BrokerLaunchResult { Ok = true, ProcessId = 1234, Token = token };

        // Act / Assert
        Assert.True(BrokerReceiptPolicy.IsAuthentic(echoed, token));
    }

    [Fact]
    public void 作业契约_经源生成JSON往返_令牌原样读回()
    {
        // Arrange：源生成上下文是两端共享的 schema，写出用 camelCase。
        var token = BrokerReceiptPolicy.NewToken();
        var job = new BrokerLaunchJob
        {
            Target = @"C:\Tools\Quicker.exe",
            Arguments = "--tail log",
            WorkingDirectory = @"C:\Tools",
            ResultFile = @"C:\Temp\result.json",
            WaitTimeoutMs = 4000,
            Token = token,
        };

        // Act
        var json = JsonSerializer.Serialize(job, BrokerJsonContext.Default.BrokerLaunchJob);
        var roundTripped = JsonSerializer.Deserialize(json, BrokerJsonContext.Default.BrokerLaunchJob);

        // Assert
        Assert.NotNull(roundTripped);
        Assert.Equal(token, roundTripped.Token);
        Assert.Contains("\"token\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 回执契约_经源生成JSON往返_伪造的令牌也会原样读回()
    {
        // Arrange / Act：序列化不做任何"净化" —— 伪造者写什么就读回什么，
        // 因此判据只能落在 IsAuthentic 上，不能指望序列化层帮忙。
        var forged = new BrokerLaunchResult { Ok = true, Token = "ffffffffffffffffffffffffffffffff" };
        var json = JsonSerializer.Serialize(forged, BrokerJsonContext.Default.BrokerLaunchResult);
        var roundTripped = JsonSerializer.Deserialize(json, BrokerJsonContext.Default.BrokerLaunchResult);

        // Assert
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped.Ok);
        Assert.False(BrokerReceiptPolicy.IsAuthentic(roundTripped, ExpectedToken));
    }

    // ---------------- 作业有效性（IsWellFormedJob）----------------
    //
    // 这组钉的是中转器的入口门禁。此前它写在 LaunchBroker/Program.cs 里，而那个 exe
    // **没有测试工程** ⇒ 条件被顺手删掉不会有人发现。Token 一项尤其危险：删掉它不会
    // 让任何东西立刻坏，而是让"缺 Token 的作业"变成一份误导性日志（说成"中转器超时"）
    // 加一次 20 秒空等。所以判据下沉到 Core 并在此钉住。

    /// <summary>造一份作业，三字段默认齐全；用例通过具名参数破坏其中一项。</summary>
    private static BrokerLaunchJob Job(
        string target = @"C:\Program Files\Quicker\Quicker.exe",
        string resultFile = @"C:\Users\test\AppData\Local\Temp\DelayStart\broker\abc\result.json",
        string token = ExpectedToken)
        => new()
        {
            Target = target,
            Arguments = string.Empty,
            WorkingDirectory = string.Empty,
            ResultFile = resultFile,
            WaitTimeoutMs = 4000,
            Token = token,
        };

    [Fact]
    public void IsWellFormedJob_三字段齐全_判真()
    {
        Assert.True(BrokerReceiptPolicy.IsWellFormedJob(Job()));
    }

    [Fact]
    public void IsWellFormedJob_作业为null_判否()
    {
        Assert.False(BrokerReceiptPolicy.IsWellFormedJob(null));
    }

    [Fact]
    public void IsWellFormedJob_缺令牌_判否()
    {
        // 🔴 核心回归：这一项曾经不在校验里。后果不是崩，而是中转器照常启动目标、
        // 回写空令牌、调度端一直拒收，白等 20 秒并把原因误报成「中转器超时」。
        Assert.False(BrokerReceiptPolicy.IsWellFormedJob(Job(token: string.Empty)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsWellFormedJob_令牌为空白_判否(string token)
    {
        // 用 IsNullOrWhiteSpace 而非 IsNullOrEmpty：空白令牌同样走不通校验，
        // 不该被当成"有令牌"放过去。
        Assert.False(BrokerReceiptPolicy.IsWellFormedJob(Job(token: token)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsWellFormedJob_目标为空白_判否(string target)
    {
        Assert.False(BrokerReceiptPolicy.IsWellFormedJob(Job(target: target)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsWellFormedJob_结果文件为空白_判否(string resultFile)
    {
        // 没有 ResultFile 就无处回写，中转器只能用退出码报错（ExitBadJob / ExitResultWriteFailed）。
        Assert.False(BrokerReceiptPolicy.IsWellFormedJob(Job(resultFile: resultFile)));
    }

    [Fact]
    public void IsWellFormedJob_仅缺令牌而另两项齐全_仍判否()
    {
        // 单独钉住"Token 这一项本身"：上面几条各自破坏一个字段，这条确保
        // 不是"反正别的字段也要查"顺带把它拦下的。
        var job = Job(token: string.Empty);

        Assert.False(string.IsNullOrWhiteSpace(job.Target));
        Assert.False(string.IsNullOrWhiteSpace(job.ResultFile));
        Assert.False(BrokerReceiptPolicy.IsWellFormedJob(job));
    }

    [Fact]
    public void 缺令牌的作业若被放行_其回执必然被拒收()
    {
        // 端到端自证：把这条链的两半接起来看 —— 缺 Token 的作业一旦被执行，
        // 它回写的结果在 IsAuthentic 面前必然不合格。这解释了为什么必须在入口拦，
        // 而不是等调度端超时才发现。
        var job = Job(token: string.Empty);
        var echoed = new BrokerLaunchResult { Ok = true, ProcessId = 1, Token = job.Token };

        // IsAuthentic 对空的**期望**令牌恒判否（防「双方都没带」的旧组合静默通过），
        // 因此即使令牌回写得一模一样，缺令牌的作业注定收不到结果。
        Assert.False(BrokerReceiptPolicy.IsAuthentic(echoed, job.Token));
    }
}
