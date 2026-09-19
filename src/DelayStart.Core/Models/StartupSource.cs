namespace DelayStart.Core.Models;

/// <summary>
/// 自启动项的来源类型。
/// </summary>
/// <remarks>
/// 取值顺序与 <see cref="StartupScope"/> 一样是稳定的，但**不要**把序号写进配置文件或
/// 注册表标记 —— 持久化一律用名字符串（见 <c>JsonContext</c> 的字符串枚举配置）。
/// </remarks>
public enum StartupSource
{
    /// <summary>注册表 Run / RunOnce 值。</summary>
    Registry,

    /// <summary>启动文件夹中的快捷方式或可执行文件。</summary>
    StartupFolder,

    /// <summary>登录触发的计划任务。</summary>
    ScheduledTask,

    /// <summary>UWP / MSIX 应用的自启动任务（TaskId）。</summary>
    Uwp,

    /// <summary>用户手动添加，在系统中没有任何可锚定的来源。</summary>
    Manual,
}
