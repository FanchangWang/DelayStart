using DelayStart.Core.Models;

namespace DelayStart.Management.Abstractions;

/// <summary>
/// 守卫计划任务（<c>\DelayStartGuard</c>）的注册 / 更新 / 删除（D74）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ISchedulerTaskRegistrar"/> 分开成两个接口而不是加一个方法：两条任务的
/// 生命周期完全不同 —— 调度任务是"必须存在"的固定规则，守卫任务则**随设置档位变化**，
/// 甚至会因为 <see cref="GuardMode.Disabled"/> 而整体消失。揉进一个接口会让
/// "调度任务缺失即补建"这条不变量被守卫的可选项污染。
/// </para>
/// <para>
/// 抽接口还有一个具体理由：<c>GuardTaskBootstrap</c> 的"缺失即补建 / 关闭即删除"分支
/// 要用假实现测，直接依赖真实 <c>TaskService</c> 就得往系统里真写计划任务。
/// </para>
/// </remarks>
public interface IGuardTaskRegistrar
{
    /// <summary>守卫计划任务的完整路径。</summary>
    string TaskPath { get; }

    /// <summary>计划任务当前是否存在且指向本程序安装目录下的守卫程序。</summary>
    /// <returns>存在则为 <see langword="true"/>。</returns>
    bool IsRegistered();

    /// <summary>
    /// 按档位幂等注册或更新守卫计划任务；<paramref name="mode"/> 为
    /// <see cref="GuardMode.Disabled"/> 时等价于 <see cref="Delete"/>。
    /// 现有定义与期望完全一致时**不重写**，以保留登录触发器的已武装状态（F1 / D112）。
    /// </summary>
    /// <param name="mode">守卫模式。</param>
    /// <param name="minutes">间隔档位（分钟）。</param>
    /// <returns>是否真正写入了任务定义（<see langword="false"/> 表示已跳过重写）。</returns>
    /// <exception cref="StartupOperationException">创建或更新失败时抛出。</exception>
    bool RegisterOrUpdate(GuardMode mode, int minutes);

    /// <summary>删除守卫计划任务。任务不存在时静默返回，不抛异常（可重复执行）。</summary>
    /// <exception cref="StartupOperationException">删除失败时抛出。</exception>
    void Delete();
}
