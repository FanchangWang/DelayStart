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
public static class BrokerReceiptPolicy
{
    /// <summary>判定一份作业是否内容完整、可被中转器执行。</summary>
    /// <param name="job">读到的作业（可为 <see langword="null"/>）。</param>
    /// <returns>
    /// <see cref="BrokerLaunchJob.Target"/>、<see cref="BrokerLaunchJob.ResultFile"/>、
    /// <see cref="BrokerLaunchJob.Token"/> 三者**都非空**才为 <see langword="true"/>。
    /// 返回 <see langword="true"/> 时保证 <paramref name="job"/> 非空，调用方不必再判一次。
    /// </returns>
    /// <remarks>
    /// 🔴 <b><see cref="BrokerLaunchJob.Token"/> 必须参与校验</b>，它不是可有可无的元数据：
    /// <see cref="IsAuthentic"/> 在期望令牌为空时恒判否，所以一份缺 Token 的作业会让
    /// 中转器照常启动目标、回写一份空令牌、随后被调度端拒收 —— 白等满
    /// <see cref="BrokerTimingPolicy.ResultPollTimeout"/> 才判失败，而日志指向的是
    /// 「中转器超时未回写」：把「作业本来就无效」说成「中转器慢了」，排查方向直接跑偏。
    /// 🔴 这条判定住在 Core 而不是中转器里，是为了**能被单测钉住** ——
    /// <c>DelayStart.LaunchBroker</c> 没有测试工程，那里的 if 条件被顺手删掉不会有人发现。
    /// 当前调度端必生成非空令牌，所以缺 Token 是死代码路径；这层校验是自保：
    /// 一旦有人改成条件生成令牌，这里给出的是正确原因而不是一个误导性的超时。
    /// </remarks>
    public static bool IsWellFormedJob(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] BrokerLaunchJob? job)
        => job is not null
           && !string.IsNullOrWhiteSpace(job.Target)
           && !string.IsNullOrWhiteSpace(job.ResultFile)
           && !string.IsNullOrWhiteSpace(job.Token);

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
