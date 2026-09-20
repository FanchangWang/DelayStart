using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;

namespace DelayStart.Management.Services;

/// <summary>
/// 首次运行的一次性初始化：装完第一次打开管理端时**自动注册调度计划任务**（D63）。
/// </summary>
/// <remarks>
/// <para>
/// 起因（2026-09-21 用户要求）：计划任务不注册，延时启动就完全不生效，而用户
/// 得先知道总览页右上角有个开关、还得知道要把它拨开 —— 一个装完就能用的程序
/// 不该把关键开关藏在这一步里。安装器本身不能做这件事（<c>PrivilegesRequired=lowest</c>，
/// 注册任务需要提权），所以落到"管理端第一次启动"，那时进程已经是管理员。
/// </para>
/// <para>
/// 🔴 **只在首次运行做一次，之后彻底不插手。** 判据是配置里的
/// <see cref="Settings.SchedulerTaskInitialized"/>；一旦落盘，后续启动连
/// <see cref="ISchedulerTaskRegistrar.IsRegistered"/> 都不查。原因不是性能：
/// 总览页那个开关（D3：开 = 注册，关 = 删除）表达的是**用户意图**，
/// 自动动作只允许发生在他表达意图之前 —— 若每次启动都"发现没注册就补上"，
/// 用户关掉开关的意愿会被无声推翻，而且他永远关不掉。
/// </para>
/// <para>
/// 注册失败**不落标记**，下次启动会重试（例如首启时被系统策略临时拦下）。
/// 用户自己开过开关的情况下，<c>IsRegistered()</c> 直接命中，只补标记不重复注册。
/// </para>
/// <para>
/// ⚠️ 一次性迁移效应：<c>schedulerTaskInitialized</c> 是后加的字段，升级上来的老配置
/// 没有它，因此下一次启动会补做一次自动注册。此前有意关掉开关的用户会被重新打开一次 ——
/// 仅此一次，之后不再发生。
/// </para>
/// <para>
/// ⚠️ 本类不抛异常：它跑在应用启动路径上，任何失败都只记日志。总览页的开关状态
/// 会如实显示"未创建"，用户仍能手动打开。
/// </para>
/// </remarks>
public sealed class FirstRunBootstrap
{
    private readonly IAppConfigStore _configStore;
    private readonly ISchedulerTaskRegistrar _registrar;
    private readonly ILogSink _log;

    /// <summary>构造首启初始化器。</summary>
    /// <param name="configStore">配置读写端（标记落在这里）。</param>
    /// <param name="registrar">调度计划任务注册端。</param>
    /// <param name="log">日志接收端。</param>
    public FirstRunBootstrap(IAppConfigStore configStore, ISchedulerTaskRegistrar registrar, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _registrar = registrar;
        _log = log;
    }

    /// <summary>幂等：标记已存在时什么都不做，可以直接在每次启动时调用。</summary>
    public void EnsureSchedulerTask()
    {
        AppConfig config;
        try
        {
            config = _configStore.Load();
        }
        catch (Exception ex)
        {
            // 配置读不了就没法判断"做过没有"。宁可不注册，也不要变成一个每次都注册的循环。
            _log.Error(ex, "首启初始化：读取配置失败，跳过自动注册计划任务");
            return;
        }

        if (config.Settings.SchedulerTaskInitialized)
        {
            return;
        }

        try
        {
            if (_registrar.IsRegistered())
            {
                // 用户已经自己开过开关（或上次装完手动跑过 --reinstall-task）：只补标记。
                _log.Info("首启初始化：调度计划任务已存在，仅记录「已初始化」标记");
            }
            else
            {
                _registrar.RegisterOrUpdate();
                _log.Info("首启初始化：已自动注册调度计划任务（D63；用户可在总览页关闭，关闭后不会被重新打开）");
            }
        }
        catch (Exception ex)
        {
            // 刻意不落标记 —— 下次启动再试。异常必须带栈：这类失败常常是三层嵌套的
            // TypeInitializationException（D34 实测），只留 Message 无法定位。
            _log.Error(ex, "首启初始化：自动注册调度计划任务失败，下次启动会重试");
            return;
        }

        MarkInitialized(config);
    }

    /// <summary>把「已初始化」标记落盘。写失败只记日志 —— 代价仅仅是下次启动重查一遍。</summary>
    private void MarkInitialized(AppConfig config)
    {
        config.Settings.SchedulerTaskInitialized = true;
        try
        {
            _configStore.Save(config);
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "首启初始化：标记未能落盘，下次启动会重复检查一次（不影响计划任务本身）");
        }
    }
}
