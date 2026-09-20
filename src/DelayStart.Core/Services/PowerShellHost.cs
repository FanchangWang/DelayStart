namespace DelayStart.Core.Services;

/// <summary>
/// <c>.ps1</c> 目标的 PowerShell 宿主解析与命令行构造（D47，2026-09-20 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么必须走宿主</b>：<c>.ps1</c> 不是 PE 映像，<c>CreateProcess</c> 家族直接报
/// <c>193 ERROR_BAD_EXE_FORMAT</c>；而 <c>ShellExecute</c> 对它也没有"打开"动词
/// （默认是"编辑"），表现为弹一个记事本或什么都不做 —— 脚本根本不会执行。
/// 唯一可靠的做法是显式起 <c>pwsh.exe</c> / <c>powershell.exe</c> 传 <c>-File</c>。
/// </para>
/// <para>
/// <b>宿主优先级（用户批复 D47-1）</b>：<c>pwsh.exe</c>（PowerShell 7+）优先，找不到才回落
/// <c>powershell.exe</c>（Windows PowerShell 5.1）。探测顺序：
/// ① <c>%ProgramFiles%\PowerShell\7\pwsh.exe</c>（MSI 标准安装）
/// ② <c>%ProgramFiles%\PowerShell\7-preview\pwsh.exe</c>
/// ③ <c>PATH</c> 里的 <c>pwsh.exe</c>
/// ④ <c>%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe</c>（应用执行别名）
/// ⑤ <c>%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe</c>（Win10/11 必有）
/// </para>
/// <para>
/// <c>-NoProfile</c>：登录时无人值守，加载用户配置只会拖慢并可能引入交互式提示。
/// <c>-ExecutionPolicy Bypass</c>：系统默认策略对脚本常是 <c>Restricted</c>，
/// 不加这个开关脚本会被直接拒绝执行；注意**组策略下发的策略优先级更高**，
/// 企业环境里 <c>Bypass</c> 会被忽略 —— 那种情况只能由用户自行放行脚本。
/// </para>
/// <para>
/// 探测入口全部参数化（含 <see cref="File.Exists"/> 委托），便于单元测试在无 PowerShell 的
/// 机器上验证优先级顺序 —— 也顺手把"这台机器上装了什么"与"优先级怎么排"这两件事解耦。
/// </para>
/// </remarks>
public static class PowerShellHost
{
    /// <summary>宿主可执行文件名（PowerShell 7+）。</summary>
    public const string PwshExecutableName = "pwsh.exe";

    /// <summary>宿主可执行文件名（Windows PowerShell 5.1）。</summary>
    public const string WindowsPowerShellExecutableName = "powershell.exe";

    /// <summary>脚本路径之前的固定开关（不含 <c>-File</c> 的位置参数，那个由路径本身占位）。</summary>
    private const string FixedArguments = "-NoProfile -ExecutionPolicy Bypass -File";

    /// <summary><c>%ProgramFiles%</c> 下的候选相对路径，按优先级排列。</summary>
    private static readonly string[] PwshRelativePaths =
    [
        Path.Combine("PowerShell", "7", PwshExecutableName),
        Path.Combine("PowerShell", "7-preview", PwshExecutableName),
    ];

    /// <summary>
    /// 解析承载脚本的 PowerShell 宿主。**永远返回一个路径**（最后一级回落是系统自带的
    /// Windows PowerShell），可用性由调用方按需复查。
    /// </summary>
    /// <param name="programFiles">覆盖 <c>%ProgramFiles%</c>（测试用）。</param>
    /// <param name="localAppData">覆盖 <c>%LOCALAPPDATA%</c>（测试用）。</param>
    /// <param name="systemRoot">覆盖 <c>%SystemRoot%</c>（测试用）。</param>
    /// <param name="pathVariable">覆盖 <c>PATH</c>（测试用）。</param>
    /// <param name="fileExists">覆盖文件存在性判定（测试用）；默认 <see cref="File.Exists"/>。</param>
    /// <returns>宿主可执行文件路径。</returns>
    public static string ResolveExecutable(
        string? programFiles = null,
        string? localAppData = null,
        string? systemRoot = null,
        string? pathVariable = null,
        Func<string, bool>? fileExists = null)
    {
        var probe = fileExists ?? File.Exists;

        var programFilesRoot = programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFilesRoot))
        {
            foreach (var relative in PwshRelativePaths)
            {
                var candidate = Path.Combine(programFilesRoot, relative);
                if (probe(candidate))
                {
                    return candidate;
                }
            }
        }

        var fromPath = FindOnPath(pathVariable ?? Environment.GetEnvironmentVariable("PATH"), probe);
        if (fromPath is not null)
        {
            return fromPath;
        }

        var localRoot = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localRoot))
        {
            // 应用执行别名（0 字节重解析点）：MSI / Store 安装都会生成，CreateProcess 能解析。
            var alias = Path.Combine(localRoot, "Microsoft", "WindowsApps", PwshExecutableName);
            if (probe(alias))
            {
                return alias;
            }
        }

        var relativeSystemPath = Path.Combine("System32", "WindowsPowerShell", "v1.0", WindowsPowerShellExecutableName);

        // %SystemRoot% 拿不到时（理论上不该发生）仍返回相对路径而非空串：
        // 空串会让调用方在"判失败"与"直接启动"之间二选一，而相对路径至少能报出确定的错误。
        var root = systemRoot ?? Environment.GetEnvironmentVariable("SystemRoot");
        return string.IsNullOrWhiteSpace(root) ? relativeSystemPath : Path.Combine(root, relativeSystemPath);
    }

    /// <summary>该宿主是否为 PowerShell 7+（<c>pwsh.exe</c>）—— 只用于日志与诊断措辞。</summary>
    /// <param name="executablePath">宿主路径。</param>
    /// <returns>文件名是 <c>pwsh.exe</c> 时为 <see langword="true"/>。</returns>
    public static bool IsPowerShell7(string executablePath)
        => !string.IsNullOrWhiteSpace(executablePath)
            && string.Equals(Path.GetFileName(executablePath.Trim('"')), PwshExecutableName, StringComparison.OrdinalIgnoreCase);

    /// <summary>构造传给宿主的**参数段**（不含宿主自身路径）。</summary>
    /// <param name="scriptPath">脚本完整路径。</param>
    /// <param name="arguments">用户填的脚本参数；为空则不附加。</param>
    /// <returns>形如 <c>-NoProfile -ExecutionPolicy Bypass -File "C:\a b\c.ps1" -Foo 1</c>。</returns>
    public static string BuildArguments(string scriptPath, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        // 脚本路径一律加引号：含空格时不加会被当成多个参数；不含空格时加了也完全等价。
        var quoted = $"\"{scriptPath.Trim().Trim('"')}\"";

        return string.IsNullOrWhiteSpace(arguments)
            ? $"{FixedArguments} {quoted}"
            : $"{FixedArguments} {quoted} {arguments.Trim()}";
    }

    /// <summary>构造完整命令行（宿主路径 + 参数段），供 <c>CreateProcessWithTokenW</c> 使用。</summary>
    /// <param name="executablePath">宿主路径。</param>
    /// <param name="scriptPath">脚本完整路径。</param>
    /// <param name="arguments">脚本参数；可为 <see langword="null"/>。</param>
    /// <returns>可直接交给 <c>CreateProcess</c> 的一行命令行。</returns>
    public static string BuildCommandLine(string executablePath, string scriptPath, string? arguments)
        => CommandLineService.Build(executablePath, BuildArguments(scriptPath, arguments));

    private static string? FindOnPath(string? pathVariable, Func<string, bool> probe)
    {
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        var separator = Path.PathSeparator;
        foreach (var raw in pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = raw.Trim('"');

            string candidate;
            try
            {
                candidate = Path.Combine(directory, PwshExecutableName);
            }
            catch (ArgumentException)
            {
                // PATH 里混进非法字符（引号没配对的条目）—— 跳过，不让它毁掉整条解析。
                continue;
            }

            if (probe(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
