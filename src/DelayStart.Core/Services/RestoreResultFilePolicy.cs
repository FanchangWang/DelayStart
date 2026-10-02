namespace DelayStart.Core.Services;

/// <summary>
/// 卸载还原结果文件（<c>--result-file</c>）的落盘策略（D61 / D144）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **为什么它是一道闸而不是"顺手加个判断"**：Inno 卸载器用
/// <c>ShellExec('runas')</c> 拉起提权进程，**拿不到退出码**（D61），只能靠这个文件回读，
/// 于是"文件里的内容"直接决定卸载放行还是中止。文件路径由**卸载器的命令行**给出，
/// 而卸载器与那个抢写者可以是同一个用户的两个进程（一个是中完整性的普通进程）。
/// 路径一旦可预测、写入又允许覆盖，中完整性进程就能抢先造出同名文件填一个
/// <c>0</c>，卸载器读到"还原成功"而放行 —— 用户就此永久失去被接管自启动项的还原入口，
/// 且现场没有任何痕迹。这正是 D22 明令禁止的后果，也是"失败必须可见"（硬约束 7）被绕过。
/// </para>
/// <para>
/// 因此本策略给的是**两道独立的闸**：
/// </para>
/// <list type="number">
/// <item><description><b>白名单</b>：路径必须落在
/// <see cref="PathService.TempExchangeRoot"/>（<c>%TEMP%\DelayStart\</c>）之下，
/// 且必须先经 <see cref="Path.GetFullPath(string)"/> 归一化 —— 否则 <c>..\</c> 穿越能一次绕过任何
/// 基于字符串前缀的比较。不在范围内一律**拒绝写**，绝不"改写到安全位置"，
/// 因为悄悄换个地方写等于让卸载器永远等不到它，失败就变成静默的了。</description></item>
/// <item><description><b>独占创建</b>：<see cref="FileMode.CreateNew"/>。
/// <see cref="File.WriteAllText(string, string)"/> 的隐式覆盖会把上面那道闸的意义架空 ——
/// 预置文件会被无声地改写，抢写者看不到任何异常。</description></item>
/// </list>
/// <para>
/// 🔴 关于残留风险，说清楚边界：同用户的文件通道在原理上无法自证真伪（抢写者与
/// 卸载器同权限，任何内容它都能伪造）。真正把窗口关掉的是**文件名不可预测**
/// （Inno 侧每次卸载随机）+ **写不进就报错**；白名单与独占创建是纵深防御与可见性的保证。
/// 要再进一步只能换通道（比如改回可读退出码的启动方式），那是 D61 明确划走的路。
/// </para>
/// <para>
/// 放在 Core 而不是 App：本类型是纯逻辑，而 Tests **绝不引用 App**（设计书 §7.1）——
/// 留在 App 里就等于零单测。抽取到有测试工程的那一层是本项目的既有规矩
/// （与 <see cref="BrokerResultPolicy"/> 同理）。
/// </para>
/// </remarks>
public static class RestoreResultFilePolicy
{
    /// <summary>
    /// 判定 <c>--result-file</c> 给出的候选路径是否允许作为结果文件的落盘位置。
    /// </summary>
    /// <param name="candidatePath">命令行给出的原始路径（未归一化）。</param>
    /// <param name="exchangeRoot">允许的交换目录（<see cref="PathService.TempExchangeRoot"/>）。</param>
    /// <returns>落在交换目录**之下**（不含目录本身）时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 🔴 比对一律在 <see cref="Path.GetFullPath(string)"/> 归一化之后做，且用
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>：Windows 路径不区分大小写，
    /// 而 <c>%TEMP%\DELAYSTART\x.txt</c> 与 <c>%TEMP%\DelayStart\x.txt</c> 指同一个文件。
    /// <para>
    /// 目录**本身**判否：白名单管的是"文件写在哪"，把目录本身当作结果文件路径
    /// 只会得到一个必然打不开的路径（<c>FileStream</c> 对已存在的目录抛
    /// <see cref="UnauthorizedAccessException"/>）。判否让这个错误在闸门口就说清楚，
    /// 而不是伪装成一次"写入失败"。
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="exchangeRoot"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="exchangeRoot"/> 为空或纯空白。</exception>
    public static bool IsAllowedPath(string? candidatePath, string exchangeRoot)
    {
        ArgumentNullException.ThrowIfNull(exchangeRoot);
        if (string.IsNullOrWhiteSpace(exchangeRoot))
        {
            throw new ArgumentException("交换目录不能为空。", nameof(exchangeRoot));
        }

        // 空串/空白不是"路径"，直接判否 —— 拿它去 GetFullPath 会得到当前工作目录，
        // 那等于把工作目录悄悄当成合法落点。
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string normalizedRoot;
        string normalizedCandidate;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exchangeRoot));
            normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            // 非法路径字符、过长、NUL 嵌入等：归一化不出来的路径一律判否，
            // 绝不放行"解析不了就先写着试试"。
            return false;
        }

        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 🔴 必须自己补一个分隔符再比：`C:\Temp\DelayStart-evil` 与 `C:\Temp\DelayStart`
        //    用朴素前缀比会判"在里面"，而它其实在**兄弟目录**里。
        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 独占创建结果文件并写入内容：文件**已存在即失败**，绝不覆盖。
    /// </summary>
    /// <param name="path">目标文件完整路径（调用方应先用 <see cref="IsAllowedPath"/> 判定）。</param>
    /// <param name="content">十进制退出码文本（无换行，卸载器按 <c>StrToIntDef</c> 解析）。</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空或纯空白。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="IOException">文件已存在，或写入过程失败。</exception>
    /// <remarks>
    /// 与 <see cref="AtomicFileWriter"/> 的差别是刻意存在的：那条链路要的是"覆盖后仍然完整"，
    /// 而这条链路要的是"**只有我写得出**"。所以不用临时文件 + 改名 ——
    /// 改名会把 <see cref="FileMode.CreateNew"/> 提供的"文件此前不存在"这一条证据丢掉。
    /// </remarks>
    public static void WriteExclusive(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }
}