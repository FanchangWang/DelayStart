using DelayStart.Core.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 应用自有计划任务的精确身份集合（B3）。
/// </summary>
/// <remarks>
/// <para>
/// 计划任务来源扫描的是用户的第三方自启动项，不应把 DelayStart 自己用来维持运行的任务
/// 列给用户，更不应允许用户误接管它们。这里只认两个**根级完整路径**，不使用
/// <c>StartsWith("\DelayStart")</c> 之类的宽泛匹配，避免误伤第三方任务。
/// </para>
/// <para>
/// 计划任务注册器各自保留原有公开常量；本类只负责把两处身份汇总成扫描与启动配置清理共用的判据。
/// </para>
/// </remarks>
internal static class OwnedScheduledTaskPolicy
{
    /// <summary>调度端自有任务路径。</summary>
    public static string SchedulerPath => TaskRegistrationService.TaskPathConstant;

    /// <summary>守卫端自有任务路径。</summary>
    public static string GuardPath => GuardTaskRegistrar.TaskPathConstant;

    /// <summary>判断完整任务路径是否属于本应用。</summary>
    /// <param name="taskPath">任务计划程序返回的完整路径。</param>
    /// <returns>精确匹配调度端或守卫端路径时为 <see langword="true"/>。</returns>
    public static bool IsOwnedPath(string? taskPath)
        => !string.IsNullOrWhiteSpace(taskPath)
            && (string.Equals(taskPath, SchedulerPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(taskPath, GuardPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>判断配置条目是否是历史上误接管的应用自有计划任务。</summary>
    /// <param name="item">配置中的延时启动条目。</param>
    /// <returns>来源、作用域和来源键都指向应用自有根任务时为 <see langword="true"/>。</returns>
    public static bool IsHistoricalOwnedItem(DelayedItem? item)
        => item is not null
            && item.Source == StartupSource.ScheduledTask
            && item.Scope == StartupScope.None
            && IsOwnedPath(item.SourceKey);
}
