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

    /// <summary>计划任务当前是否存在且指向本程序安装目录下的调度端。</summary>
    /// <returns>存在则为 <see langword="true"/>。</returns>
    bool IsRegistered();

    /// <summary>
    /// 幂等注册或更新计划任务：已存在则更新，不存在则创建（FR-3.3 / FR-11.2）。
    /// </summary>
    /// <remarks>
    /// 幂等是硬要求 —— 管理端每次启动都会调它（D22 的「首启幂等注册」），
    /// 不能因为任务已存在就报错，也不能每次都删除重建（会把上次运行时间等统计清零）。
    /// </remarks>
    /// <exception cref="StartupOperationException">创建或更新失败时抛出（E12）。</exception>
    void RegisterOrUpdate();

    /// <summary>删除计划任务。任务不存在时静默返回，不抛异常（卸载路径要能重复执行）。</summary>
    /// <exception cref="StartupOperationException">删除失败时抛出。</exception>
    void Delete();
}
