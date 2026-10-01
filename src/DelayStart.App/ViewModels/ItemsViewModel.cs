using System.ComponentModel;
using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DelayStart.App.Services;
using DelayStart.Core.Abstractions;
using DelayStart.Core.Models;
using DelayStart.Management.Abstractions;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 「自启动项」来源页的 ViewModel（UI v2：四个来源共用本类，由
/// <see cref="SourceFilter"/> 区分当前页）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **数据读自 <see cref="ScanCacheService"/>，不重复扫描**（bug#7）：管理端启动后
/// 全量扫过一次，进页面直接出列表；「刷新本页」只重扫当前来源。
/// </para>
/// <para>
/// 🔴 **行级局部刷新**（bug#6）：接管 / 移出 / 禁用 / 启用成功后只替换那一行
/// （<see cref="StartupEntry"/> 是不可变模型，用修改后的标志位重建一条替换之），
/// 不整表重扫 —— 滚动位置、搜索词、筛选条件全部原样保留。
/// </para>
/// </remarks>
public sealed partial class ItemsViewModel : ObservableObject
{
    private readonly ScanCacheService _cache;

    /// <summary>
    /// 当前已渲染到界面上的内容指纹（A2.3）。<see langword="null"/> = 还没渲染过任何东西。
    /// </summary>
    private string? _renderedFingerprint;
    private readonly TakeoverService _takeover;
    private readonly ConfigEditService _editor;
    private readonly IReadOnlyList<IStartupSource> _sources;
    private readonly IAppConfigStore _configStore;
    private readonly GuardService _guard;
    private readonly ILogSink _log;

    /// <summary>当前页面对应的来源；<see langword="null"/> 表示全部来源（保留给潜在的全量入口）。</summary>
    public StartupSource? SourceFilter { get; set; }

    /// <summary>页头副标题，形如 `注册表 · 共 12 项 · 已接管 2 项 · 已禁用 3 项`。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; }

    /// <summary>搜索词。匹配名称 / 命令行 / 位置三处（大小写不敏感，子串匹配）。</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>状态筛选的序号：0 全部状态 / 1 已启用 / 2 已禁用 / 3 已接管（docs/design.md 7.5）。</summary>
    [ObservableProperty]
    public partial int StatusFilterIndex { get; set; }

    /// <summary>排序方式的序号：0 按名称 / 1 按状态。</summary>
    /// <remarks>
    /// 来源页内不再有"按来源"排序（同一页只有一种来源），按名称是自然默认。
    /// </remarks>
    [ObservableProperty]
    public partial int SortIndex { get; set; }

    /// <summary>是否正在扫描（首次进页或手动刷新）。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>来源级失败提示；无失败时为 <see langword="null"/>（FR-1.4）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial string? FailureText { get; set; }

    /// <summary>是否存在来源级失败。XAML 的 <c>InfoBar.IsOpen</c> 只吃布尔值。</summary>
    public bool HasFailure => FailureText is not null;

    /// <summary>构造来源页 ViewModel。</summary>
    /// <param name="cache">扫描缓存（读列表 + 局部重扫）。</param>
    /// <param name="takeover">接管 / 释放服务。</param>
    /// <param name="editor">条目级编辑服务（改已接管项的延时 / 参数 / 工作目录）。</param>
    /// <param name="sources">全部来源实例（纯禁用 / 启用写 StartupApproved 用）。</param>
    /// <param name="configStore">配置读取端（预设值、上限、接管判定）。</param>
    /// <param name="guard">守卫巡检服务（v0.6.1：加载本页数据时同步跑一次）。</param>
    /// <param name="log">日志接收端。</param>
    public ItemsViewModel(
        ScanCacheService cache,
        TakeoverService takeover,
        ConfigEditService editor,
        IReadOnlyList<IStartupSource> sources,
        IAppConfigStore configStore,
        GuardService guard,
        ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(takeover);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(log);

        _cache = cache;
        _takeover = takeover;
        _editor = editor;
        _sources = [.. sources];
        _configStore = configStore;
        _guard = guard;
        _log = log;

        Subtitle = "正在读取…";
        DelayPresets = new Settings().DelayPresets;
        DefaultPreset = new Settings().DefaultPreset;
    }

    /// <summary>全部条目（当前来源）。缓存快照的投影，进页即出。</summary>
    public ObservableCollection<StartupEntryRow> Rows { get; } = [];

    /// <summary>筛选与搜索后的可见条目，列表实际绑定的集合。</summary>
    public ObservableCollection<StartupEntryRow> FilteredRows { get; } = [];
    /// <summary>全部条目是否为空（真正"一个都没有"，而不是被筛光了）。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>可见列表是否为空。空状态提示的显隐吃这个值。</summary>
    public bool FilteredEmpty => FilteredRows.Count == 0;

    /// <summary>空状态文案：区分"系统里没有"与"被筛选光了"。</summary>
    public string EmptyText => IsEmpty
        ? "此位置没有自启动项\n点「刷新本页」重新检查"
        : "没有符合当前搜索 / 筛选条件的条目\n试着清空搜索词或选「全部状态」";

    /// <summary>延时预设值（秒），驱动编辑器的快选按钮（FR-4.2）。</summary>
    public int[] DelayPresets { get; private set; }

    /// <summary>默认预设（秒）：编辑器打开时预选的延时（2026-09-19 用户批复）。</summary>
    public int DefaultPreset { get; private set; }

    /// <summary>加载列表（读缓存，秒回；缓存为空时触发全量扫描一次）。</summary>
    /// <remarks>
    /// 🔴 <b>两段式</b>（A2.4）：先无条件把已有缓存渲染出来，再决定要不要后台重扫。
    /// <para>
    /// 原来只有一段：<c>LoadSnapshotAsync</c> 决定读缓存还是重扫，而重扫要走一遍
    /// 注册表 + 计划任务 + UWP 全量枚举 + 图标提取，往往要一两秒 —— 这一两秒里页面
    /// 是**空的**，用户看到"此位置没有自启动项"。而缓存其实就在那儿。
    /// </para>
    /// <para>
    /// 两段之后：第一段永远是微秒级的（纯内存），第二段的成果只在**指纹真的变了**时
    /// 才落到界面上（A2.3）。于是"有缓存"是瞬间出内容，"无缓存"是先显示加载中、
    /// 扫完再出内容 —— 两种情况都不会出现"明明有数据却显示空列表"。
    /// </para>
    /// </remarks>
    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            LoadSettings();

            // ── 第一段：先把缓存里已有的东西无条件渲染出来 ──────────────────
            if (_cache.Current is { } cached)
            {
                Apply(cached);
            }
            else
            {
                Rows.Clear();
                FilteredRows.Clear();
                OnPropertyChanged(nameof(IsEmpty));
            }

            // ── 第二段：后台重扫（必要时），指纹变了才应用 ──────────────────
            var snapshot = await LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
            ApplyIfChanged(snapshot);

            // ── 第三段：拿着这一份扫描结果跑一次守卫 ────────────────────
            if (RunGuardAlongsideLoad(snapshot))
            {
     // 守卫动过系统状态 ⇒ 缓存已标脏，必须重扫一次并重画，
                // 否则列表会一直停在"已启用"（审计 P2-3）。
    var after = await LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
      ApplyIfChanged(after);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取自启动项缓存失败");
            FailureText = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 指纹与当前已渲染内容相同 ⇒ 什么都不做（A2.3）。
    /// </summary>
    /// <remarks>
    /// 🔴 这一层是"两段式加载"能成立的前提。第一段刚渲染完缓存，第二段重扫回来的结果
    /// 若与之相同（**常见情况**：自启动项不会每秒都变），就一个通知都不发 ——
    /// 否则用户会看到列表无端闪一下、滚动位置跳回顶部。
    /// <para>
    /// 判据用**整个快照**的指纹而不是"条目数变了没"：只比数量会漏掉"某一项的状态翻了"
    /// 这种最该被看见的变化。
    /// </para>
    /// </remarks>
    private void ApplyIfChanged(ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (string.Equals(_renderedFingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        Apply(snapshot);
    }

    /// <summary>拿这一份扫描结果跑一次守卫巡检（v0.6.1 / F11.2）。</summary>
    /// <param name="snapshot">本轮刚拿到的快照。</param>
    /// <remarks>
    /// 🔴 <b>为什么是"加载自启动项数据时"而不是"管理端启动时"</b>：
    /// 用户打开这一页就是想确认"系统里到底有什么、哪些被我接管了"。守卫纠正的正是
    /// **同一批对象**（被接管项有没有被写回启用），而巡检要的就是刚扫出来的这份数据 ——
    /// 两者一起做只多一次策略判定，不多一次扫描，也不必再派生一个守卫进程。
    /// <para>
    /// 🔴 <b>不派生 <c>DelayStart.Guard.exe</c></b>：派生就意味着多一个进程、多一次提权握手，
    /// 而且它会**再扫一遍**，于是用户看到的列表与守卫看到的可能不是同一时刻的状态 ——
    /// 表现出来就是"刚纠正完，列表里还写着已启用"。
    /// </para>
    /// <para>
    /// 🔴 <b>同步跑，且吞掉所有异常</b>：<c>IsBusy</c> 此时为真，界面本就显示"正在读"，
    /// 多花几百毫秒是可接受的；而异常一旦逃出去就会变成"自启动项页打不开"。
    /// 巡检失败**不影响**这一页的数据显示 —— 列表是读操作，守卫是写操作，两件事必须解耦。
    /// </para>
    /// <para>
    /// 🔴 <b>不派生系统通知</b>：管理端就在用户眼前，弹通知既多余又会打断他。
    /// 结果只进日志；确实纠正了东西时补一行副标题，让他知道发生了什么。
    /// </para>
    /// </remarks>
    private bool RunGuardAlongsideLoad(ScanSnapshot snapshot)
    {
        try
        {
            // 把快照还原成 Management 层的形状好让守卫复用它；守卫不认识 App 层的 Pixels，
            // 它要的只是条目 + 失败 + 配置可用性。UWP 兜底解析后的名字一并带过去 ——
            // 那才是用户看到的名字，用它判定"哪一项被写回了"才对得上界面。
            var scan = new ScanResult
            {
                Entries = snapshot.Entries,
                Failures = snapshot.Failures,
                ConfigUnavailable = snapshot.ConfigUnavailable,
            };

            var report = _guard.RunOnce(scan, snapshot.CoveredScopes);

            if (report.AlreadyRunning)
            {
                _log.Info("加载自启动项数据时顺带巡检：已有一轮守卫在进行，本次跳过。");
                return false;
            }

            if (report.GuardDisabled)
            {
                _log.Info("加载自启动项数据时顺带巡检：守卫处于关闭档位，未执行巡检。");
                return false;
            }

            if (report.ConfigUnavailable)
            {
                // 🔴 入口接住整体失败：GuardService 自己不抛，但 ConfigUnavailable 必须有人看得见 ——
                // 静默跳过就变成"守卫今天什么都没做"而没人知道。
                _log.Warn("加载自启动项数据时顺带巡检：配置不可用，本轮未纠正任何项目。");
                return false;
            }

            if (report.Corrections.Count > 0)
  {
      var ok = report.Corrections.Count(static outcome => outcome.Succeeded);
      _log.Info(
         $"加载自启动项数据时顺带巡检：纠正 {ok}/{report.Corrections.Count} 项被写回启用的接管项。");

      // 🔴 **必须把缓存标脏**（审计 P2-3）：守卫纠正的是**系统状态**
      //（StartupApproved 软禁用标记），而这一页显示的"已接管 / 已启用"是从
      // **扫描快照**派生的。不标脏的话副标题说「已纠正 N 项」而行里还写着「已启用」——
         // 自相矛盾，而且要等到下一次真的重扫才会对（缓存是进程级的，可能整个会话都不重扫）。
      InvalidateAfterCorrections();

        // 只有真的动了系统状态才上副标题 —— 每次进页面都加一句"巡检正常"是噪声。
        Subtitle += $" · 已纠正 {ok} 项被写回启用的接管项";

        return true;
        }

     return false;
  }
        catch (Exception ex)
  {
    _log.Error(ex, "加载自启动项数据时顺带巡检失败（已忽略，不影响本页显示）");
   return false;
        }
    }

    /// <summary>守卫纠正系统状态之后，把受影响的来源在扫描缓存里标脏（审计 P2-3）。</summary>
    /// <remarks>
    /// 🔴 <b>为什么标当前页的来源就够</b>：纠正只可能落在"被接管的项"上，而被接管的项
  /// 要么在当前来源里、要么不在这页的显示范围内 —— 不在这页的那些，用户看不到，
    /// 也就不需要为它们付重扫代价。
 /// <para>
    /// 本页无来源筛选（全量视图）时逐个标脏全部来源：那种情况下所有行都在屏幕上。
    /// </para>
    /// </remarks>
    private void InvalidateAfterCorrections()
    {
        if (SourceFilter is { } shown)
        {
   _cache.Invalidate(shown);
            return;
        }

        foreach (var kind in _sources.Select(static source => source.Kind).Distinct())
        {
            _cache.Invalidate(kind);
        }
    }

    /// <summary>
    /// 取列表快照：当前来源被标记为过期时重扫该来源，否则读缓存秒回。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>扫描快照。</returns>
    /// <remarks>
    /// D41：接管 / 移出只改了当前页的行对象，缓存快照仍是旧值；页面每次导航都新建实例，
    /// 不认过期标记就会出现「在延时启动页移出 UWP → 进自启动项·UWP 页还是显示已接管」。
    /// </remarks>
    private Task<ScanSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_cache.IsStale(SourceFilter))
        {
            return _cache.EnsureLoadedAsync(cancellationToken);
        }

        return SourceFilter is { } kind
            ? _cache.RefreshSourceAsync(kind, cancellationToken)
            : _cache.RefreshAsync(cancellationToken);
    }

    /// <summary>「刷新本页」：只重扫当前来源（bug#7），其余来源沿用缓存。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            LoadSettings();
            var snapshot = SourceFilter is { } kind
                ? await _cache.RefreshSourceAsync(kind, cancellationToken).ConfigureAwait(true)
                : await _cache.RefreshAsync(cancellationToken).ConfigureAwait(true);
            ApplyIfChanged(snapshot);

            // 🔴 「刷新本页」只重扫了一个来源，所以守卫也只能处理刷到的那些（F11.3）——
            // 快照的 CoveredScopes 就是这句话的数据形式。
            
if (RunGuardAlongsideLoad(snapshot))
            {
                // 同上：纠正动了系统状态 ⇒ 重扫一次（审计 P2-3）。
                var after = SourceFilter is { } kind2
                    ? await _cache.RefreshSourceAsync(kind2, cancellationToken).ConfigureAwait(true)
                    : await _cache.RefreshAsync(cancellationToken).ConfigureAwait(true);
                ApplyIfChanged(after);
            }
        }
        catch (OperationCanceledException)
        {
            // 连续点刷新时上一条命令被取消，属正常路径。
        }
        catch (Exception ex)
        {
            _log.Error(ex, "局部重扫失败");
            FailureText = $"刷新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>接管一个系统自启动项（加入延时启动）。</summary>
    /// <param name="entry">要接管的条目。</param>
    /// <param name="options">用户在编辑器里做的选择。</param>
    /// <param name="row">列表中对应的行；接管成功后就地替换（bug#6 局部刷新）。</param>
    /// <returns>接管结果。</returns>
    public TakeoverOutcome Takeover(StartupEntry entry, TakeoverOptions options, StartupEntryRow row)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(row);

        var outcome = _takeover.Takeover(entry, options);
        if (outcome.Succeeded)
        {
            ReplaceRow(row, WithState(entry, isTakenOver: true, isEnabled: false));
            RefreshSubtitleFromCache();
        }

        return outcome;
    }

    /// <summary>移出延时启动（bug#4：行上直达入口），系统项恢复接管前状态。</summary>
    /// <param name="row">要移出的行。</param>
    /// <returns>操作结果；失败时 <see cref="TakeoverOutcome.Message"/> 可直接呈现。</returns>
    public TakeoverOutcome Release(StartupEntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var item = FindConfigItem(row.Entry.Id);
        if (item is null)
        {
            return TakeoverOutcome.Failure(row.Entry.Id, StartupFailureReason.Unknown, "配置中找不到该条目，请先刷新列表。");
        }

        var outcome = _takeover.Release(item);
        if (outcome.Succeeded)
        {
            ReplaceRow(row, WithState(row.Entry, isTakenOver: false, isEnabled: item.OriginalState.WasEnabled));
            RefreshSubtitleFromCache();
        }

        return outcome;
    }

    /// <summary>
    /// 纯禁用 / 启用（D3：与接管共用 StartupApproved 机制，但不写配置 ——
    /// 接管 = 加延时 + 禁用，禁用 = 纯禁用）。
    /// </summary>
    /// <param name="row">目标行。</param>
    /// <param name="enabled"><see langword="true"/> 启用（删标记）；<see langword="false"/> 禁用（写标记）。</param>
    /// <returns>成功为 <see langword="true"/>；失败时已写日志。</returns>
    public bool SetEntryEnabled(StartupEntryRow row, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(row);

        var source = _sources.FirstOrDefault(candidate =>
            candidate.Kind == row.Entry.Source && candidate.Scope == row.Entry.Scope);
        if (source is null)
        {
            _log.Error($"找不到来源 ({row.Entry.Source}, {row.Entry.Scope})，无法{(enabled ? "启用" : "禁用")}『{row.Entry.Name}』");
            return false;
        }

        try
        {
            if (enabled)
            {
                source.Enable(row.Entry);
            }
            else
            {
                source.Disable(row.Entry);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"『{row.Entry.Name}』{(enabled ? "启用" : "禁用")}失败");
            return false;
        }

        ReplaceRow(row, WithState(row.Entry, isTakenOver: false, isEnabled: enabled));
        RefreshSubtitleFromCache();
        return true;
    }

    /// <summary>取该行对应的配置条目（打开编辑器用）。</summary>
    /// <param name="row">目标行。</param>
    /// <returns>配置条目。</returns>
    /// <exception cref="InvalidOperationException">配置里没有该条目（数据不同步，先刷新）。</exception>
    public DelayedItem GetItemFor(StartupEntryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return FindConfigItem(row.Entry.Id)
            ?? throw new InvalidOperationException($"配置中找不到『{row.Entry.Name}』，请先刷新本页。");
    }

    /// <summary>保存对已接管项的编辑（延时 / 参数 / 工作目录）。</summary>
    /// <param name="row">目标行。</param>
    /// <param name="values">编辑值。</param>
    /// <returns>成功为 <see langword="true"/>。</returns>
    public bool ApplyEdit(StartupEntryRow row, DelayItemValues values)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(values);

        var item = FindConfigItem(row.Entry.Id);
        if (item is null)
        {
            return false;
        }

        _editor.ApplyEdit(item.Id, values);
        return true;
    }

    /// <summary>按稳定主键在配置里找条目（接管 / 编辑共用）。</summary>
    private DelayedItem? FindConfigItem(string id)
    {
        try
        {
            return _configStore.Load().Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "读取配置失败");
            return null;
        }
    }

    /// <summary>用修改后的标志位重建条目（<see cref="StartupEntry"/> 不可变，只能换新实例）。</summary>
    /// <remarks>
    /// 🔴 换新实例时**必须把每个字段都搬过去** —— 漏一个，那个字段就会静默变成默认值。
    /// 这里曾经漏掉 <see cref="StartupEntry.ExecutablePath"/>，症状是任何一次行内局部刷新
    /// （接管 / 禁用 / 回滚）之后，图标提取的目标路径变空，于是图标消失。
    /// 那种故障极难定位：扫一次是对的，点一下按钮图标就没了，再扫又回来。
    /// </remarks>
    private static StartupEntry WithState(StartupEntry entry, bool isTakenOver, bool isEnabled) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        Path = entry.Path,
        Arguments = entry.Arguments,
        Source = entry.Source,
        Scope = entry.Scope,
        SourceKey = entry.SourceKey,
        SourceDetail = entry.SourceDetail,
        ExecutablePath = entry.ExecutablePath,
        IsEnabled = isEnabled,
        IsMissing = entry.IsMissing,
        IsProtected = entry.IsProtected,
        IsTakenOver = isTakenOver,
    };

    /// <summary>行级局部刷新：新旧行同图标，就地替换（bug#6）。</summary>
    private void ReplaceRow(StartupEntryRow oldRow, StartupEntry updated)
    {
        var newRow = new StartupEntryRow(updated, oldRow.Pixels);
        ReplaceIn(Rows, oldRow, newRow);
        ReplaceIn(FilteredRows, oldRow, newRow);
        OnPropertyChanged(nameof(IsEmpty));

        // D41：本页的就地替换只改了行对象，缓存快照还是旧的 —— 标过期，
        // 下次进入本来源（或总览读缓存）时重扫，否则离开再回来会看到移出前的状态。
        _cache.Invalidate(updated.Source);

        // 替换后统一重算筛选：新状态可能让该行在当前筛选条件下出现 / 消失
        // （例如筛选"已启用"时禁用了唯一一条）。
        ApplyFilters();
    }

    /// <summary>在集合中原位替换一行（保持位置与滚动状态）。</summary>
    private static void ReplaceIn(ObservableCollection<StartupEntryRow> collection, StartupEntryRow oldRow, StartupEntryRow newRow)
    {
        var index = collection.IndexOf(oldRow);
        if (index >= 0)
        {
            collection[index] = newRow;
        }
    }

    /// <summary>页头副标题按缓存现状重算（不重扫，只数缓存里的数据）。</summary>
    private void RefreshSubtitleFromCache()
    {
        var snapshot = _cache.Current;
        if (snapshot is null)
        {
            return;
        }

        Subtitle = BuildSubtitle(snapshot, SourceFilter);
    }

    /// <summary>读取延时预设值与默认预设；失败时保持上一次的值，不打扰用户。</summary>
    private void LoadSettings()
    {
        try
        {
            var settings = _configStore.Load().Settings;
            DelayPresets = settings.DelayPresets;
            DefaultPreset = settings.DefaultPreset;
        }
        catch (Exception ex)
        {
            _log.Warn(ex, "读取延时预设失败，沿用默认值");
        }
    }

    /// <summary>把快照灌进列表（只保留当前来源）。</summary>
    private void Apply(ScanSnapshot snapshot)
    {
        _renderedFingerprint = snapshot.Fingerprint;

        Rows.Clear();
        foreach (var entry in snapshot.Entries)
        {
            if (SourceFilter is not { } kind || entry.Source == kind)
            {
                Rows.Add(new StartupEntryRow(entry, snapshot.Pixels.GetValueOrDefault(entry)));
            }
        }

        ApplyFilters();
        Subtitle = BuildSubtitle(snapshot, SourceFilter);

        // 🔴 配置不可用 ⇒ 每一行的「是否已接管」都是错的（FR-12）。此时**不摆列表**：
        // 摆出来就是一屏可点的"未接管"，用户点「接管」就是双重接管（系统项其实早被接管了）。
        // 空列表 + 明确说明，好过一份会诱导危险操作的假数据。
        if (snapshot.ConfigUnavailable)
        {
            Rows.Clear();
            ApplyFilters();
            Subtitle = "配置不可用";
            FailureText = "配置不可用，无法判断哪些项已被接管 —— 已停止显示列表，以免误操作。"
                + "请修复或删除损坏的 config.json 后重新扫描。";
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        if (snapshot.Failures.Count > 0)
        {
            var names = string.Join("、", snapshot.Failures
                .Where(failure => SourceFilter is not { } kind || failure.Source == kind)
                .Select(static failure => failure.DisplayName));
            FailureText = names.Length == 0 ? null : $"以下来源扫描失败，列表可能不完整：{names}。详情见调度日志。";
        }
        else
        {
            FailureText = null;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>搜索词变化 → 重算可见列表。</summary>
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    /// <summary>状态筛选变化 → 重算可见列表。</summary>
    partial void OnStatusFilterIndexChanged(int value) => ApplyFilters();

    /// <summary>排序方式变化 → 重算可见列表。</summary>
    partial void OnSortIndexChanged(int value) => ApplyFilters();

    /// <summary>
    /// 按当前搜索词与状态筛选重建 <see cref="FilteredRows"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>差量同步，绝不 <c>Clear()</c> + <c>Add()</c> 整表重摆</b>（A2.1）。
    /// <c>FilteredRows</c> 是 <c>ListView</c> 真正绑定的集合，而 <c>ObservableCollection</c>
    /// 的每一次 <c>Add</c> 都会让 ListView **新建一个容器** —— 整表重摆于是把每一行
    /// 都重建一遍，后果是行内按钮闪一下、焦点丢失、正在点的按钮被换掉。
    /// 而 <see cref="ApplyFilters"/> 在搜索框每敲一个字、每切一次筛选时都会跑。
    /// </remarks>
    private void ApplyFilters()
    {
        var keyword = SearchText.Trim();

        IEnumerable<StartupEntryRow> visible = Rows
            .Where(row => MatchesStatusFilter(row) && MatchesKeyword(row, keyword));

        visible = SortIndex switch
        {
            1 => visible.OrderBy(static row => StatusRank(row)).ThenBy(static row => row.Name, StringComparer.CurrentCulture),
            _ => visible.OrderBy(static row => row.Name, StringComparer.CurrentCulture),
        };

        SyncInPlace(FilteredRows, [.. visible]);

        OnPropertyChanged(nameof(FilteredEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>
    /// 把 <paramref name="desired"/> 差量同步进 <paramref name="current"/>：只对**真的变了**的位置
    /// 发通知，位置相同且内容相同的行一个通知都不发。
    /// </summary>
    /// <remarks>
    /// 逐位比较用引用相等而不是 <c>Equals</c>：行对象是不可变的（换状态就换新实例），
    /// 同一个行对象在前后两次筛选里出现就代表它一个字节都没变。走 <c>Equals</c> 只会
    /// 多算一遍却得到同样的结论。
    /// </remarks>
    private static void SyncInPlace(ObservableCollection<StartupEntryRow> current, List<StartupEntryRow> desired)
    {
        // ① 找到第一个不同的位置。之前的前缀原样不动 —— 这就是"差量"的含义。
        var start = 0;
        while (start < current.Count && start < desired.Count && ReferenceEquals(current[start], desired[start]))
        {
            start++;
        }

        // ② 尾部多余的从后往前删：往前删会让后面所有行的索引位移，白发一批通知。
        while (current.Count > desired.Count)
        {
            current.RemoveAt(current.Count - 1);
        }

        for (var i = start; i < desired.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], desired[i]))
            {
                continue;
            }

            if (i >= current.Count)
            {
                current.Add(desired[i]);
                continue;
            }

            // ③ 这一行在后面别处已经出现过 ⇒ 它只是**位置变了**，不该重建容器。
            //   排序一变就是这种情况，而"重建容器"正是行内操作闪烁的来源。
            //   ObservableCollection<T>.Move（.NET 9+）发的是一条真正的 Move 通知；
            //   自己用 RemoveAt + Insert 拼则会发两条 —— WinUI 对 Remove 是销毁容器、
            //   对 Add 是新建容器，于是排一次序整屏行都没了。
            var existingIndex = IndexOfFrom(current, desired[i], i + 1);
            if (existingIndex >= 0)
            {
                current.Move(existingIndex, i);
                continue;
            }

            // ④ 内容确实变了（状态翻转 / 换图标）—— 这一格只能 Replace。
            current[i] = desired[i];
        }
    }

    private static int IndexOfFrom(ObservableCollection<StartupEntryRow> rows, StartupEntryRow target, int startIndex)
    {
        for (var i = startIndex; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private static int StatusRank(StartupEntryRow row) => row.StatusKind switch
    {
        "taken" => 0,
        "disabled" => 1,
        "missing" => 2,
        "protected" => 3,
        _ => 4,
    };

    /// <summary>判断一行是否通过状态筛选。</summary>
    private bool MatchesStatusFilter(StartupEntryRow row) => StatusFilterIndex switch
    {
        1 => row.Entry.IsEnabled && !row.Entry.IsTakenOver,
        2 => !row.Entry.IsEnabled && !row.Entry.IsTakenOver,
        3 => row.Entry.IsTakenOver,
        _ => true,
    };

    /// <summary>判断一行是否命中搜索词。</summary>
    private static bool MatchesKeyword(StartupEntryRow row, string keyword)
    {
        if (keyword.Length == 0)
        {
            return true;
        }

        return row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || row.CommandLine.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || row.LocationText.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>拼页头副标题。</summary>
    private static string BuildSubtitle(ScanSnapshot snapshot, StartupSource? filter)
    {
        var entries = filter is { } kind
            ? snapshot.Entries.Where(entry => entry.Source == kind).ToList()
            : [.. snapshot.Entries];

        var disabled = entries.Count(
            static entry => !entry.IsEnabled && !entry.IsTakenOver && !entry.IsMissing);
        var taken = entries.Count(static entry => entry.IsTakenOver);

        var head = filter is { } source ? DisplayText.SourceOf(source) : "全部来源";
        return $"{head} · 共 {entries.Count} 项 · 已接管 {taken} 项 · 已禁用 {disabled} 项";
    }
}