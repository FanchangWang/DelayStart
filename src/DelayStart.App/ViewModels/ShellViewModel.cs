using CommunityToolkit.Mvvm.ComponentModel;

using DelayStart.Core.Abstractions;
using DelayStart.Management.Abstractions;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 主窗口外壳（导航栏底部的「调度任务」状态条）的 ViewModel。
/// </summary>
/// <remarks>
/// <para>
/// 状态条要回答的是设计稿里的那一行：`调度任务：已创建 · 登录后 3 秒触发`，
/// 或者任务缺失时的警示 —— 后者不是装饰：**调度任务不存在时延时启动根本不生效**，
/// 而用户看不到任何别的迹象。
/// </para>
/// <para>
/// 只读查询，不注册。注册动作由 <c>--reinstall-task</c> 与接管流程负责（D22 的首启幂等注册），
/// 状态条去写系统会让"看一眼状态"变成一个写操作。
/// </para>
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ISchedulerTaskRegistrar _registrar;
    private readonly ILogSink _log;

    /// <summary>调度任务状态文案。</summary>
    [ObservableProperty]
    public partial string TaskStatusText { get; set; }

    /// <summary>调度任务缺失（或查询失败）时为 <see langword="true"/>，状态条转警示色并给出「立即创建」。</summary>
    [ObservableProperty]
    public partial bool IsTaskMissing { get; set; }

    /// <summary>构造外壳 ViewModel。</summary>
    /// <param name="registrar">计划任务注册端，只用来查询。</param>
    /// <param name="log">日志接收端。</param>
    public ShellViewModel(ISchedulerTaskRegistrar registrar, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentNullException.ThrowIfNull(log);

        _registrar = registrar;
        _log = log;

        TaskStatusText = "正在检查调度任务…";
    }

    /// <summary>重新查询调度任务状态。</summary>
    public void RefreshTaskStatus()
    {
        try
        {
            if (_registrar.IsRegistered())
            {
                IsTaskMissing = false;
                TaskStatusText = $"{_registrar.TaskPath.TrimStart('\\')}：已创建 · 登录后 3 秒触发";
                return;
            }

            IsTaskMissing = true;
            TaskStatusText = "尚未创建开机调度任务，延时启动不会生效";
        }
        catch (Exception ex)
        {
            // 查询本身失败（任务计划服务不可用 / 权限不足）与"任务不存在"是两件事，
            // 但对用户而言后果相同：延时启动不会生效。所以同样进警示态，但日志要能区分。
            _log.Error(ex, "查询调度计划任务状态失败");
            IsTaskMissing = true;
            TaskStatusText = "无法确认调度任务状态，延时启动可能不会生效";
        }
    }
}
