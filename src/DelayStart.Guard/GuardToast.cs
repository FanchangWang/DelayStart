using System.Security;
using System.Text;
using System.Text.RegularExpressions;

using DelayStart.Core.Launch;
using DelayStart.Management.Services;

using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DelayStart.Guard;

/// <summary>
/// 守卫的系统通知（D79）：右下角横幅 + 停留通知中心，点击拉起管理端。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不是弹框</b>：原来的 <c>TaskDialogIndirect</c> 是**屏幕中央的模态框**，
/// 它把用户从手上正在做的事里拽出来，还会一直占着位置等 60 秒
/// （2026-09-22 用户批复：改用系统通知，别打断用户）。系统通知是同一件事的"低打扰"形态：
/// 横幅自己消失，内容停在通知中心，用户想起来再点。
/// </para>
/// <para>
/// <b>通知是"发出即忘"的</b>：本进程发完就退出，通知与用户后续的点击都由系统持有与转发。
/// 用户点通知时真正被启动的是**管理端**（经 <c>delaystart:</c> 协议，
/// 见 <see cref="ShellRegistrationService"/>），跟这里没有任何回调关系 ——
/// 这也正是它比"进程内模态框"更适合周期任务的原因：守卫不必为了等一个可能永远不来的点击而活着。
/// </para>
/// <para>
/// 🔴 <b>同一 <see cref="Tag"/> + <see cref="Group"/> 的通知会互相替换</b>。
/// 周期档位下每轮都可能"有变化"，不替换的话通知中心会被同一件事刷满。
/// 替换的代价是上一轮的具体条目名被覆盖 —— 但那些条目在管理端里看得到，
/// 而"通知中心被刷屏"是会让人直接关闭通知权限的。
/// </para>
/// <para>
/// 🔴 <b>失败一律只记日志</b>：通知是尽力而为的旁路。真实世界里它会因为
/// "系统通知被用户关掉 / 专注助手开着 / 组策略禁用"而不显示，这些都不是巡检失败。
/// </para>
/// </remarks>
internal static partial class GuardToast
{
    /// <summary>通知标题（系统渲染的应用名之上的那一行）。</summary>
    public const string Heading = "检测到自启动项变化";

    /// <summary>本节条目名最多列几个（超出折成"等 N 项"）。</summary>
    public const int MaxNames = 6;

    /// <summary>替换用的通知标识。</summary>
    private const string Tag = "guard-change";

    /// <summary>替换用的通知分组。</summary>
    private const string Group = "delaystart";

    /// <summary>
    /// 发一条系统通知。
    /// </summary>
    /// <param name="launchTarget">点击后落到的位置令牌（取值见 <see cref="UiNavigationTarget"/>）。</param>
    /// <param name="summary">第二行：计数概览。</param>
    /// <param name="detail">第三行：具体条目名（可为空串）。</param>
    /// <returns>通知已交给系统时为 <see langword="true"/>；失败（已记日志）为 <see langword="false"/>。</returns>
    public static bool TryShow(string launchTarget, string summary, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchTarget);
        ArgumentNullException.ThrowIfNull(summary);

        try
        {
            var document = new XmlDocument();
            document.LoadXml(BuildXml(launchTarget, summary, detail));

            var toast = new ToastNotification(document)
            {
                // Tag + Group 相同 ⇒ 相同通知被替换而不是堆叠。
                Tag = Tag,
                Group = Group,
            };

            // AUMID 必须与开始菜单快捷方式上登记的那个一致（ShellRegistrationService）。
            // 不传参数的重载对未打包进程会直接抛"元素未找到"。
            ToastNotificationManager
                .CreateToastNotifier(ShellRegistrationService.AppUserModelId)
                .Show(toast);

            return true;
        }
        catch (Exception ex)
        {
            GuardLog.Warn(ex, "系统通知发送失败（本次不通报，巡检结果仍已写入日志）");
            return false;
        }
    }

    /// <summary>
    /// 拼通知 XML。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <c>activationType="protocol"</c> + <c>launch="delaystart://…"</c> 是点击行为的全部实现：
    /// 点击由 Shell 启动协议处理器（管理端）。刻意不用 <c>foreground</c> 激活 ——
    /// 那要求注册一个 COM 通知激活服务器（<c>INotificationActivationCallback</c>），
    /// 而这条通知的发起进程（守卫）发完就退出了。
    /// </para>
    /// <para>
    /// <c>duration</c> 刻意**不设**：它只影响横幅停留时长，通知进不进通知中心与此无关 ——
    /// "停留在里面"是系统通知的默认行为（除非调用方主动 <c>History.Remove</c>，本类不碰 History）。
    /// </para>
    /// </remarks>
    private static string BuildXml(string launchTarget, string summary, string detail)
    {
        var builder = new StringBuilder();
        _ = builder.Append("<toast activationType=\"protocol\" launch=\"")
            .Append(Escape(AppActivation.ProtocolUriPrefix + launchTarget))
            .Append("\"><visual><binding template=\"ToastGeneric\">")
            .Append("<text>").Append(Escape(Heading)).Append("</text>")
            .Append("<text>").Append(Escape(summary)).Append("</text>");

        if (detail.Length > 0)
        {
            _ = builder.Append("<text>").Append(Escape(detail)).Append("</text>");
        }

        return builder.Append("</binding></visual></toast>").ToString();
    }

    /// <summary>
    /// 转义要放进 XML 文本节点的内容。
    /// </summary>
    /// <param name="value">原文（条目名来自系统，可能含 <c>&amp;</c> / <c>&lt;</c>）。</param>
    /// <returns>可直接拼进 XML 的字符串。</returns>
    /// <remarks>
    /// 先由 <see cref="SecurityElement.Escape"/> 处理五个 XML 实体，再删掉 XML 1.0
    /// **不允许**出现的控制字符（条目名里混进一个就会让整个 <c>LoadXml</c> 失败，
    /// 而失败的表现是"通知偶尔弹不出来"，极难复现）。此处的正则由源生成器编译，
    /// 不引入运行期正则解析开销。
    /// </remarks>
    private static string Escape(string value) =>
        InvalidXmlChars().Replace(SecurityElement.Escape(value) ?? string.Empty, string.Empty);

    /// <summary>XML 1.0 不允许的控制字符（制表 / 换行 / 回车除外）。</summary>
    /// <returns>用于剔除非法字符的正则。</returns>
    [GeneratedRegex("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]")]
    private static partial Regex InvalidXmlChars();
}
