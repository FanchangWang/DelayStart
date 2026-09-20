namespace DelayStart.Core.Services;

/// <summary>
/// 可延时启动的目标类型白名单（D47，2026-09-20 用户批复）—— 全项目**唯一**事实来源。
/// </summary>
/// <remarks>
/// <para>
/// 编辑器（文件选择器白名单、拖放校验）与调度端（启动路由）必须共用这一份清单。
/// 两边各写一份的已知后果是"选得进来却启不动"或"启得动却选不进来" ——
/// 这种漂移不会报错，只会让用户看到一条永远失败的条目。
/// </para>
/// <para>
/// 🔴 <b>D47 移除 <c>.msi</c></b>：MSI 包不是 PE 映像，<c>CreateProcess</c> 家族对它一律返回
/// <c>193 ERROR_BAD_EXE_FORMAT</c>（真机实测：同一链路上 <c>.bat</c> / <c>.cmd</c> 正常）。
/// 且"登录后延时启动"的场景本就不该包含安装包 —— 安装向导不该被延时静默拉起。
/// </para>
/// <para>
/// 🔴 <b>D47 新增 <c>.ps1</c></b>：脚本不是可执行映像，必须由 PowerShell 宿主承载
/// （<see cref="PowerShellHost"/>），直接交给 <c>CreateProcess</c> 同样报 193。
/// </para>
/// </remarks>
public static class LaunchTargetTypes
{
    /// <summary>PowerShell 脚本扩展名。</summary>
    public const string PowerShellScriptExtension = ".ps1";

    private static readonly string[] PowerShellExtensions = [PowerShellScriptExtension];

    /// <summary>允许作为延时启动目标的扩展名（含点，小写）。</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".exe", ".lnk", ".bat", ".cmd", PowerShellScriptExtension];

    /// <summary>界面上展示的白名单文案，形如 <c>.exe / .lnk / .bat / .cmd / .ps1</c>。</summary>
    public static string DisplayList { get; } = string.Join(" / ", Extensions);

    /// <summary>该路径是否属于受支持的目标类型。</summary>
    /// <param name="path">目标路径；<see langword="null"/> 或空视为不支持。</param>
    /// <returns>扩展名在白名单内为 <see langword="true"/>。</returns>
    public static bool IsSupported(string? path) => Matches(path, Extensions);

    /// <summary>是否为需要 PowerShell 宿主承载的脚本（<c>.ps1</c>）。</summary>
    /// <param name="path">目标路径。</param>
    /// <returns>是 <c>.ps1</c> 时为 <see langword="true"/>。</returns>
    public static bool IsPowerShellScript(string? path) => Matches(path, PowerShellExtensions);

    private static bool Matches(string? path, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var extension in extensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
