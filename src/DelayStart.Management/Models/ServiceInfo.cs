using System.Collections.Concurrent;

namespace DelayStart.Management.Models;

/// <summary>
/// 一个系统服务 / 驱动的只读展示信息（FR-7.1 / FR-7.3）。
/// </summary>
/// <param name="ServiceName">服务键名。</param>
/// <param name="DisplayName">显示名（MUI 间接字符串已解析；解析不了保留原文）。</param>
/// <param name="Description">服务描述（注册表 <c>Description</c> 值，MUI 已解析；读不到为空串）。</param>
/// <param name="StartType">启动类型文案（自动 / 自动（延迟）/ 手动 / 已禁用 / 系统…）。</param>
/// <param name="StatusText">运行状态文案（正在运行 / 已停止 / 暂停）。</param>
/// <param name="IsRunning">是否处于运行态（状态配色用；暂停 / 停止均为 <see langword="false"/>）。</param>
/// <param name="DelayedAuto">是否为 Windows 原生"延迟自动启动"（FR-7.2 的判定依据）。</param>
/// <param name="IsDriver">是否内核 / 文件系统驱动（FR-7.3，只读标注）。</param>
/// <param name="BinaryPath">可执行映像路径（注册表 <c>ImagePath</c>，已展开环境变量、去掉引号与参数）；读不到为空串。</param>
/// <param name="IsBuiltin">是否 Windows 内置（查询时算好存进来；签名判据要读 PE 文件，不能留在 UI 过滤路径上算）。</param>
public sealed record ServiceInfo(
    string ServiceName,
    string DisplayName,
    string Description,
    string StartType,
    string StatusText,
    bool IsRunning,
    bool DelayedAuto,
    bool IsDriver,
    string BinaryPath,
    bool IsBuiltin = false)
{
    /// <summary>是否 Windows 内置服务 / 驱动（D2/D3 判据；过滤视图用）。</summary>
    public bool IsBuiltinWindows => IsBuiltin;

    /// <summary>内置判定缓存（签名读取要打开 PE 文件，同一镜像路径只算一次）。</summary>
    private static readonly ConcurrentDictionary<string, bool> BuiltinCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>内置判据总入口（在后台查询线程调用，2026-09-20 批复 1）。</summary>
    /// <param name="binaryPath">映像路径（已展开、已去引号与参数）。</param>
    /// <returns>属于 Windows 内置为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 签名判据优先：读 PE 内嵌 Authenticode 的签名者主题。
    /// <list type="bullet">
    /// <item><description>主题含 <c>Microsoft</c>（Microsoft Windows / Microsoft Corporation 签发）→ 内置；</description></item>
    /// <item><description>主题是 <c>Microsoft Windows Hardware Compatibility Publisher</c> → 微软**代签**的
    /// WHQL 第三方驱动（NVIDIA / Intel 等），算第三方；</description></item>
    /// <item><description>没有内嵌签名（cat 签名的老驱动等）→ 退回路径判据 <see cref="IsBuiltinPath"/>。</description></item>
    /// </list>
    /// </remarks>
    internal static bool ComputeIsBuiltin(string binaryPath) =>
        BuiltinCache.GetOrAdd(binaryPath, static path => ComputeUncached(path));

    private static bool ComputeUncached(string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return true; // 读不到路径 → 保守视为内置（默认视图隐藏）
        }

        var subject = Interop.Authenticode.TryGetSignerSubject(binaryPath);
        if (!string.IsNullOrEmpty(subject))
        {
            if (subject.Contains("hardware compatibility publisher", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return subject.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }

        return IsBuiltinPath(binaryPath);
    }

    /// <summary>路径判据（无内嵌签名时的回退；<see cref="ComputeIsBuiltin"/> 内部使用）。</summary>
    /// <param name="binaryPath">映像路径（已展开、已去引号）。</param>
    /// <returns>属于 Windows 内置为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 三类都算内置（2026-09-19 用户批复 11：Windows Defender 一族此前被误判为第三方）：
    /// ① 落在 Windows 目录下；
    /// ② 路径里含 <c>Windows Defender</c> / <c>Windows Security</c> —— 它们装在
    /// <c>Program Files\Windows Defender</c> 与 <c>ProgramData\Microsoft\Windows Defender</c>，
    /// 目录判据覆盖不到；
    /// ③ 路径为空 → 保守视为内置，宁可少显示也不要把来历不明的东西当"第三方"。
    /// </remarks>
    internal static bool IsBuiltinPath(string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return true; // 读不到路径 → 保守视为内置（默认视图隐藏）
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows)
            && binaryPath.StartsWith(
                windows.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Windows Defender / 安全中心一族：安装位置在 Windows 目录之外，按特征匹配。
        return binaryPath.Contains("windows defender", StringComparison.OrdinalIgnoreCase)
            || binaryPath.Contains("windows security", StringComparison.OrdinalIgnoreCase)
            || binaryPath.Contains("microsoft security", StringComparison.OrdinalIgnoreCase);
    }
}
