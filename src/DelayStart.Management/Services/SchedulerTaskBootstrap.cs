using DelayStart.Core.Abstractions;
using DelayStart.Management.Abstractions;

namespace DelayStart.Management.Services;

/// <summary>
/// 调度计划任务的启动期保障：管理端**每次启动**都检测一次，任务缺失即自动补建。
/// </summary>
/// <remarks>
/// <para>
/// 2026-09-21 批复（原型 v3，取代 D63 的「仅首启一次」语义）：本软件强依赖调度器 ——
/// 没有计划任务，延时启动就完全不生效，任务缺失不再是被允许的常态，而是要自动修复的故障。
/// 因此语义从「首启注册一次、之后绝不插手（保护总览页开关的用户意图）」改为
/// 「每次启动检测、缺失即补建」—— 总览页的开关已随同轮批复删除，旧的
/// "自动补建会推翻用户关掉开关的意愿" 这条反对理由不复存在。
/// </para>
/// <para>
/// 2026-09-24（D113，F1 调度端镜像）：任务**已存在且定义一致**时不重写，保留已武装的登录触发器与
/// 运行统计。调度端是固定规则、不随设置变化，相同定义下每次启动都不应触碰它 —— 否则
/// <c>CreateOrUpdate</c> 重建登录触发器会重置「登录后 N 秒」这一一次性窗口，并覆盖「上次运行时间」
/// 等统计（与守卫 D112 同源的问题，只是守卫因此会静默吞掉巡检、调度端是统计失真）。
/// 只有定义确实变了（安装目录迁移 / 任务被改坏）或任务缺失时才真正写入。
/// </para>
/// <para>
/// 原先 <c>FirstRunBootstrap</c> 依赖的 <c>Settings.SchedulerTaskInitialized</c>
/// 标记随之废止（配置模型里的字段已删除；老配置里残留的该键会被 JSON 反序列化忽略）。
/// </para>
/// <para>
/// ⚠️ 本类不抛异常：它跑在应用启动路径上，任何失败都只记日志。
/// 总览页状态卡会显示失败态并给出「重试创建」按钮，修复路径不在这里。
/// </para>
/// </remarks>
public sealed class SchedulerTaskBootstrap
{
    private readonly ISchedulerTaskRegistrar _registrar;
    private readonly ILogSink _log;

    /// <summary>构造调度任务启动期保障器。</summary>
    /// <param name="registrar">调度计划任务注册端。</param>
    /// <param name="log">日志接收端。</param>
    public SchedulerTaskBootstrap(ISchedulerTaskRegistrar registrar, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(log);

        _registrar = registrar;
        _log = log;
    }

    /// <summary>
    /// 幂等：任务已存在且定义一致时跳过重写（保留触发器与运行统计）；缺失或定义变更则补建。
    /// 可在每次启动时调用（镜像守卫 <see cref="GuardTaskBootstrap"/> 的 F1 行为，D113）。
    /// </summary>
    public void EnsureSchedulerTask()
    {
        try
        {
            // 与守卫 F1（D112）同一思路：先判断任务是否存在，再让他决定要不要写。
            // RegisterOrUpdate 内部会比对完整定义，完全一致则返回 false 跳过重写，
            // 保留触发器的"已武装"状态与「上次运行时间」等统计；只有缺失或定义确实变了才写。
            var existed = _registrar.Exists();
            var changed = _registrar.RegisterOrUpdate();

            _log.Info((existed, changed) switch
            {
                (true, false) => "调度计划任务已是最新，无需重建（保留触发器与运行统计）。",
                (true, true) => "调度计划任务定义已变更，已自动重新创建。",
                (false, _) => "调度计划任务缺失，已自动重新创建（2026-09-21 批复：缺失即补建）。",
            });
        }
        catch (Exception ex)
        {
            // 异常必须带栈：这类失败常常是三层嵌套的 TypeInitializationException（D34 实测），
            // 只留 Message 无法定位。失败状态由总览页状态卡呈现并允许用户重试。
            _log.Error(ex, "调度计划任务自动补建失败，可在总览页点击「重试创建」");
        }
    }
}
