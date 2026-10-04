namespace DelayStart.Management.Models;

/// <summary>
/// 守卫的「已通报失效条目」状态（D148）：上一次发出系统通知时通报过哪些失效条目。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>它与 <see cref="GuardBaseline"/> 是两件事，刻意分文件</b>：
/// 基线回答"上一次扫描时系统里有哪些自启动项"（判定新增 / 失效的<b>输入</b>），
/// 本状态回答"哪些失效已经通知过"（只服务<b>通报</b>）。把后者塞进前者会让基线不再是基线，
/// 而且基线只由扫描结果构成 —— 手动添加的条目（七个来源实例里没有 Manual）根本不在里面，
/// 拿它当通知判据会让那类失效<b>永久静默</b>。
/// </para>
/// <para>
/// 🔴 <b>单写者：只有守卫进程</b>（<c>DelayStart.Guard.exe</c>）。
/// 管理端「自启动项」页 /「延时启动」页虽然也跑一次巡检（<c>GuardService.RunOnce</c>），
/// 但它们<b>不发通知</b>，因此不读也不写这份状态。
/// 单写者由结构保证，不需要额外的开关参数（D148 早期版本用
/// <c>RunOnce(advanceBaseline: false)</c> 来表达同一件事，那是把"谁有资格推进"这件事
/// 变成了每次调用都要传对的参数）。
/// </para>
/// <para>
/// 🔴 <b>每轮整体替换，不只增不减</b>：条目的失效状态恢复了（用户装回程序）就必须出集合，
/// 否则"恢复之后再失效"永远不会被通报。
/// </para>
/// </remarks>
public sealed class GuardNotifyState
{
    /// <summary>格式版本，便于将来演进。</summary>
    public int Version { get; set; } = 1;

    /// <summary>更新时间（yyyy-MM-dd HH:mm:ss），便于排查"这次为什么没通知"。</summary>
    public string? UpdatedAt { get; set; }

    /// <summary>已通报的失效条目主键（<c>ItemKeyBuilder</c> 的稳定主键）。</summary>
    public List<string> NotifiedItemIds { get; set; } = [];
}