using System.Collections.ObjectModel;
using System.Diagnostics;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
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
    private readonly PathService _paths;
    private readonly SchedulerProbe _scheduler;
    private readonly DeElevatedProcessLauncher _launcher;
    private readonly ToastService _toast;
    private readonly ILogSink _log;

    /// <summary>最近一次刷新用到的周期信息（今天 + 法定日历 + 周期表）。行对象在构造时读它。</summary>
    private CycleInfoProvider? _cycles;

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
    /// <param name="paths">路径服务（法定日历的落点）。</param>
    /// <param name="log">日志接收端。</param>
    /// <param name="scheduler">调度端手动启动 + 单实例探测（F11.3）。</param>
    /// <param name="launcher">降权启动链（F11.1：每行「启动」按钮）。</param>
    /// <param name="toast">应用内右下角通知（F11.1/F11.3 的结果出口）。</param>
    public DelayViewModel(
        IAppConfigStore configStore,
        ScanService scanner,
        TakeoverService takeover,
        ConfigEditService editor,
        IconProvider icons,
        ScanCacheService scanCache,
        PathService paths,
        SchedulerProbe scheduler,
        DeElevatedProcessLauncher launcher,
        ToastService toast,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(takeover);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(scanCache);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(toast);
        ArgumentNullException.ThrowIfNull(log);

        _configStore = configStore;
        _scanner = scanner;
        _takeover = takeover;
        _editor = editor;
        _icons = icons;
        _scanCache = scanCache;
        _paths = paths;
        _scheduler = scheduler;
        _launcher = launcher;
        _toast = toast;
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

    private string _cycleSummary = string.Empty;

    /// <summary>
    /// 「本次登录预览」一句话（FR-15.23）：今天有多少项会进计划、多少项被周期跳过。
    /// </summary>
    /// <remarks>
    /// 列表行内的标记回答"这一行怎么了"，这一行回答"整体今天会发生什么" ——
    /// 两者分工明确，所以编辑器弹窗里不再重复摆"今天会不会启动"。
    /// </remarks>
    public string CycleSummary
    {
        get => _cycleSummary;
        private set
        {
            if (SetProperty(ref _cycleSummary, value))
            {
                OnPropertyChanged(nameof(HasCycleSummary));
            }
        }
    }

    /// <summary>是否存在「本次登录预览」文案（XAML 的 <c>Visibility</c> 只吃布尔值）。</summary>
    public bool HasCycleSummary => _cycleSummary.Length > 0;

    private string _holidayNotice = string.Empty;

    /// <summary>
    /// 「次年节假日数据还没到」的到期提醒（§6.5）。
    /// </summary>
    /// <remarks>
    /// 只在三条同时成立时出现：① 已经过了 11 月 1 日（国务院差不多每年这个时间公布次年安排）；
    /// ② 次年数据缺失；③ **确实有条目在用法定两档** —— 没有条目用的时候这条提示纯属噪音，
    /// 因为没有任何判定会受影响。
    /// </remarks>
    public string HolidayNoticeText
    {
        get => _holidayNotice;
        private set
        {
            if (SetProperty(ref _holidayNotice, value))
            {
                OnPropertyChanged(nameof(HasHolidayNotice));
            }
        }
    }

    /// <summary>是否显示到期提醒。</summary>
    public bool HasHolidayNotice => _holidayNotice.Length > 0;

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

                        // 🔴 配置不可用时不做任何"判定"：失效标记与图标都建立在"这是配置里的
                        // 第 N 条"之上，缺了配置算出来的结果一律是假的。跳过即可 ——
                        // 紧随其后的 Load() 会把错误原文摆到界面上。
                        AppConfig config;
                        try
                        {
                            config = _configStore.Load();
                        }
                        catch (StartupOperationException)
                        {
                            _staleKinds = [];
                            _pendingPixels = [];
                            return;
                        }

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

        // 周期信息（FR-15）：一次刷新一份快照，全部行共用 ——
        // 今天与法定日历在本次刷新里必须是同一个值，否则同一屏里上下两行可能不属于"同一天"。
        _cycles = CreateCycleProvider(config);

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
            Rows.Add(new DelayRow(item, staleKind, canConvert, pixels, _cycles) { Order = ++order });
        }

        RebuildGroups();
        CycleSummary = BuildCycleSummary();
        HolidayNoticeText = BuildHolidayNotice(config);

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
    /// 删除后调度端不再调度它，调度日志里那条"每次登录都失败"的记录也随之消失。
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

    /// <summary>
    /// 造本次刷新的周期信息提供者（今天 + 本地法定日历 + 周期表）。
    /// </summary>
    /// <remarks>
    /// 🔴 法定日历**只读本地**（NFR-x）：管理端也不在页面加载时联网，
    /// 下载是设置页里由用户点「立即更新」才做的事。读取失败时日历为 <see langword="null"/>，
    /// 法定两档退化成星期判定，并且徽标会带 <c>≈</c> —— 用户看得见这件事。
    /// </remarks>
    private CycleInfoProvider CreateCycleProvider(AppConfig config)
    {
        HolidayCalendar? calendar = null;
        try
        {
            var result = HolidayCalendarStore.Load(_paths);
            foreach (var issue in result.Issues)
            {
                _log.Warn($"法定日历文件不可用：{issue.FilePath} —— {issue.Reason}");
            }

            calendar = result.Calendar;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取法定日历失败，法定两档按星期规律近似判定");
        }

        return new CycleInfoProvider(config.Cycles, calendar, DateOnly.FromDateTime(DateTime.Now));
    }

    /// <summary>
    /// 拼「次年数据到期提醒」（§6.5）：11 月 1 日起、次年无数据、且确实有启用的条目在用法定两档。
    /// </summary>
    /// <param name="config">当前配置。</param>
    /// <returns>提示文案；不需要提示时为空串。</returns>
    /// <remarks>
    /// 与设置页的年份行是同一件事的两个落点：那边是"你去更新"，这边是"你今天的判定正在近似"。
    /// 都不弹窗 —— 它不影响任何主功能，用横幅提醒足够了。
    /// </remarks>
    private string BuildHolidayNotice(AppConfig config)
    {
        if (_cycles is null)
        {
            return string.Empty;
        }

        var today = _cycles.Today;
        var nextYear = today.Year + 1;

        if (today < new DateOnly(today.Year, 11, 1) || _cycles.HasCalendarFor(nextYear))
        {
            return string.Empty;
        }

        // 没有条目在用法定两档时不提示：那种情况下"缺数据"不影响任何判定。
        var usesLegal = config.Items.Exists(
            static item => item.Enabled && CycleInfoProvider.IsDynamic(item.ScheduleCycleId));

        if (!usesLegal)
        {
            return string.Empty;
        }

        return $"{nextYear} 年法定放假安排尚未下载（每年约 11 月公布）。在它到位之前，"
            + "「法定工作日」「法定节假日」按星期规律近似判定，调休补班日会判错 —— "
            + "可在「设置 · 节假日数据」里更新。";
    }

    /// <summary>拼「本次登录预览」：进计划几项、跳过几项（跳过时最多列三个名字）。</summary>
    private string BuildCycleSummary()
    {
        if (_cycles is null || Rows.Count == 0)
        {
            return string.Empty;
        }

        var today = _cycles.Today;
        var skipped = Rows.Where(static row => row.HasNotRunToday).Select(static row => row.Name).ToArray();
        var run = Rows.Count - skipped.Length;

        var text = $"本次登录预览 · {today:yyyy-MM-dd}（{CycleInfoProvider.NameOfDay(today.DayOfWeek)}）："
            + $"进计划 {run} 项，跳过 {skipped.Length} 项";

        if (skipped.Length > 0)
        {
            var names = string.Join("、", skipped.Take(3));
            text += skipped.Length > 3 ? $" — {names} 等" : $" — {names}";
        }

        if (run == 0)
        {
            text += "。全部被跳过 → 本次不会有启动动作。";
        }

        return text;
    }

    /// <summary>同步延时预设值与默认预设。</summary>
    /// <param name="settings">配置里的设置段。</param>
    private void ApplySettings(Settings settings)
    {
        DelayPresets = settings.DelayPresets;
        DefaultPreset = settings.DefaultPreset;
    }

    // ══════════════════════════════════════════════════════════════════
    // F11.1 / F11.3：单条启动 + 运行调度
    // ══════════════════════════════════════════════════════════════════

    /// <summary>调度端抢到单实例互斥体的最长等待秒数（F11.3）。</summary>
    private const int SchedulerTakeoverTimeoutSeconds = 30;

    /// <summary>轮询间隔。</summary>
    private static readonly TimeSpan SchedulerPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>「运行调度」是否正在进行中（单实例预检 → 启动 → 等待接管）。</summary>
    [ObservableProperty]
    public partial bool IsSchedulerBusy { get; set; }

    /// <summary>调度端当前是否已在运行（按钮的禁用依据，构造时探一次）。</summary>
    [ObservableProperty]
    public partial bool IsSchedulerRunning { get; set; } = SchedulerProbe.IsSchedulerRunning();

    /// <summary>「运行调度」按钮此刻该不该可点（审计 P2-1）。</summary>
    /// <remarks>
    /// 🔴 判据是"调度端已在跑"而不是"空闲"：第二个实例会**秒退**，
    /// 点下去用户只会看到"没反应" —— 而那正是最容易被反复点击的时刻。
    /// <para>
    /// 之所以之前没生效：属性写了、注释也写了，但 XAML 上没有 <c>IsEnabled</c> 绑定，
    /// 于是那段注释描述的是一个不存在的行为。
    /// </para>
    /// </remarks>
    public bool IsRunSchedulerAvailable => !IsSchedulerRunning && !IsSchedulerBusy;

    /// <summary><see cref="IsSchedulerRunning"/> 变化时连带刷新按钮可用态。</summary>
    /// <param name="value">新值。</param>
    partial void OnIsSchedulerRunningChanged(bool value)
        => OnPropertyChanged(nameof(IsRunSchedulerAvailable));

    /// <summary><see cref="IsSchedulerBusy"/> 变化时连带刷新按钮可用态。</summary>
    /// <param name="value">新值。</param>
    partial void OnIsSchedulerBusyChanged(bool value)
  => OnPropertyChanged(nameof(IsRunSchedulerAvailable));

    /// <summary>
    /// 「运行调度」：确认 → 单实例预检 → 启动调度端 → 轮询等它接管（F11.3）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>确认框里必须有摘要</b>：这个按钮会真的启动程序，而"启动了哪些"决定用户要不要
    /// 先把某些程序关掉（带锁文件的应用最典型）。只写"确定要运行吗"等于让他在不知情的情况下
    /// 替自己做决定 —— 那违反三原则里的"不替用户做决定"。
    /// <para>
    /// 🔴 <b>轮询而不是假设成功</b>：<c>Process.Start</c> 返回只说明进程创建成功，不代表它
    /// 正确接管了。实测里"点了没反应、托盘图标没出现"是真实故障，而用户看到的就是"点了没反应"。
    /// 轮询单实例互斥体是唯一不依赖状态文件的判据 —— F6 删掉了 current-run.json 之后就
    /// 更没有状态文件可看了，而互斥体本来就覆盖了"正在运行"这个问题的全部含义。
    /// </para>
    /// <para>
    /// 🔴 <b>30 秒上限</b>：不等就是"假装成功"；不设上限就是"按钮永远转圈"。
    /// 两者的失败症状都比一个明确的超时提示难查。
    /// </para>
    /// </remarks>
    /// <summary>
    /// 单条启动：按条目配置走**同一条**降权链启动它，结果进应用内 toast（F11.1）。
    /// </summary>
    /// <param name="row">要启动的那一行。</param>
    /// <remarks>
    /// 🔴 <b>失效条目也能点</b>：条目"失效"说的是系统锚点没了或目标程序不在，
    /// 而"程序还在、只是没有接管关系"的情况（源丢失）恰恰是用户最想手动试一下的时候 ——
    /// 把它一并禁用掉，用户就只能回日志里去猜它还能不能跑。
    /// <para>
    /// 🔴 <b>失败必带原因</b>：启动链里有五六个可能失败的点（UWP / 快捷方式 / uiAccess /
    /// 令牌复制 / CreateProcess），每一条都有自己的处置办法。只报"启动失败"等于让用户猜。
    /// </para>
    /// <para>
    /// 🔴 <b>必须 async</b>（审计 P2-2）：降权链里有两段同线程长等待 ——
    /// <c>WaitForShellWindow</c> 最多 10 秒、<c>PollBrokerResult</c> 最多 20 秒。
    /// 调度端能忍（后台进程，且它的 1.5 秒复查窗口本来就是同步的）；
    /// WinUI 里就是**窗口冻结最长 30 秒且无任何反馈** —— 用户只会以为程序卡死了，
    /// 于是反复点「启动」，反而起更多进程。
    /// </para>
    /// <para>
    /// 🔴 <b>不判"程序是否真的起来了"</b>：判定要等 1.5 秒复查窗口，而那属于调度端的职责。
    /// 这里只报"已发起"，不假装成功。
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task LaunchOneAsync(DelayRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

     // 在途标记：飞行中把该行的「启动」按钮禁掉。不做的话用户会以为没点上而连点，
        // 而每次点击都会真的再走一遍降权链 —— 那是"重复启动多个实例"最直接的来源。
        row.IsLaunching = true;

        try
        {
            var outcome = await Task.Run(() => _launcher.Launch(row.Item)).ConfigureAwait(true);

            if (outcome.Created)
            {
                _toast.ShowSuccess(
  $"已启动：{row.Name}",
         outcome.DeElevationFellBack
   ? "降权失败，已按调度端同样的方式交给系统处理。"
             : null);

   _log.Info($"用户在延时启动页手动启动了『{row.Name}』。");
                return;
            }

   var reason = outcome.FailureMessage ?? "未知原因";
            _log.Warn($"手动启动『{row.Name}』失败：{reason}");
 _toast.ShowError($"启动失败：{row.Name}", reason);
        }
        catch (Exception ex)
        {
            // 🔴 绝不让异常逃到 UI：管理端会静默消失，而用户只看到"点了没反应"。
       _log.Error(ex, $"手动启动『{row.Name}』时异常");
     _toast.ShowError($"启动失败：{row.Name}", ex.Message);
        }
        finally
     {
            row.IsLaunching = false;
        }
    }

    /// <summary>
    /// 「运行调度」：确认 → 单实例预检 → 启动调度端 → 轮询等它接管（F11.3）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>确认框里必须有摘要</b>：这个按钮会真的启动程序，而"启动了哪些"决定用户要不要
    /// 先把某些程序关掉（带锁文件的应用最典型）。只写"确定要运行吗"等于让他在不知情的情况下
    /// 替自己做决定 —— 那违反三原则里的"不替用户做决定"。
    /// <para>
    /// 🔴 <b>轮询而不是假设成功</b>：<c>Process.Start</c> 返回只说明进程创建成功，不代表它
    /// 正确接管了。实测里"点了没反应、托盘图标没出现"是真实故障，而用户看到的就是"点了没反应"。
    /// 轮询单实例互斥体是唯一不依赖状态文件的判据 —— F6 删掉了 current-run.json 之后就
    /// 更没有状态文件可看了，而互斥体本来就覆盖了"正在运行"这个问题的全部含义。
    /// </para>
    /// <para>
    /// 🔴 <b>30 秒上限</b>：不等就是"假装成功"；不设上限就是"按钮永远转圈"。
    /// 两者的失败症状都比一个明确的超时提示难查。
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task RunSchedulerAsync()
    {
        if (IsSchedulerBusy)
        {
            return;
        }

        // 单实例预检：已经在跑就别再起一个 —— 第二个会秒退，用户看到的是"点了没反应"。
        if (SchedulerProbe.IsSchedulerRunning())
        {
            _toast.ShowSuccess("调度已在运行中", "本轮已经在执行，无需重复启动。");
            IsSchedulerRunning = true;
            return;
        }

        var confirmed = await ConfirmLaunchAsync(_scheduler.DescribeLaunch()).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        IsSchedulerBusy = true;
        try
        {
            if (!_scheduler.Launch())
            {
                _log.Error("启动调度端失败：可执行文件不存在（安装不完整或文件被删）。");
                _toast.ShowError("运行调度失败", $"找不到调度端程序：{_paths.SchedulerExecutablePath}");
                return;
            }

            if (await WaitForSchedulerAsync().ConfigureAwait(true))
            {
                IsSchedulerRunning = true;
                _toast.ShowSuccess("调度已启动", "本次启动计划正在执行。");
                return;
            }

            IsSchedulerRunning = false;
            _log.Error($"启动调度端后 {SchedulerTakeoverTimeoutSeconds} 秒内未抢到单实例互斥体。");
            _toast.ShowError(
                "调度未接管",
                $"已拉起进程，但 {SchedulerTakeoverTimeoutSeconds} 秒内没有接管。"
                + "请在「查看调度日志」里看它为什么退出。");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "「运行调度」异常");
            _toast.ShowError("运行调度失败", ex.Message);
        }
        finally
        {
            IsSchedulerBusy = false;
        }
    }

    /// <summary>轮询单实例互斥体，等调度端真正接管。</summary>
    /// <returns>在超时前接管为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 逐段 <c>Task.Delay</c> 而不是一把 <c>Thread.Sleep</c>：等的那 30 秒里 UI 必须还能动，
    /// 用户随时可以切走或关掉窗口。
    /// </remarks>
    private static async Task<bool> WaitForSchedulerAsync()
    {
        var deadline = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(SchedulerTakeoverTimeoutSeconds);

        while (deadline.Elapsed < timeout)
        {
            if (SchedulerProbe.IsSchedulerRunning())
            {
                return true;
            }

            await Task.Delay(SchedulerPollInterval).ConfigureAwait(true);
        }

        return false;
    }

    /// <summary>
    /// 弹确认框的实现：入参是要显示的摘要，返回用户是否点了确定。
    /// 默认返回 <see langword="false"/>（没人接 = 不擅自替用户决定）。
    /// </summary>
    /// <remarks>
    /// ViewModel 不该直接构造 <c>ContentDialog</c>（要 XamlRoot，且无头测试里没有窗口），
    /// 所以由 <c>DelayPage</c> 在构造时把真实现赋进来。
    /// <para>
    /// 🔴 <b>默认值是"不确认"</b>而不是"直接执行"：万一某条路径漏了赋值，按钮表现为点了没反应
    /// （一眼能看出没接上）；反过来默认执行的代价是"没接上"时静默启动了程序。
    /// </para>
    /// </remarks>
    public Func<string, Task<bool>> ConfirmLaunchAsync { get; set; } =
        static _ => Task.FromResult(false);
}
