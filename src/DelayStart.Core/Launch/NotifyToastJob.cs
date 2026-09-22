namespace DelayStart.Core.Launch;

/// <summary>
/// 通知中转器（<c>DelayStart.NotifyBroker.exe</c>）的作业描述（N1，2026-09-22 批复）。
/// </summary>
/// <remarks>
/// <para>
/// 调度端写进一次性作业文件（<c>%TEMP%\DelayStart\notify\&lt;Guid&gt;\job.json</c>），
/// 中转器读取后组 toast XML 发出。schema 由 <c>BrokerJsonContext</c> 源生成共享
/// （与 UIAccess 中转器的作业同一份上下文）。
/// </para>
/// <para>
/// 🔴 中转器**只认作业里给的东西**：不读配置、不注册任何系统资源、不自行解析 AUMID ——
/// 它是"跑完即退"的哑进程，AUMID 未登记时 <c>Show()</c> 失败记日志退出码 3，仅此而已。
/// </para>
/// </remarks>
public sealed record NotifyToastJob
{
    /// <summary>
    /// 本产品的应用用户模型 ID（AUMID）。跨进程契约，不要改。
    /// </summary>
    /// <remarks>
    /// 🔴 必须与 <c>ShellRegistrationService.AppUserModelId</c>（开始菜单快捷方式上登记的）
    /// 一致 —— 通知的名字与图标都由那个快捷方式提供。值收拢在这里是为了让
    /// Scheduler（不能引用 Management）与 NotifyBroker（同理）也能拿到同一个常量。
    /// </remarks>
    public const string DefaultAumid = "DelayStart";

    /// <summary>调度完成通知的替换标识（与守卫的 guard-change 不同 Tag，避免通知中心互相替换）。</summary>
    public const string ScheduleDoneTag = "schedule-done";

    /// <summary>调度完成通知的替换分组（与守卫同组即可 —— 替换按 Tag+Group 联合判定）。</summary>
    public const string ScheduleDoneGroup = "delaystart";

    /// <summary>通知点击后经 <c>delaystart:</c> 协议落到的位置（D82：运行日志页）。</summary>
    public const string ScheduleDoneLaunch = "delaystart://runs-log";

    /// <summary>发出通知所用的 AUMID（见 <see cref="DefaultAumid"/>）。</summary>
    public string Aumid { get; init; } = DefaultAumid;

    /// <summary>通知替换标识（同 Tag + Group 的通知互相替换而不是堆叠）。</summary>
    public string Tag { get; init; } = ScheduleDoneTag;

    /// <summary>通知替换分组。</summary>
    public string Group { get; init; } = ScheduleDoneGroup;

    /// <summary>通知标题（首行加粗）。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>通知正文（可多行）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>点击通知后由 Shell 打开的 URI（<c>activationType="protocol"</c>）。</summary>
    /// <remarks>
    /// 🔴 默认值**必须**是 <see cref="ScheduleDoneLaunch"/>，不能是 <see langword="null"/>/空串：
    /// 调度端构造作业时只填 Title / Message，其余全靠这里的默认值 —— 曾因默认值是
    /// <see langword="string.Empty"/> 导致发出去的 toast <c>launch=""</c>，
    /// 点击永远拉不起管理端（2026-09-22 真机实锤，见 notifybroker.log 的 <c>Launch=</c> 空值）。
    /// </remarks>
    public string Launch { get; init; } = ScheduleDoneLaunch;
}
