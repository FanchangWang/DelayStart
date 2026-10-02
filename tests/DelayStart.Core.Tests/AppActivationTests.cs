using DelayStart.Core.Launch;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="AppActivation"/> 的单元测试：管理端启动参数的跨进程契约（D74 / D79 / D82）。
/// </summary>
/// <remarks>
/// 🔴 <b>为什么这些字面量值得单独钉住</b>：它们一旦漂移，症状是"点了通知没反应"——
/// 而"没反应"与"通知根本没发出来"在用户眼里一模一样，几乎无法从现场倒推。
/// 编译期能发现的只有"忘了改引用它的那个文件"，改错了字面量本身没人拦。
/// 所以这里同时断言<b>常量值</b>与<b>判定函数</b>两层。
/// </remarks>
public sealed class AppActivationTests
{
    [Fact]
    public void 常量_提权重拉标记_拼写不得漂移()
    {
        // 🔴 D82 防"提权重拉死循环"的唯一凭据。入口在未提权时把命令原样重拉一遍，
        // 万一同一进程又被判成未提权（UAC 被禁用、令牌异常……），没有这个标记就无限弹 UAC。
        Assert.Equal("--elevation-attempted", AppActivation.ElevationAttemptArgument);
    }

    [Fact]
    public void 常量_看日志参数_拼写不得漂移()
    {
        // D18：调度端通知点击唤起管理端并落到「调度日志」页。
        Assert.Equal("--goto-log", AppActivation.GotoLogArgument);
    }

    [Fact]
    public void 常量_看自启动项参数_拼写不得漂移()
    {
        // D74：守卫通知点击。
        Assert.Equal("--goto-startup", AppActivation.GotoStartupArgument);
    }

    [Fact]
    public void 常量_失效条目参数_拼写不得漂移()
    {
        // D77：并入「延时启动」页后仍保留该参数。
        Assert.Equal("--stale", AppActivation.StaleArgument);
    }

    [Fact]
    public void 常量_来源页参数前缀_拼写不得漂移()
    {
        // 形如 --source=registry。
        Assert.Equal("--source=", AppActivation.SourceArgumentPrefix);
    }

    [Fact]
    public void 常量_协议方案与URI前缀_必须自洽且拼写不得漂移()
    {
        // 协议处理器注册在 HKCU\Software\Classes\delaystart；
        // 通知里 launch 属性写 delaystart://delay。两处不一致 = 点了通知没反应。
        Assert.Equal("delaystart", AppActivation.ProtocolScheme);
        Assert.Equal("delaystart://", AppActivation.ProtocolUriPrefix);
        Assert.StartsWith(
            $"{AppActivation.ProtocolScheme}://",
            AppActivation.ProtocolUriPrefix,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 常量_五个以双横线开头的参数_拼写不得互相重复()
    {
        var arguments = new[]
        {
            AppActivation.GotoLogArgument,
            AppActivation.GotoStartupArgument,
            AppActivation.StaleArgument,
            AppActivation.ElevationAttemptArgument,
        };

        Assert.Equal(arguments.Length, arguments.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void IsElevationAttempt_参数为null_返回false()
    {
        // 命令行里根本没有这个参数是常态，不能当成"已尝试过提权"。
        Assert.False(AppActivation.IsElevationAttempt(null));
    }

    [Fact]
    public void IsElevationAttempt_参数为标记本身_返回true()
    {
        Assert.True(AppActivation.IsElevationAttempt(AppActivation.ElevationAttemptArgument));
    }

    [Fact]
    public void IsElevationAttempt_参数大小写不同_返回true()
    {
        // 判定刻意不区分大小写：命令行参数的大小写敏感性在各处实现里并不统一，
        // 这里宁可放过（多报一次"已尝试过"）也不要漏判成"没试过"而进死循环。
        Assert.True(AppActivation.IsElevationAttempt("--ELEVATION-ATTEMPTED"));
        Assert.True(AppActivation.IsElevationAttempt("--Elevation-Attempted"));
    }

    [Fact]
    public void IsElevationAttempt_参数为无关字符串_返回false()
    {
        // 几种容易"看起来像"的情形：真的很容易误判。
        Assert.False(AppActivation.IsElevationAttempt(string.Empty));
        Assert.False(AppActivation.IsElevationAttempt("   "));
        Assert.False(AppActivation.IsElevationAttempt("--goto-log"));
        Assert.False(AppActivation.IsElevationAttempt("--elevation-attempted-please"));
        Assert.False(AppActivation.IsElevationAttempt("--elevation-attempts"));
        Assert.False(AppActivation.IsElevationAttempt("-elevation-attempted"));
        Assert.False(AppActivation.IsElevationAttempt("--elevationattempted"));
    }
}