using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.App.Services;

/// <summary>
/// 扫描结果的进程级缓存（bug#7：管理端启动时扫一次，各页面直接读缓存）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么放 App 层而不是 Management：<see cref="ScanService"/> 明确"不负责缓存"
/// （时效性由调用方决定），而缓存要在 GUI 与页面之间共享 —— 单例挂在组合根正合适。
/// </para>
/// <para>
/// 「刷新本页」（bug#6/#7 的配套）只重扫当前来源：<see cref="RefreshSourceAsync"/>
/// 复用缓存里其余来源的数据，合并后重新排序，页面无感。
/// </para>
/// <para>
/// 2026-09-20 批复 2：扫描后追加一步 **UWP 显示名兜底** —— Management 的注册表链路
/// 解析不出的（系统拆分包），用 App 层 WinRT 包目录 API 取系统解析名。
/// </para>
/// </remarks>
public sealed class ScanCacheService : IDisposable
{
    private readonly ScanService _scanner;
    private readonly IReadOnlyList<IStartupSource> _sources;
    private readonly IAppConfigStore _configStore;
    private readonly IconProvider _icons;
    private readonly ILogSink _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ScanSnapshot? _snapshot;

    /// <summary>已过期的来源集合（<see cref="Invalidate"/> 写入，重扫成功后清除）。</summary>
    private readonly HashSet<StartupSource> _staleSources = [];

    /// <summary>守护 <see cref="_staleSources"/> 的锁（与信号量 <see cref="_gate"/> 分开，避免语义混淆）。</summary>
    private readonly object _staleLock = new();

    /// <summary>构造扫描缓存。</summary>
    /// <param name="scanner">全量扫描服务。</param>
    /// <param name="sources">全部来源实例（按来源局部重扫时筛选用）。</param>
    /// <param name="configStore">配置读取端（重扫时判断已接管主键）。</param>
    /// <param name="icons">图标提取服务。</param>
    /// <param name="log">日志接收端。</param>
    public ScanCacheService(
        ScanService scanner,
        IEnumerable<IStartupSource> sources,
        IAppConfigStore configStore,
        IconProvider icons,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(log);

        _scanner = scanner;
        _sources = [.. sources];
        _configStore = configStore;
        _icons = icons;
        _log = log;
    }

    /// <summary>当前缓存快照；从未扫描过为 <see langword="null"/>。</summary>
    public ScanSnapshot? Current => _snapshot;

    /// <summary>
    /// 标记一个来源的数据已过期 —— 下次该来源的页面载入时重扫（D41：跨页刷新）。
    /// </summary>
    /// <param name="kind">要标记的来源。</param>
    /// <remarks>
    /// <para>
    /// 🔴 **为什么需要它**：接管 / 移出会同时改动两处状态（系统的
    /// <c>StartupApproved</c> 与 <c>config.json</c>），而行级局部刷新只改了**当前页的行对象**，
    /// 缓存快照里的 <see cref="StartupEntry"/> 仍是旧值。页面每次导航都新建实例并读缓存，
    /// 于是「在延时启动页移出 UWP → 进自启动项·UWP 页」看到的是移出前的状态
    /// （2026-09-20 实测：必须手动刷新整页才对）。
    /// </para>
    /// <para>
    /// 只标记**受影响的那一个来源**，其余来源的缓存继续命中 ——
    /// 全量重扫会把图标重新提取一遍，代价没必要。
    /// </para>
    /// </remarks>
    public void Invalidate(StartupSource kind)
    {
        lock (_staleLock)
        {
            _ = _staleSources.Add(kind);
        }
    }

    /// <summary>判断指定来源是否需要重扫；<see langword="null"/> 表示"任意来源有过期即可"。</summary>
    /// <param name="kind">来源；<see langword="null"/> 时只要有任一来源过期就返回 <see langword="true"/>。</param>
    /// <returns>是否需要重扫。</returns>
    public bool IsStale(StartupSource? kind)
    {
        lock (_staleLock)
        {
            return kind is { } specific ? _staleSources.Contains(specific) : _staleSources.Count > 0;
        }
    }

    /// <summary>清掉过期标记（重扫成功后调用）。</summary>
    /// <param name="kind">刚重扫的来源；<see langword="null"/> 表示全量重扫，清空全部标记。</param>
    private void ClearStale(StartupSource? kind)
    {
        lock (_staleLock)
        {
            if (kind is { } specific)
            {
                _ = _staleSources.Remove(specific);
            }
            else
            {
                _staleSources.Clear();
            }
        }
    }

    /// <summary>取缓存；没有就先做一次全量扫描（启动后的第一次进入）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扫描快照。</returns>
    public async Task<ScanSnapshot> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_snapshot is { } cached)
        {
            return cached;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>强制全量重扫。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新的扫描快照。</returns>
    public async Task<ScanSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshot = await Task.Run(() => ScanAllAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            ClearStale(kind: null);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>只重扫指定来源（bug#7：「刷新本页」），其余来源沿用缓存。</summary>
    /// <param name="kind">要重扫的来源类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>合并后的新快照；缓存为空时回退为全量扫描。</returns>
    public async Task<ScanSnapshot> RefreshSourceAsync(StartupSource kind, CancellationToken cancellationToken = default)
    {
        if (_snapshot is null)
        {
            return await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshot = await Task.Run(() => RescanSourceAsync(kind, cancellationToken), cancellationToken).ConfigureAwait(false);
            ClearStale(kind);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>全量扫描 + 图标提取 + UWP 名兜底（后台线程调用）。</summary>
    private async Task<ScanSnapshot> ScanAllAsync(CancellationToken cancellationToken)
    {
        var result = _scanner.Scan();
        var pixels = new Dictionary<StartupEntry, IconPixels?>(result.Entries.Count);
        foreach (var entry in result.Entries)
        {
            pixels[entry] = ExtractIcon(entry);
        }

        var entries = await ResolveUwpNamesAsync(result.Entries, pixels, cancellationToken).ConfigureAwait(false);
        return new ScanSnapshot(entries, pixels, result.Failures);
    }

    /// <summary>重扫单个来源并合并进缓存（后台线程调用）。</summary>
    private async Task<ScanSnapshot> RescanSourceAsync(StartupSource kind, CancellationToken cancellationToken)
    {
        var existing = _snapshot!;

        var takenOver = LoadTakenOverKeys();
        var entries = existing.Entries.Where(entry => entry.Source != kind).ToList();
        var failures = existing.Failures.Where(failure => failure.Source != kind).ToList();
        var pixels = new Dictionary<StartupEntry, IconPixels?>(existing.Pixels);

        foreach (var source in _sources.Where(source => source.Kind == kind))
        {
            try
            {
                var scanned = source.Scan(takenOver);
                foreach (var entry in scanned)
                {
                    entries.Add(entry);
                    pixels[entry] = ExtractIcon(entry);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"来源『{source.DisplayName}』局部重扫失败（FR-1.4）");
                failures.Add(new ScanFailure
                {
                    Source = source.Kind,
                    Scope = source.Scope,
                    DisplayName = source.DisplayName,
                    Message = ex is StartupOperationException semantic ? semantic.Message : ex.Message,
                });
            }
        }

        entries.Sort(CompareEntries);
        var resolved = await ResolveUwpNamesAsync(entries, pixels, cancellationToken).ConfigureAwait(false);
        ((List<StartupEntry>)resolved).Sort(CompareEntries);
        return new ScanSnapshot(resolved, pixels, failures);
    }

    /// <summary>
    /// UWP 显示名兜底：Management 链路仍解析不出的条目（残留 ms-resource / 包名前缀），
    /// 换成 WinRT 包目录给的系统解析名。
    /// </summary>
    /// <remarks>
    /// <see cref="StartupEntry"/> 是不可变模型：换名字 = 换实例，图标字典要同步换键。
    /// 同一包族的结果整轮缓存（一次扫描里同包多任务很常见）。
    /// </remarks>
    private static async Task<IReadOnlyList<StartupEntry>> ResolveUwpNamesAsync(
        IReadOnlyList<StartupEntry> entries,
        Dictionary<StartupEntry, IconPixels?> pixels,
        CancellationToken cancellationToken)
    {
        var output = new List<StartupEntry>(entries.Count);
        var resolvedCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Source != StartupSource.Uwp
                || !UwpNameResolver.LooksUnresolved(entry.Name, entry.SourceDetail))
            {
                output.Add(entry);
                continue;
            }

            if (!resolvedCache.TryGetValue(entry.SourceDetail, out var resolved))
            {
                resolved = await UwpNameResolver.TryResolveAsync(entry.SourceDetail).ConfigureAwait(false);
                resolvedCache[entry.SourceDetail] = resolved;
            }

            if (resolved is null)
            {
                output.Add(entry);
                continue;
            }

            var updated = ReplaceName(entry, resolved);
            output.Add(updated);
            if (pixels.Remove(entry, out var icon))
            {
                pixels[updated] = icon;
            }
        }

        return output;
    }

    /// <summary>以更换 <see cref="StartupEntry.Name"/> 为唯一目的的复制。</summary>
    private static StartupEntry ReplaceName(StartupEntry entry, string name) => new()
    {
        Id = entry.Id,
        Name = name,
        Path = entry.Path,
        ExecutablePath = entry.ExecutablePath,
        Arguments = entry.Arguments,
        Source = entry.Source,
        Scope = entry.Scope,
        SourceKey = entry.SourceKey,
        SourceDetail = entry.SourceDetail,
        IsEnabled = entry.IsEnabled,
        IsMissing = entry.IsMissing,
        IsProtected = entry.IsProtected,
        IsTakenOver = entry.IsTakenOver,
    };

    private IconPixels? ExtractIcon(StartupEntry entry)
    {
        var parsingName = entry.Source == StartupSource.Uwp
            // UWP 的 Path 就是 AUMID（PFN!TaskId），SourceKey 只是 TaskId —— 用错会拼出不存在的解析名。
            ? string.IsNullOrWhiteSpace(entry.Path) ? null : $"shell:AppsFolder\\{entry.Path}"
            // 批复 9：计划任务 cmd 包装等形态，图标优先用来源解析出的真实 exe。
            : string.IsNullOrWhiteSpace(entry.ExecutablePath)
                ? string.IsNullOrWhiteSpace(entry.Path) ? null : entry.Path
                : entry.ExecutablePath;

        return parsingName is null ? null : _icons.TryGetIcon(parsingName);
    }

    private HashSet<string> LoadTakenOverKeys()
    {
        try
        {
            return [.. _configStore.Load().Items.Select(static item => item.Id)];
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "配置无法加载，局部重扫按「没有接管项」处理");
            return [];
        }
    }

    /// <summary>与 <see cref="ScanService"/> 一致的排序：来源 → 作用域 → 名称。</summary>
    private static int CompareEntries(StartupEntry left, StartupEntry right)
    {
        var bySource = left.Source.CompareTo(right.Source);
        if (bySource != 0)
        {
            return bySource;
        }

        var byScope = left.Scope.CompareTo(right.Scope);
        return byScope != 0
            ? byScope
            : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    /// <remarks>缓存是进程级单例，容器释放时才会走到这里；Gate 无等待者，立即释放。</remarks>
    public void Dispose()
    {
        _gate.Dispose();
    }
}

/// <summary>一次扫描的不可变快照：条目 + 图标像素 + 来源级失败。</summary>
/// <param name="Entries">全部条目（已按来源 → 作用域 → 名称排序）。</param>
/// <param name="Pixels">每个条目的图标像素；缺失为 <see langword="null"/>。</param>
/// <param name="Failures">来源级扫描失败（FR-1.4）。</param>
public sealed record ScanSnapshot(
    IReadOnlyList<StartupEntry> Entries,
    IReadOnlyDictionary<StartupEntry, IconPixels?> Pixels,
    IReadOnlyList<ScanFailure> Failures);
