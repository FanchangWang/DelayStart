using System.Reflection;

namespace DelayStart.Core.Tests;

/// <summary>
/// Phase 0 冒烟测试：只验证「测试宿主 + 项目引用图」在**运行期**是通的，不含业务断言。
///
/// 这个文件存在的理由不是凑数，它挡的是两个真实会出事的地方：
///
/// <list type="number">
/// <item>
///   <b>测试宿主配置</b>：xUnit v3 要求测试项目 <c>OutputType=Exe</c>（自托管在
///   Microsoft.Testing.Platform 上）。改回 Library 会直接报
///   "xUnit.net v3 test projects must be executable"—— 这个坑 Phase 0 真实踩过一次。
/// </item>
/// <item>
///   <b>零测试的静默失败</b>：没有本文件时，<c>dotnet test</c> 会输出
///   "运行了零个测试" 并**返回退出码 5**（MTP 的 ZeroTests）。后果是 CI 红色噪音，
///   或者更糟 —— 让人以为"测试通过"。
/// </item>
/// </list>
///
/// 业务断言从 Phase 1 开始写（DelayCalculator / ItemKeyBuilder / ConfigService /
/// LaunchResultEvaluator …）。届时可以删掉本文件，但**必须先有替代它的真实用例**。
/// </summary>
public sealed class ScaffoldSmokeTests
{
    /// <summary>
    /// 引用图断言：Core 与 Management 能按程序集名解析到，说明 ProjectReference 的
    /// "复制到输出目录"这一环没断。
    /// （编译期能引用、运行期加载失败，是另一类故障，编译通过挡不住。）
    /// </summary>
    [Fact]
    public void ReferencedAssembliesResolveAtRuntime()
    {
        Assert.NotNull(Assembly.Load("DelayStart.Core"));
        Assert.NotNull(Assembly.Load("DelayStart.Management"));
    }

    /// <summary>
    /// 运行时断言：测试宿主跑在 .NET 10 上。
    /// 挡的是 global.json / TFM 被误改导致整个测试矩阵悄悄降级。
    /// </summary>
    [Fact]
    public void TestHostRunsOnNet10()
    {
        Assert.Equal(10, Environment.Version.Major);
    }
}
