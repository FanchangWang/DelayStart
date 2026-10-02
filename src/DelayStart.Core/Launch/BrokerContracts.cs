namespace DelayStart.Core.Launch;

/// <summary>
/// 调度端 → <c>DelayStart.LaunchBroker.exe</c>（UIAccess 中转器）的作业描述（D70）。
/// </summary>
/// <remarks>
/// <para>
/// 调度端（High）写成本 JSON 文件，经 CPWT 降权拉起中转器时以唯一参数传**作业文件路径**。
/// 走文件而不是命令行传参：目标路径与参数段里的引号、反斜杠、空格组合
/// （如 <c>--tail "C:\temp\sub dir\"</c>）在命令行二次转义下极易失真 ——
/// demo2 方法 6 用参数交接文件实测过这条路，JSON 转义由序列化器保证。
/// </para>
/// </remarks>
public sealed class BrokerLaunchJob
{
    /// <summary>目标可执行文件完整路径（<c>uiAccess="true"</c> 的 exe）。</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>目标命令行参数段（原样转交 ShellExecute 的 lpParameters，可为空）。</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>目标工作目录（可为空 —— 空则由 ShellExecute 用默认解析）。</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>结果回写文件完整路径（中转器写 <see cref="BrokerLaunchResult"/> 到这里）。</summary>
    public string ResultFile { get; init; } = string.Empty;

    /// <summary>
    /// 本次作业的一次性回执令牌（调度端随机生成，见 <see cref="BrokerReceiptPolicy.NewToken"/>）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>这个字段的全部意义是防伪造，不是业务数据。</b>
    /// 作业与结果目录在 <c>%TEMP%\DelayStart\broker\&lt;Guid&gt;</c> 下，同用户的任何 Medium
    /// 进程（含本调度端自己拉起的其他降权程序）都能抢先落一份
    /// <c>result.json</c>（<c>Ok=true</c>）。而调度端此前只判 <c>File.Exists</c> + 反序列化，
    /// <b>完全不校验写入者</b> —— 于是一个从没跑过的 uiAccess 条目会被永久标记成"已启动"，
    /// 且因 D20「不提权回退」不再重试，用户界面上一切正常。
    /// <para>
    /// 令牌把"这份回执确实是本次作业的中转器写的"变成可校验的事实：只有读到作业的
    /// 中转器才能原样回写它。🔴 刻意<b>不用 ACL 收紧目录</b> —— 同项目已定的教训是
    /// ACL 挡不住同用户进程（同用户的令牌拥有同一份 DACL 能做的事），令牌才能。
    /// </para>
    /// <para>
    /// 全程不做跨版本兼容（D121）：调度端与中转器同目录部署、同时替换，旧中转器回写不出
    /// 这个字段的作业一律按"回执无效"处理（继续轮询至超时 ⇒ 判失败），不会静默放行。
    /// </para>
    /// </remarks>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// 中转器为识别"目标秒退"而等待目标退出的时长（毫秒）。
    /// 超时即认为目标在正常运行（GUI 程序不会退出），立即回写结果。
    /// </summary>
    public int WaitTimeoutMs { get; init; } = 4_000;
}

/// <summary>
/// <c>DelayStart.LaunchBroker.exe</c> → 调度端的目标启动结果（D70）。
/// </summary>
/// <remarks>
/// 🔴 这是<b>目标应用 C</b> 的启动状态，不是中转器 B 自己的 ——
/// B 的进程退出码只表达"结果有没有回写成功"（0=已回写；2=作业文件不可读；3=回写失败）。
/// </remarks>
public sealed class BrokerLaunchResult
{
    /// <summary>目标是否被创建出来（ShellExecuteEx 成功，或经 DDE/外壳激活但句柄不可用也算创建）。</summary>
    public bool Ok { get; init; }

    /// <summary>目标进程 ID；经外壳激活拿不到句柄时为 <see langword="null"/>。</summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// 目标在等待窗口内自行退出（true ≠ 失败：进程确实创建过，秒退原因看 <see cref="ExitCode"/>）。
    /// </summary>
    public bool ExitedImmediately { get; init; }

    /// <summary>目标秒退时的退出码（<see cref="ExitedImmediately"/> 为 false 时无意义）。</summary>
    public uint? ExitCode { get; init; }

    /// <summary>ShellExecuteEx 失败时的 Win32 错误码（如 740 = ERROR_ELEVATION_REQUIRED）。</summary>
    public int Win32Error { get; init; }

    /// <summary>附加说明（中文，失败原因 / 外壳激活提示），成功且句柄可用时为 <see langword="null"/>。</summary>
    public string? Message { get; init; }

    /// <summary>
    /// 原样回写的作业令牌（<see cref="BrokerLaunchJob.Token"/>）；中转器**不得**自行生成。
    /// </summary>
    /// <remarks>
    /// 🔴 调度端只在令牌与本次期望值<b>逐字节相同</b>时才采信这份回执（见
    /// <see cref="BrokerReceiptPolicy.IsAuthentic"/>）；令牌缺失或不符一律当作
    /// "这不是我的中转器写的"，继续轮询直到超时 ⇒ 该条目判失败。
    /// 这就是防同用户 Medium 进程伪造 <c>Ok=true</c> 回执的唯一凭据，
    /// <b>删掉本字段等于删掉整个防伪造能力</b>。
    /// </remarks>
    public string Token { get; init; } = string.Empty;
}
