using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Services;

using Microsoft.Win32;

namespace DelayStart.Management.Sources;

/// <summary>
/// 注册表 <c>Run</c> 来源（FR-1 / <c>pitfalls.md</c> 一）。同一实现按 <see cref="StartupScope"/>
/// 实例化三次：HKCU / HKLM / HKLM-WOW6432Node。
/// </summary>
/// <remarks>
/// <para>
/// 三个实例的差异只有"打开哪个键"，所以用参数而不是三个类 —— 这保证三元组
/// （键路径、hive、标记子键）的对应关系只有**一处**定义，不会有第三个实例被漏改。
/// </para>
/// <para>
/// 🔴 读取一律用 <see cref="RegistryView.Registry64"/> 打开，WOW6432Node 通过
/// **显式子键路径**访问，而不是切到 <see cref="RegistryView.Registry32"/> ——
/// 后者会让"64 位进程读 32 位视图"依赖宿主位数，行为不稳定（<c>pitfalls.md</c> 一）。
/// </para>
/// </remarks>
public sealed class RegistryStartupSource : IStartupSource
{
    private const string RunSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Wow6432RunSubKeyPath = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    private readonly IClock _clock;
    private readonly ILogSink _log;

    /// <summary>构造一个注册表来源实例。</summary>
    /// <param name="scope">必须是 <see cref="StartupScope.Hkcu"/> / <see cref="StartupScope.Hklm"/> / <see cref="StartupScope.HklmWow"/> 之一。</param>
    /// <param name="clock">时间源，仅用于写禁用标记的时间戳。</param>
    /// <param name="log">日志接收端。</param>
    public RegistryStartupSource(StartupScope scope, IClock clock, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        if (scope is not (StartupScope.Hkcu or StartupScope.Hklm or StartupScope.HklmWow))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                "注册表来源只支持 Hkcu / Hklm / HklmWow 三种作用域。");
        }

        Scope = scope;
        _clock = clock;
        _log = log;
    }

    /// <inheritdoc />
    public StartupSource Kind => StartupSource.Registry;

    /// <inheritdoc />
    public StartupScope Scope { get; }

    /// <inheritdoc />
    public string DisplayName => Scope switch
    {
        StartupScope.Hkcu => "当前用户",
        StartupScope.Hklm => "所有用户",
        StartupScope.HklmWow => "所有用户（32 位）",
        _ => "注册表",
    };

    /// <inheritdoc />
    public bool RequiresElevation => Scope != StartupScope.Hkcu;

    /// <summary>本实例读取的注册表子键路径（面向用户展示）。</summary>
    public string RegistrySubKeyPath => Scope == StartupScope.HklmWow ? Wow6432RunSubKeyPath : RunSubKeyPath;

    /// <inheritdoc />
    public IReadOnlyList<StartupEntry> Scan(IReadOnlySet<string> takenOverKeys)
    {
        ArgumentNullException.ThrowIfNull(takenOverKeys);

        var entries = new List<StartupEntry>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ResolveHive(), RegistryView.Registry64);
            using var runKey = baseKey.OpenSubKey(RegistrySubKeyPath, writable: false);

            if (runKey is null)
            {
                // 键不存在不是错误：HKCU\...\Run 在某些精简安装上确实可能没有。
                _log.Info($"注册表 Run 键不存在，跳过：{DescribeKey()}");
                return entries;
            }

            foreach (var valueName in runKey.GetValueNames())
            {
                // 默认值（空名）不是自启动项，跳过。
                if (string.IsNullOrWhiteSpace(valueName))
                {
                    continue;
                }

                try
                {
                    entries.Add(BuildEntry(runKey, valueName, takenOverKeys));
                }
                catch (Exception ex)
                {
                    // FR-1.4：单条读取失败必须跳过而不是让整次扫描失败。
                    _log.Warn(ex, $"读取注册表启动项『{valueName}』失败，已跳过（FR-1.4）");
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 整个来源打不开是另一回事，交给 ScanService 汇总为"来源级失败"。
            _log.Error(ex, $"打开注册表 Run 键失败：{DescribeKey()}");
            throw;
        }

        return entries;
    }

    /// <inheritdoc />
    public void Disable(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StartupApprovedStore.Disable(Kind, Scope, entry.SourceKey, _clock.Now);
    }

    /// <inheritdoc />
    public void Enable(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StartupApprovedStore.Enable(Kind, Scope, entry.SourceKey);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 整个 Run 键打不开（返回 <see langword="null"/>）时答 <see langword="false"/> 是安全的：
    /// 键没了，它下面的值必然也没了。而 ACL 拒绝会**抛** <see cref="UnauthorizedAccessException"/>，
    /// 按约定向上传播 —— 那不是"源没了"，是"看不真切"，两者绝不能混。
    /// </remarks>
    public bool Exists(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var baseKey = RegistryKey.OpenBaseKey(ResolveHive(), RegistryView.Registry64);

        // 🔴 用只读打开：探测存在性不需要写权限。用 writable:true 会把"能读不能写"
        // 也变成异常，那是无谓的 —— 而按约定异常意味着"看不真切"，
        // 会让本来能确定的问题白白升级成失败。
        using var runKey = baseKey.OpenSubKey(RegistrySubKeyPath, writable: false);
        if (runKey is null)
        {
            return false;
        }

        // 三级候选名而不是只查 SourceKey 原名（坑 1 的同一份列表）：
        // 错答 false 的代价是永久丢失还原依据，保守一点只多一次无副作用的空操作恢复。
        foreach (var candidate in StartupApprovedStore.GetCandidateNames(entry.SourceKey))
        {
            if (runKey.GetValueNames().Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private StartupEntry BuildEntry(RegistryKey runKey, string valueName, IReadOnlySet<string> takenOverKeys)
    {
        // -- 原始命令行：GetValue 对 REG_EXPAND_SZ 会自动展开环境变量，正是我们要的。
        var rawCommand = runKey.GetValue(valueName)?.ToString() ?? string.Empty;
        var parsed = CommandLineService.Parse(rawCommand);

        var id = ItemKeyBuilder.Build(Kind, Scope, valueName);

        return new StartupEntry
        {
            Id = id,
            Name = valueName,
            Path = parsed.Path,
            Arguments = parsed.Arguments,
            Source = Kind,
            Scope = Scope,
            SourceKey = valueName,
            SourceDetail = DescribeKey(),
            // FR-1.5 / 机制 2：三级回退匹配，少试一个候选就会把已禁用的项误报为启用。
            IsEnabled = !StartupApprovedStore.IsDisabled(Kind, Scope, valueName),
            // FR-1.10：判据集中在 TargetFileProbe 一处（2026-09-22）—— 注册表 / 启动文件夹 /
            // 计划任务原本各写了一遍同一条式子，守卫的手动条目判定还需要第四份。
            // 其中最要紧的是"只有绝对路径才做存在性检查"：注册表里合法地存在 OneDrive、
            // SecurityHealth 这类裸命令名（由 PATH 解析），直接 File.Exists 必然为 false。
            IsMissing = TargetFileProbe.IsMissing(parsed.Path),
            // 注册表 Run 项本身没有"受保护"概念（组策略下发的是 Policy 键，不在这条路径上）。
            IsProtected = false,
            // D147：本来源**查不到**"这一项启动要不要管理员"，一律按普通身份。
            // HKLM Run 里的 exe 是否需要提权没有可靠判据：路径位置不算证据
            //（Program Files 下大把程序不需要提权），去读 RT_MANIFEST 又对脚本 / 打包器无效。
            // 判错方向是「按普通身份启动」——用户看得见、能自己改；不猜。
            RequiresAdminRun = false,
            // FR-1.6 / 机制 1：用稳定主键匹配，不用 (Name, Source) 二元组。
            IsTakenOver = takenOverKeys.Contains(id),
        };
    }

    private RegistryHive ResolveHive()
        => Scope == StartupScope.Hkcu ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private string DescribeKey()
        => $@"{(Scope == StartupScope.Hkcu ? "HKCU" : "HKLM")}\{RegistrySubKeyPath}";
}
