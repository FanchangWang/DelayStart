using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.App.Services;
using DelayStart.Core.Services;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 「延时启动」页的 ViewModel。数据来自 <c>config.json</c>，不是系统扫描结果。
/// </summary>
/// <remarks>
/// <para>
/// 与「自启动项」页的关键区别：那一页回答"系统里有什么"，这一页回答"我接管了什么、
/// 打算怎么启动它们"。所以本页可以包含**手动添加**的条目 —— 它在系统里没有对应物。
/// </para>
/// <para>
/// 本页只有列表一种视图。时间轴视图已按 <c>D37 = B</c> 整体删除
/// （原设计的双视图 + 搜索态同步过滤是本页最容易长 bug 的分支）。
/// </para>
/// </remarks>
public sealed partial class DelayViewModel : ObservableObject
{
    private readonly IAppConfigStore _configStore;
    private readonly ScanService _scanner;
    private readonly TakeoverService _takeover;
    private readonly ConfigEditService _editor;
    private readonly IconProvider _icons;
    private readonly ScanCacheService _scanCache;
    private readonly ILogSink _log;

    /// <summary>最近一次后台刷新提取到的图标像素，键为条目引用。</summary>
    /// <remarks>
    /// <see cref="Load"/> 在 UI 线程同步构建行，等不起逐条提取 ——
    /// 图标像素由 <see cref="RefreshAsync"/> 的后台任务预先备好；
    /// 缓存未命中（手动添加 / 编辑后的局部刷新）才在 UI 线程回退单条提取，
    /// 而那一条通常已被 <see cref="IconProvider"/> 缓存，代价接近零。
    /// </remarks>
    private Dictionary<DelayedItem, IconPixels?> _pendingPixels = [];

    /// <summary>页头副标题，形如 `共 5 项（含手动添加 1 项）· 最后一个在登录后 1 分 00 秒启动`。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; }

    /// <summary>是否正在读取 / 刷新。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>读取配置或扫描失败时的提示；正常时为 <see langword="null"/>。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    /// <summary>是否存在错误提示。XAML 的 <c>InfoBar.IsOpen</c> 只吃布尔值。</summary>
    /// <remarks>计算属性而不是独立的可观察字段，理由同 <c>ItemsViewModel.HasFailure</c>。</remarks>
    public bool HasError => ErrorText is not null;

    /// <summary>构造延时启动页 ViewModel。</summary>
    /// <param name="configStore">配置读写端。</param>
    /// <param name="scanner">扫描服务，仅用于判断条目是否已失效（E3）。</param>
    /// <param name="takeover">接管 / 释放服务。</param>
    /// <param name="editor">条目级编辑服务（改延时 / 切开关 / 调顺序 / 手动添加）。</param>
    /// <param name="icons">图标提取服务（D30）。</param>
    /// <param name="scanCache">扫描缓存（移出后标记对应来源过期，供来源页重扫，D41）。</param>
    /// <param name="log">日志接收端。</param>
    public DelayViewModel(
        IAppConfigStore configStore,
        ScanService scanner,
        TakeoverService takeover,
        ConfigEditService editor,
        IconProvider icons,
        ScanCacheService scanCache,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(takeover);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(scanCache);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _scanner = scanner;
        _takeover = takeover;
        _editor = editor;
        _icons = icons;
        _scanCache = scanCache;
        _log = log;

        Subtitle = "正在读取配置…";
        DelayPresets = new Settings().DelayPresets;
        DefaultPreset = new Settings().DefaultPreset;
    }

    /// <summary>列表内容，按「延时 → 顺序」排序（与调度端的发起顺序一致）。</summary>
    public ObservableCollection<DelayRow> Rows { get; } = [];

    /// <summary>列表是否为空（用于区分两种空状态）。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>延时预设值（秒），驱动编辑器的快选按钮（FR-4.2）。</summary>
    public int[] DelayPresets { get; private set; }

    /// <summary>默认预设（秒）：编辑器打开时预选的延时（2026-09-19 用户批复）。</summary>
    public int DefaultPreset { get; private set; }

    /// <summary>读取配置并刷新列表。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>刷新完成的 <see cref="Task"/>。</returns>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            // 失效判定与图标提取共用这次扫描 —— 原实现 Load() 里还会再扫一遍，
            // 改成快照后一次后台扫描同时喂两处。
            await Task.Run(
                    () =>
                    {
                        var scan = _scanner.Scan();
                        _canJudgeStaleness = !scan.HasFailures;
                        _knownKeys = new HashSet<string>(
                            scan.Entries.Select(static entry => entry.Id),
                            StringComparer.Ordinal);

                        // 图标提取在同一个后台任务里顺带完成：单张 5~15ms，
                        // 放 UI 线程会把刷新冻住；单独开任务又多一次线程切换。
                        var pixels = new Dictionary<DelayedItem, IconPixels?>();
                        foreach (var item in _configStore.Load().Items)
                        {
                            var source = IconSourceOf(item);
                            pixels[item] = source is null ? null : _icons.TryGetIcon(source);
                        }

                        _pendingPixels = pixels;
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            Load();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取延时启动配置失败");
            ErrorText = $"读取配置失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>最近一次后台扫描中仍然存在的条目标识（E3 失效判定的依据）。</summary>
    private HashSet<string> _knownKeys = new(StringComparer.Ordinal);

    /// <summary>当前 <see cref="_knownKeys"/> 是否完整到可以下"失效"结论。</summary>
    private bool _canJudgeStaleness;

    /// <summary>同步读取配置并刷新列表。页面首次进入时调用。</summary>
    public void Load()
    {
        AppConfig config;
        try
        {
            config = _configStore.Load();
        }
        catch (StartupOperationException ex)
        {
            // 配置版本高于本程序时的唯一正确行为是**拒绝加载**而不是尽力解析（会把用户配置写坏）。
            // 但界面必须把这件事说出来，否则用户看到的是"我的配置全没了"。
            _log.Error(ex, "配置无法加载");
            Rows.Clear();
            Subtitle = "配置不可用";
            ErrorText = ex.Message;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        // 预设值与上限随配置一起刷新：用户在设置页改过之后，编辑器立刻用新值。
        ApplySettings(config.Settings);

        // 失效判定需要"系统里还有没有这一项"（来自 RefreshAsync 的后台扫描快照）。
        // 扫描整体失败时无法判断，此时**一律不标失效** —— 误标会让用户把好条目删掉，
        // 代价远大于漏标。页面尚未刷新过时同样不判（快照为空且不可判定）。
        var canJudgeStaleness = _canJudgeStaleness;

        Rows.Clear();
        var order = 0;
        foreach (var item in config.Items.OrderBy(static item => item, StartupSortComparer.Instance))
        {
            var stale = canJudgeStaleness && !item.IsManual && !_knownKeys.Contains(item.Id);
            var pixels = _pendingPixels.GetValueOrDefault(item) ?? _icons.TryGetIcon(IconSourceOf(item) ?? string.Empty);
            Rows.Add(new DelayRow(item, stale, pixels) { Order = ++order });
        }

        Subtitle = BuildSubtitle(config.Items);
        ErrorText = canJudgeStaleness
            ? null
            : "系统扫描未完全成功，暂时无法判断哪些条目已失效。";
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>移除一个条目：恢复系统项 + 删除配置。</summary>
    /// <param name="row">要移除的行。</param>
    /// <returns>移除结果；调用方据此给出成功 / 失败反馈。</returns>
    /// <remarks>
    /// 交给 <see cref="TakeoverService.Release"/> 而不是自己删配置：释放动作是有顺序的
    /// （先恢复系统项、成功后才删配置），顺序错了会永久丢失还原依据。
    /// </remarks>
    public TakeoverOutcome Release(DelayRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var outcome = _takeover.Release(row.Item);
        if (outcome.Succeeded)
        {
            // D41：系统侧的接管状态也变回去了，而「自启动项」各页读的是扫描缓存 ——
            // 不标过期的话，移出 UWP 后再进「自启动项 · UWP」页仍显示"已接管"。
            // 手动条目没有系统对应物，标记同样无害（重扫结果里本来就没有它）。
            _scanCache.Invalidate(row.Item.Source);
            Load();
        }

        return outcome;
    }

    /// <summary>保存一次编辑（改延时 / 身份 / 参数，手动条目还可改名称与路径）。</summary>
    /// <param name="row">被编辑的行。</param>
    /// <param name="values">编辑器收集到的值。</param>
    /// <remarks>
    /// 失败时抛出 <see cref="StartupOperationException"/>，由页面弹窗告知用户 ——
    /// 编辑失败只有一种后果（配置没改），重试即可，不需要复杂的结果类型。
    /// </remarks>
    public void ApplyEdit(DelayRow row, DelayItemValues values)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(values);

        _editor.ApplyEdit(row.Item.Id, values);
        Load();
    }

    /// <summary>切换条目级开关（FR-4.6）。关闭后本次登录不启动，系统侧状态不变。</summary>
    /// <param name="row">目标行。</param>
    /// <param name="enabled">是否启用。</param>
    public void SetEnabled(DelayRow row, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(row);

        _editor.SetEnabled(row.Item.Id, enabled);
        Load();
    }

    /// <summary>在同一延时的组内上移 / 下移一位（FR-4.7）。</summary>
    /// <param name="row">目标行。</param>
    /// <param name="delta">位移量：<c>-1</c> 上移、<c>+1</c> 下移。</param>
    /// <returns>顺序是否真的变了（已在组内端点时为 <see langword="false"/>）。</returns>
    public bool Move(DelayRow row, int delta)
    {
        ArgumentNullException.ThrowIfNull(row);

        var moved = _editor.Move(row.Item.Id, delta);
        if (moved)
        {
            Load();
        }

        return moved;
    }

    /// <summary>手动添加一个条目（FR-3.4：不动系统任何设置）。</summary>
    /// <param name="values">编辑器收集到的值。</param>
    /// <remarks>
    /// 与「接管」的区别必须让用户看得见：接管会把系统自启动项软禁用，
    /// 手动添加只往 <c>config.json</c> 里加一条记录。第 ④ 块文案已经在编辑器里说清了这件事。
    /// </remarks>
    public void AddManual(DelayItemValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        _editor.AddManual(values);
        Load();
    }

    /// <summary>条目的图标解析名（D30）。</summary>
    /// <param name="item">配置里的延时条目。</param>
    /// <returns>
    /// UWP 条目用 <c>shell:AppsFolder\&lt;AUMID&gt;</c>，其余用路径本身；
    /// 手动条目恒为路径。没有可解析的名字时为 <see langword="null"/>。
    /// </returns>
    private static string? IconSourceOf(DelayedItem item)
    {
        if (item.Source == StartupSource.Uwp)
        {
            // 🔴 用 Path（= 完整 AUMID `<PFN>!<TaskId>`），不能用 SourceKey ——
            // SourceKey 只有 TaskId，拼出来的解析名缺包族名，图标必然解析失败（D41 修复）。
            var parsingName = UwpParsingName.Build(item.Path);
            return parsingName.Length == 0 ? null : parsingName;
        }

        return string.IsNullOrWhiteSpace(item.Path) ? null : item.Path;
    }

    /// <summary>同步延时预设值与默认预设。</summary>
    /// <param name="settings">配置里的设置段。</param>
    private void ApplySettings(Settings settings)
    {
        DelayPresets = settings.DelayPresets;
        DefaultPreset = settings.DefaultPreset;
    }

    /// <summary>拼页头副标题。</summary>
    /// <param name="items">配置里的全部条目。</param>
    /// <returns>形如 `共 5 项（含手动添加 1 项）· 最后一个在登录后 1 分 00 秒启动`。</returns>
    /// <remarks>
    /// 形参用具体 <see cref="List{T}"/> 而不是 <c>IReadOnlyCollection</c>：调用点传的永远是
    /// <c>AppConfig.Items</c>（就是 <see cref="List{T}"/>），接口分发在这里是纯开销（CA1859）。
    /// </remarks>
    private static string BuildSubtitle(List<DelayedItem> items)
    {
        if (items.Count == 0)
        {
            return "还没有延时启动项";
        }

        var manual = items.Count(static item => item.IsManual);
        var head = manual > 0 ? $"共 {items.Count} 项（含手动添加 {manual} 项）" : $"共 {items.Count} 项";

        // 「最后一个」按**延时值**取最大，不是按列表末行 —— 列表按延时排序，
        // 末行确实是最大延时，但显式取 Max 才不依赖排序实现的稳定性。
        var last = items.Max(static item => item.DelaySeconds);
        return $"{head} · 最后一个在{DisplayText.DelayOf(last)}启动";
    }
}
