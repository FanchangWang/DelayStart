using DelayStart.Core.Launch;
using DelayStart.Core.Models;

namespace DelayStart.Core.Abstractions;

/// <summary>
/// 进程启动抽象（机制 6）。实现按条目的 <see cref="DelayedItem.RunAsAdmin"/> 分流：
/// 管理员条目继承调度端令牌直接启动；普通用户条目走
/// <c>WTSQueryUserToken</c> → <c>CreateEnvironmentBlock</c> → <c>CreateProcessAsUser</c> 降权启动
/// （NFR-3.3 / FR-5.6 / FR-5.8）。
/// </summary>
/// <remarks>
/// <para>
/// 抽象成接口有两个目的：一是让调度引擎可以注入假实现做逻辑测试，
/// 二是把 P/Invoke 与"什么时候启动哪个条目"的调度决策解耦。
/// </para>
/// <para>
/// TODO(Phase 4): 具体实现 <c>ProcessLauncher</c> 与 <c>TokenHelper</c> 随调度引擎一起落地。
/// 按 <c>coding-standards.md</c> §14.1，"进程实际启动"属于真机手工验证范围，不进单元测试。
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
