using System.Text.Json;
using System.Text.Json.Serialization;

namespace DelayStart.Core.Launch;

/// <summary>
/// 跨进程"打开管理端并定位到某处"的请求（D74）。
/// </summary>
/// <remarks>
/// 守卫点「查看」时写、管理端读取后删除。之所以需要一份文件，是因为唤起用的是
/// 命名事件（<see cref="System.Threading.EventWaitHandle"/>）——**它不带载荷**，
/// 来源参数跨进程传不过去。一份一次性文件比"每个来源一个具名事件"更省，
/// 也便于将来加 <c>--id=&lt;itemId&gt;</c> 高亮某一条。
/// </remarks>
/// <param name="Target">目标位置令牌，取值见 <see cref="UiNavigationTarget"/>。</param>
public sealed record UiNavigationRequest(string Target);

/// <summary>导航目标令牌。</summary>
/// <remarks>
/// 🔴 这些字符串是**跨进程契约**（守卫写、管理端读），改动等于改协议。
/// 用常量而不是各写各地面字符串，避免拼错一个连字符后表现成"点了查看没反应"。
/// </remarks>
public static class UiNavigationTarget
{
    /// <summary>注册表来源页。</summary>
    public const string Registry = "registry";

    /// <summary>启动文件夹来源页。</summary>
    public const string StartupFolder = "startup-folder";

    /// <summary>计划任务来源页。</summary>
    public const string ScheduledTask = "scheduled-task";

    /// <summary>UWP 来源页。</summary>
    public const string Uwp = "uwp";

    /// <summary>「延时启动」页（失效条目并入此页后，失效通报也落这里，D81）。</summary>
    public const string Delay = "delay";

    /// <summary>
    /// 失效条目所在位置（孤儿 + 已失效）。
    /// </summary>
    /// <remarks>
    /// ⚠️ D81 起这里指向的就是「延时启动」页 —— 失效条目不再是独立页面。
    /// 令牌本身**保留不改**：它是跨进程契约，旧版本管理端 / 已发出的通知里都写着它。
    /// </remarks>
    public const string Stale = "stale";

    /// <summary>「调度日志」页（D82：通知点击经文件中转时用它表达"看日志"）。</summary>
    /// <remarks>
    /// <para>
    /// 存在的理由：D82 起未提权的进程**只能靠请求文件**把意图交给正在运行的实例
    /// （跨完整性级别写不了高完整性对象的状态，命名事件那条路被堵死）。
    /// 而 <c>--goto-log</c> 原来的表达方式是"清空请求 + 发个不带载荷的事件"，
    /// 换成文件通道后就需要一个令牌来承载它。
    /// </para>
    /// <para>
    /// 兼容性：新增**令牌**是安全的，旧版本管理端遇到不认识的令牌只前置窗口、不切页；
    /// 反过来（旧进程写新令牌）不存在，因为写入方与读取方同版本分发。
    /// </para>
    /// </remarks>
    public const string RunsLog = "runs-log";
}

/// <summary>
/// 跨进程 UI 定位请求的 JSON 上下文。
/// </summary>
/// <remarks>
/// 单独一份**公开**上下文（与 <c>BrokerJsonContext</c> 同理）：写入方是守卫进程、
/// 读取方是管理端，两端都引用 Core 但各自是独立程序集。
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UiNavigationRequest))]
public sealed partial class UiRequestJsonContext : JsonSerializerContext
{
}
