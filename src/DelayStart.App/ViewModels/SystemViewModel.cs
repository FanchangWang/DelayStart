using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DelayStart.Management.Models;
using DelayStart.Management.Services;

namespace DelayStart.App.ViewModels;

/// <summary>
/// 系统启动项页 ViewModel（FR-7，全部只读）。
/// </summary>
/// <remarks>
/// 服务与驱动默认**只显示第三方**（D2/D3）：Windows 内置项数百条，会把用户真正
/// 要排查的内容淹没。内置判据是 <see cref="ServiceInfo.IsBuiltinWindows"/>（按映像路径），
/// 页内开关随时切回全量 —— 过滤只影响显示，数据每次进页都全量拉取。
/// </remarks>
public partial class SystemViewModel : ObservableObject
{
    private readonly ServiceQueryService _serviceQuery;

    /// <summary>服务全量数据（不过滤）。<see cref="Services"/> 是它的过滤视图。</summary>
    private readonly List<ServiceInfo> _allServices = [];

    /// <summary>驱动全量数据（不过滤）。<see cref="Drivers"/> 是它的过滤视图。</summary>
    private readonly List<ServiceInfo> _allDrivers = [];

    /// <summary>构造系统启动项页 ViewModel。</summary>
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

    /// <summary>组策略登录脚本。</summary>
    public ObservableCollection<ReadOnlyEntry> LogonScripts { get; } = [];

    /// <summary>是否同时显示 Windows 内置服务 / 驱动。默认只看第三方。</summary>
    [ObservableProperty]
    public partial bool ShowBuiltinServices { get; set; }

    /// <summary>开关切换 → 重填两个过滤视图。</summary>
    partial void OnShowBuiltinServicesChanged(bool value) => RefillServiceViews();

    /// <summary>是否正在加载。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>页头副标题。</summary>
    [ObservableProperty]
    public partial string Subtitle { get; set; } = "正在读取…";

    /// <summary>服务区标题（带数量）。放在 ViewModel 是因为 Page 不支持 OneWay 绑定自身属性。</summary>
    [ObservableProperty]
    public partial string ServicesHeaderText { get; set; } = "Win32 服务";

    /// <summary>驱动区标题（带数量）。</summary>
    [ObservableProperty]
    public partial string DriversHeaderText { get; set; } = "内核 / 文件系统驱动";

    /// <summary>Winlogon 区标题（带数量）。</summary>
    [ObservableProperty]
    public partial string WinlogonHeaderText { get; set; } = "Winlogon 关键值";

    /// <summary>登录脚本区标题（带数量）。</summary>
    [ObservableProperty]
    public partial string ScriptsHeaderText { get; set; } = "组策略登录脚本";

    /// <summary>加载全部只读数据。页面进入时调用。</summary>
    /// <returns>异步任务。</returns>
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var services = await Task.Run(_serviceQuery.QueryWin32Services).ConfigureAwait(true);
            var drivers = await Task.Run(_serviceQuery.QueryDrivers).ConfigureAwait(true);
            var winlogon = await Task.Run(SystemStartupInspector.ReadWinlogon).ConfigureAwait(true);
            var scripts = await Task.Run(SystemStartupInspector.ReadLogonScripts).ConfigureAwait(true);

            _allServices.Clear();
            _allServices.AddRange(services);
            _allDrivers.Clear();
            _allDrivers.AddRange(drivers);

            WinlogonEntries.Clear();
            foreach (var item in winlogon)
            {
                WinlogonEntries.Add(item);
            }

            LogonScripts.Clear();
            foreach (var item in scripts)
            {
                LogonScripts.Add(item);
            }

            RefillServiceViews();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>按开关重填服务 / 驱动的过滤视图，并同步标题与副标题的计数。</summary>
    private void RefillServiceViews()
    {
        var showBuiltin = ShowBuiltinServices;

        Services.Clear();
        Drivers.Clear();
        foreach (var item in _allServices)
        {
            if (showBuiltin || !item.IsBuiltinWindows)
            {
                Services.Add(item);
            }
        }

        foreach (var item in _allDrivers)
        {
            if (showBuiltin || !item.IsBuiltinWindows)
            {
                Drivers.Add(item);
            }
        }

        var servicesNote = showBuiltin
            ? $"全部 {Services.Count}"
            : $"第三方 {Services.Count}（内置 {_allServices.Count - Services.Count} 已隐藏）";
        var driversNote = showBuiltin
            ? $"全部 {Drivers.Count}"
            : $"第三方 {Drivers.Count}（内置 {_allDrivers.Count - Drivers.Count} 已隐藏）";

        ServicesHeaderText = $"Win32 服务 · {servicesNote}";
        DriversHeaderText = $"内核 / 文件系统驱动 · {driversNote}";
        Subtitle = $"服务 {Services.Count} · 驱动 {Drivers.Count} · Winlogon {WinlogonEntries.Count} · 登录脚本 {LogonScripts.Count}";
    }
}
