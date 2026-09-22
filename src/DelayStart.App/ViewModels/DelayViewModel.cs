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
/// <para>
/// 2026-09-22（D81）：**失效条目并入本页** —— 不再有独立的「失效条目」页面。
/// 失效行由 <see cref="DelayRow"/> 的 <c>IsStale</c> 系列标志表达，
/// 清理动作（删除 / 转为手动）就在行内。
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
    /// <param name="editor">条目级编辑服务（改延时 / 切开关 / 调顺序 / 手动添加 / 删除 / 转手动）。</param>
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

        DelayPresets = new Settings().DelayPresets;
        DefaultPreset = new Settings().DefaultPreset;
    }

    /// <summary>列表内容，按「延时 → 顺序」排序（与调度端的发起顺序一致）。</summary>
    public ObservableCollection<DelayRow> Rows { get; } = [];

    /// <summary>
    /// 同一批内容的**分组视图**：按延时值分组，组标题就是延时本身（2026-09-20 用户批复）。
    /// </summary>
    /// <remarks>
    /// <see cref="Rows"/> 仍是事实来源（顺序 / 计数 / 判空都用它），<see cref="Groups"/> 只是
    /// 把它按延时切片。两份都灌：切片是纯内存操作，比让 XAML 侧做分组模板省事得多。
    /// </remarks>
    public ObservableCollection<DelayGroup> Groups { get; } = [];

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
                        _scanHadFailures = scan.HasFailures;

                        var config = _configStore.Load();

                        var failures = scan.Failures
                            .Select(static failure => new ScanScope(failure.Source, failure.Scope))
                            .ToArray();

                        // 失效判定交给 Core 的策略而不是"扫描结果里找不到就算失效"：
                        // 后者只能发现孤儿，发现不了"启动项还在、程序文件没了"（FR-1.10）。
                        _staleKinds = GuardStalePolicy
                            .SelectStaleItems(config.Items, scan.Entries, failures)
                            .ToDictionary(
                                static stale => stale.Item.Id,
                                static stale => stale.Kind,
                                StringComparer.Ordinal);

                        // 图标提取在同一个后台任务里顺带完成：单张 5~15ms，
                        // 放 UI 线程会把刷新冻住；单独开任务又多一次线程切换。
                        var pixels = new Dictionary<DelayedItem, IconPixels?>();
                        foreach (var item in config.Items)
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

    /// <summary>最近一次后台扫描得出的失效条目（主键 → 失效类型）。</summary>
    private Dictionary<string, StaleKind> _staleKinds = new(StringComparer.Ordinal);

    /// <summary>最近一次后台扫描是否有来源整体失败（只影响那行提示文案）。</summary>
    private bool _scanHadFailures;

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
            Groups.Clear();
            ErrorText = ex.Message;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        // 预设值与上限随配置一起刷新：用户在设置页改过之后，编辑器立刻用新值。
        ApplySettings(config.Settings);

        // 失效判定需要"系统里还有没有这一项"（来自 RefreshAsync 的后台扫描快照）。
        // 扫描整体失败时快照为空，此时**一律不标失效** —— 误标会让用户把好条目删掉，
        // 代价远大于漏标。页面尚未刷新过时同样不判（快照为空）。
        var staleKinds = _staleKinds;

        Rows.Clear();
        var order = 0;
        foreach (var item in config.Items.OrderBy(static item => item, StartupSortComparer.Instance))
        {
            var staleKind = staleKinds.TryGetValue(item.Id, out var kind) ? kind : (StaleKind?)null;

            // 「转为手动」的前提是目标程序还在（D81）。只在失效行上做这个判断 ——
            // 正常行不显示这个按钮，为它们各做一次文件探测纯属浪费。
            var canConvert = staleKind is not null && File.Exists(item.Path);

            var pixels = _pendingPixels.GetValueOrDefault(item) ?? _icons.TryGetIcon(IconSourceOf(item) ?? string.Empty);
            Rows.Add(new DelayRow(item, staleKind, canConvert, pixels) { Order = ++order });
        }

        RebuildGroups();

        ErrorText = _scanHadFailures
            ? "系统扫描未完全成功，部分条目的失效状态暂时无法判断。"
            : null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// 按延时值把已排好序的 <see cref="Rows"/> 切成组（2026-09-20 用户批复）。
    /// </summary>
    /// <remarks>
    /// 顺序扫一遍切片而不是 <c>GroupBy</c>：列表本来就已经按「延时 → 顺序」排好，
    /// 同一延时的行必然相邻 —— 再 <c>GroupBy</c> 反而会丢掉"组按延时升序"这个既有保证，
    /// 还得重新排一次。
    /// </remarks>
    private void RebuildGroups()
    {
        Groups.Clear();

        var start = 0;
        while (start < Rows.Count)
        {
            var delay = Rows[start].Item.DelaySeconds;

            var end = start;
            while (end < Rows.Count && Rows[end].Item.DelaySeconds == delay)
            {
                end++;
            }

            var rows = new List<DelayRow>(end - start);
            for (var index = start; index < end; index++)
            {
                rows.Add(Rows[index]);
            }

            Groups.Add(new DelayGroup(delay, rows));
            start = end;
        }
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

    /// <summary>
    /// 删除一个**失效**条目：只从配置里移除，绝不动系统（D81）。
    /// </summary>
    /// <param name="row">要删除的行。</param>
    /// <exception cref="StartupOperationException">配置写盘失败，或该条目已不在配置里。</exception>
    /// <remarks>
    /// <para>
    /// 与「移出延时」不是一回事，两者不能互相替代：
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>移出延时</b>（<see cref="Release"/>）会**恢复系统的原始自启动项** ——
    /// 它假设那个系统项还存在、还归我们管；</description></item>
    /// <item><description><b>删除</b>只删配置：孤儿条目的系统项早已被删除，没有还原对象；
    /// 目标程序已不存在的条目保持"最后一次纠正后的禁用态"最干净 ——
    /// 把路径残缺的项重新启用既没意义，又制造一条开机报错。</description></item>
    /// </list>
    /// <para>
    /// 删除后调度端不再调度它，运行日志里那条"每次登录都失败"的记录也随之消失。
    /// </para>
    /// </remarks>
    public void Remove(DelayRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!_editor.Remove(row.Item.Id))
        {
            throw new StartupOperationException(
                StartupFailureReason.Unknown,
                row.Item.Id,
                $"配置里已经没有『{row.Item.Name}』，列表可能已过期，请刷新后重试。");
        }

        Load();
    }

    /// <summary>
    /// 把一个失效条目转成**手动条目**：留着它，但不再去系统里找它（D81）。
    /// </summary>
    /// <param name="row">要转换的行。</param>
    /// <exception cref="StartupOperationException">目标程序已不存在，或配置写盘失败。</exception>
    /// <remarks>
    /// 用于"程序还是我想要的，只是它当初的自启动项已经没了"这一种场景：
    /// 转换后条目与系统完全脱钩，但调度端照样按当前的延时 / 参数 / 身份启动它。
    /// 前提与判定都在 <see cref="DelayRow.CanConvertToManual"/> 与
    /// <see cref="ConfigEditService.ConvertToManual"/> 里（两侧都查一遍文件，
    /// 服务层不假设调用方守规矩）。
    /// </remarks>
    public void ConvertToManual(DelayRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        _ = _editor.ConvertToManual(row.Item.Id);
        Load();
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
    /// <remarks>
    /// 2026-09-21 批复：**局部更新** —— 落盘成功后只改这一行的 <see cref="DelayRow.IsEnabled"/>，
    /// 不再整表 <see cref="Load"/>。整表重建会让 ScrollViewer 的滚动位置与整页视觉状态归零，
    /// 用户看到的就是"拨一下开关整个页面都在刷"。回灌（绑定把 <c>IsOn</c> 推回来再触发一次）
    /// 由 <c>row.IsEnabled == enabled</c> 挡掉。
    /// </remarks>
    public void SetEnabled(DelayRow row, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.IsEnabled == enabled)
        {
            return;
        }

        _editor.SetEnabled(row.Item.Id, enabled);
        row.SetEnabledState(enabled);
    }

    /// <summary>在同一延时的组内上移 / 下移一位（FR-4.7）。</summary>
    /// <param name="row">目标行。</param>
    /// <param name="delta">位移量：<c>-1</c> 上移、<c>+1</c> 下移。</param>
    /// <returns>顺序是否真的变了（已在组内端点时为 <see langword="false"/>）。</returns>
    /// <remarks>
    /// 2026-09-21 批复：**局部更新** —— 落盘成功后在组内 <see cref="ObservableCollection{T}.Move"/>
    /// 交换相邻两行并互换顺序号，列表只重排这两个容器；不再整表 <see cref="Load"/>
    /// （滚动位置归零问题同 <see cref="SetEnabled"/>）。
    /// </remarks>
    public bool Move(DelayRow row, int delta)
    {
        ArgumentNullException.ThrowIfNull(row);

        var moved = _editor.Move(row.Item.Id, delta);
        if (!moved)
        {
            return false;
        }

        // 组是快照、行对象是同一实例 —— 在行所在的组内做最小变更。
        var group = Groups.FirstOrDefault(g => g.Rows.Contains(row));
        if (group is not null)
        {
            var rows = group.Rows;
            var index = rows.IndexOf(row);
            var target = index + delta;
            if (index >= 0 && target >= 0 && target < rows.Count)
            {
                rows.Move(index, target);

                // Move 之后邻居恰好落回 index（无论上移还是下移），互换全局顺序号。
                var neighbor = rows[index];
                (row.Order, neighbor.Order) = (neighbor.Order, row.Order);
            }
        }

        return true;
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
}
