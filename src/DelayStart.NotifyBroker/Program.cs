using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Logging;
using DelayStart.Core.Serialization;
using DelayStart.Core.Services;

using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DelayStart.NotifyBroker;

/// <summary>
/// 通知中转器（N1，2026-09-22 用户批复）—— 调度端收尾通知的代发进程。
/// </summary>
/// <remarks>
/// <para>
/// <b>链路</b>：调度端 A（High）写作业 JSON → 经外壳令牌 + CPWT 降权拉起本程序 B
/// （Medium）→ B 读作业文件 → 组 toast XML → <c>ToastNotificationManager.Show()</c> →
/// 驻留约 500ms → 退出。
/// </para>
/// <para>
/// 🔴 <b>为什么必须经中转器（双重理由）</b>：
/// ① 调度端以 <c>RunLevel=Highest</c> 提权运行，Win10/11 抑制来自提权进程的系统通知，
///    必须由中完整性的进程代发；
/// ② 调度端是纯 Win32 NativeAOT（D24），TFM 无平台版本、没有 WinRT 投影，自己发不了。
/// </para>
/// <para>
/// 🔴 <b>职责单一</b>：无窗口、无托盘、不注册任何系统资源（AUMID 未登记 →
/// <c>Show()</c> 抛异常 → 退出码 3）、不写配置、不读管理端状态。
/// 通知发出后由系统持有，点击经 <c>delaystart:</c> 协议转给管理端 —— 本进程与点击无关。
/// </para>
/// <para>
/// 🔴 <b>Show 后必须驻留约 500ms</b>：未打包应用的本地 toast 在进程立即退出时
/// 可能被系统撤销（横幅还没弹出来进程就没了）。固定宽限后必退，无定时器、无监听、无残留。
/// </para>
/// <para>
/// 退出码（对齐 LaunchBroker 风格）：0 = 已发送；2 = 作业不可读；3 = 发送失败。
/// 日志：<c>%LOCALAPPDATA%\DelayStart\logs\notifybroker.log</c>；日志失败绝不影响发送主流程。
/// </para>
/// </remarks>
internal static partial class Program
{
    private const int ExitSent = 0;
    private const int ExitBadJob = 2;
    private const int ExitSendFailed = 3;

    /// <summary>Show 之后驻留的宽限时长（防"进程秒退把本地 toast 撤销"）。</summary>
    private static readonly TimeSpan DwellAfterShow = TimeSpan.FromMilliseconds(500);

    /// <summary>通知中转器主入口。</summary>
    /// <param name="args">唯一参数 = 作业文件路径。</param>
    /// <returns>进程退出码：0 已发送；2 作业不可读；3 发送失败。</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        // 日志最先建立：无参数（疑似手动双击）也要留下痕迹。
        var log = new FileLogger(new PathService().NotifyBrokerLogPath, "NotifyBroker", SystemClock.Instance);

        if (args.Length != 1)
        {
            log.Warn($"参数数量为 {args.Length}（期望 1 个作业文件路径），疑似手动双击启动 —— 退出码 {ExitBadJob}。");
            return ExitBadJob;
        }

        NotifyToastJob? job;
        try
        {
            var json = File.ReadAllText(args[0]);
            job = JsonSerializer.Deserialize(json, BrokerJsonContext.Default.NotifyToastJob);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"读取作业文件失败：{args[0]} —— 退出码 {ExitBadJob}。");
            return ExitBadJob;
        }

        if (job is null
            || string.IsNullOrWhiteSpace(job.Aumid)
            || string.IsNullOrWhiteSpace(job.Title)
            || string.IsNullOrWhiteSpace(job.Message))
        {
            log.Warn("作业内容无效（缺 Aumid / Title / Message）—— 退出码 " + ExitBadJob + "。");
            return ExitBadJob;
        }

        log.Info($"收到作业：Aumid={job.Aumid}；Tag={job.Tag}；Group={job.Group}；Title={job.Title}；Launch={job.Launch}。");

        if (string.IsNullOrWhiteSpace(job.Launch))
        {
            // 纵深防御（2026-09-22 教训：launch="" 的通知点击拉不起管理端，且当时无任何日志痕迹）。
            // 通知本身照发 —— 完成通报的价值不因点击失效而归零；这里只负责让现场可查。
            log.Warn("作业 Launch 为空：本条通知点击后将无法拉起管理端（请检查调度端的作业构造）。");
        }

        try
        {
            var document = new XmlDocument();
            document.LoadXml(BuildXml(job));

            var toast = new ToastNotification(document)
            {
                // Tag + Group 相同 ⇒ 相同通知被替换而不是堆叠（调度完成只保留最新一条）。
                Tag = string.IsNullOrWhiteSpace(job.Tag) ? NotifyToastJob.ScheduleDoneTag : job.Tag,
                Group = string.IsNullOrWhiteSpace(job.Group) ? NotifyToastJob.ScheduleDoneGroup : job.Group,
            };

            // AUMID 必须与开始菜单快捷方式上登记的那个一致（ShellRegistrationService）。
            // 未登记（管理端从未启动过）时这里抛"元素未找到"—— 记日志、退出码 3。
            // 中转器绝不自行注册快捷方式（保持零 COM / 零 shell 写入）。
            ToastNotificationManager.CreateToastNotifier(job.Aumid).Show(toast);

            log.Info("系统通知已交给系统（Show 成功），驻留后退出。");
            Thread.Sleep(DwellAfterShow);
            return ExitSent;
        }
        catch (Exception ex)
        {
            // 通知是尽力而为的旁路（N12）：AUMID 未登记 / 专注助手 / 系统策略拦截，
            // 对调度端而言都只是"用户没看到"，调度端早已退出，这里只留日志。
            log.Error(ex, "系统通知发送失败 —— 退出码 " + ExitSendFailed + "。");
            return ExitSendFailed;
        }
    }

    /// <summary>拼通知 XML（与守卫 GuardToast 同款模板）。</summary>
    /// <remarks>
    /// 🔴 <c>activationType="protocol"</c> + <c>launch</c> 是点击行为的全部实现：
    /// 点击由 Shell 启动协议处理器（管理端）。刻意不用 <c>foreground</c> 激活 ——
    /// 那要求注册 COM 通知激活服务器（INotificationActivationCallback），而本进程发完即退。
    /// <c>duration</c> 刻意不设：它只影响横幅停留时长，"停在通知中心"是系统默认行为。
    /// </remarks>
    private static string BuildXml(NotifyToastJob job)
    {
        var builder = new StringBuilder();
        _ = builder.Append("<toast activationType=\"protocol\" launch=\"")
            .Append(Escape(job.Launch))
            .Append("\"><visual><binding template=\"ToastGeneric\">")
            .Append("<text>").Append(Escape(job.Title)).Append("</text>")
            .Append("<text>").Append(Escape(job.Message)).Append("</text>")
            .Append("</binding></visual></toast>");
        return builder.ToString();
    }

    /// <summary>
    /// 转义要放进 XML 文本节点的内容（与守卫同款：五个实体 + 剔除 XML 1.0 非法控制字符）。
    /// </summary>
    private static string Escape(string value) =>
        InvalidXmlChars().Replace(SecurityElement.Escape(value) ?? string.Empty, string.Empty);

    /// <summary>XML 1.0 不允许的控制字符（制表 / 换行 / 回车除外）。</summary>
    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]")]
    private static partial Regex InvalidXmlChars();
}
