using System.Runtime.InteropServices;

namespace DelayStart.Management.Models;

/// <summary>
/// 一个系统服务 / 驱动的只读展示信息（FR-7.1 / FR-7.3）。
/// </summary>
/// <param name="ServiceName">服务键名。</param>
/// <param name="DisplayName">显示名。</param>
/// <param name="StartType">启动类型文案（自动 / 自动（延迟）/ 手动 / 已禁用 / 系统…）。</param>
/// <param name="StatusText">运行状态文案（正在运行 / 已停止 / 暂停）。</param>
/// <param name="DelayedAuto">是否为 Windows 原生"延迟自动启动"（FR-7.2 的判定依据）。</param>
/// <param name="IsDriver">是否内核 / 文件系统驱动（FR-7.3，只读标注）。</param>
/// <param name="BinaryPath">可执行映像路径（注册表 <c>ImagePath</c>，已展开环境变量、去掉引号与参数）；读不到为空串。</param>
public sealed record ServiceInfo(
    string ServiceName,
    string DisplayName,
    string StartType,
    string StatusText,
    bool DelayedAuto,
    bool IsDriver,
    string BinaryPath)
{
    /// <summary>延迟自动启动的标注文案；XAML 的 x:Bind 表达式里写不了字符串三元式。</summary>
    public string DelayedAutoText => DelayedAuto ? "延迟启动" : string.Empty;

    /// <summary>
    /// 是否 Windows 内置服务 / 驱动（D2/D3 判据：映像位于 Windows 目录之下）。
    /// </summary>
    /// <remarks>
    /// 系统启动项页默认只展示第三方项 —— 数百个内置项会淹没用户真正需要排查的内容。
    /// 判据用**路径**而不是发布商签名（读版本信息要打开每个 PE，代价高一个量级）：
    /// 内置服务与驱动几乎全部落在 <c>C:\Windows\</c> 下；读不到路径时保守视为内置，
    /// 宁可少显示也不要把来历不明的东西当成"第三方"推荐给用户。
    /// </remarks>
    public bool IsBuiltinWindows => IsBuiltinPath(BinaryPath);

    /// <summary>内置判据的纯函数（可单测）。</summary>
    /// <param name="binaryPath">映像路径（已展开、已去引号）。</param>
    /// <returns>属于 Windows 内置为 <see langword="true"/>。</returns>
    internal static bool IsBuiltinPath(string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return true; // 读不到路径 → 保守视为内置（默认视图隐藏）
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            return false; // 拿不到 Windows 目录时退回"全显示"，避免误过滤
        }

        return binaryPath.StartsWith(
            windows.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
