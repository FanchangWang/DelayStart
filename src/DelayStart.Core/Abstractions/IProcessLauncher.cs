using DelayStart.Core.Launch;
using DelayStart.Core.Models;

namespace DelayStart.Core.Abstractions;

/// <summary>
/// 进程启动抽象（机制 6）。实现按条目的 <see cref="DelayedItem.RunAsAdmin"/> 分流：
/// 管理员条目继承调度端提权令牌直接启动；普通用户条目经**普通用户代理进程**
/// （<c>DelayStart.Agent.exe</c>，D38）启动 —— 代理由独立计划任务以普通用户身份运行，
/// 天然持有交互用户令牌，调度端（提权）绝不亲自降权创建进程
/// （NFR-3.3 / FR-5.6 / FR-5.8）。
/// </summary>
/// <remarks>
/// <para>
/// 抽象成接口有两个目的：一是让调度引擎可以注入假实现做逻辑测试，
/// 二是把"经代理启动"的 IPC 细节与"什么时候启动哪个条目"的调度决策解耦。
/// </para>
/// <para>
/// D38（2026-09-20）：放弃提权进程直接降权（<c>CreateProcessWithTokenW</c> 经
/// seclogon 服务中转，真机实测 ACCESS_DENIED(5) 且会无响应挂起）。
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
