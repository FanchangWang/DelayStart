using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 命令行的解析与拼接（§2.2）。**纯字符串逻辑，无任何系统依赖**，是本项目重点测试对象之一。
/// </summary>
/// <remarks>
/// <para>
/// 注册表 <c>Run</c> 值里存的是"一行完整命令行"，程序名与参数混在一起，
/// 而启动进程需要把两者拆开（拿路径查图标、判断文件是否存在、决定是否降权）。
/// 这件事没有官方 API 可做，只能自己实现 —— 所以必须把它测扎实。
/// </para>
/// <para>
/// 🔴 **已知局限（有意接受）**：Windows 命令行本身是有歧义的 ——
/// <c>C:\my.exe folder\app.exe</c> 既可解释为"程序 C:\my.exe + 参数 folder\app.exe"，
/// 也可解释为"程序 C:\my.exe folder\app.exe"。本实现按前者处理。
/// 凡路径中含空格，**正确写法是加引号**，这也是 Windows 自己的要求。
/// </para>
/// </remarks>
public static class CommandLineService
{
    private const char Quote = '"';

    /// <summary>
    /// 能被"路径 + 参数"形式拆分的可执行扩展名。
    /// </summary>
    /// <remarks>
    /// D47 起加 <c>.ps1</c>：脚本已属于受支持的目标类型（由 <c>PowerShellHost</c> 承载），
    /// 注册表 <c>Run</c> 值里 <c>C:\x\a.ps1 param</c> 这种不带 <c>-</c> 的写法也要能拆对。
    /// </remarks>
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd", ".ps1"];

    /// <summary>
    /// 把一行命令行拆成「程序路径」与「参数」。
    /// </summary>
    /// <param name="raw">原始命令行，可为 <see langword="null"/> 或空。</param>
    /// <returns>拆分结果；输入为空时两段均为空串。</returns>
    /// <remarks>
    /// 判定顺序：
    /// <list type="number">
    /// <item><description>以引号开头 → 引号内是路径，其余是参数（引号不配对时整串按路径处理，不猜）。</description></item>
    /// <item><description>否则从左往右找第一个空格，满足**任一**条件即为分割点：
    /// 后半段以 <c>-</c> 或 <c>/</c> 开头（Windows 参数惯例），
    /// 或前半段以可执行文件扩展名结尾。</description></item>
    /// <item><description>都不满足 → 整串都是路径，无参数。</description></item>
    /// </list>
    /// </remarks>
    public static ParsedCommandLine Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ParsedCommandLine(string.Empty, string.Empty);
        }

        var value = raw.Trim();

        if (value[0] == Quote)
        {
            var closing = value.IndexOf(Quote, 1);
            if (closing > 1)
            {
                return new ParsedCommandLine(value[1..closing], value[(closing + 1)..].Trim());
            }

            return new ParsedCommandLine(value[1..].Trim(), string.Empty);
        }

        var splitIndex = FindArgumentStart(value);

        return splitIndex < 0
            ? new ParsedCommandLine(value, string.Empty)
            : new ParsedCommandLine(value[..splitIndex].Trim(), value[splitIndex..].Trim());
    }

    /// <summary>
    /// 把「程序路径 + 参数」拼回一行命令行，路径含空格时自动加引号。
    /// </summary>
    /// <param name="path">程序路径，可已带引号。</param>
    /// <param name="arguments">参数；为空则不附加（FR-4.5）。</param>
    /// <returns>可直接交给 <c>CreateProcess</c> 的命令行；路径为空时返回空串。</returns>
    public static string Build(string? path, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var command = path.Trim();
        if (!IsQuoted(command) && command.Contains(' '))
        {
            command = $"{Quote}{command}{Quote}";
        }

        return string.IsNullOrWhiteSpace(arguments)
            ? command
            : $"{command} {arguments.Trim()}";
    }

    private static int FindArgumentStart(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsWhiteSpace(value[index]))
            {
                continue;
            }

            var next = index;
            while (next < value.Length && char.IsWhiteSpace(value[next]))
            {
                next++;
            }

            if (next >= value.Length)
            {
                break;
            }

            // 判据①：后半段以 - 或 / 开头，必是参数。
            // 这是区分「C:\Program Files\a.exe -arg」与「C:\Program Files\a.exe」的关键。
            if (value[next] is '-' or '/')
            {
                return index;
            }

            // 判据②：前半段以可执行文件扩展名结尾，则它自己就是程序，
            // 后半段是参数（覆盖 "rundll32.exe C:\a.dll,EntryPoint" 这类不以 - / 开头的参数）。
            if (LooksLikeExecutable(value[..index].Trim()))
            {
                return index;
            }

            // 两个判据都不满足 → 认为是「路径本身含空格」，继续往右找。
        }

        return -1;
    }

    private static bool LooksLikeExecutable(string token)
    {
        foreach (var extension in ExecutableExtensions)
        {
            if (token.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsQuoted(string value)
        => value.Length >= 2 && value[0] == Quote && value[^1] == Quote;
}
