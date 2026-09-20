using DelayStart.Core.Launch;
using DelayStart.Core.Models;

namespace DelayStart.Core.Abstractions;

/// <summary>
/// 进程启动抽象（机制 6）。实现按条目的 <see cref="DelayedItem.RunAsAdmin"/> 与
/// 目标形态分流：管理员条目继承调度端提权令牌直接启动；普通条目由调度端
/// **亲自降权**启动（D40，方案 7：外壳令牌 → <c>DuplicateTokenEx</c> 主令牌 →
/// <c>CreateProcessWithTokenW</c>）；<c>.lnk</c> / UWP 经外壳委托
/// （NFR-3.3 / FR-5.6 / FR-5.8）。
/// </summary>
/// <remarks>
/// <para>
/// 抽象成接口有两个目的：一是让调度引擎可以注入假实现做逻辑测试，
/// 二是把"怎么把进程起来"的系统调用细节与"什么时候启动哪个条目"的调度决策解耦。
/// </para>
/// <para>
/// D40（2026-09-20）：删除 D38 的普通用户代理进程（<c>DelayStart.Agent.exe</c>）。
/// 该代理本身也是经 explorer 委托拉起的 —— 与直接降权依赖同一个外壳，
/// 却多一层进程、多一条管道、拿不到真实的失败原因；方案 7 在同一台机器上真机实测
/// 子进程完整性 0x2000（普通用户），命令行参数原样送达。
/// </para>
/// </remarks>
public interface IProcessLauncher
{
    /// <summary>
    /// 按条目配置发起目标进程。**不等待目标程序完成初始化**，也不做成败判定 ——
    /// 判定由 <c>LaunchResultEvaluator</c> 在延时复查后完成。
    /// </summary>
    /// <param name="item">要启动的条目。</param>
    /// <returns>进程创建结果；创建失败时返回失败结果而**不抛异常**，由调用方记入 <c>RunItemResult</c>（FR-5.5）。</returns>
    LaunchOutcome Launch(DelayedItem item);
}
