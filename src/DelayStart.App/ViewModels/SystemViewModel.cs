using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DelayStart.Management.Models;
using DelayStart.Management.Services;

using System.Security.Cryptography;
using System.Text;

namespace DelayStart.App.ViewModels;

/// <summary>「系统启动项」的四个分区（UI v2：分区即导航入口）。</summary>
public enum SystemSection
{
    /// <summary>Win32 服务（默认只看第三方，D2）。</summary>
    Services,

    /// <summary>内核 / 文件系统驱动（默认只看第三方，D3）。</summary>
    Drivers,

    /// <summary>Winlogon 关键值。</summary>
    Winlogon,

    /// <summary>组策略启动项（D2 = A：Policies\Explorer\Run + GPO 脚本）。</summary>
    GroupPolicy,
}

/// <summary>
/// 「系统启动项」分区页 ViewModel（FR-7，全部只读；四个分区共用本类）。
/// </summary>
/// <remarks>
/// 服务与驱动默认**只显示第三方**（D2/D3）：Windows 内置项数百条，会把用户真正
/// 要排查的内容淹没。内置判据是 <see cref="ServiceInfo.IsBuiltinWindows"/>（2026-09-20
/// 批复 1 起以 PE 内嵌 Authenticode 签名者主题为准，无签名退回路径判据），
/// 页内开关随时切回全量 —— 过滤只影响显示。
/// </remarks>
public partial class SystemViewModel : ObservableObject
{
    private readonly ServiceQueryService _serviceQuery;

    /// <summary>当前分区（页面由导航标签设置）。</summary>
    public SystemSection Section { get; set; } = SystemSection.Services;

    /// <summary>服务全量数据（不过滤）。<see cref="Services"/> 是它的过滤视图。</summary>
    private readonly List<ServiceInfo> _allServices = [];

    /// <summary>驱动全量数据（不过滤）。<see cref="Drivers"/> 是它的过滤视图。</summary>
    private readonly List<ServiceInfo> _allDrivers = [];

    /// <summary>构造分区页 ViewModel。</summary>
    /// <param name="serviceQuery">服务查询服务。</param>
    public SystemViewModel(ServiceQueryService serviceQuery)
    {
        ArgumentNullException.ThrowIfNull(serviceQuery);

        _serviceQuery = serviceQuery;
    }

    /// <summary>Win32 服务列表（已按「显示内置服务」开关过滤）。</summary>
    public ObservableCollection<ServiceInfo> Services { get; } = [];

    /// <summary>内核 / 文件系统驱动列表（已按「显示内置服务」开关过滤）。</summary>
    public ObservableCollection<ServiceInfo> Drivers { get; } = [];

    /// <summary>Winlogon 关键值。</summary>
    public ObservableCollection<ReadOnlyEntry> WinlogonEntries { get; } = [];

    /// <summary>组策略启动项（策略 Run 键 + GPO 脚本）。</summary>
    public ObservableCollection<ReadOnlyEntry> GroupPolicyEntries { get; } = [];

    /// <summary>当前页是否展示服务类列表（服务 / 驱动共用一版行模板）。</summary>
    public bool IsServiceSection => Section is SystemSection.Services or SystemSection.Drivers;

    /// <summary>当前页是否展示名值列表（Winlogon / 组策略共用一版行模板）。</summary>
    public bool IsTextSection => !IsServiceSection;

    /// <summary>是否同时显示 Windows 内置服务 / 驱动。默认只看第三方。</summary>
    [ObservableProperty]
    public partial bool ShowBuiltinServices { get; set; }

    /// <summary>开关切换 → 重填过滤视图。</summary>
    partial void OnShowBuiltinServicesChanged(bool value) => RefillServiceViews();

    /// <summary>服务 / 驱动过滤视图为空（用于空列表提示）。</summary>
    [ObservableProperty]
    public partial bool IsListEmpty { get; set; }

    /// <summary>服务 / 驱动空列表的提示文案（批复 5：驱动默认视图常为空，要说清楚去哪看）。</summary>
    [ObservableProperty]
    public partial string EmptyHint { get; set; } = string.Empty;

    /// <summary>名值列表（Winlogon / 组策略）的空提示；空串 = 不显示（批复 7）。</summary>
    [ObservableProperty]
    public partial string TextEmptyHint { get; set; } = string.Empty;

    /// <summary>
    /// 名值列表（Winlogon / 组策略）当前有没有数据。为 <see langword="false"/> 时
    /// 列表区域整体让位给空提示（2026-09-21 批复：提示展示在列表占位区域，
    /// 与总览「最近一次开机调度」的空状态同一表达）。
    /// </summary>
    [ObservableProperty]
    public partial bool HasTextEntries { get; set; }

    /// <summary>是否正在加载。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// 四个分区各自的进程级缓存（A3.1）。四个入口互相独立，各存各的。
    /// </summary>
    /// <remarks>
    /// <b>进程内有效</b>：这一页本来就是只读展示，缓存的生命周期与应用一致就够 ——
    /// 服务列表不会在应用运行期间被人从外面改到需要另起一份缓存的程度，
    /// 而落盘一份服务快照的失效判断比自己实现一套更难做对。
    /// </remarks>
    private IReadOnlyList<ServiceInfo>? _servicesCache;
    private IReadOnlyList<ServiceInfo>? _driversCache;
    private IReadOnlyList<ReadOnlyEntry>? _winlogonCache;
    private IReadOnlyList<ReadOnlyEntry>? _gpoCache;

    private string? _servicesFingerprint;
    private string? _driversFingerprint;
    private string? _winlogonFingerprint;
    private string? _gpoFingerprint;

    /// <summary>页头副标题（分区 + 计数）。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; } = "正在读取…";

    public async Task LoadAsync()
    {
        // 🔴 **重入守卫**（审计 P3-6）：本页会被两条路同时调 —— 用户切页进来，
        // 以及守卫通知把用户导航到本页后外壳发的 `IReloadablePage.Reload()`。
        // 两次调用重叠时有两个具体问题：
        // ① 两轮 `RefreshCurrentSectionAsync` 并发，谁后返回谁说了算 ——
        // 先发起的慢查询可能**后**落地，把更新的数据覆盖成旧的；
  // ② `IsBusy` 各自置 false，先完成的那次就把「忙」抹掉了，
        // 而另一轮还在查 —— 转圈消失而进度条其实没停。
        //
        // 合并而不是排队：两轮读的是同一个活体系统状态，**后一轮的结果不会比先一轮更新**
        //（没有「我要刷新到更新的数据」这个语义），所以等在途那一轮就等于拿到了同一份答案。
  // 排队则是白等一轮。
  if (_loadInFlight is { } running)
   {
   await running.ConfigureAwait(true);
            return;
        }

        var load = LoadCoreAsync();
        _loadInFlight = load;

   try
        {
            await load.ConfigureAwait(true);
    }
     finally
        {
 // 🔴 只能用**引用相等**清：`_loadInFlight` 可能已经被下一轮换成了别的 Task，
     // 那时把它置 null 会把下一轮的在途状态抹掉，下一轮就失去守卫了。
            if (ReferenceEquals(_loadInFlight, load))
  {
  _loadInFlight = null;
            }
        }
    }

    /// <summary>在途加载任务（重入合并用，见 <see cref="LoadAsync"/>）。</summary>
    private Task? _loadInFlight;

    /// <summary>真正干活的加载。</summary>
    /// <returns>异步任务。</returns>
    /// <remarks>
  /// 单独拆出来是为了让 <see cref="LoadAsync"/> 的重入守卫只管「要不要再发起一轮」——
    /// 业务步骤与重入逻辑混在一个方法里会让两件事都看不清。
    /// </remarks>
    private async Task LoadCoreAsync()
    {
        IsBusy = true;
        try
        {
   // ── 第一段：先摆缓存（同步，微秒级）────────────────────────────────
            RenderFromCache();

     // ── 第二段：后台重查，指纹门控刷 ───────────────────────────────────
  await RefreshCurrentSectionAsync().ConfigureAwait(true);
     }
        finally
        {
  IsBusy = false;
        }
    }

    /// <summary>把本进程缓存里当前分区的内容渲染出来（没有缓存就什么都不做）。</summary>
    /// <remarks>
    /// 🔴 "没有缓存就什么都不做"是刻意的：不要在这里 <c>Clear</c>。
    /// 那一段是给"重查回来发现内容变了"用的；第一段若无脑清空，
    /// 用户就会看到"有内容 → 空 → 有内容"的来回闪。
    /// </remarks>
    private void RenderFromCache()
    {
        switch (Section)
        {
            case SystemSection.Services when _servicesCache is { } cachedServices:
                _allServices.Clear();
                _allServices.AddRange(cachedServices);
                RefillServiceViews();
                break;

            case SystemSection.Drivers when _driversCache is { } cachedDrivers:
                _allDrivers.Clear();
                _allDrivers.AddRange(cachedDrivers);
                RefillServiceViews();
                break;

            case SystemSection.Winlogon when _winlogonCache is { } cachedWinlogon:
                    ReplaceAll(WinlogonEntries, cachedWinlogon);
                    ApplyWinlogonTextState(cachedWinlogon.Count);
                    break;

            case SystemSection.GroupPolicy when _gpoCache is { } cachedGpo:
                    ReplaceAll(GroupPolicyEntries, cachedGpo);
                    ApplyGroupPolicyTextState(cachedGpo.Count);
                    break;
        }
    }

    private async Task RefreshCurrentSectionAsync()
    {
        switch (Section)
        {
            case SystemSection.Services:
            {
                var services = await Task.Run(_serviceQuery.QueryWin32Services).ConfigureAwait(true);

                // 指纹相同 ⇒ 一个集合通知都不发。A3 的核心收益就在这里：
                // 自启动的服务/驱动列表在两次查询之间通常**完全一样**，
                // 而整刷一次意味着 ListView 重建全部容器（滚动位置、展开状态全丢）。
                var servicesFingerprint = FingerprintOf(services);

                if (_servicesFingerprint != servicesFingerprint)
                {
                    _servicesFingerprint = servicesFingerprint;
                    _servicesCache = services;
                    _allServices.Clear();
                    _allServices.AddRange(services);
                    RefillServiceViews();
                }

                break;
            }

            case SystemSection.Drivers:
            {
                var drivers = await Task.Run(_serviceQuery.QueryDrivers).ConfigureAwait(true);

                var driversFingerprint = FingerprintOf(drivers);

                if (_driversFingerprint != driversFingerprint)
                {
                    _driversFingerprint = driversFingerprint;
                    _driversCache = drivers;
                    _allDrivers.Clear();
                    _allDrivers.AddRange(drivers);
                    RefillServiceViews();
                }

                break;
            }

            case SystemSection.Winlogon:
            {
                var winlogon = await Task.Run(SystemStartupInspector.ReadWinlogon).ConfigureAwait(true);

                var winlogonFingerprint = FingerprintOf(winlogon);

                if (_winlogonFingerprint != winlogonFingerprint)
                {
                    _winlogonFingerprint = winlogonFingerprint;
                    _winlogonCache = winlogon;
                    ReplaceAll(WinlogonEntries, winlogon);
                    ApplyWinlogonTextState(winlogon.Count);
                }

                break;
            }

            case SystemSection.GroupPolicy:
            {
                var gpo = await Task.Run(SystemStartupInspector.ReadGroupPolicy).ConfigureAwait(true);

                var gpoFingerprint = FingerprintOf(gpo);

                if (_gpoFingerprint != gpoFingerprint)
                {
                    _gpoFingerprint = gpoFingerprint;
                    _gpoCache = gpo;
                    ReplaceAll(GroupPolicyEntries, gpo);
                    ApplyGroupPolicyTextState(gpo.Count);
                }

                break;
            }
        }
    }

    /// <summary>算出内容指纹（顺序 + 每项的关键字段），用于"这次查询有没有变化"。</summary>
    /// <remarks>
    /// 🔴 参与计算的字段必须是**页面上看得见的全部**：少算一个，那次真实变化就会被
    /// 判成"没变"而被静默吞掉 —— 界面上停留在旧状态，零提示。
    /// <para>
    /// 顺序也算进去：排序变了对用户就是一次可见的变化。
    /// </para>
    /// </remarks>
    private static string FingerprintOf(IReadOnlyList<ServiceInfo> items)
    {
        var canonical = new StringBuilder(items.Count * 64);
        foreach (var item in items)
        {
            canonical.Append(item.ServiceName).Append('|')
                .Append(item.DisplayName).Append('|')
                .Append(item.Description).Append('|')
                .Append(item.StartType).Append('|')
                .Append(item.StatusText).Append('|')
                .Append(item.IsRunning ? '1' : '0')
                .Append(item.DelayedAuto ? '1' : '0')
                .Append(item.IsDriver ? '1' : '0')
                .Append(item.BinaryPath).Append('|')
                .Append(item.IsBuiltinWindows ? '1' : '0')
                .Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string FingerprintOf(IReadOnlyList<ReadOnlyEntry> items)
    {
        var canonical = new StringBuilder(items.Count * 64);
        foreach (var item in items)
        {
            canonical.Append(item.Location).Append('|')
                .Append(item.Name).Append('|')
                .Append(item.Detail).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private void ApplyWinlogonTextState(int count)
    {
        Subtitle = $"Winlogon 关键值 · {count} 项";
        TextEmptyHint = count == 0 ? "没有读到 Winlogon 自启动项。" : string.Empty;
        HasTextEntries = count > 0;
    }

    private void ApplyGroupPolicyTextState(int count)
    {
        Subtitle = $"组策略启动项 · {count} 项";
        TextEmptyHint = count == 0
            ? "本机没有组策略下发的自启动项 —— 这是正常现象：只有域环境统一推送，或手动用 gpedit.msc 配置过「启动脚本 / 策略 Run」的机器，这里才会有内容。"
            : string.Empty;
        HasTextEntries = count > 0;
    }

    /// <summary>按开关重填服务 / 驱动的过滤视图，并同步副标题计数。</summary>
    private void RefillServiceViews()
    {
        var showBuiltin = ShowBuiltinServices;

        var isDrivers = Section == SystemSection.Drivers;
        var all = isDrivers ? _allDrivers : _allServices;
        var view = isDrivers ? Drivers : Services;

        view.Clear();
        foreach (var item in all)
        {
            if (showBuiltin || !item.IsBuiltinWindows)
            {
                view.Add(item);
            }
        }

        var note = showBuiltin
            ? $"全部 {view.Count}"
            : $"第三方 {view.Count}（内置 {all.Count - view.Count} 已隐藏）";

        Subtitle = isDrivers
            ? $"内核 / 文件系统驱动 · {note}"
            : $"Win32 服务 · {note}";

        IsListEmpty = view.Count == 0;
        EmptyHint = all.Count == 0
            ? "没有查询到任何条目。"
            : isDrivers
                ? $"没有第三方驱动 —— 本机安装的 {all.Count} 个驱动全部来自 Windows 内置。要查看全部内置驱动，请打开右上角「显示 Windows 内置驱动」开关。"
                : "没有第三方服务 —— 打开右上角「显示 Windows 内置服务」开关可查看 Windows 内置服务。";
    }

    /// <summary>集合整体替换（UI 线程调用）。</summary>
    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}