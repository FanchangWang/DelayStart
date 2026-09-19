using DelayStart.Core.Launch;
using DelayStart.Core.Models;
using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="LaunchResultEvaluator"/> 的单元测试：启动成败的四个判定分支（机制 7 / FR-5.9）。
/// </summary>
public sealed class LaunchResultEvaluatorTests
{
    [Fact]
    public void RecheckDelay_IsFifteenHundredMilliseconds()
    {
        // 机制 7 冻结的复查窗口。改它等于改变"启动成功"的定义，必须是有意识的决定。
        Assert.Equal(1500, LaunchResultEvaluator.RecheckDelayMilliseconds);
    }

    [Fact]
    public void Evaluate_ProcessNotCreated_ReturnsFailedWithLaunchFailed()
    {
        // Arrange
        var outcome = LaunchOutcome.Failure("找不到可执行文件。");

        // Act
        var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshotAfterDelay: null);

        // Assert
        Assert.Equal(RunItemState.Failed, evaluation.State);
        Assert.Equal(StartupFailureReason.LaunchFailed, evaluation.Reason);
        Assert.Equal("找不到可执行文件。", evaluation.Message);
        Assert.False(evaluation.IsSuccess);
    }

    [Fact]
    public void Evaluate_ProcessNotCreatedWithoutMessage_UsesFallbackMessage()
    {
        // 兜底文案：绝不能让失败项带着 null 消息出现在日志页上
        var outcome = LaunchOutcome.Failure(string.Empty);

        var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshotAfterDelay: null);

        Assert.False(string.IsNullOrWhiteSpace(evaluation.Message));
    }

    [Fact]
    public void Evaluate_StillRunningAfterRecheck_ReturnsDone()
    {
        // Arrange
        var outcome = LaunchOutcome.Success(processId: 1234);
        var snapshot = new ProcessSnapshot(HasExited: false, ExitCode: 0);

        // Act
        var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshot);

        // Assert
        Assert.Equal(RunItemState.Done, evaluation.State);
        Assert.Equal(StartupFailureReason.None, evaluation.Reason);
        Assert.Null(evaluation.Message);
        Assert.True(evaluation.IsSuccess);
    }

    [Fact]
    public void Evaluate_ExitedWithZero_ReturnsDone()
    {
        // E4：拉起已有实例后立刻退出（msedge.exe 等）是正常行为。
        // 严格判定会产生大量假失败，这条就是那份"必要的宽容"。
        var outcome = LaunchOutcome.Success(processId: 1234);
        var snapshot = new ProcessSnapshot(HasExited: true, ExitCode: 0);

        var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshot);

        Assert.Equal(RunItemState.Done, evaluation.State);
        Assert.True(evaluation.IsSuccess);
    }

    [Fact]
    public void Evaluate_ExitedWithNonZero_ReturnsFailedAndRecordsExitCode()
    {
        // E5：启动后立即以非零码退出 = 失败，且退出码必须进消息（否则用户无从排查）
        var outcome = LaunchOutcome.Success(processId: 1234);
        var snapshot = new ProcessSnapshot(HasExited: true, ExitCode: 3);

        var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshot);

        Assert.Equal(RunItemState.Failed, evaluation.State);
        Assert.Equal(StartupFailureReason.ExitedNonZero, evaluation.Reason);
        Assert.NotNull(evaluation.Message);
        Assert.Contains("3", evaluation.Message, StringComparison.Ordinal);
        Assert.False(evaluation.IsSuccess);
    }

    [Fact]
    public void Evaluate_NegativeExitCode_ReturnsFailed()
    {
        // 退出码为负（异常终止）同样算失败
        var outcome = LaunchOutcome.Success(processId: 1234);
        var snapshot = new ProcessSnapshot(HasExited: true, ExitCode: -1);

        var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshot);

        Assert.Equal(RunItemState.Failed, evaluation.State);
        Assert.Equal(StartupFailureReason.ExitedNonZero, evaluation.Reason);
    }

    [Fact]
    public void Evaluate_NoProcessHandle_ReturnsDone()
    {
        // UWP 经 shell 激活拿不到目标 PID（D28 / R12），只能乐观判定。
        // 此时能确认的只有"shell 接受了这次激活"。
        var outcome = LaunchOutcome.Success(processId: null);

        var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshotAfterDelay: null);

        Assert.Equal(RunItemState.Done, evaluation.State);
        Assert.True(evaluation.IsSuccess);
    }

    [Fact]
    public void Evaluate_DeElevationFellBack_DoesNotAffectSuccess()
    {
        // E6：降权失败后回退直接启动，**不算失败** —— 程序确实起来了。
        // 回退信息由调用方读 LaunchOutcome.DeElevationFellBack 后附加提示。
        var outcome = LaunchOutcome.Success(processId: 1234, deElevationFellBack: true);
        var snapshot = new ProcessSnapshot(HasExited: false, ExitCode: 0);

        var evaluation = LaunchResultEvaluator.Evaluate(outcome, snapshot);

        Assert.True(evaluation.IsSuccess);
        Assert.True(outcome.DeElevationFellBack);
    }

    [Fact]
    public void Evaluate_NullOutcome_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => LaunchResultEvaluator.Evaluate(null!, snapshotAfterDelay: null));
    }
}
