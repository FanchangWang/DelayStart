using DelayStart.Core.Launch;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="NotifyToastJob"/> 契约默认值的锁定测试。
/// </summary>
/// <remarks>
/// 🔴 背景（2026-09-22 真机实锤）：<see cref="NotifyToastJob.Launch"/> 的默认值曾是
/// <see langword="string.Empty"/>（<c>ScheduleDoneLaunch</c> 常量从未接成默认值），
/// 调度端按注释"靠契约默认值"构造作业，发出的 toast <c>launch=""</c>，
/// 点击永远拉不起管理端。这组测试把"默认值 = 契约常量"焊死，改坏任何一处立刻红灯。
/// </remarks>
public sealed class NotifyToastJobTests
{
    [Fact]
    public void Defaults_Launch_IsScheduleDoneLaunch()
        => Assert.Equal(NotifyToastJob.ScheduleDoneLaunch, new NotifyToastJob().Launch);

    [Fact]
    public void Defaults_Launch_IsWellFormedProtocolUri()
    {
        var launch = new NotifyToastJob().Launch;
        Assert.StartsWith("delaystart://", launch, StringComparison.Ordinal);
        Assert.True(launch.Length > "delaystart://".Length, "launch URI 必须带令牌，不能只有 scheme。");
    }

    [Fact]
    public void Defaults_Aumid_IsContractAumid()
        => Assert.Equal("DelayStart", new NotifyToastJob().Aumid);

    [Fact]
    public void Defaults_TagAndGroup_AreScheduleDoneIdentity()
    {
        var job = new NotifyToastJob();
        Assert.Equal(NotifyToastJob.ScheduleDoneTag, job.Tag);
        Assert.Equal(NotifyToastJob.ScheduleDoneGroup, job.Group);
    }
}
