using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Services;
using DelayStart.Management.Models;
using DelayStart.Management.Serialization;

namespace DelayStart.Management.Services;

/// <summary>
/// 守卫「已通报失效条目」状态的读写（D148）。
/// </summary>
/// <remarks>
/// <para>
/// 落点 <c>%LOCALAPPDATA%\DelayStart\guard\notify-state.json</c>，走
/// <see cref="AtomicFileWriter"/>（先写 <c>.tmp</c> 再替换），与基线同一套理由：
/// 守卫被强杀时留下的半截 JSON 读回来会变成"解析失败"，
/// 而"解析失败"这一支的语义是"当作没有状态"，代价是<b>多报一次</b>通知（可接受）。
/// </para>
/// <para>
/// 🔴 <b>单写者：只有守卫进程。</b>管理端两页也跑巡检，但它们不发通知，
/// 因此不接触本状态（见 <see cref="GuardNotifyState"/> 的说明）。
/// 这是本方案相对早期版本的关键简化："谁有资格推进通知状态"由
/// <b>文件归属</b>保证，而不是靠每次调用都传对一个布尔参数。
/// </para>
/// <para>
/// 读不到或解析失败一律返回 <see langword="null"/>（等价于"尚未通报过任何条目"）：
/// 取静默这一支，而不是把所有失效都当成新的 —— 后者会在状态文件出问题时
/// 引发一轮通知风暴。
/// </para>
/// </remarks>
public sealed class GuardNotifyStateStore
{
    private readonly PathService _paths;
    private readonly ILogSink _log;
    private readonly IClock _clock;

    /// <summary>构造通知状态存储。</summary>
    /// <param name="paths">路径服务。</param>
    /// <param name="log">日志接收端。</param>
    /// <param name="clock">时间源（给状态打时间戳）。</param>
    public GuardNotifyStateStore(PathService paths, ILogSink log, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _paths = paths;
        _log = log;
        _clock = clock;
    }

    /// <summary>读取已通报的主键集合。</summary>
    /// <returns>
    /// 已通报主键的只读集合；状态不存在、读不到或解析失败时为 <see langword="null"/>
    /// （= 尚未通报过任何条目 ⇒ 本轮静默）。
    /// </returns>
    public IReadOnlySet<string>? Read()
    {
        string? json;
        try
        {
            json = AtomicFileWriter.ReadAllTextOrNull(_paths.GuardNotifyStatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, "读取守卫通知状态失败，本轮按「尚未通报过」处理（不通报失效条目）");
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize(json, GuardJsonContext.Default.GuardNotifyState);
            if (state is null)
            {
                return null;
            }

            // 🔴 空集合 ≠ null：空集合是"上一轮一条失效都没有"（⇒ 本轮全部该报），
            // null 是"没有上一次"（⇒ 本轮静默）。两者折成同一个会让状态文件第一次
            // 写入时把那一轮的失效全吞掉。
            return new HashSet<string>(state.NotifiedItemIds, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            _log.Warn(ex, "守卫通知状态解析失败，本轮按「尚未通报过」处理并重建（不通报失效条目）");
            return null;
        }
    }

    /// <summary>原子写入本轮已通报的主键集合。</summary>
    /// <param name="notifiedIds">本轮的全量失效主键（整体替换，不是累加）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="notifiedIds"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 🔴 写失败只记 Warn，绝不抛：状态写不进去的后果仅仅是"下一轮多报一次"，
    /// 而让整次巡检因此失败是事故级的失衡。
    /// </remarks>
    public void Write(IReadOnlySet<string> notifiedIds)
    {
        ArgumentNullException.ThrowIfNull(notifiedIds);

        var state = new GuardNotifyState
        {
            Version = 1,
            UpdatedAt = _clock.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            NotifiedItemIds = [.. notifiedIds],
        };

        try
        {
            var json = JsonSerializer.Serialize(state, GuardJsonContext.Default.GuardNotifyState);
            AtomicFileWriter.WriteAllText(_paths.GuardNotifyStatePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(ex, "写入守卫通知状态失败，下轮巡检将重复通报本轮失效条目（多报一次，不静默漏报）");
        }
    }
}