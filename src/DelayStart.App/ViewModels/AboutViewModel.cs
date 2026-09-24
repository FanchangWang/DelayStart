using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

using CommunityToolkit.Mvvm.ComponentModel;

using DelayStart.App.Services;
using DelayStart.Core.Services;

using Windows.ApplicationModel.DataTransfer;

namespace DelayStart.App.ViewModels;

/// <summary>一条开源库致谢（「关于」页开源节，D115）。</summary>
/// <param name="Name">库名。</param>
/// <param name="Url">项目主页。</param>
/// <param name="Usage">本程序用它做什么。</param>
/// <param name="License">许可证。</param>
public sealed record OpenSourceLibrary(string Name, string Url, string Usage, string License);

/// <summary>
/// 「关于」页的 ViewModel（2026-09-24 批复 28）。
/// </summary>
/// <remarks>
/// <para>
/// 这一页存在的意义是"出问题时能一眼抄下来"，所以每一项都取**当下**的真实值：
/// 版本号来自程序集、系统版本来自注册表、各项路径来自 <see cref="PathService"/>
/// （D23 的唯一路径来源）。它**不落盘、不联网**。
/// </para>
/// <para>
/// 🔴 刻意**不**显示"最新版本是多少"这类在线信息：本程序的设计前提是离线可用
/// （NFR-x 只允许节假日数据联网，且在设置页可控）。一个会变慢、会在无网时留白的位置，
/// 不值得放进"关于"。
/// </para>
/// <para>
/// ⚠ 那几个"值是常量"的成员写成**带初始值的自动属性**而不是 `=> 常量` 表达式体：
/// 分析器会为后者报 CA1822（不访问实例数据 → 建议 static），而 <c>x:Bind</c>
/// 绑不到静态属性上。带初始值的自动属性有后备字段，不触发这条。
/// </para>
/// </remarks>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly PathService _paths;
    private readonly ToastService _toast;

    /// <summary>构造「关于」页 ViewModel。</summary>
    /// <param name="paths">路径服务（本页列出配置 / 数据 / 日志 / 安装四个根目录）。</param>
    /// <param name="toast">应用内通知服务（"已复制"这类成功动作给一次可见反馈）。</param>
    public AboutViewModel(PathService paths, ToastService toast)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(toast);

        _paths = paths;
        _toast = toast;

        Version = ResolveVersion();
        ConfigPath = paths.ConfigRoot;
        DataPath = paths.LocalRoot;
        LogsPath = paths.LogsRoot;
        InstalledPath = paths.InstalledRoot;
    }

    /// <summary>应用版本号，形如 <c>0.3.1</c>；取不到时是「未知」。</summary>
    public string Version { get; }

    /// <summary>卡片上那行「版本 x.y.z · MIT 许可证」。</summary>
    public string VersionLine => $"版本 {Version} · MIT 许可证";

    /// <summary>一句话定位（卡片正文）。</summary>
    public string Summary { get; } =
        "Windows 开机自启动的错峰管理器：扫描全部自启动位置 → 软禁用（不删任何数据）→ "
        + "由独立的调度进程按延时逐个启动，并由自启动项守卫持续看护。";

    /// <summary>.NET 运行时描述，形如 <c>.NET 10.0.1</c>。</summary>
    public string RuntimeText { get; } = RuntimeInformation.FrameworkDescription;

    /// <summary>操作系统描述，形如 <c>Windows 11 24H2（内部版本 26100）</c>。</summary>
    public string OsText { get; } = DescribeWindows();

    /// <summary>进程与系统的架构，形如 <c>X64（64 位）· 系统 X64</c>。</summary>
    public string ArchText { get; } = DescribeArchitecture();

    /// <summary>当前运行身份：管理员 / 标准用户。</summary>
    /// <remarks>
    /// 🔴 用 <see cref="Environment.IsPrivilegedProcess"/> 而不是 <c>WindowsIdentity</c>：
    /// 后者在 WinUI 3 的框架集里不保证可用（`System.Security.Principal.Windows`
    /// 属桌面框架），而这一档信息来自基础库，语义正是"当前进程是否持有提权令牌"。
    /// 程序自身是 <c>asInvoker</c> + 入口自提权门（D82），所以正常跑起来就是管理员。
    /// </remarks>
    public string ElevationText { get; } =
        Environment.IsPrivilegedProcess ? "管理员（高完整性）" : "标准用户";

    /// <summary>配置（漫游）目录：<c>%APPDATA%\DelayStart</c>。</summary>
    public string ConfigPath { get; }

    /// <summary>数据目录：<c>%LOCALAPPDATA%\DelayStart</c>（状态、归档、节假日数据）。</summary>
    public string DataPath { get; }

    /// <summary>日志目录。</summary>
    public string LogsPath { get; }

    /// <summary>安装目录（只读；仅用于取自身 exe 路径，NFR-6.7）。</summary>
    public string InstalledPath { get; }

    /// <summary>项目主页。</summary>
    public string ProjectUrl { get; } = "https://github.com/FanchangWang/DelayStart";

    /// <summary>本程序引用的开源库（D115，2026-09-24 用户要求）。</summary>
    /// <remarks>
    /// 🔴 刻意**不写版本号**：版本写在 <c>Directory.Packages.props</c>（CPM）里，
    /// 界面上再抄一份就是两处维护，且用户真正关心的是"用了谁的什么东西"，不是版本。
    /// 顺序按"离界面近 → 远"：UI 框架 → MVVM → 控件 → DI → 计划任务 → 测试。
    /// </remarks>
    public IReadOnlyList<OpenSourceLibrary> Libraries { get; } =
    [
        new("Windows App SDK（WinUI 3）", "https://github.com/microsoft/WindowsAppSDK", "应用框架与整个界面", "MIT"),
        new("CommunityToolkit.Mvvm", "https://github.com/CommunityToolkit/dotnet", "MVVM 框架（源生成器）", "MIT"),
        new("CommunityToolkit.WinUI", "https://github.com/CommunityToolkit/Windows", "设置页与关于页的卡片控件（SettingsCard）", "MIT"),
        new("Microsoft.Extensions.DependencyInjection", "https://github.com/dotnet/runtime", "依赖注入容器", "MIT"),
        new("TaskScheduler", "https://github.com/dahall/TaskScheduler", "Windows 计划任务的注册与读取", "MIT"),
        new("xUnit v3", "https://github.com/xunit/xunit", "单元测试框架", "Apache-2.0"),
    ];

    /// <summary>节假日数据的上游项目主页（D115，2026-09-24 用户要求）。</summary>
    public string HolidaySourceUrl { get; } = "https://github.com/NateScarlet/holiday-cn";

    /// <summary>节假日数据来源的致谢说明（设置页的数据也来自这里）。</summary>
    /// <remarks>
    /// 🔴 与 README 的「开源致谢」节是**同一套口径**：holiday-cn 是国务院放假安排的
    /// 社区整理版，本程序的「法定工作日 / 法定节假日」两档周期判定全部吃它的数据。
    /// </remarks>
    public string HolidayCredit { get; } =
        "法定节假日数据来自 NateScarlet/holiday-cn —— 依据国务院历年公布的放假安排整理，"
        + "本程序「法定工作日 / 法定节假日」周期判定用的就是这份数据。感谢其维护者与贡献者。";

    /// <summary>最后一次操作失败的原因；空串表示没有错误。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>是否有错误要显示。</summary>
    /// <remarks>
    /// 🔴 必须是 <c>[ObservableProperty]</c> 的 <see cref="StatusText"/> + 下面那个通知，
    /// 而不是把可见性做成独立字段：x:Bind OneWay 要求路径上有通知源。
    /// 漏掉通知的后果不是"提示不好看"，而是**报错全部静默** ——
    /// Message 变了但 IsOpen 永远停在 false，按钮表现为"点了没反应"
    /// （2026-09-23 设置页真机实测过同一个坑）。
    /// </remarks>
    public bool HasError => StatusText.Length > 0;

    /// <summary><see cref="StatusText"/> 变化时补发 <see cref="HasError"/> 的通知。</summary>
    /// <param name="value">新文案。</param>
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    /// <summary>把版本、系统与四项路径拼成一段纯文本复制到剪贴板。</summary>
    /// <remarks>
    /// 它取的就是页面上显示的那几行 —— 让用户去逐项手抄（或截图）才是这一页最大的失败。
    /// </remarks>
    public void CopyDiagnostics()
    {
        var text = string.Join(
            Environment.NewLine,
            [
                $"DelayStart {Version}",
                $"运行时：{RuntimeText}",
                $"操作系统：{OsText}",
                $"架构：{ArchText}",
                $"运行身份：{ElevationText}",
                $"配置目录：{ConfigPath}",
                $"数据目录：{DataPath}",
                $"日志目录：{LogsPath}",
                $"安装目录：{InstalledPath}",
            ]);

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            _toast.Show("诊断信息已复制到剪贴板");
        }
        catch (Exception ex)
        {
            // 剪贴板是系统级共享资源，被别的进程占着就会拒绝写入 —— 报出来即可，不必重试。
            Fail($"复制失败：{ex.Message}");
        }
    }

    /// <summary>在资源管理器里打开一个目录。</summary>
    /// <param name="path">目录的完整路径（由行的 <c>Tag</c> 带过来）。</param>
    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // 目录由"写入方按需创建"：全新机器上数据 / 日志目录可能还不存在，
            // 打开一个不存在的路径会让资源管理器直接弹"找不到"。
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Fail($"打开目录失败：{ex.Message}");
        }
    }

    /// <summary>用默认浏览器打开项目主页。</summary>
    /// <remarks>
    /// ⚠ 本进程是提权的（D82），ShellExecute 一个 http 地址所启动的浏览器**可能**继承提权
    /// —— 现代浏览器一般会自行降权重启。要彻底规避得用调度端那套
    /// <c>DeElevatedProcessLauncher</c>（降权令牌 + 父进程），但它的服务对象是"启动用户程序"，
    /// 为一条"打开网页"引入跨进程启动器不划算。
    /// </remarks>
    public void OpenProjectPage() => OpenUrl(ProjectUrl, "项目主页");

    /// <summary>用默认浏览器打开一个网址（开源库 / 节假日数据源与项目主页共用，D115）。</summary>
    /// <param name="url">目标网址。</param>
    /// <param name="what">出问题时提示里的称呼。</param>
    public void OpenUrl(string url, string what = "链接")
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Fail($"打开{what}失败：{ex.Message}");
        }
    }

    /// <summary>取程序集里记的版本号。</summary>
    /// <remarks>
    /// 版本号的唯一来源是 <c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>，
    /// MSBuild 会把它同时写进 <c>AssemblyInformationalVersion</c> 与文件版本。
    /// SourceLink 会在信息版后面接 <c>+&lt;commit&gt;</c>，所以取 <c>+</c> 之前那一段。
    /// </remarks>
    private static string ResolveVersion()
    {
        var assembly = typeof(AboutViewModel).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "未知";
    }

    /// <summary>描述操作系统；注册表读不到时退回运行时给的描述。</summary>
    /// <remarks>
    /// 🔴 Windows 11 的 <c>ProductName</c> 至今仍写 <c>Windows 10</c>（微软没改），
    /// 只能靠内部版本号分档：**22000 起为 11**。别照 ProductName 显示。
    /// </remarks>
    private static string DescribeWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            var build = key?.GetValue("CurrentBuildNumber") as string;
            if (string.IsNullOrWhiteSpace(build))
            {
                return RuntimeInformation.OSDescription;
            }

            var family =
                int.TryParse(build, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    && number >= 22000
                    ? "Windows 11"
                    : "Windows 10";

            // DisplayVersion 是"24H2"这种半年频道标记；LTSB / LTSC 等版本可能没有它。
            var display = key?.GetValue("DisplayVersion") as string;

            return string.IsNullOrWhiteSpace(display)
                ? $"{family}（内部版本 {build}）"
                : $"{family} {display}（内部版本 {build}）";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return RuntimeInformation.OSDescription;
        }
    }

    /// <summary>描述进程与系统的架构。</summary>
    /// <remarks>
    /// 两个值都报：Arm64 系统上跑 x64 进程是正常的模拟运行，只报一个数会让人以为装错了包。
    /// </remarks>
    private static string DescribeArchitecture() =>
        $"{RuntimeInformation.ProcessArchitecture}（{(Environment.Is64BitProcess ? 64 : 32)} 位）"
        + $" · 系统 {RuntimeInformation.OSArchitecture}";

    private void Fail(string message) => StatusText = message;
}
