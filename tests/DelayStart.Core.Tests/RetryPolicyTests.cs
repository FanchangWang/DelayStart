using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="RetryPolicy"/> 的单元测试：重试额度边界与防御性输入（FR-9.6）。
/// </summary>
public sealed class RetryPolicyTests
{
    [Theory]
    [InlineData(0, 1, RetryDecision.Fail)]
    [InlineData(1, 1, RetryDecision.Retry)]
    [InlineData(1, 2, RetryDecision.Fail)]
    [InlineData(2, 1, RetryDecision.Retry)]
    [InlineData(2, 2, RetryDecision.Retry)]
    [InlineData(2, 3, RetryDecision.Fail)]
    public void Decide_BoundaryTable_MatchesSpec(int retryCount, int attempts, RetryDecision expected)
    {
        Assert.Equal(expected, RetryPolicy.Decide(attempts, retryCount));
    }

    [Fact]
    public void Decide_RetryCountZero_FirstFailureIsTerminal()
    {
        // 关掉重试的配置
        Assert.Equal(RetryDecision.Fail, RetryPolicy.Decide(1, 0));
    }

    [Fact]
    public void Decide_RetryCountIsRetriesNotTotalAttempts()
    {
        // retryCount = 2 表示"允许 2 次重试" ⇒ 最多 3 次尝试。
        // 若被误读成"总尝试次数 2"，第 2 次尝试就会被判终态 —— 这条断言钉死口径。
        const int retryCount = 2;

        Assert.Equal(RetryDecision.Retry, RetryPolicy.Decide(1, retryCount));
        Assert.Equal(RetryDecision.Retry, RetryPolicy.Decide(2, retryCount));
        Assert.Equal(RetryDecision.Fail, RetryPolicy.Decide(3, retryCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Decide_NonPositiveAttempts_FailsDefensively(int attempts)
    {
        // 该输入在现有调用点不可达（Attempts 初值 0 且每次尝试前自增），
        // 用例只锁定"不明状态不重试"，防止将来误用造成无限重试。
        Assert.Equal(RetryDecision.Fail, RetryPolicy.Decide(attempts, 5));
    }
}
