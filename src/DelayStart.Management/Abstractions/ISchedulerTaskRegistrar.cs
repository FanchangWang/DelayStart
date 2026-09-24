using DelayStart.Core.Models;

namespace DelayStart.Management.Abstractions;

/// <summary>
/// 调度端与普通用户代理计划任务（<c>\DelayStart\Scheduler</c> / <c>\DelayStart\Agent</c>，
/// D38）的注册、更新与删除（FR-11）。
/// </summary>
/// <remarks>
/// <para>
/// 抽成接口的目的很具体：<c>TakeoverService</c> 的接管流程第 4 步是"确保计划任务存在"，
/// 而 <c>D33</c> 要求用假实现测出「第 4 步失败时前面几步是否被正确回滚」——
/// 直接依赖真实 <c>TaskService</c> 就得在测试里往系统里真写一个计划任务。
/// </para>
/// <para>
/// 实现在未提权时会失败，但管理端按 D20 全程提权，正常路径不会遇到。
/// </para>
/// </remarks>
public interface ISchedulerTaskRegistrar
{
    /// <summary>调度端计划任务的完整路径（形如 <c>\DelayStart\Scheduler</c>）。</summary>
    string TaskPath { get; }

    /// <summary>计划任务当前是否存在，不检查 action 是否指向当前安装目录。</summary>
    /// <returns>存在则为 <see langword="true"/>。</returns>
    bool Exists();

    /// <summary>计划任务是否存在且至少一个 ExecAction 指向本程序安装目录下的调度端。</summary>
    /// <returns>存在且 action 路径匹配则为 <see langword="true"/>。</returns>
    bool Matches();

    /// <summary>
    /// 幂等注册或更新计划任务：已存在则视情况更新，不存在则创建（FR-3.3 / FR-11.2）。
    /// </summary>
    /// <remarks>
    /// 幂等是硬要求 —— 调用点有三个，且都可能重复执行：管理端的首启自动注册（<c>D63</c>，
    /// 只做一次）、接管流程的第 4 步（每次接管）、以及命令行 <c>--reinstall-task</c>。
    /// 不能因为任务已存在就报错，也不能每次都删除重建（会把上次运行时间等统计清零）。
    /// 现有定义与期望完全一致时**不重写**，以保留触发器与运行统计（F1 的调度端镜像，D113）。
    /// </remarks>
    /// <returns>是否真正写入了任务定义（<see langword="false"/> 表示现有定义已与期望一致、已跳过重写）。</returns>
    /// <exception cref="StartupOperationException">创建或更新失败时抛出（E12）。</exception>
    bool RegisterOrUpdate();

    /// <summary>删除计划任务。任务不存在时静默返回，不抛异常（卸载路径要能重复执行）。</summary>
    /// <exception cref="StartupOperationException">删除失败时抛出。</exception>
    void Delete();
}
