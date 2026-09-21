using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 遍历全部来源、汇总扫描结果（FR-1.1–FR-1.3 / <c>design.md</c> 7.4）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **每个来源单独 try/catch**（FR-1.4）。一个来源整体失败（例如注册表键被 ACL 拒绝、
/// 计划任务服务不可用）不能让它后面的来源也不跑 —— 那会让用户看到一份"少了半截"的列表
/// 却不知道少了什么。失败被打包成 <see cref="ScanFailure"/> 返回，由 UI 显式提示。
/// </para>
/// <para>
/// 扫描**无副作用**：只读注册表/文件系统/任务库，不改任何东西。所有写入都走
/// <see cref="TakeoverService"/>。
/// </para>
/// <para>
/// 本类**不负责缓存**。扫描结果的时效性由调用方决定（FR-1.2 要求进入管理端就扫一次）。
/// </para>
/// </remarks>
public sealed class ScanService
{
    private readonly IReadOnlyList<IStartupSource> _sources;
    private readonly IAppConfigStore _configStore;
    private readonly ILogSink _log;

    /// <summary>构造扫描服务。</summary>
    /// <param name="sources">全部来源实例（顺序即展示顺序）。</param>
    /// <param name="configStore">配置读取端，用于判断"哪些项已被接管"（FR-1.6）。</param>
    /// <param name="log">日志接收端。</param>
    public ScanService(IEnumerable<IStartupSource> sources, IAppConfigStore configStore, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(log);

        _sources = [.. sources];
        _configStore = configStore;
        _log = log;
    }

    /// <summary>
    /// 执行一次全量扫描。
    /// </summary>
    /// <returns>汇总结果；来源级失败收集在 <see cref="ScanResult.Failures"/> 中而不是抛出。</returns>
    public ScanResult Scan()
    {
        // 接管判定必须基于**稳定主键**，而不是 (Name, Source) 二元组（坑 6 / FR-1.6）。
        var takenOverKeys = LoadTakenOverKeys();

        var entries = new List<StartupEntry>();
        var failures = new List<ScanFailure>();

        foreach (var source in _sources)
        {
            try
            {
                entries.AddRange(source.Scan(takenOverKeys));
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"来源『{source.DisplayName}』扫描失败，其余来源继续（FR-1.4）");
                failures.Add(new ScanFailure
                {
                    Source = source.Kind,
                    Scope = source.Scope,
                    DisplayName = source.DisplayName,
                    Message = ex is StartupOperationException semantic ? semantic.Message : ex.Message,
                });
            }
        }

        // 排序放在这里而不是各来源内部：来源之间需要一致的次序，否则合并后会出现
        // "同一来源的条目被别的来源隔开"的杂乱列表。用 OrdinalIgnoreCase 而非区域性比较，
        // 保证不同机器、不同系统区域下顺序一致（可被测试断言）。
        entries.Sort(static (left, right) =>
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
        });

        return new ScanResult { Entries = entries, Failures = failures };
    }

    /// <summary>
    /// 从配置里读出全部已接管条目的稳定主键。
    /// </summary>
    /// <remarks>
    /// 配置损坏时 <c>ConfigService.Load</c> 会重建默认配置并返回空集合 —— 此时扫描结果里
    /// 所有条目都会显示为"未接管"。这是**安全的降级方向**：宁可显示"没接管过"，
    /// 也不能把没接管过的项显示成"已接管"而让用户失去操作入口。
    /// </remarks>
    private HashSet<string> LoadTakenOverKeys()
    {
        try
        {
            var config = _configStore.Load();
            return new HashSet<string>(
                config.Items.Select(static item => item.Id),
                StringComparer.Ordinal);
        }
        catch (StartupOperationException ex)
        {
            // 配置版本高于本程序（前向兼容保护）—— 拒绝加载是正确行为，
            // 但扫描本身不该因此不可用。
            _log.Warn(ex, "配置无法加载，本次扫描按「没有任何条目被接管」处理");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
