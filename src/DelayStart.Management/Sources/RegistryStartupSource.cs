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
            IsMissing = IsTargetMissing(parsed.Path),
            // 注册表 Run 项本身没有"受保护"概念（组策略下发的是 Policy 键，不在这条路径上）。
            IsProtected = false,
            // FR-1.6 / 机制 1：用稳定主键匹配，不用 (Name, Source) 二元组。
            IsTakenOver = takenOverKeys.Contains(id),
        };
    }

    /// <summary>
    /// 判断目标是否已失效（FR-1.10）。
    /// </summary>
    /// <remarks>
    /// 🔴 只有**绝对路径**才做存在性检查。注册表里合法地存在
    /// <c>OneDrive</c>、<c>SecurityHealth</c> 这类"裸命令名"（由 <c>PATH</c> 解析），
    /// 对它们调 <see cref="File.Exists"/> 必然为 <see langword="false"/>，
    /// 直接标"已失效"会造成大量误报 —— 用户会看到一堆其实好好的程序被打上失效标记。
    /// </remarks>
    private static bool IsTargetMissing(string path)
        => !string.IsNullOrWhiteSpace(path)
            && Path.IsPathFullyQualified(path)
            && !File.Exists(path);

    private RegistryHive ResolveHive()
        => Scope == StartupScope.Hkcu ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private string DescribeKey()
        => $@"{(Scope == StartupScope.Hkcu ? "HKCU" : "HKLM")}\{RegistrySubKeyPath}";
}
