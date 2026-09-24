using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;

using DelayStart.Management.Abstractions;

namespace DelayStart.Management.Services;

/// <summary>一次守卫任务同步的结果。</summary>
public enum GuardTaskSyncResult
{
    /// <summary>无需改动（守卫关闭且本来就没有任务，或守卫任务定义已是最新、跳过重写）。</summary>
    NoChange,

    /// <summary>任务此前缺失，已自动补建。</summary>
    Registered,

    /// <summary>任务已存在，已按当前档位同步。</summary>
    Updated,

    /// <summary>守卫已关闭，任务已删除。</summary>
    Deleted,

    /// <summary>同步失败（原因在消息里，也写进了日志）。</summary>
    Failed,
}

/// <summary>一次守卫任务同步的结果与说明。</summary>
/// <param name="Result">结果分类。</param>
/// <param name="Message">面向用户 / 日志的一句话说明。</param>
public sealed record GuardTaskSyncOutcome(GuardTaskSyncResult Result, string Message);

/// <summary>
/// 守卫计划任务的启动期保障：管理端**每次启动**都检测一次，缺失即补建（D74，2026-09-22 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 语义与 <see cref="SchedulerTaskBootstrap"/> 对齐 —— 用户的原话是"跟调度器的计划任务一样
/// 进行检测复建，目的都是防丢失"：计划任务被安全软件清掉、被用户在任务计划程序里误删、
/// 或安装目录迁移导致 action 指向失效，都会让守卫**在毫无提示的情况下永远不再运行**。
/// 守卫的失效尤其隐蔽：它本来就不常驻、不弹窗，用户没有任何途径发现它已经没了。
/// </para>
/// <para>
/// 与调度任务的两点差异，都是"守卫随设置变化"带来的：
/// </para>
/// <list type="bullet">
/// <item><description>
/// 调度任务已存在时不重写（省一次写）；守卫任务也只在"现有定义与期望不一致"时重写
/// （F1 / D112，2026-09-24 修正）。此前每次启动都重写，而 <c>CreateOrUpdate</c> 会重建登录
/// 触发器，把"登录后 N 分钟"这个一次性窗口丢掉——登录事件已经过去，新触发器在本会话内
/// 等不到下一次登录，导致该次巡检被静默吞掉。现在改为先比对定义，完全一致就跳过写入；
/// 档位调大调小、安装目录迁移、任务被改坏仍会即时补写。
/// </description></item>
/// <item><description>
/// 档位为 <see cref="GuardMode.Disabled"/> 时不是"什么都不做"，而是**删除任务** ——
/// 否则用户关掉守卫后任务仍在按旧档位悄悄跑。
/// </description></item>
/// </list>
/// <para>
/// ⚠️ <see cref="SyncWithSettings"/> **不抛异常**：它跑在应用启动路径上，任何失败都只记日志
/// 并把原因放进返回值，由总览页的守卫设置区呈现。
/// </para>
/// </remarks>
public sealed class GuardTaskBootstrap
{
    private readonly IGuardTaskRegistrar _registrar;
    private readonly IAppConfigStore _configStore;
    private readonly ILogSink _log;

    /// <summary>构造守卫任务启动期保障器。</summary>
    /// <param name="registrar">守卫计划任务注册端。</param>
    /// <param name="configStore">配置读取端（守卫档位存在 <c>settings</c> 节点下）。</param>
    /// <param name="log">日志接收端。</param>
    public GuardTaskBootstrap(
        IGuardTaskRegistrar registrar,
        IAppConfigStore configStore,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(log);

        _registrar = registrar;
        _configStore = configStore;
        _log = log;
    }

    /// <summary>
    /// 按当前设置同步守卫计划任务。幂等，可在每次启动与每次设置变更时调用。
    /// </summary>
    /// <returns>同步结果；失败也返回结果对象而不是抛出。</returns>
    public GuardTaskSyncOutcome SyncWithSettings()
    {
        GuardMode mode;
        int minutes;

        try
        {
            var settings = _configStore.Load().Settings;
            mode = settings.GuardMode;
            minutes = settings.GuardMinutes;
        }
        catch (Exception ex)
        {
            // 配置版本高于本程序会拒绝加载（前向兼容保护）。此时不能拿默认值去改任务 ——
            // 那等于用一份我们不理解的配置覆盖用户的守卫设置。
            _log.Error(ex, "读取守卫设置失败，本次不同步守卫计划任务");
            return new GuardTaskSyncOutcome(GuardTaskSyncResult.Failed, $"读取配置失败：{ex.Message}");
        }

        try
        {
            var existed = _registrar.IsRegistered();

            if (mode is GuardMode.Disabled)
            {
                if (!existed)
                {
                    return new GuardTaskSyncOutcome(GuardTaskSyncResult.NoChange, "守卫已关闭。");
                }

                _registrar.Delete();
                return new GuardTaskSyncOutcome(GuardTaskSyncResult.Deleted, "守卫已关闭，计划任务已删除。");
            }

            var changed = _registrar.RegisterOrUpdate(mode, minutes);

            var description = GuardTaskRegistrar.DescribeMode(mode, minutes);
            return (existed, changed) switch
            {
                (true, false) => new GuardTaskSyncOutcome(
                    GuardTaskSyncResult.NoChange, $"守卫计划任务已是最新（{description}），无需重写。"),
                (true, true) => new GuardTaskSyncOutcome(
                    GuardTaskSyncResult.Updated, $"守卫计划任务已同步（{description}）。"),
                (false, _) => new GuardTaskSyncOutcome(
                    GuardTaskSyncResult.Registered, $"守卫计划任务缺失，已自动补建（{description}）。"),
            };
        }
        catch (Exception ex)
        {
            _log.Error(ex, "同步守卫计划任务失败（可在总览页的守卫设置区重试）");
            return new GuardTaskSyncOutcome(GuardTaskSyncResult.Failed, $"同步守卫计划任务失败：{ex.Message}");
        }
    }
}
