using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>
/// 从条目推导出的启动目标：真实可执行文件 + 其进程名。
/// </summary>
/// <param name="ExecutablePath">全限定的可执行文件路径（已展开环境变量、已去引号）。</param>
/// <param name="ProcessName">进程名（不含扩展名），用于 <c>Process.GetProcessesByName</c>。</param>
public sealed record LaunchTarget(string ExecutablePath, string ProcessName);

/// <summary>
/// 从 <see cref="DelayedItem"/> 动态推导"该条目真正会拉起哪个 exe"（D76，2026-09-22 用户批复）。
/// </summary>
/// <remarks>
/// <para>
/// 用途只有一个：调度端在启动前判断目标进程是否已在运行，避免双启动（§8）。
/// 🔴 推导不出来时**一律返回 <see langword="null"/>，调用方据此跳过检查、照常启动** ——
/// 漏判双启动只是多跑一个进程，而误判"已存在"会让用户的条目永远不启动（功能回归），
/// 两个方向的代价不对称，所以整条链路都往保守一侧倒。
/// </para>
/// <para>
/// 各来源的 <see cref="DelayedItem.Path"/> 实际存的东西并不一样（已逐一核对代码）：
/// </para>
/// <list type="bullet">
/// <item><description>注册表 Run / 手动条目 —— 解析后的可执行路径，参数另存；直接取文件名。</description></item>
/// <item><description>启动文件夹 —— 接管时已由 COM 解析成真实目标；解析失败才回落成 <c>.lnk</c>，那类跳过。</description></item>
/// <item><description>计划任务 —— 常是 <c>cmd.exe /c start "" "C:\app\x.exe"</c> 这类包装器形态，真目标在参数里；本类会往参数里挖一层。</description></item>
/// <item><description>UWP —— 裸 AUMID，不是文件路径；跳过。</description></item>
/// </list>
/// <para>
/// 🔴 不解析 <c>.lnk</c>：能进到配置里的 <c>.lnk</c> 路径，都是在管理端用 COM 解析失败后才回落的，
/// 而调度端是 NativeAOT、按其设计**零 COM**（D28），在这里另写一份 Shell Link 解析既违约束也无胜算。
/// <c>.cmd</c> / <c>.bat</c> / <c>.ps1</c> 同样跳过（真正跑起来的是 cmd.exe / pwsh.exe，不是脚本本身）。
/// </para>
/// </remarks>
public static class LaunchTargetResolver
{
    /// <summary>常见的"包装器"可执行文件名 —— 它们只是拉起真正程序的跳板。</summary>
    /// <remarks>
    /// 与 <c>ScheduledTaskSource</c> 原先维护的那份同源，搬迁到这里供两端共用：
    /// 计划任务的动作路径经常是包装器，按它取进程名会得出 <c>cmd</c>，而 <c>cmd</c> 几乎总是
    /// 存活着的 —— 那会让被接管的计划任务**每轮都判"进程已存在"、永不启动**。
    /// </remarks>
    private static readonly HashSet<string> WrapperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe",
        "conhost.exe",
        "cmd",
        "powershell.exe",
        "pwsh.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe",
        "rundll32.exe",
        "explorer.exe",
    };

    /// <summary>从配置条目推导启动目标。</summary>
    /// <param name="item">条目。</param>
    /// <returns>推导出的目标；推导不出时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> 为 <see langword="null"/>。</exception>
    public static LaunchTarget? Resolve(DelayedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return Resolve(item.Path, item.Arguments, item.Source);
    }

    /// <summary>从路径 / 参数 / 来源推导启动目标（供测试与复用）。</summary>
    /// <param name="path">条目路径。</param>
    /// <param name="arguments">条目参数。</param>
    /// <param name="source">来源类型。</param>
    /// <returns>推导出的目标；推导不出时为 <see langword="null"/>。</returns>
    public static LaunchTarget? Resolve(string? path, string? arguments, StartupSource source)
    {
        // UWP 的 Path 是 AUMID（<PackageFamilyName>!<AppId>），与文件系统无关。
        if (source is StartupSource.Uwp)
        {
            return null;
        }

        var raw = Unquote(path);
        if (raw.Length == 0)
        {
            return null;
        }

        // 包装器判定按**文件名**而不是"是否全限定"：计划任务的动作路径既可能是
        // C:\Windows\System32\cmd.exe，也可能就写 cmd.exe，两种都得往参数里挖。
        if (WrapperNames.Contains(Path.GetFileName(raw)))
        {
            var nested = FindExecutableInArguments(arguments);
            return nested is null ? null : Create(nested);
        }

        // 非包装器：必须是全限定的 .exe 才算数。`.lnk` / `.bat` / `.cmd` / `.ps1` /
        // AUMID / 无路径的裸程序名（如 "chrome.exe"）在这里全部落到 null ——
        // 裸程序名要经 PATH / App Paths 才能解析出真实路径，而"猜错了路径"会导致
        // 误判"进程已存在"进而永不启动，方向不可接受。
        var direct = NormalizeExecutable(raw);
        return direct is null ? null : Create(direct);
    }

    /// <summary>
    /// 从参数串里挖出第一个像"真目标"的可执行文件。
    /// </summary>
    /// <remarks>
    /// 逐 token 扫描而不是只看第一个：<c>cmd.exe /c start "" "C:\app\x.exe"</c> 的真目标在第 4 个 token。
    /// 空引号（<c>start</c> 的第一个参数，表示窗口标题）会被 tokenizer 直接丢弃。
    /// </remarks>
    private static string? FindExecutableInArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        foreach (var token in Tokenize(arguments))
        {
            var candidate = NormalizeExecutable(token);
            if (candidate is not null && !WrapperNames.Contains(Path.GetFileName(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>把参数串按空白切分，尊重双引号；空引号被丢弃。</summary>
    private static IEnumerable<string> Tokenize(string arguments)
    {
        var index = 0;
        while (index < arguments.Length)
        {
            while (index < arguments.Length && char.IsWhiteSpace(arguments[index]))
            {
                index++;
            }

            if (index >= arguments.Length)
            {
                yield break;
            }

            if (arguments[index] == '"')
            {
                var start = ++index;
                while (index < arguments.Length && arguments[index] != '"')
                {
                    index++;
                }

                var quoted = arguments[start..index];
                if (index < arguments.Length)
                {
                    index++; // 跳过后引号
                }

                if (quoted.Length > 0)
                {
                    yield return quoted;
                }

                continue;
            }

            var tokenStart = index;
            while (index < arguments.Length && !char.IsWhiteSpace(arguments[index]))
            {
                index++;
            }

            yield return arguments[tokenStart..index];
        }
    }

    /// <summary>展开环境变量、去掉外层引号；空值返回空串。</summary>
    private static string Unquote(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (expanded.Length > 1 && expanded.StartsWith('"'))
        {
            var end = expanded.IndexOf('"', 1);
            if (end > 1)
            {
                return expanded[1..end];
            }
        }

        return expanded;
    }

    /// <summary>判定是否"全限定的 .exe"；不像则返回 <see langword="null"/>。</summary>
    private static string? NormalizeExecutable(string? raw)
    {
        var expanded = Unquote(raw);
        if (expanded.Length == 0
            || !expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(expanded))
        {
            return null;
        }

        return expanded;
    }

    private static LaunchTarget Create(string executablePath)
        => new(executablePath, Path.GetFileNameWithoutExtension(executablePath));
}
