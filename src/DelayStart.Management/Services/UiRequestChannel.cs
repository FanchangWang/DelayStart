using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Services;

namespace DelayStart.Management.Services;

/// <summary>
/// 跨进程「打开管理端并定位到某处」的一次性请求通道（D74，2026-09-22 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 写入方有两处：守卫进程（新增 / 失效提示框点「查看」）与管理端自己
/// （CLI <c>--goto-startup</c>）。读取方只有一个：管理端实例（已跑着的，或本次刚起来的）。
/// </para>
/// <para>
/// 🔴 **为什么是文件而不是命名事件**：唤起用的 <see cref="System.Threading.EventWaitHandle"/>
/// 不带载荷，来源参数跨进程传不过去（见 <see cref="UiNavigationRequest"/> 的说明）。
/// 放在 Management 而不是 App，是为了让守卫与管理端共用同一份实现 ——
/// 守卫引用 Management 但不引用 App，写在 App 里守卫就用不上，只能各写一份 JSON 解析。
/// </para>
/// <para>
/// 🔴 **读即删**：请求是一次性的。不删的话之后任何一次普通唤起都会重复跳到同一个位置。
/// 写入方因此总是**覆盖写**：不带来源的 <c>--goto-log</c>（D18）写的是
/// <see cref="UiNavigationTarget.RunsLog"/> 令牌 —— 它表达的是"看日志"这个**更新的**意图，
/// 覆盖掉可能残留的定位请求，等价于 D82 之前那句"先 <see cref="Discard"/> 清场再发信号"
/// （D82 起**信号就是文件本身**：未提权的点击方写不了提权实例的内核对象，
/// 见 <c>App.StartRequestWatcher</c>）。
/// </para>
/// </remarks>
public sealed class UiRequestChannel
{
    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>构造定位请求通道。</summary>
    /// <param name="paths">路径服务（请求文件落在 <c>%LOCALAPPDATA%\DelayStart</c>）。</param>
    /// <param name="log">日志接收端。</param>
    public UiRequestChannel(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _log = log;
    }

    /// <summary>写入一次性定位请求（覆盖已有的待处理请求）。</summary>
    /// <param name="target">目标令牌，取值见 <see cref="UiNavigationTarget"/>。</param>
    /// <remarks>
    /// 失败只记日志不抛：写不进去的后果是"管理端打开在默认位置"，不该因此让守卫
    /// 或 CLI 整条路径失败。
    /// </remarks>
    public void Write(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        try
        {
            var json = JsonSerializer.Serialize(
                new UiNavigationRequest(target),
                UiRequestJsonContext.Default.UiNavigationRequest);

            AtomicFileWriter.WriteAllText(_paths.UiRequestFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, "写入 UI 定位请求失败：管理端将打开在默认位置");
        }
    }

    /// <summary>读取并删除待处理的定位请求。</summary>
    /// <returns>目标令牌；没有请求或请求不可读时为 <see langword="null"/>（视为"不定位"）。</returns>
    public string? Consume()
    {
        string? json;
        try
        {
            json = AtomicFileWriter.ReadAllTextOrNull(_paths.UiRequestFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, "读取 UI 定位请求失败，按「不定位」处理");
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        // 无论解析成败都要删：坏掉的请求文件留在那儿只会让后续每次唤起都白读一遍。
        Discard();

        try
        {
            return JsonSerializer.Deserialize(json, UiRequestJsonContext.Default.UiNavigationRequest)?.Target;
        }
        catch (JsonException ex)
        {
            _log.Warn(ex, "UI 定位请求解析失败，已丢弃");
            return null;
        }
    }

    /// <summary>丢弃待处理的定位请求（不存在时静默返回）。</summary>
    public void Discard()
    {
        try
        {
            File.Delete(_paths.UiRequestFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉不致命：下一次写入会覆盖它。
            _log.Warn(ex, "删除 UI 定位请求文件失败");
        }
    }
}
