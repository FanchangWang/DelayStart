namespace DelayStart.Core.Models;

/// <summary>
/// 自启动项所在的作用域（注册表 hive / 启动文件夹归属）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **必须显式持久化**，禁止用 <c>SourceDetail</c> 字符串 <c>StartsWith("HKLM")</c> 去猜 hive
/// —— 恢复时写错 hive 是**静默失效**：没有报错，用户以为恢复了其实没有。
/// 见 <c>docs/api-analysis.md</c> 坑 5、<c>docs/architecture.md</c> 机制 3。
/// </para>
/// <para>
/// <see cref="None"/> 用于计划任务、UWP 与手动条目 —— 它们不属于任何注册表 hive
/// 或启动文件夹，恢复动作走各自的通道（任务定义本身 / <c>State</c> 值 / 无动作）。
/// </para>
/// </remarks>
public enum StartupScope
{
    /// <summary>不属于注册表或启动文件夹（计划任务 / UWP / 手动条目）。</summary>
    None,

    /// <summary><c>HKEY_CURRENT_USER</c>。</summary>
    Hkcu,

    /// <summary><c>HKEY_LOCAL_MACHINE</c>（64 位视图）。</summary>
    Hklm,

    /// <summary><c>HKEY_LOCAL_MACHINE\Software\WOW6432Node</c>（32 位视图）。</summary>
    /// <remarks>
    /// ⚠️ 该作用域的软禁用标记写在 <c>StartupApproved\Run32</c>，**不是** <c>Run</c>。
    /// 见 <c>docs/api-analysis.md</c> 坑 2。
    /// </remarks>
    HklmWow,

    /// <summary>当前用户的启动文件夹。</summary>
    UserFolder,

    /// <summary>所有用户的公共启动文件夹。</summary>
    SystemFolder,
}
