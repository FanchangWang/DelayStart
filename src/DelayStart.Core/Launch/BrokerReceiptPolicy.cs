namespace DelayStart.Core.Launch;

/// <summary>
/// UIAccess 降权链的<b>回执令牌</b>：一次性令牌的生成与回执真伪判定。**纯逻辑**，便于单测。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 这条链上曾经有一个静默的假成功：作业与结果目录在
/// <c>%TEMP%\DelayStart\broker\&lt;Guid&gt;</c> 下，<b>同用户的任何 Medium 进程</b>都能
/// 抢先落一份 <c>result.json</c>（<c>Ok=true</c>）。调度端此前只判 <c>File.Exists</c> +
/// 反序列化，<b>不校验写入者</b>，于是一个从未运行过的 uiAccess 条目被永久标记成
/// "已启动"，又因 D20「不提权回退」不再重试 —— 用户界面上一切正常。
/// </para>
/// <para>
/// 修法是让调度端在写作业时带一枚随机令牌，中转器原样回写，读取时逐字节比对。
/// 只有真正拿到作业的中转器才知道这枚令牌，因此"这份回执来自本次作业的中转器"
/// 从"不可知"变成"可校验"。
/// </para>
/// <para>
/// 🔴 刻意<b>不收紧目录 ACL</b>：同项目已定的教训是同用户进程的 DACL 能做的事一样多，
/// ACL 挡不住同用户进程，令牌才能。
/// </para>
/// </remarks>
internal static class BrokerReceiptPolicy
{
    /// <summary>
    /// 生成一枚新的一次性回执令牌。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="Guid.NewGuid"/> 的 "N" 格式，与作业目录名的取法一致（同一风格，不引入第二套随机源）。
    /// 🔴 必须每次调用都不同：令牌一旦可预测或可复用，同一用户的 Medium 进程就能
    /// 提前算出它并伪造回执，本类整个存在的意义就没了。
    /// </remarks>
    internal static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>判定一份回执是否确实来自本次作业的中转器。</summary>
    /// <param name="result">读到的回执（可为 <see langword="null"/>）。</param>
    /// <param name="expectedToken">调度端本次作业生成、期望回写的令牌。</param>
    /// <returns>令牌逐字节相同才为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 <b>空令牌永不通过</b>：即使 <paramref name="expectedToken"/> 为空串也判否。
    /// 否则一个"双方都没带令牌"的旧版本组合会静默通过校验，防伪造能力形同虚设。
    /// 缺 <c>Token</c> 字段的反序列化结果就是空串，因此天然落在这里。
    /// </remarks>
    internal static bool IsAuthentic(BrokerLaunchResult? result, string expectedToken)
        => result is not null
           && !string.IsNullOrEmpty(expectedToken)
           && string.Equals(result.Token, expectedToken, StringComparison.Ordinal);
}
