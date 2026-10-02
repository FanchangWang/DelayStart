using System.Reflection;

using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ElevationCheck"/> 的令牌提升判定（D20 / D78）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <see cref="ElevationCheck.IsElevated"/> 必须读真令牌，而单元测试禁止触碰进程
/// （硬约束），因此这里直接测它抽出来的纯判定面 <c>IsElevatedFromTokenInfo</c>。
/// 这条判据守着 D20 不变量「图形界面与 CLI 业务绝不以非提权身份运行」——
/// 判错的方向永远是**漏门**（把未提权当成已提权），所以用例都按漏门方向设计。
/// </para>
/// <para>
/// 「必须查 <c>TokenElevation</c> 而不是 <c>IsInRole(Administrator)</c>」这条判据本身
/// 由本类的常量用例钉住（见 <c>IsElevated_查的是TokenElevation而非IsInRole_常量不得漂移</c>）。
/// </para>
/// </remarks>
public sealed class ElevationCheckTests
{
    /// <summary>读取 <see cref="ElevationCheck"/> 里的私有常量（缺失即视为测试失败）。</summary>
    private static object ReadPrivateConstant(string name)
        => typeof(ElevationCheck)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetRawConstantValue()
            ?? throw new MissingFieldException(typeof(ElevationCheck).FullName, name);

    [Fact]
    public void IsElevatedFromTokenInfo_查询成功且已提升_返回真()
    {
        // Arrange：GetTokenInformation 成功，读回 IsElevated = 1（计划任务 Highest / 管理端批准）。
        const bool querySucceeded = true;
        const uint elevationValue = 1;

        // Act
        var result = ElevationCheck.IsElevatedFromTokenInfo(querySucceeded, elevationValue);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsElevatedFromTokenInfo_查询成功但未提升_返回假()
    {
        // Arrange：🔴 最关键的一条。IsElevated = 0 就是"用户手动双击 exe"那个令牌 ——
        // 调度端与守卫端靠它静默退出，从而保证全场只有计划任务一条运行路径（D20）。
        // D82 挡掉 IsInRole(Administrator) 正是因为：UAC 下的**受限管理员**令牌，
        // IsInRole 同样返回 true，而本条判据必须把它判成未提权。
        // 一旦它被改成"有值就算"，用户双击就会多出第二条不受管理的运行路径。
        const bool querySucceeded = true;
        const uint elevationValue = 0;

        // Act
        var result = ElevationCheck.IsElevatedFromTokenInfo(querySucceeded, elevationValue);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsElevatedFromTokenInfo_查询失败_按保守方向返回假()
    {
        // Arrange：GetTokenInformation 返回 false，但结构体里残留着非零的 IsElevated
        // （失败时该字段内容未定义，可能是上一次调用留下的）。判据必须先看 ok。
        const bool querySucceeded = false;
        const uint elevationValue = 1;

        // Act
        var result = ElevationCheck.IsElevatedFromTokenInfo(querySucceeded, elevationValue);

        // Assert：查不到就不算已提升 —— 宁可拒绝运行，不放行。
        Assert.False(result);
    }

    [Fact]
    public void IsElevatedFromTokenInfo_查询失败且令牌值为零_返回假()
    {
        // Arrange：对照组。两个入参都指向"未提权"，用于钉住两个入参**同时**为假时
        // 与上面那条的差别只在 elevationValue —— 证明判据确实读了两个入参。
        const bool querySucceeded = false;
        const uint elevationValue = 0;

        // Act
        var result = ElevationCheck.IsElevatedFromTokenInfo(querySucceeded, elevationValue);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// 穷举查询结果 × 令牌取值：判据是「查询成功 **且** 取值非零」，
    /// 两条都必须是硬条件，缺一条就是 D20 漏门。
    /// </summary>
    /// <param name="querySucceeded">GetTokenInformation 是否成功。</param>
    /// <param name="elevationValue">读回的 TokenElevation.IsElevated。</param>
    /// <param name="expected">期望的判定结果。</param>
    [Theory]
    [InlineData(false, 0u, false)]
    [InlineData(false, 1u, false)]
    [InlineData(false, 2u, false)]
    [InlineData(false, uint.MaxValue, false)]
    [InlineData(true, 0u, false)]
    [InlineData(true, 1u, true)]
    // 2 与 uint.MaxValue 不是"看起来奇怪"的取值，而是"Win32 只承诺非零即提升"的直接后果：
    // 若判据写成 == 1（看着更"精确"），未来真出现别的取值就会被误判成未提权 ——
    // 那是另一种漏门（已提权却按未提权退出，用户看到"双击没反应"）。
    [InlineData(true, 2u, true)]
    [InlineData(true, uint.MaxValue, true)]
    public void IsElevatedFromTokenInfo_穷举查询结果与令牌取值_判据为查询成功且取值非零(
        bool querySucceeded,
        uint elevationValue,
        bool expected)
    {
        // Arrange / Act
        var result = ElevationCheck.IsElevatedFromTokenInfo(querySucceeded, elevationValue);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsElevated_查的是TokenElevation而非IsInRole_常量不得漂移()
    {
        // Arrange：ElevationCheck 的判据实现只有两个常量可被"顺手优化"掉 ——
        // ① TokenElevation 写成别的 infoClass；② TokenQuery 的访问掩码放宽/收紧。
        // 两者任一漂移都会让 D20 的门禁读到不同的东西，且**不报任何错**。
        var infoClass = ReadPrivateConstant("TokenElevation");
        var desiredAccess = ReadPrivateConstant("TokenQuery");

        // Act / Assert
        // ntddk.h / winnt.h：TokenElevation = 20（TokenInformationClass 枚举值）。
        Assert.Equal(20, (int)infoClass);
        // winnt.h：TOKEN_QUERY = 0x0008。
        Assert.Equal(0x0008u, (uint)desiredAccess);
    }
}
