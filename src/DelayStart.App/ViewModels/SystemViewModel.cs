using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DelayStart.Management.Models;
using DelayStart.Management.Services;

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

    /// <summary>是否正在加载。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>页头副标题（带计数）。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; } = "正在读取…";

    /// <summary>加载当前分区的只读数据。页面进入时调用。</summary>
    /// <returns>异步任务。</returns>
    /// <remarks>
    /// 只拉当前分区要用的数据：四个入口各自独立，进「服务」页没必要读 Winlogon。
    /// </remarks>
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            switch (Section)
            {
                case SystemSection.Services:
                {
                    _allServices.Clear();
                    _allServices.AddRange(await Task.Run(_serviceQuery.QueryWin32Services).ConfigureAwait(true));
                    RefillServiceViews();
                    break;
                }

                case SystemSection.Drivers:
                {
                    _allDrivers.Clear();
                    _allDrivers.AddRange(await Task.Run(_serviceQuery.QueryDrivers).ConfigureAwait(true));
                    RefillServiceViews();
                    break;
                }

                case SystemSection.Winlogon:
                {
                    var winlogon = await Task.Run(SystemStartupInspector.ReadWinlogon).ConfigureAwait(true);
                    ReplaceAll(WinlogonEntries, winlogon);
                    Subtitle = $"Winlogon 关键值 · {WinlogonEntries.Count} 项";
                    TextEmptyHint = string.Empty;
                    break;
                }

                case SystemSection.GroupPolicy:
                {
                    var gpo = await Task.Run(SystemStartupInspector.ReadGroupPolicy).ConfigureAwait(true);
                    ReplaceAll(GroupPolicyEntries, gpo);
                    Subtitle = $"组策略启动项 · {GroupPolicyEntries.Count} 项";
                    TextEmptyHint = GroupPolicyEntries.Count == 0
                        ? "本机没有组策略下发的自启动项 —— 这是正常现象：只有域环境统一推送，或手动用 gpedit.msc 配置过「启动脚本 / 策略 Run」的机器，这里才会有内容。"
                        : string.Empty;
                    break;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
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
