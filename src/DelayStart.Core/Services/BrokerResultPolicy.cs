using DelayStart.Core.Launch;

namespace DelayStart.Core.Services;

/// <summary>
/// 中转器回执的判定（FR-5.9 / D70 / B4）。**纯逻辑**，便于单测。
/// </summary>
/// <remarks>
/// <para>
/// 回答一个问题：拿到 <see cref="Launch.BrokerLaunchResult"/> 之后，调度端该不该把这次启动
/// 判成失败。
/// </para>
/// <para>
/// 🔴 关键在于分清两种"进程已经不在了"：
/// </para>
/// <list type="bullet">
/// <item><description><b>显式非零退出码</b>（中转器等到了退出事件并读到了码）⇒ 目标**确实启动失败**。
/// 必须在**此刻**判失败：若放过它去走延时复查，进程早已消失，
/// <c>ProbeProcess</c> 会按 E4 的宽容原则返回"退出码 0"，于是一个真的失败的目标
/// 被静默报成"已启动"—— 这是 B4 修的那个 bug。</description></item>
/// <item><description><b>退出码为 0 的秒退</b> ⇒ 判成功。大量程序在拉起已有实例后会立刻退出
/// （<c>msedge.exe</c> 是典型），严格判定会产生大量假失败，用户会很快不再相信这个列表（E4）。</description></item>
/// <item><description><b>退出码未知</b>（中转器没读到码）⇒ 按 E4 宽容处理，但要留下痕迹 ——
/// 这是"不知道"而不是"成功"，日志里必须能看出来。</description></item>
/// </list>
/// <para>
/// 另有一条独立于秒退的规则：中转器自身没把目标创建出来（<c>Ok == false</c>）⇒ 失败。
/// 它的判定要看 <c>Win32Error</c>（740 = 目标要求提权 / 清单校验未过），文案在调用方。
/// </para>
/// </remarks>
public static class BrokerResultPolicy
{
    /// <summary>依据中转器回执判定这次启动是否失败。</summary>
    /// <param name="result">中转器回执。</param>
    /// <returns>失败原因；<see langword="null"/> 表示按成功继续。</returns>
    public static string? FailureReason(BrokerLaunchResult? result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Ok)
        {
            return DescribeCreationFailure(result);
        }

        if (result.ExitedImmediately
            && result.ExitCode is { } exitCode
            && exitCode != 0)
        {
            return $"目标进程创建后立即退出（退出码 {exitCode}）。";
        }

        return null;
    }

    /// <summary>把"目标根本没被创建出来"的回执转成人能看懂的原因。</summary>
    /// <param name="result">中转器回执（<see cref="BrokerLaunchResult.Ok"/> 为 false）。</param>
    /// <returns>失败原因描述。</returns>
    private static string DescribeCreationFailure(BrokerLaunchResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Message))
        {
            return $"中转器报告目标启动失败（Win32Error={result.Win32Error}）。";
        }

        // 740 值得单独点名：它是"目标要求提权 / 清单校验未过"最常见的形态，
        // 用户看到裸数字完全无从判断发生了什么。
        var elevationHint = result.Win32Error == 740
            ? "（740 = ERROR_ELEVATION_REQUIRED：目标要求提权或清单校验未过）"
            : string.Empty;

        return $"中转器报告目标启动失败：{result.Message}{elevationHint}";
    }

    /// <summary>回执是否需要在日志里留一条"秒退但按成功处理"的说明。</summary>
    /// <param name="result">中转器回执。</param>
    /// <returns>
    /// 需要记录时返回一句话（含退出码或"未知"），否则返回 <see langword="null"/>。
    /// </returns>
    public static string? SecondsExitNote(BrokerLaunchResult? result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.ExitedImmediately)
        {
            return null;
        }

        return result.ExitCode is { } exitCode
            ? $"目标进程创建后立即退出（退出码 {exitCode}）—— 按 E4 记为成功。"
            : "目标进程创建后立即退出（退出码未知）—— 按 E4 记为成功，但这次退出没有被确认过。";
    }
}
