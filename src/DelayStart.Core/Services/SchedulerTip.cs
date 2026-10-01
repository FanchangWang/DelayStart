namespace DelayStart.Core.Services;

/// <summary>
/// 调度端托盘悬停提示的文案合成（v0.6.1）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么在 Core 而不是调度端</b>：调度端**没有测试工程**，纯逻辑放在那儿就等于
/// 没有测试。这与 <c>TargetPrefilter</c> 是同一个理由 —— 不是"想复用"，是"要能测"。
/// 而它确实只有一处调用方（<c>SchedulerEngine.BuildTooltip</c>），所以不做成策略接口。
/// </para>
/// <para>
/// 🔴 <b>三行，不是三段</b>：<c>NOTIFYICONDATA.szTip</c> 是 128 字符的定长缓冲（含终止符），
/// 而 Windows 托盘 tooltip **真的支持多行**（<c>\r\n</c>）。但超过缓冲区就是**静默截断** ——
/// 截掉一半的句子比短句糟糕得多，所以这里在**合成时**就把全文压进预算，而不是交给调用方截。
/// </para>
/// <para>
/// 三行的分工（用户 2026-10-02 选定方案 B）：
/// <list type="number">
/// <item><description><b>DelayStart</b> —— 品牌。通知中心与任务栏都可能把它裁成一行，
/// 第一行必须是名字，否则用户看到一段没头没尾的状态文字。</description></item>
/// <item><description><b>进度</b> —— 已成功启动几项 / 共几项，失败与跳过**各自单列**。
/// 三者缺一，用户就算不出账（2026-10-02 用户实测：跳过没统计，两边数字对不上）。</description></item>
/// <item><description><b>下一项</b> —— 名称与倒计时。这两件事原先挤在同一行里，
/// 结果"下一项 12 秒"永远只有一个数字而没有名字，用户不知道在等谁。</description></item>
/// </list>
/// </para>
/// </remarks>
public static class SchedulerTip
{
    /// <summary>品牌行（固定）。</summary>
    public const string BrandLine = "DelayStart";

    /// <summary>行分隔符（CRLF —— 托盘 tooltip 的通用写法，LF 在个别 shell 上不换行）。</summary>
    private const string LineBreak = "\r\n";

    /// <summary>
    /// 字符预算：<c>szTip</c> 是 128 字符且含终止符，所以可用 127。
    /// </summary>
    /// <remarks>
    /// 与 <c>TrayIconHost.MaxTipLength</c> 是同一个数字的**两份**：
    /// 一个是缓冲区事实，一个在这里是合成预算。改一处必须改另一处 ——
    /// 而这里之所以要独立一份，是因为合成侧必须**自己**知道预算才能截得漂亮，
    /// 不能写完再让调用方去砍。
    /// </remarks>
    public const int MaxLength = 127;

    /// <summary>第二行留白的最小值（第一行 10 字 + 两个 CRLF = 12，剩给后两行）。</summary>
    private const int MinRemainingBudget = 12;

    /// <summary>「即将启动」的判定阈值：剩余秒数 ≤ 它就算"马上"。</summary>
    private const int ImminentSeconds = 2;

    /// <summary>合成"启动中"的提示。</summary>
    /// <param name="succeededCount">已**成功**启动的条目数。</param>
    /// <param name="failedCount">已失败的条目数。</param>
    /// <param name="skippedCount">已跳过的条目数（进程已在跑等）。</param>
    /// <param name="totalCount">本轮计划条目数。</param>
    /// <param name="nextName">下一项的名称；没有下一项时传 <see langword="null"/>。</param>
    /// <param name="remainingSeconds">距离下一项启动的秒数（向上取整）。</param>
    /// <returns>三行提示全文。</returns>
    /// <remarks>
    /// 🔴 <b>跳过必须与成功/失败分开报</b>（用户 2026-10-02 实测）：
    /// 「已启动 6/8」而 8 项里有 2 项是跳过的，用户对不上账 —— 他看到的是一个
    /// 永远走不到头的进度条。四个计数缺一不可，签名就是为此拆开的。
    /// </remarks>
    public static string Building(
        int succeededCount,
        int failedCount,
        int skippedCount,
        int totalCount,
        string? nextName,
        int remainingSeconds)
        => Compose(
            BrandLine,
            ProgressLine(succeededCount, failedCount, skippedCount, totalCount, "已启动"),
            nextName is null
                ? "正在完成…"
                : remainingSeconds <= ImminentSeconds
                    ? $"正在启动：{nextName}…"
                    : $"下一项：{nextName} · {remainingSeconds} 秒后");

    /// <summary>合成"已收尾"的提示。</summary>
    /// <param name="succeededCount">成功启动的条目数。</param>
    /// <param name="failedCount">失败条目数。</param>
    /// <param name="skippedCount">跳过条目数。</param>
    /// <param name="totalCount">本轮计划条目数。</param>
    /// <returns>三行提示全文。</returns>
    /// <remarks>
    /// 🔴 <b>「全部启动」只在真的全部启动时才许出现</b>：原先的条件只有
    /// <c>failedCount == 0</c>，于是"6 项启动 + 2 项跳过"会被写成
    /// 「调度完成 · 8 项全部启动」—— 那是**对用户说假话**，而且是唯一一处
    /// 用户不会再去查日志的地方（托盘提示只停留两秒）。
    /// </remarks>
    public static string Finished(int succeededCount, int failedCount, int skippedCount, int totalCount)
    {
        var clean = failedCount == 0 && skippedCount == 0;

        return Compose(
            BrandLine,
            clean
                ? $"调度完成 · {totalCount} 项全部启动"
                : "调度完成 · " + ProgressLine(succeededCount, failedCount, skippedCount, totalCount, "启动"),
            failedCount > 0
                ? "点托盘图标看调度日志"
                : skippedCount > 0
                    ? "跳过的项未启动，点托盘看原因"
                    : "即将退出");
    }

    /// <summary>拼"成功/失败/跳过"的计数段。</summary>
    /// <param name="succeededCount">成功数。</param>
    /// <param name="failedCount">失败数。</param>
    /// <param name="skippedCount">跳过数。</param>
    /// <param name="totalCount">总数。</param>
    /// <param name="label">计数的标签（"已启动" / "启动"）。</param>
    /// <returns>计数段。</returns>
    /// <remarks>
    /// 🔴 **失败与跳过往后才出现**：都是 0 时不占位置，而这两位数字是用户唯一
    /// 需要在"一切正常"时被排除掉视线的东西。
    /// </remarks>
    private static string ProgressLine(
        int succeededCount,
        int failedCount,
        int skippedCount,
        int totalCount,
        string label)
    {
        var text = $"{label} {succeededCount}/{totalCount}";

        if (failedCount > 0)
        {
            text += $" · 失败 {failedCount}";
        }

        if (skippedCount > 0)
        {
            text += $" · 跳过 {skippedCount}";
        }

        return text;
    }

    /// <summary>合成三行并把全文压进 <see cref="MaxLength"/>。</summary>
    /// <param name="brand">第一行。</param>
    /// <param name="progress">第二行。</param>
    /// <param name="detail">第三行（可能是要截断的那一行）。</param>
    /// <returns>全文。</returns>
    /// <remarks>
    /// 🔴 截断**只发生在第三行**，且尽量在词中间断开处收尾：
    /// 前两行是被反复读到的主信息，砍它们等于把最该看到的部分让给一个超长的程序名。
    /// 留 <c>…</c> 是为了让用户知道"这里被砍过"，而不是以为名字就那么长。
    /// </remarks>
    private static string Compose(string brand, string progress, string detail)
    {
        var fixedPart = brand + LineBreak + progress + LineBreak;
        var budget = MaxLength - fixedPart.Length;

        if (budget <= 0)
        {
            // 理论上到不了（第一行 10 + 第二行不超过 ~24 字），但不能因此让返回值超预算：
            // 返回超长的结果会被 SzTip 静默截断，而截断点由系统决定，不是我们决定的。
            return Truncate(fixedPart.TrimEnd('\r', '\n'), MaxLength);
        }

        return fixedPart + Truncate(detail, budget);
    }

    /// <summary>把一段文本截到指定预算内，必要时补一个省略号。</summary>
    /// <param name="text">原文本。</param>
    /// <param name="budget">预算字符数。</param>
    /// <returns>截断后的文本。</returns>
    private static string Truncate(string text, int budget)
    {
        if (text.Length <= budget)
        {
            return text;
        }

        if (budget <= 1)
        {
            return text[..Math.Max(0, budget)];
        }

        return string.Concat(text.AsSpan(0, budget - 1), "…");
    }
}