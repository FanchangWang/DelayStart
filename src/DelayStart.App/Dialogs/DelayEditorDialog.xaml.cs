using DelayStart.App.Interop;
using DelayStart.App.Services;
using DelayStart.App.ViewModels;
using DelayStart.Core.Models;
using DelayStart.Core.Services;
using DelayStart.Management.Models;
using DelayStart.Management.Services;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DelayStart.App.Dialogs;

/// <summary>
/// 延时配置编辑器（<c>design-spec.md</c> 三之二）。4 种进入方式共用这一个弹窗，
/// 靠"这条记录有没有系统来源"分叉成 2 种形态（目标程序块：只读卡片 / 可选目标 + 页签）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 **文案一律取自设计稿**，不在这里临场发挥。
/// </para>
/// <para>
/// 各文本控件在构造函数里直接赋值而不用 <c>x:Bind</c>：这些值在弹窗显示前一次性确定，
/// 绑定带来的复杂度大于收益。
/// </para>
/// <para>
/// ① 块的目标面板按形态切可见性（只读卡片 / 可选目标），而不是做两个弹窗 ——
/// ②③④ 是四种进入方式完全共享的部分。
/// </para>
/// <para>
/// 2026-09-19 用户批复：**单条目延时上限已移除**（自定义值只受 7 天的输入兜底约束）；
/// 手动编辑自定义延时后提交需要**二次确认** —— 弹窗内的确认面板，点「确认使用」才真正提交。
/// bug 批复：延时预设与启动身份均为**胶囊**形态；文件选择走 Win32 对话框（提权进程里
/// WinRT 选择器打不开）；弹窗打开后对主窗口子树放行 UIPI 拖放消息。
/// </para>
/// <para>
/// 🔴 <b>D46 / D47（2026-09-20 用户批复）</b>：手动形态可选 UWP 应用（UWP 条目在配置里存的是
/// <c>shell:AppsFolder\&lt;AUMID&gt;</c> <b>解析名而不是文件路径</b>，文件选择器选不到它）；
/// 白名单 <c>.exe / .lnk / .bat / .cmd / .ps1</c>（移除 <c>.msi</c>：不是 PE 映像，
/// <c>CreateProcess</c> 报 193；新增 <c>.ps1</c>：由 <c>pwsh.exe</c> / <c>powershell.exe</c> 承载），
/// 清单取自 <see cref="LaunchTargetTypes"/>。
/// </para>
/// <para>
/// 🔴 <b>D48（2026-09-20 用户真机反馈）</b>：UWP 条目点「更换」弹出文件选择器 = 换不了目标。
/// </para>
/// <para>
/// 🔴 <b>D53 / D54（2026-09-20 用户批复，本轮）</b>：① 块顶部改成 <b>tab</b>（「程序」/「UWP 应用」）
/// —— tab 决定目标**是哪一类**，换类别就是换页签，而不再是"往同一个字段里互相覆盖"
/// （D48 那种"换了 UWP 还能不能换回来"的问题从结构上就不存在了）；
/// ② 删除底部活摘要（"将在 xx 秒后以 xx 身份启动"）；
/// ③ UWP 页签不摆命令行 / 工作目录（对 UWP 都不成立）；
/// ④ UWP 来源的条目（「自启动项 · UWP Apps」页进入）只显示应用本身的信息。
/// </para>
/// <para>
/// 🔴 <b>D55–D58（2026-09-20 用户批复，本轮）</b>：
/// <b>D55</b> 宽度**回归框架默认**（删掉 <c>ContentDialogMaxWidth = 700</c> 的覆盖；框架默认 548，
/// 见 WinUI 的 <c>generic.xaml</c>）；
/// <b>D56</b> 每个功能块改成「子标题 + 一张边框卡片」，并去掉数字序号，页签从 <c>SelectorBar</c>
/// （选中态只是一小段下划线）换成两颗**分段式 <c>ToggleButton</c>**（选中那颗是 accent 实心填充）；
/// <b>D57</b> 整块删除原 ④（接管对象 / 系统影响）；
/// <b>D58</b> 选择或拖入文件时**工作目录保持留空**，不再自动填成程序所在目录。
/// </para>
/// </remarks>
public sealed partial class DelayEditorDialog : ContentDialog
{
    /// <summary>自定义延时数字框的输入兜底上限：7 天（上限配置已移除，只挡明显的误输入）。</summary>
    private const int FallbackMaxDelay = 604800;

    private readonly WindowHandleProvider? _handles;

    /// <summary>图标提取服务（D46：UWP 应用选择列表复用列表页那套提取）。</summary>
    private readonly IconProvider? _icons;

    private bool _suppressSync;
    private int _delaySeconds;
    private bool _nameWasAutoFilled;

    /// <summary>用户是否手动改过自定义延时（选预设不算）。</summary>
    private bool _manualDelayEdit;

    /// <summary>自定义延时是否已经过确认面板确认。</summary>
    private bool _manualDelayConfirmed;

    /// <summary>初始化是否已跑完。XAML 解析期间 tab 的 <c>SelectionChanged</c> 会早于字段就绪触发。</summary>
    private bool _initialized;

    /// <summary>形态：目标可更换（手动条目）还是只读（系统自启动项绑定）。构造时定型。</summary>
    private readonly bool _manualForm;

    /// <summary>只读形态下目标是不是 UWP 来源的系统项（决定是否显示名称 / 命令行 / 工作目录）。</summary>
    private readonly bool _systemIsUwp;

    /// <summary>手动形态下当前停在哪一页签（<see langword="true"/> = 「UWP 应用」）。</summary>
    private bool _uwpTabActive;

    /// <summary>身份胶囊里是否摆着「管理员」那颗（UWP 目标不给）。</summary>
    private bool _adminPillShown;

    /// <summary>
    /// 用户**想要**的启动身份。单独记一份而不是从胶囊反读：切到 UWP 页签时管理员胶囊会被摘掉，
    /// 反读会把用户之前在「程序」页签选的管理员悄悄丢掉；切回来时应该还在。
    /// </summary>
    private bool _wantAdmin;

    /// <summary>只读形态的目标路径（系统项自带，不可改）。</summary>
    private string _readOnlyPath = string.Empty;

    /// <summary>「程序」页签选中的文件路径。</summary>
    private string _filePath = string.Empty;

    /// <summary>「UWP 应用」页签选中的解析名（<c>shell:AppsFolder\&lt;AUMID&gt;</c>）。</summary>
    private string _uwpPath = string.Empty;

    /// <summary>「UWP 应用」页签选中应用的显示名。</summary>
    private string _uwpName = string.Empty;

    /// <summary>已读到的 UWP 应用列表（懒加载，弹窗内复用）。</summary>
    /// <remarks>形参用具体 <see cref="List{T}"/> 而不是接口：赋值两端都是 <c>List</c>，
    /// 接口分发在这里是纯开销（CA1859 已是本项目禁止的写法）。</remarks>
    private List<UwpAppListItem> _uwpApps = [];

    /// <summary>列表是否已经读过。失败时回落为 <see langword="false"/>，允许再次点击重试。</summary>
    private bool _uwpAppsLoaded;

    /// <summary>构造「加入系统项」形态：目标程序由扫描到的自启动项绑定，不可更改。</summary>
    /// <param name="entry">要接管的系统自启动项。</param>
    /// <param name="presets">延时预设值（秒），来自 <c>Settings.DelayPresets</c>。</param>
    /// <param name="defaultPreset">默认预设（秒）—— 接管时预选的延时。</param>
    public DelayEditorDialog(StartupEntry entry, int[] presets, int defaultPreset)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // 形态与 UWP 判定必须在 InitializeCommon 之前定好：BuildIdentityPills 会读 UwpMode。
        _manualForm = false;
        _systemIsUwp = IsUwpSource(entry.Source, entry.Path);

        InitializeComponent();
        InitializeCommon(presets, defaultPreset);

        Title = "加入延时启动";
        PrimaryButtonText = "加入延时启动";
        SubtitleText.Text = $"来自「{DisplayText.SourceOf(entry.Source)}」，加入后原自启动项将被软禁用";

        ShowReadOnlyTarget(entry.Name, entry.Path, entry.SourceKey, entry.Source, entry.Scope);
        ArgumentsBox.Text = entry.Arguments;

        UseReadOnlyTarget();
        SetDelay(defaultPreset);
        SetIdentity(false);
    }

    /// <summary>构造「编辑已有条目」形态：系统项的目标程序只读，手动项可在 tab 里更换。</summary>
    /// <param name="item">要编辑的配置条目。</param>
    /// <param name="presets">延时预设值（秒）。</param>
    /// <param name="defaultPreset">默认预设（秒）；编辑形态下预选条目现有延时。</param>
    /// <param name="handles">主窗口句柄提供者，手动形态选文件时需要。</param>
    /// <param name="icons">图标提取服务，UWP 应用选择列表需要（D46）。</param>
    public DelayEditorDialog(
        DelayedItem item,
        int[] presets,
        int defaultPreset,
        WindowHandleProvider handles,
        IconProvider icons)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(icons);

        _handles = handles;
        _icons = icons;

        var manual = item.IsManual;
        var uwp = IsUwpSource(item.Source, item.Path);

        _manualForm = manual;
        _systemIsUwp = !manual && uwp;
        _uwpTabActive = manual && uwp;

        InitializeComponent();
        InitializeCommon(presets, defaultPreset);

        Title = "编辑延时设置";
        PrimaryButtonText = "保存修改";

        if (manual)
        {
            // 2026-09-21 批复：手动形态的标题下描述整段移除 —— 下方 UI 已表达同样的信息。
            SubtitleText.Visibility = Visibility.Collapsed;

            UsePickTarget();

            if (_uwpTabActive)
            {
                // 🔴 UWP 手动条目的 Path 是解析名，不能走"按文件名回填"那条路
                // （Path.GetFileNameWithoutExtension 会把 AUMID 截成一个莫名其妙的短名）。
                ApplyPickedUwpTarget(item.Name, item.Path);
            }
            else
            {
                ApplyPickedFile(item.Path);
                NameBox.Text = item.Name;
                _nameWasAutoFilled = false;
            }

            WorkingDirBox.Text = item.WorkingDirectory;
        }
        else
        {
            SubtitleText.Text = "修改已接管条目，原自启动项保持软禁用";

            ShowReadOnlyTarget(item.Name, item.Path, item.SourceKey, item.Source, item.Scope);

            UseReadOnlyTarget();
            WorkingDirBox.Text = item.WorkingDirectory;
        }

        ArgumentsBox.Text = item.Arguments;
        SetDelay(item.DelaySeconds);
        SetIdentity(item.RunAsAdmin);
    }

    /// <summary>构造「手动添加」形态：目标程序由用户在「程序」/「UWP 应用」页签里选。</summary>
    /// <param name="presets">延时预设值（秒）。</param>
    /// <param name="defaultPreset">默认预设（秒）—— 打开时预选的延时。</param>
    /// <param name="handles">主窗口句柄提供者，选文件时需要。</param>
    /// <param name="icons">图标提取服务，UWP 应用选择列表需要（D46）。</param>
    public DelayEditorDialog(int[] presets, int defaultPreset, WindowHandleProvider handles, IconProvider icons)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(icons);

        _handles = handles;
        _icons = icons;
        _manualForm = true;

        InitializeComponent();
        InitializeCommon(presets, defaultPreset);

        Title = "手动添加延时启动";
        PrimaryButtonText = "加入延时启动";

        // 2026-09-21 批复：标题下描述（"选择一个程序…"）移除 —— 拖放区已表达同样的信息。
        SubtitleText.Visibility = Visibility.Collapsed;

        UsePickTarget();
        SetDelay(defaultPreset);
        SetIdentity(false);
    }

    /// <summary>最终选择的延时秒数。</summary>
    public int DelaySeconds => _delaySeconds;

    /// <summary>
    /// 自定义延时是否已经过确认面板确认。
    /// </summary>
    /// <remarks>
    /// 二次确认走弹窗内的确认面板（ContentDialog 之上不能叠第二个 ContentDialog），
    /// 确认后用 <c>Hide()</c> 关闭 —— 结果值为 <see cref="ContentDialogResult.None"/>，
    /// 调用方必须用「Primary 或本属性为真」判定提交。
    /// </remarks>
    public bool DelayConfirmed => _manualDelayConfirmed;

    /// <summary>最终选择的启动身份。UWP 目标恒为普通身份（D45 真机实测）。</summary>
    public bool RunAsAdmin => _wantAdmin && !UwpMode;

    /// <summary>命令行参数。UWP 目标恒为空 —— 外壳委托不转发参数（D46）。</summary>
    public string Arguments => UwpMode ? string.Empty : ArgumentsBox.Text.Trim();

    /// <summary>显示名。只读形态下恒为空 —— 系统条目的名字由系统项决定，不参与编辑。</summary>
    public string ItemName => _manualForm && _uwpTabActive ? _uwpName : NameBox.Text.Trim();

    /// <summary>目标程序路径。UWP 目标为 <c>shell:AppsFolder\…</c> 解析名（系统项则是 AUMID）。</summary>
    public string TargetPath => _manualForm
        ? _uwpTabActive ? _uwpPath : _filePath
        : _readOnlyPath;

    /// <summary>工作目录；留空表示用程序所在目录。UWP 目标恒为空。</summary>
    public string WorkingDirectory => UwpMode ? string.Empty : WorkingDirBox.Text.Trim();

    /// <summary>
    /// 当前目标是不是 UWP：手动形态看页签，只读形态看来源。
    /// </summary>
    /// <remarks>
    /// UWP 目标下三件事同时成立：① 身份只能是普通身份（D45 实测）；② 命令行参数不会被转发；
    /// ③ 没有工作目录可言。手动形态下这三件事由"停在哪个页签"决定，只读形态下由来源决定。
    /// </remarks>
    private bool UwpMode => _manualForm ? _uwpTabActive : _systemIsUwp;

    /// <summary>把弹窗当前内容收成一份编辑值，供 <c>ConfigEditService</c> 使用。</summary>
    /// <returns>编辑值。</returns>
    public DelayItemValues ToValues() => new()
    {
        DelaySeconds = _delaySeconds,
        RunAsAdmin = RunAsAdmin,
        Arguments = Arguments,
        Name = ItemName,
        Path = TargetPath,
        WorkingDirectory = WorkingDirectory,
    };

    /// <summary>四种形态共用的初始化：延时预设、身份胶囊、tab 面板、拖放接收。</summary>
    /// <param name="presets">延时预设值（秒）。</param>
    /// <param name="defaultPreset">默认预设（秒），用于无值时的预选兜底。</param>
    private void InitializeCommon(int[] presets, int defaultPreset)
    {
        CustomDelayBox.Maximum = FallbackMaxDelay;
        FillDelayPresets(presets, defaultPreset);

        // 支持的类型清单不在这里另抄一份（D47：LaunchTargetTypes 是唯一事实来源，
        // 界面上几处文案都跟着它走）。
        PickFileTypesText.Text = $"支持 {LaunchTargetTypes.DisplayList}";

        // 只读形态在构造函数后段才切面板，这里只需把身份胶囊与 tab 状态摆对。
        BuildIdentityPills();
        ApplyTabState();

        // 批复 4：拖放走经典 WM_DROPFILES（FileDropReceiver 子类化接收），
        // 弹窗打开时对弹层 HWND 再补一轮启用；收到文件路径后填进目标程序。
        Opened += OnDialogOpened;
        Closed += OnDialogClosed;
        FileDropReceiver.FilesDropped += OnFilesDropped;

        _initialized = true;
    }

    /// <summary>弹窗已打开：此时弹层 HWND 已创建，放行 UIPI 白名单 + 启用经典拖放。</summary>
    private void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_handles is not null)
        {
            UipiMessageFilter.AllowDragDropForTree(_handles.Handle);
            FileDropReceiver.EnableTree(_handles.Handle);
        }
    }

    /// <summary>弹窗关闭：退订全局拖放广播，避免残留订阅。</summary>
    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        FileDropReceiver.FilesDropped -= OnFilesDropped;
    }

    /// <summary>经典拖放收到文件（批复 4）：取第一个受支持的填进「程序」页签。</summary>
    private void OnFilesDropped(string[] paths)
    {
        // 只读形态（目标由系统项绑定）不接受更换 —— 静默忽略。
        if (!_manualForm || paths.Length == 0)
        {
            return;
        }

        var path = Array.Find(paths, static candidate => LaunchTargetTypes.IsSupported(candidate));
        if (string.IsNullOrEmpty(path))
        {
            ShowValidation("不支持的文件类型", $"只接受 {LaunchTargetTypes.DisplayList}。");
            return;
        }

        // 拖进来的是文件 → 自动切回「程序」页签。用户拖文件就是要用文件启动，
        // 此时还停在「UWP 应用」页签的话，填进去的东西用户根本看不见。
        SelectTab(uwp: false);
        ApplyPickedFile(path);
    }

    /// <summary>填充延时预设胶囊（一行多个，选中 = 当前延时）。</summary>
    private void FillDelayPresets(int[] presets, int defaultPreset)
    {
        // 调用方传空（配置损坏兜底）时用内置默认列表 —— 取自 Settings，不在这里另抄一份。
        var usable = presets is { Length: > 0 } ? presets : new Settings().DelayPresets;
        if (!usable.Contains(defaultPreset))
        {
            defaultPreset = usable[0];
        }

        foreach (var preset in usable)
        {
            DelayChoices.Items.Add(
                CreatePill(DisplayText.DelayOf(preset), preset, preset == defaultPreset, OnDelayPillChecked));
        }
    }

    /// <summary>
    /// 填充启动身份胶囊（普通身份 / 管理员，一行两个）。
    /// </summary>
    /// <remarks>
    /// D45：UWP 条目<b>不给</b>「管理员」胶囊 —— 真机实测 UWP 进程恒为普通用户身份，
    /// 选了管理员只会得到一个名不副实的条目（调度端会按普通用户启动并记一条说明）。
    /// 让用户在界面上就选不出来，比事后在日志里解释更好。
    /// D46/D53：目标可在两页签间来回换，故本方法可重复调用（先清空再填）。
    /// </remarks>
    private void BuildIdentityPills()
    {
        IdentityChoices.Children.Clear();

        IdentityChoices.Children.Add(
            CreatePill("普通身份", "normal", isChecked: false, OnIdentityNormalChecked));

        _adminPillShown = !UwpMode;
        if (_adminPillShown)
        {
            IdentityChoices.Children.Add(
                CreatePill("管理员", "admin", isChecked: false, OnIdentityAdminChecked));
        }

        PaintIdentity();
    }

    /// <summary>按"当前时间点该不该有管理员胶囊"决定重建还是只重画选中态。</summary>
    private void SyncIdentityPills()
    {
        if (_adminPillShown != !UwpMode)
        {
            BuildIdentityPills();
            return;
        }

        PaintIdentity();
    }

    /// <summary>把 <see cref="_wantAdmin"/> 画到胶囊上（UWP 目标下恒为普通身份）。</summary>
    private void PaintIdentity()
    {
        var admin = RunAsAdmin;

        _suppressSync = true;
        foreach (var child in IdentityChoices.Children)
        {
            if (child is ToggleButton { Tag: string tag } pill)
            {
                pill.IsChecked = admin ? tag == "admin" : tag == "normal";
            }
        }

        _suppressSync = false;

        UpdateIdentityHint();
    }

    /// <summary>是不是 UWP 目标：UWP 来源，或路径已经是 <c>shell:AppsFolder\…</c> 解析名。</summary>
    private static bool IsUwpSource(StartupSource source, string? path)
        => source == StartupSource.Uwp || UwpParsingName.IsParsingName(path ?? string.Empty);

    /// <summary>造一个胶囊按钮。</summary>
    /// <remarks>
    /// 先设 <c>IsChecked</c> 再订阅 <c>Checked</c>：初始选中态不应触发"用户选择"的逻辑。
    /// Margin 统一右 8 下 8 —— 与延时胶囊 / 设置页胶囊保持一致的固定间距（二轮 bug 批复 4）。
    /// </remarks>
    private static ToggleButton CreatePill(string text, object tag, bool isChecked, RoutedEventHandler onChecked)
    {
        var pill = new ToggleButton
        {
            Content = text,
            Tag = tag,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 8),
            IsChecked = isChecked,
        };

        pill.Checked += onChecked;
        return pill;
    }

    /// <summary>把 <paramref name="source"/> 以外的胶囊全部弹起（单选语义）。</summary>
    private void MakeExclusive(System.Collections.IEnumerable pills, ToggleButton source)
    {
        _suppressSync = true;
        foreach (var pill in pills)
        {
            if (pill is ToggleButton button && !ReferenceEquals(button, source))
            {
                button.IsChecked = false;
            }
        }

        _suppressSync = false;
    }

    /// <summary>摆好只读形态的目标卡片（系统项绑定，改名 / 改路径都不允许）。</summary>
    /// <param name="name">条目名。</param>
    /// <param name="path">目标路径（UWP 是 AUMID）。</param>
    /// <param name="sourceKey">来源键，路径为空时用它兜底显示。</param>
    /// <param name="source">来源。</param>
    /// <param name="scope">作用域。</param>
    private void ShowReadOnlyTarget(
        string name,
        string path,
        string sourceKey,
        StartupSource source,
        StartupScope scope)
    {
        TargetNameText.Text = name;
        TargetPathText.Text = BuildTargetPathText(source, path, sourceKey);
        TargetSourceText.Text = $"{DisplayText.SourceOf(source)} · {DisplayText.ScopeOf(scope)}";

        // 🔴 提交用的路径始终是**原始值**，不是上面那行给人看的文案。
        _readOnlyPath = path;
    }

    /// <summary>只读卡片里的"路径"行：UWP 显示成解析名，让人一眼看出它不是文件。</summary>
    private static string BuildTargetPathText(StartupSource source, string path, string sourceKey)
    {
        if (source == StartupSource.Uwp)
        {
            var parsingName = UwpParsingName.Build(path);
            return parsingName.Length > 0 ? $"UWP 应用 · {parsingName}" : "UWP 应用";
        }

        return string.IsNullOrWhiteSpace(path) ? sourceKey : path;
    }

    /// <summary>切到"目标程序只读"形态（系统条目：路径由系统绑定，不可更改）。</summary>
    /// <remarks>
    /// 名称 / 参数 / 工作目录三项的可见性不在这里写死 —— 交给
    /// <see cref="ApplyTargetFieldVisibility"/> 按"形态 + 当前页签"统一算（D59 修正）。
    /// </remarks>
    private void UseReadOnlyTarget()
    {
        ReadOnlyTargetPanel.Visibility = Visibility.Visible;
        PickTargetPanel.Visibility = Visibility.Collapsed;

        ApplyTargetFieldVisibility();
    }

    /// <summary>切到"目标可更换"形态（手动条目）：面板由页签决定。</summary>
    private void UsePickTarget()
    {
        ReadOnlyTargetPanel.Visibility = Visibility.Collapsed;
        PickTargetPanel.Visibility = Visibility.Visible;

        ApplyTabState();
    }

    /// <summary>
    /// 按"形态 + 当前页签"决定名称 / 命令行参数 / 工作目录三项的可见性。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>D59（2026-09-20 本轮修正，缺陷修复）</b>：三项的可见性原先散在
    /// <c>UseReadOnlyTarget</c> / <c>UsePickTarget</c> 里写死，而 D53 之后它们被摆进了
    /// 「程序」页签的面板 —— 系统条目走的是只读卡片、那个面板整体折叠，于是"接管系统条目时
    /// 能填参数与工作目录"这条需求**静默失效**（对折叠的父面板写 <c>Visibility</c> 不起作用，
    /// 而且不会有任何报错）。本轮把参数与工作目录移到页签之外，可见性也收敛到这一处。
    /// </para>
    /// <para>
    /// 规则：**名称**只在"手动条目 + 「程序」页签"出现（系统条目的名字由系统项决定）；
    /// **参数与工作目录**在**除 UWP 之外**的所有情况都出现 —— UWP 参数不转发、也没有工作目录
    /// 可言（D53/D54），而非 UWP 的系统条目必须能填（bug#5：有些程序依赖工作目录找配置）。
    /// </para>
    /// </remarks>
    private void ApplyTargetFieldVisibility()
    {
        NameBox.Visibility = _manualForm && !_uwpTabActive ? Visibility.Visible : Visibility.Collapsed;

        var files = UwpMode ? Visibility.Collapsed : Visibility.Visible;
        ArgumentsBox.Visibility = files;
        WorkingDirBox.Visibility = files;
    }

    /// <summary>按当前页签切换面板、页签选中态与身份胶囊（手动形态专用；只读形态直接返回）。</summary>
    private void ApplyTabState()
    {
        if (!_manualForm)
        {
            return;
        }

        ProgramTabPanel.Visibility = _uwpTabActive ? Visibility.Collapsed : Visibility.Visible;
        UwpTabPanel.Visibility = _uwpTabActive ? Visibility.Visible : Visibility.Collapsed;

        // 页签自身的选中态也一并写：编辑既有 UWP 条目时若只切面板，
        // "看得见的选中项"与"真正生效的目标"会对不上。
        // 写 IsChecked 会同步触发 Checked / Unchecked —— 靠 _suppressSync 挡住重入。
        _suppressSync = true;
        TargetTabProgram.IsChecked = !_uwpTabActive;
        TargetTabUwp.IsChecked = _uwpTabActive;
        _suppressSync = false;

        ApplyTargetFieldVisibility();
        SyncIdentityPills();
    }

    /// <summary>切页签（含程序化切换：拖放落文件、编辑既有条目时定位）。</summary>
    /// <param name="uwp">是否切到「UWP 应用」页签。</param>
    private void SelectTab(bool uwp)
    {
        _uwpTabActive = uwp;
        ApplyTabState();
    }

    /// <summary>用户点「程序」页签。</summary>
    private void OnProgramTabChecked(object sender, RoutedEventArgs e)
    {
        // 程序化改写 IsChecked（初始化 / 切换时）会同步回调进来，用 _suppressSync 挡住。
        if (!_initialized || _suppressSync)
        {
            return;
        }

        SelectTab(uwp: false);
    }

    /// <summary>用户点「UWP 应用」页签。</summary>
    private void OnUwpTabChecked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressSync)
        {
            return;
        }

        SelectTab(uwp: true);
    }

    /// <summary>「程序」页签被取消选中 —— 页签是单选，顶回去。</summary>
    private void OnProgramTabUnchecked(object sender, RoutedEventArgs e) => RestoreTabSelection();

    /// <summary>「UWP 应用」页签被取消选中 —— 页签是单选，顶回去。</summary>
    private void OnUwpTabUnchecked(object sender, RoutedEventArgs e) => RestoreTabSelection();

    /// <summary>
    /// 把两颗页签的选中态恢复成"当前页签被选中"。
    /// </summary>
    /// <remarks>
    /// <c>ToggleButton</c> 默认允许"再点一下取消选中" —— 不顶回去的话，用户点一下已选中的页签
    /// 就会得到"两颗都没选中、面板却还停在那一页"的矛盾状态。
    /// <see cref="ApplyTabState"/> 内的置位带 <c>_suppressSync</c>，不会与 <c>Checked</c> 打转。
    /// </remarks>
    private void RestoreTabSelection()
    {
        if (!_initialized || _suppressSync)
        {
            return;
        }

        ApplyTabState();
    }

    /// <summary>记录一次文件选择：显示已选文件，必要时把名称框填成文件名。</summary>
    /// <param name="path">选中的文件路径。</param>
    /// <remarks>
    /// 🔴 <b>D58（2026-09-20 用户批复）</b>：**不动工作目录框**。此前会把程序所在目录自动填进去，
    /// 用户看到框里已经有值就留着了 —— 但"留空"本身有明确语义（用程序所在目录），
    /// 自动填一个同样的值只是把"没设置"变成"设置成了同一个值"，还会在用户换了程序之后
    /// 留下一个陈旧路径。
    /// </remarks>
    private void ApplyPickedFile(string path)
    {
        _filePath = path;

        PickedNameText.Text = System.IO.Path.GetFileNameWithoutExtension(path);
        PickedPathText.Text = path;
        PickEmptyBorder.Visibility = Visibility.Collapsed;
        PickFilledBorder.Visibility = Visibility.Visible;

        // 用户手动改过名字就不覆盖（_nameWasAutoFilled 记的是"这个名字还是自动填的"）。
        if (_nameWasAutoFilled || string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(path);
            _nameWasAutoFilled = true;
        }
    }

    /// <summary>记录一次 UWP 应用选择（D46）：目标存**解析名**，显示名自带。</summary>
    /// <param name="displayName">应用显示名（来自系统应用清单）。</param>
    /// <param name="parsingName">AUMID 或已带前缀的解析名。</param>
    /// <remarks>
    /// 🔴 存解析名而不是裸 AUMID：调度端与图标链路都靠它认出这是 UWP
    /// （<c>UwpParsingName.IsParsingName</c>）。存裸 AUMID 会被当成普通 exe 走到
    /// <c>File.Exists</c> 判"目标文件不存在"（D41 已踩过一次）。
    /// 这里顺手把老条目的裸 AUMID 也归一成解析名。
    /// </remarks>
    private void ApplyPickedUwpTarget(string displayName, string parsingName)
    {
        _uwpPath = UwpParsingName.Build(parsingName);
        _uwpName = string.IsNullOrWhiteSpace(displayName) ? _uwpPath : displayName;

        PickedUwpNameText.Text = _uwpName;
        PickedUwpPathText.Text = $"UWP 应用 · {_uwpPath}";
        PickUwpEmptyBorder.Visibility = Visibility.Collapsed;
        PickedUwpBorder.Visibility = Visibility.Visible;

        SelectTab(uwp: true);
    }

    /// <summary>打开文件选择对话框选一个程序。</summary>
    /// <returns>选中的文件路径；用户取消时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 🔴 WinRT 的 <c>FileOpenPicker</c> 在提权进程里打不开（选择器 broker 拒绝高完整性
    /// 令牌，表现为"点击选择程序没反应"—— 2026-09-19 用户实测）。改用 Win32 的
    /// <see cref="Win32FilePicker"/>（记事本等系统提权程序用的同一套对话框）。
    /// 这是「手动添加」的**主路径**，拖放只是增强 —— 按 <c>architecture.md</c> 第九节，
    /// 这条路径必须 100% 可用。对话框自己泵模态消息循环，同步调用即可。
    /// </remarks>
    private Task<string?> PickProgramAsync()
    {
        if (_handles is null || _handles.Handle == 0)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(Win32FilePicker.PickFile(
            _handles.Handle,
            "选择延时启动的程序",
            LaunchTargetTypes.Extensions));
    }

    /// <summary>「程序」页签：拖入区域与「更换」共用的文件选择入口。</summary>
    private async void OnPickFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await PickProgramAsync();
            if (string.IsNullOrEmpty(path))
            {
                // 用户取消选择是正常路径；但 Handle 未登记时也走这里 —— 把这种情况
                // 显式说出来（bug#1：此前"点击没反应"就是它被静默吞掉）。
                if (_handles is null || _handles.Handle == 0)
                {
                    ShowValidation("无法打开文件选择器", "主窗口句柄尚未就绪，请关闭弹窗后重试；或直接把文件拖入本区域。");
                }

                return;
            }

            ApplyPickedFile(path);
        }
        catch (Exception ex)
        {
            // 文件选择器失败（权限 / COM 状态异常）不再静默：弹窗内直接给出原因，
            // 拖放路径仍可用 —— 弹窗打开时已对主窗口子树放行 UIPI 拖放消息。
            ShowValidation("文件选择器打开失败", $"{ex.Message}\n\n也可以直接把文件拖入上方区域。");
        }
    }

    /// <summary>「UWP 应用」页签：打开应用选择面板。</summary>
    private async void OnPickUwpApp(object sender, RoutedEventArgs e) => await ShowUwpPickerAsync();

    /// <summary>显示 UWP 应用选择面板（首次打开时读一次应用清单）。</summary>
    private async Task ShowUwpPickerAsync()
    {
        UwpFilterBox.Text = string.Empty;
        UwpPickerOverlay.Visibility = Visibility.Visible;

        await EnsureUwpAppsLoadedAsync();
    }

    /// <summary>懒加载 UWP 应用列表：WinRT 枚举包 → 后台提图标 → UI 线程建行（D46）。</summary>
    /// <remarks>
    /// 🔴 图标提取放后台：单个 ~5–15ms，几十个应用在 UI 线程串起来就是可见的卡顿；
    /// 像素数据是纯值对象（<c>IconPixels</c>），可以安全跨线程，只有
    /// <c>WriteableBitmap</c> 必须回 UI 线程建。
    /// </remarks>
    private async Task EnsureUwpAppsLoadedAsync()
    {
        if (_uwpAppsLoaded)
        {
            ApplyUwpFilter();
            return;
        }

        UwpPickerHintText.Text = "正在读取已安装的应用…";

        IReadOnlyList<UwpAppEntry> entries;
        try
        {
            entries = await UwpAppCatalog.ListAsync();
        }
        catch (Exception ex)
        {
            // 目录读取整体失败（非预期异常）：允许用户重开面板重试。
            UwpPickerHintText.Text = $"读取应用列表失败：{ex.Message}";
            return;
        }

        IconPixels?[] pixels;
        if (_icons is null)
        {
            pixels = [];
        }
        else
        {
            var provider = _icons;
            pixels = await Task.Run(() => entries
                .Select(entry => provider.TryGetIcon(UwpParsingName.Build(entry.AppUserModelId)))
                .ToArray());
        }

        var items = new List<UwpAppListItem>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            items.Add(new UwpAppListItem(entries[index], pixels[index]));
        }

        _uwpApps = items;
        _uwpAppsLoaded = true;
        ApplyUwpFilter();
    }

    /// <summary>按筛选词刷新列表显示。</summary>
    private void ApplyUwpFilter()
    {
        if (!_uwpAppsLoaded)
        {
            return;
        }

        var filtered = _uwpApps.Where(item => item.Matches(UwpFilterBox.Text)).ToList();

        UwpAppList.ItemsSource = filtered;
        UwpAppList.SelectedIndex = filtered.Count > 0 ? 0 : -1;

        UwpPickerHintText.Text = filtered.Count > 0
            ? $"共 {filtered.Count} 个应用"
            : _uwpApps.Count == 0
                ? "没有找到可启动的 UWP 应用。"
                : "没有匹配的应用，换个关键词试试。";
    }

    private void OnUwpFilterChanged(object sender, TextChangedEventArgs e) => ApplyUwpFilter();

    /// <summary>确认选择一个 UWP 应用。</summary>
    private void OnUwpPickConfirmed(object sender, RoutedEventArgs e)
    {
        if (UwpAppList.SelectedItem is not UwpAppListItem selected)
        {
            UwpPickerHintText.Text = "请先在列表中选中一个应用。";
            return;
        }

        ApplyPickedUwpTarget(selected.DisplayName, selected.ParsingName);
        UwpPickerOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>放弃选择，只收起面板（已读到的列表留在内存里复用）。</summary>
    private void OnUwpPickCancelled(object sender, RoutedEventArgs e)
        => UwpPickerOverlay.Visibility = Visibility.Collapsed;

    private void ShowValidation(string title, string message)
    {
        ValidationBar.Title = title;
        ValidationBar.Message = message;
        ValidationBar.IsOpen = true;

        // 提示条在内容区顶部：内容超高时把视口带回顶部，保证看得见（二轮 bug 批复 5）。
        ValidationBar.StartBringIntoView();
    }

    /// <summary>某个延时胶囊被选中。</summary>
    private void OnDelayPillChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSync || sender is not ToggleButton { Tag: int seconds } pill)
        {
            return;
        }

        MakeExclusive(DelayChoices.Items, pill);
        SetCustomDelayValue(seconds);
        _delaySeconds = seconds;

        // 选预设不算手动编辑：不需要二次确认。
        _manualDelayEdit = false;
    }

    private void OnIdentityNormalChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSync || sender is not ToggleButton pill)
        {
            return;
        }

        MakeExclusive(IdentityChoices.Children, pill);
        _wantAdmin = false;
        UpdateIdentityHint();
    }

    private void OnIdentityAdminChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressSync || sender is not ToggleButton pill)
        {
            return;
        }

        MakeExclusive(IdentityChoices.Children, pill);
        _wantAdmin = true;
        UpdateIdentityHint();
    }

    private void OnCustomDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressSync)
        {
            return;
        }

        if (double.IsNaN(args.NewValue))
        {
            // 用户清空输入框时的中间态：保留上一次的值，等他敲完。
            return;
        }

        _delaySeconds = (int)Math.Clamp(args.NewValue, 0, CustomDelayBox.Maximum);

        // 自定义值不再等于任何预设 → 弹起全部胶囊，避免"选了 30 秒却显示 45"的矛盾。
        SyncDelayPresetSelection();

        // 用户批复 6：手动编辑自定义延时后提交需要二次确认。
        _manualDelayEdit = true;
        _manualDelayConfirmed = false;
    }

    /// <summary>把延时设为指定值，同步胶囊选中态与自定义输入框。</summary>
    /// <param name="seconds">延时秒数。</param>
    private void SetDelay(int seconds)
    {
        _delaySeconds = seconds;
        _manualDelayEdit = false;
        _manualDelayConfirmed = false;
        SetCustomDelayValue(seconds);
        SyncDelayPresetSelection();
    }

    private void SetCustomDelayValue(int seconds)
    {
        _suppressSync = true;
        CustomDelayBox.Value = seconds;
        _suppressSync = false;
    }

    /// <summary>让延时胶囊组反映当前延时值；无匹配时全部弹起。</summary>
    private void SyncDelayPresetSelection()
    {
        _suppressSync = true;
        foreach (var item in DelayChoices.Items)
        {
            if (item is ToggleButton { Tag: int preset } pill)
            {
                pill.IsChecked = preset == _delaySeconds;
            }
        }

        _suppressSync = false;
    }

    /// <summary>设置启动身份（记为"想要的"身份，UWP 目标下由 <see cref="RunAsAdmin"/> 归一）。</summary>
    /// <param name="runAsAdmin">是否以管理员身份启动。</param>
    private void SetIdentity(bool runAsAdmin)
    {
        _wantAdmin = runAsAdmin;
        SyncIdentityPills();
    }

    /// <summary>
    /// 身份卡的一句话说明（2026-09-21 批复：只定义功能，不再按选择摆操作指导）。
    /// </summary>
    /// <remarks>
    /// UWP 目标下说明**为什么只有一颗胶囊**；其余情况一句话定义"这是干什么的"。
    /// 原先按普通 / 管理员 / UWP 三态各写一段的文案已删 —— 胶囊选中态本身已在表达选择。
    /// </remarks>
    private void UpdateIdentityHint()
    {
        IdentityHintText.Text = UwpMode
            ? "UWP 应用由系统外壳激活，仅支持以普通用户身份启动。"
            : "本条目启动时使用的 Windows 账户身份。";
    }

    /// <summary>确认面板上的「确认使用」：确认后按已确认收尾。</summary>
    private void OnConfirmDelayAccepted(object sender, RoutedEventArgs e)
    {
        _manualDelayConfirmed = true;
        ConfirmOverlay.Visibility = Visibility.Collapsed;

        // Hide() 的结果值是 None；调用方用 DelayConfirmed 属性区分"确认提交"与"取消"。
        Hide();
    }

    /// <summary>确认面板上的「返回修改」：回到编辑状态。</summary>
    private void OnConfirmDelayRejected(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 校验是同步的，不需要 GetDeferral —— 取了 deferral 就必须保证每条路径都 Complete，
        // 在这里只会增加"某条分支忘记 Complete 导致弹窗卡死"的风险。

        // 选择面板还开着时按提交：先收起面板并取消提交 —— 此时列表里选中的应用还没确认，
        // 直接提交会用上一个目标（甚至空目标）建条目，用户会以为"选了没用"。
        if (UwpPickerOverlay.Visibility == Visibility.Visible)
        {
            UwpPickerOverlay.Visibility = Visibility.Collapsed;
            args.Cancel = true;
            return;
        }

        // 目标校验只看**当前页签**的目标：两个页签各存各的，互不覆盖。
        // 文案按原型 v3 的校验条（2026-09-21 批复）。
        if (_manualForm && string.IsNullOrWhiteSpace(TargetPath))
        {
            ShowValidation(
                "还没有选择目标",
                "请先选择要延时启动的程序或 UWP 应用。");
            args.Cancel = true;
            return;
        }

        if (_delaySeconds < 0)
        {
            ShowValidation("延时值不合法", "延时不能为负数。");
            args.Cancel = true;
            return;
        }

        // 用户批复 6：手动编辑的自定义延时在提交前需要二次确认。
        if (_manualDelayEdit && !_manualDelayConfirmed)
        {
            ConfirmText.Text = $"将使用自定义延时 {DisplayText.DelayOf(_delaySeconds)}（不在预设列表里）。确定采用吗？";
            ConfirmOverlay.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
    }
}
