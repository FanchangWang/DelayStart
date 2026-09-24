using DelayStart.Core.Launch;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="BrokerResultPolicy"/> 的单元测试（B4 / FR-5.9 / D70 / E4）。
/// </summary>
/// <remarks>
/// 这组用例钉的是一个**已经真实发生过的判错方向**：中转器拿到了目标的真实退出码，
/// 但调度端只记一条日志就放行；等延时复查时进程早已消失，<c>ProbeProcess</c>
/// 按 E4 的宽容原则返回"退出码 0" → 判成功。于是**一个以退出码 1 失败的目标
/// 被静默报成"已启动"**，日志里唯一线索是一条 Warn，用户完全看不出来。
/// <para>
/// 修法是把"显式非零退出码"与"进程已消失、退出码不可得"这两种"进程不在了"分开 ——
/// 前者是确凿的失败证据，必须当场判失败；后者才适用 E4 的宽容。
/// </para>
/// </remarks>
public sealed class BrokerResultPolicyTests
{
    [Fact]
    public void FailureReason_ExitedImmediatelyWithNonZeroExitCode_IsFailure()
    {
        // 🔴 本条是 B4 的核心回归用例：真实非零退出码必须判失败。
        var result = new BrokerLaunchResult
        {
            Ok = true,
            ProcessId = 4321,
            ExitedImmediately = true,
            ExitCode = 1,
        };

        var reason = BrokerResultPolicy.FailureReason(result);

        Assert.NotNull(reason);
        Assert.Contains("1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureReason_ExitedImmediatelyWithLargeExitCode_IsFailure()
    {
        // Windows 上失败退出码常见高位值（如 0xC0000135 = DLL 找不到），不能只判小数字。
        var result = new BrokerLaunchResult { Ok = true, ExitedImmediately = true, ExitCode = 0xC0000135 };

        Assert.NotNull(BrokerResultPolicy.FailureReason(result));
    }

    [Fact]
    public void FailureReason_ExitedImmediatelyWithZeroExitCode_IsSuccess()
    {
        // E4：大量程序拉起已有实例后立刻退出（msedge.exe 典型），严格判定会产生大量假失败。
        var result = new BrokerLaunchResult
        {
            Ok = true,
            ProcessId = 1234,
            ExitedImmediately = true,
            ExitCode = 0,
        };

        Assert.Null(BrokerResultPolicy.FailureReason(result));
    }

    [Fact]
    public void FailureReason_ExitedImmediatelyWithUnknownExitCode_IsSuccess()
    {
        // 退出码读不到（中转器 GetExitCodeProcess 失败）属于"不知道"，按 E4 宽容，
        // 但绝不能因此判失败 —— 那会让偶发的读取失败变成假失败。
        var result = new BrokerLaunchResult
        {
            Ok = true,
            ProcessId = 1234,
            ExitedImmediately = true,
            ExitCode = null,
        };

        Assert.Null(BrokerResultPolicy.FailureReason(result));
    }

    [Fact]
    public void FailureReason_NotExited_IsSuccess()
    {
        var result = new BrokerLaunchResult { Ok = true, ProcessId = 1234 };

        Assert.Null(BrokerResultPolicy.FailureReason(result));
    }

    [Fact]
    public void FailureReason_NotCreated_IsFailure()
    {
        var result = new BrokerLaunchResult
        {
            Ok = false,
            Win32Error = 5,
            Message = "ShellExecuteEx 失败，Win32Error=5",
        };

        var reason = BrokerResultPolicy.FailureReason(result);

        Assert.NotNull(reason);
        Assert.Contains("ShellExecuteEx", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureReason_ElevationRequired_Explains740()
    {
        // 740 值得单独点名：用户看到裸数字完全无从判断发生了什么。
        var result = new BrokerLaunchResult
        {
            Ok = false,
            Win32Error = 740,
            Message = "ShellExecuteEx 失败，Win32Error=740",
        };

        var reason = BrokerResultPolicy.FailureReason(result);

        Assert.NotNull(reason);
        Assert.Contains("740", reason, StringComparison.Ordinal);
        Assert.Contains("提权", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureReason_NotCreatedWithoutMessage_StillReportsErrorCode()
    {
        var result = new BrokerLaunchResult { Ok = false, Win32Error = 2 };

        var reason = BrokerResultPolicy.FailureReason(result);

        Assert.NotNull(reason);
        Assert.Contains("2", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondsExitNote_NotExited_IsNull()
    {
        var result = new BrokerLaunchResult { Ok = true, ProcessId = 1234 };

        Assert.Null(BrokerResultPolicy.SecondsExitNote(result));
    }

    [Fact]
    public void SecondsExitNote_ZeroExitCode_SaysSuccess()
    {
        var result = new BrokerLaunchResult { Ok = true, ExitedImmediately = true, ExitCode = 0 };

        var note = BrokerResultPolicy.SecondsExitNote(result);

        Assert.NotNull(note);
        Assert.Contains("E4", note, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondsExitNote_UnknownExitCode_SaysUnconfirmed()
    {
        // "不知道"与"成功"必须能在日志里区分开，否则又是一次静默的判断替换。
        var result = new BrokerLaunchResult { Ok = true, ExitedImmediately = true, ExitCode = null };

        var note = BrokerResultPolicy.SecondsExitNote(result);

        Assert.NotNull(note);
        Assert.Contains("未知", note, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondsExitNote_NonZeroExitCode_StillProducesNote()
    {
        // 非零码已经由 FailureReason 判失败了；这条 note 在那条路径上不会被读到，
        // 但策略本身不该依赖调用方的执行顺序才保持正确。
        var result = new BrokerLaunchResult { Ok = true, ExitedImmediately = true, ExitCode = 5 };

        Assert.NotNull(BrokerResultPolicy.SecondsExitNote(result));
    }
}
