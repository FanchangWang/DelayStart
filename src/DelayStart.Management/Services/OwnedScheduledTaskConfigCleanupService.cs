using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;

namespace DelayStart.Management.Services;

/// <summary>启动时清理应用自有计划任务配置的结果。</summary>
/// <param name="RemovedCount">已从配置中移除的自有任务条目数。</param>
/// <param name="Succeeded">配置是否成功读取并保存；没有匹配项时也为 <see langword="true"/>。</param>
public sealed record OwnedScheduledTaskCleanupReport(int RemovedCount, bool Succeeded);

/// <summary>
/// 管理端启动时清理延时配置中的应用自有计划任务条目（B3）。
/// </summary>
/// <remarks>
/// <para>
/// 本服务只修改配置，不触碰计划任务系统状态。应用自有任务的当前存在性、启用状态和
/// 定义由后面的 <c>SchedulerTaskBootstrap</c> / <c>GuardTaskBootstrap</c> 决定：
/// 缺失或定义失效时按当前设置同步，守卫关闭时按设置删除。
/// </para>
/// <para>
/// 历史条目中的 <c>OriginalState</c> 不再参与恢复：它描述的是用户过去接管第三方启动项时
/// 的状态，不应覆盖应用当前对自有基础设施的生命周期管理。
/// </para>
/// </remarks>
public sealed class OwnedScheduledTaskConfigCleanupService
{
    private readonly IAppConfigStore _configStore;
    private readonly ILogSink _log;

    /// <summary>构造自有任务配置清理器。</summary>
    /// <param name="configStore">配置读写端。</param>
    /// <param name="log">日志接收端。</param>
    public OwnedScheduledTaskConfigCleanupService(IAppConfigStore configStore, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _log = log;
    }

    /// <summary>移除配置中精确匹配应用自有根路径的延时条目。</summary>
    /// <returns>清理结果；配置读取或保存失败时保留原配置并返回失败。</returns>
    public OwnedScheduledTaskCleanupReport RemoveOwnedTasks()
    {
        AppConfig config;
        try
        {
            // 配置未知时不能把它当成空配置，也不能继续写盘。
            config = _configStore.LoadForMutation();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取配置失败，未清理应用自有计划任务条目");
            return new OwnedScheduledTaskCleanupReport(RemovedCount: 0, Succeeded: false);
        }

        var ownedCount = config.Items
            .OfType<DelayedItem>()
            .Count(OwnedScheduledTaskPolicy.IsHistoricalOwnedItem);

        if (ownedCount == 0)
        {
            return new OwnedScheduledTaskCleanupReport(RemovedCount: 0, Succeeded: true);
        }

        config.Items.RemoveAll(item => OwnedScheduledTaskPolicy.IsHistoricalOwnedItem(item));
        try
        {
            _configStore.Save(config);
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"保存配置失败，保留 {ownedCount} 条应用自有计划任务条目");
            return new OwnedScheduledTaskCleanupReport(RemovedCount: 0, Succeeded: false);
        }

        _log.Info($"已从延时配置移除 {ownedCount} 条应用自有计划任务条目；计划任务生命周期交由启动自检处理");
        return new OwnedScheduledTaskCleanupReport(RemovedCount: ownedCount, Succeeded: true);
    }
}
