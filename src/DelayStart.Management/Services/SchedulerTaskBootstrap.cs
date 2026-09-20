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

    /// <summary>幂等：任务已存在时直接返回，可以在每次启动时调用。</summary>
    public void EnsureSchedulerTask()
    {
        try
        {
            if (_registrar.IsRegistered())
            {
                _log.Info("调度计划任务已存在，无需补建");
                return;
            }

            _registrar.RegisterOrUpdate();
            _log.Info("调度计划任务缺失，已自动重新创建（2026-09-21 批复：缺失即补建）");
        }
        catch (Exception ex)
        {
            // 异常必须带栈：这类失败常常是三层嵌套的 TypeInitializationException（D34 实测），
            // 只留 Message 无法定位。失败状态由总览页状态卡呈现并允许用户重试。
            _log.Error(ex, "调度计划任务自动补建失败，可在总览页点击「重试创建」");
        }
    }
}
