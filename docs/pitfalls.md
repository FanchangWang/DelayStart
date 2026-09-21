# DelayStart — 踩坑大全

> **写相关代码前逐条看完。** 每条都带着"为什么会错、正确做法是什么"，都是真机上踩出来的。
> 坑 1–10 编号沿用（代码注释按编号引用）；后续新踩的坑必须追加进本文。

---

## 一、注册表与启动文件夹

- **坑 1（键名三级回退）**：任务管理器写 `StartupApproved` 标记用的名字不保证与 `Run` 值名一致 → 匹配必须依次试 `原名 → 原名+.exe → 去扩展名`，三次未命中才算"未禁用"。不做回退会把已禁用项误报为启用。
- **坑 2（Run32 只对 WOW6432Node 生效）**：对 32 位项误写 `Run` 键完全失效且无报错。
- `StartupApproved` 标记为 12 字节 REG_BINARY：`[0]=0x03` 禁用 / `[1..3]=0` / `[4..11]=FILETIME`；启用 = `DeleteValue`，原 `Run` 值从未动过。
- Run 值解析：带引号取到第二个 `"`；否则按第一个空格切，**仅当后半段以 `/` 或 `-` 开头才认定是参数**（防 `C:\Program Files\...` 被切坏）。
- 全部按 `RegistryView.Registry64` 打开；WOW6432Node 走**显式子键路径**，不用 `RegistryView.Registry32`。
- 启动文件夹扫 `*.lnk`/`*.url`；`.lnk` 目标解析用 `IShellLinkW` + `IPersistFile`。

## 二、计划任务与调度端

- **坑 3（GPO/系统任务改不动）**：`Regenerate` / GPO 下发、`\Microsoft\Windows\*` 下的任务改不动或会被还原 → 过滤或标记只读。
- **坑 7（时序语义）**：延时是相对登录时刻的**绝对时间点**。"A=30s、B=30s 要求 A 先完成启动"不可靠——只保证发起顺序；UI 必须提示有依赖用不同延时值。
- **坑 8（会话 ID）**：优先 `WTSGetActiveConsoleSessionId()`，失败退 `Process.GetCurrentProcess().SessionId`。
- 🔴 **`t.Path` ≠ `t.Folder?.Path`**：`Path` = 文件夹+任务名。受保护判据必须写 `@"\Microsoft\"`（**带尾分隔符**）——少一个字符就把"名字带 Microsoft"的第三方任务（Edge 更新）整批静默滤掉（D66，16 例单测钉住）。
- 🔴 **禁用粒度（D67）**：`task.Enabled=false` 是任务级开关，会连带关掉同一任务的所有触发器。规则：任务级已关 → 一个字节不动；触发器 ≤1 → 切任务级；>1 → **全部**切登录/启动触发器（只切第一个 = 程序照常自启 + 我们再拉一次 = 启动两次）；启用先开任务级再开触发器。`Trigger.Id` 普遍为空串，**不能**当记账标识。改动无变化时不调 `RegisterChanges()`（会刷新时间戳、触发 RegistrationTrigger）。
- 🔴 **计划任务身份**：必须交互用户 + `RunLevel=Highest`；**绝不能 SYSTEM**——`%APPDATA%` 解析到 systemprofile，配置读不到、日志写错位置且不报错。`schtasks /delay`、`/ru SYSTEM` 都是雷；用 `TaskService` API 创建。
- 包名是 **`TaskScheduler` 2.12.2**；`Microsoft.Win32.TaskScheduler` 是 2016 年死包，.NET 10 不可用。
- 降权（D40 七方案对照）：直接交 explorer 进程令牌必 `Win32Error=5`；`CreateProcessAsUserW`=1314；**COM `Shell.Application.ShellExecute` 不降权**（网上技巧实测不成立，禁用）。正解 = 外壳令牌 → `DuplicateTokenEx` → `CreateProcessWithTokenW`；失败**判失败继续，绝不提权回退**。`CREATE_UNICODE_ENVIRONMENT|CREATE_NEW_CONSOLE`；`LibraryImport` 需要 `AllowUnsafeBlocks`。
- 🔴 **uiAccess="true" 目标（如 Quicker）降权启动必失败 740，且无 Medium 降权路径**（demo2 AppA 2026-09-21 实测）：目标清单生效行 `asInvoker uiAccess="true"`（注意 `requireAdministrator` 常只是清单注释模板，别被字符串匹配骗了）。`uiAccess` 生效四前提：签名 + 安全位置 + **调用方持 SeTcbPrivilege** + **对复制令牌显式 `SetTokenInformation(TokenUIAccess)`**——CPWT 不会自动把清单声明应用到复制的令牌；缺任一前提 `CreateProcessWithTokenW/AsUser` 一律报 `ERROR_ELEVATION_REQUIRED(740)` 且不指明缺哪个。SeTcb 只有 SYSTEM 有 ⇒ 管理员进程走令牌路线永远修不了。explorer 委托"成功"也不是 Medium：shell 走 AppInfo 服务（RAiLaunchAdminProcess）对合规 uiAccess 目标**静默把 IL 提到 High**（受限管理员 → 0x3000，无 UAC 弹窗）——"子进程 High"不是目标自我提权，是系统策略。定性：uiAccess 目标**不存在降权到 Medium 的路径**，740 应特判为"目标是 UIAccess 程序"而非管道 bug。权威依据（Forshaw，Google Project Zero 2026-02 + MS "Security Considerations for Assistive Technologies"）：`SetTokenInformation(TokenUIAccess)` 需 **SeTcbPrivilege（仅 SYSTEM）**；AppInfo(RAiLaunchAdminProcess) 是唯一认可通道且**必须**顺带抬 IL——受限管理员→High，非管理员用户→medium+（仍无法操作 High UI），**"Medium+UIAccess"这个组合在 Windows 里不存在**；普通 CreateProcess 不经 AppInfo，进程能起但 UIAccess 标志静默丢失（辅助功能降级）。
- 🔴 **SHELLEXECUTEINFOW 的 `dwHotKey` 后还有 `union { hIcon; hMonitor; }`，然后才是 `hProcess`**（2026-09-21）：x64 下 `sizeof` 必须是 **112**；漏掉联合体的布局是 104——`cbSize` 传错 + `SEE_MASK_NOCLOSEPROCESS` 下原生越过 104 字节缓冲写 hProcess（堆越界）。必须在调用前做 ABI 自检（非 112 拒绝调用）。诊断价值：失败时 `hInstApp` 带 SE_ERR_*（0..32），可直接区分 FILE_NOT_FOUND / ACCESS_DENIED 等。
- 🔴 **LaunchBroker 调 `ShellExecuteEx` 前必须 `CoInitializeEx(NULL, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE)`**（2026-09-21 首测报 `Win32Error=5`）：shell 函数要求调用线程是 **STA COM**——MSDN ShellExecuteEx Remarks 明文要求；未初始化/MTA 时报 `ERROR_ACCESS_DENIED(5)`（Raymond Chen "One possible reason why ShellExecute returns SE_ERR_ACCESSDENIED" + KB287087）。uiAccess 目标启动握手走 COM 激活路径必踩。⚠️ 沙箱/受限环境下 ShellExecuteEx 对**不存在的文件**也报 5（正常应 2），冒烟结果不可信，验证必须实机。
- 🔴 **面板倒计时与退出时机耦合（UI v2，2026-09-21）**：完成态"关闭面板 = 退出进程"，于是退出不能再由定时器单独决定——`Finish()` 判定要弹面板时置 `_awaitingPanelClose`，`Tick()` 里的 `_quitAt` 判断必须让位，否则倒计时没走完进程先退了；鼠标 hover 暂停期间同样不能退（暂停时长要计入）。退出入口做成幂等（`_quitRequested`）：面板倒计时归零、手动 ✕、菜单「退出」三条路径可能重复触发。
- **面板自绘的两个小坑**：① 面板自己的 1 秒 `WM_TIMER` 用 **id=2**（托盘宿主用 id=1），两个窗口各收各的不会串，但 `Hide()` 必须 `KillTimer`，否则隐藏后仍在倒数并触发退出；② GDI 画空心图形（等待态状态点）要用 `GetStockObject(NULL_BRUSH=5)`——选入实心刷再 `Ellipse` 画出来是实心点，注释写"空心"而代码画实心是自欺。`RoundRect` 用当前 brush 填充 + 当前 pen 描边，两个都得选入。
- **接管与恢复语义（坑 5、坑 6）**：坑 5——`scope` 必须显式枚举，靠 `source_detail.StartsWith("HKLM")` 猜 hive 会静默写错位置；坑 6——"是否已接管"必须用稳定主键 `source:scope:sourceKey`，`(Name, Source)` 二元组同名即误判。全部动作可逆软禁用，`originalState.wasEnabled` 驱动精确还原（默认 true 安全侧）。

## 三、UWP

- **坑 4（延迟激活被拒）**：UWP 自启动由系统 AppModel 调度，第三方无法延后；语义只有 `State=0/2`。UI 文案必须写明"延时启动 UWP = 禁用系统自启 + 到点激活，会绕过系统启动管理"。
- 🔴 **`StartupTask.TaskId` ≠ `Application.Id`**（D41）：注册表子键名是 TaskId，正确 AUMID 需要 `UwpAppIdResolver` 读 `AppxManifest.xml` 建 TaskId→AppId 映射；解析不出沿用 TaskId。
- 配置里存**解析名** `shell:AppsFolder\<AUMID>`（不是裸 AUMID）——裸 AUMID 会被 `File.Exists` 判"目标不存在"（D41/D43 两次踩）。
- **UWP 进程恒为普通用户身份**（D45 真机实测）：中转 explorer 用谁的令牌都不改结果；编辑器不提供管理员胶囊。涉令牌/权限的结论必须测到进程完整性级别。
- 显示名：`SplashScreen\<AUMID>\AppName` → `SHLoadIndirectString` → 兜底包名前缀（MrtCache 反查未实现，可读兜底已够）。
- 手动添加 UWP：走 `PackageManager.FindPackagesForUser("")` → `GetAppListEntriesAsync()`（唯一能同时拿显示名与真实 AUMID 的通道）；`AppListEntry` 投影里不是可命名类型（CS0234），只能 `var`。

## 四、构建工具链与编译器

- 🔴 **XamlCompiler pass-1 静默崩溃**（2026-09-21 实锤，二分定位到单个 XAML 文件）：退出码 1、零诊断输出、`output.json` 不重写（残留旧错误）、事件日志无记录、手动运行同样静默。**增量缓存的 .g.cs 会污染对照实验**——二分必须 `dotnet clean` + `--no-incremental`。修复 = 改写触发崩溃的 XAML 写法（本案：垃圾桶裸 TextBlock + Pointer 事件；回退嵌套 Button 即过）。
- 🔴 **WMC1506**：对非 INPC 属性写 `Mode=OneWay` 是编译错误（`TreatWarningsAsErrors` 下）。不可变行模型用 OneTime；get-only 计算属性要联动用 `[NotifyPropertyChangedFor]`（只删通知联动不删 OneWay = 必崩）。
- `dotnet test` 不可用（D26）：xunit.v3.mtp-v2 + SDK 10 报"零个测试"退出码 5；规范命令 `dotnet run --project tests/DelayStart.Core.Tests -c Release`。
- 先建 CPM 再跑模板 → `dotnet add package` 报 NU1008；props 的 XML 注释里出现 `--` 是非法 XML → 整个 CPM 静默失效（NU1015）。
- AOT 链接 `LNK1181 找不到 advapi32.lib`：csproj 显式注入 Windows SDK `um/ucrt` 库目录（按 `$(_WinSdkLibArch)` 切架构）。
- 构建报 `MSB3021/3027`：正在运行的管理端锁住了 bin 里的 DLL → 构建前先关。
- XamlCompiler 与 targets 由 NuGet 包提供，命令行构建不依赖 VS 组件（组件属体验项）。

## 五、NativeAOT 硬约束

- **坑 9（bool 封送）**：`LibraryImport` 的 bool 返回值必须显式 `[return: MarshalAs(UnmanagedType.Bool)]`——不写不报错、只出错值，最难查的一类。
- 🔴 **坑 10（P/Invoke 模块归属）**：`FillRect` 是 **user32** 的导出（winuser.h），声明到 gdi32 编译期毫无征兆，运行时 `EntryPointNotFoundException`。在 `UnmanagedCallersOnly` 窗口回调里抛出时异常无法穿越原生帧展开 → 运行时 fail-fast（`0xC0000409`）整个进程闪退，Main 的 try/catch 接不到（2026-09-21 托盘左键闪退真机实锤）。防线：① GDI 声明逐个核对模块归属（FillRect/DrawTextW/BeginPaint 等 user32；CreateSolidBrush/SelectObject/Ellipse 等 gdi32）；② `WndProcThunk` 整体 try/catch 兜底 + 日志钩子（`MessageCallbackExceptionLogger`），异常落地为日志而非进程消失；③ 此类崩溃查事件日志 ID 1000/1001（异常码 0xC0000409 = 托管异常冲出原生回调，非原生 AV）。
- 🔴 **AOT 窗口回调闪退的定位捷径**：不必上调试器——把涉及的 P/Invoke 类原样链接进一个 CoreCLR 控制台程序直接调用，EntryPointNotFoundException/Marshalling 错误当场原样抛出（CoreCLR 能展开异常，AOT 只剩 fail-fast）。
- WinForms/WPF + PublishAot 被 SDK 拦截（NETSDK1175）；放行靠内部属性 = 押注灰色地带 → 已决策纯 Win32（D24）。约束条款继续生效：禁 `.resx` 反射资源加载、`DataGridView`、`RichTextBox`、动态 COM、`System.Reflection`。
- AOT 无 built-in COM：`Marshal.GetObjectForIUnknown` / `[ComImport]` 激活在 AOT 产物里运行时必抛 `PlatformNotSupportedException`（R12）。
- 🔴 **`InvariantGlobalization=true` = 不存在任何 culture**：`TaskScheduler` 静态构造器 `CreateSpecificCulture` → 注册计划任务 100% 崩（R13，真机实测）。且该开关写入 runtimeconfig，环境变量翻不回来。Windows 上体积代价 ≈ 0（用系统 icu.dll）。
- 其余：`OutputType=WinExe`（Exe 闪黑框）、JSON 源生成、绑定手工赋值、`[GeneratedRegex]`、新增包先发一次 AOT 看 IL2026/IL3050。
- `[ComImport] class` 不能显式转接口（CS0030）→ `CoCreateInstance` + `GetObjectForIUnknown`（仅 Management 层允许）。

## 六、WinUI 3 XAML 与运行时

- **嵌套 Button 的前景被模板钉死**：Button 模板把 `ContentPresenter.Foreground` 设为主题主文本色，胶囊选中态的前景色传不进嵌套内容（浅色主题选中=白字但图标黑、深色反之）；硬编码白色只能凑对一个主题，悬停覆盖也会被 PointerOver 视觉态抢回。
- 🔴 **`Page` 不是 `ObservableObject`**：页面自身属性不能 OneWay（WMC1506），页面级派生文案一律放 ViewModel。
- `x:Bind` 表达式里写不了字符串三元式（`{x:Bind Flag ? '是' : '否'}`）——pass-1 直接崩溃且不落错误详情；换计算属性。
- `x:Bind` 不做 `bool→Visibility` 隐式转换（要 IValueConverter）；取反布尔写不了表达式，行模型补镜像属性；`TextBox.Text` 双向绑定逐键过滤需 `UpdateSourceTrigger=PropertyChanged`。
- 被 XAML/容器解析的类型必须 public（CS0051）；`ContentDialog.ShowAsync` 无 `ConfigureAwait`（CS1929）。
- `SelectorBar`/`SelectorBarItem`（WinAppSDK 1.5+）可用，但 XAML 解析期 `SelectionChanged` 早于其余字段就绪 → 用 `_initialized` 挡，否则 NRE。
- `ToggleButton` 默认"再点取消选中"——做分段页签必须在 `Unchecked` 里顶回去。
- **ContentDialog 之上不能叠第二个 ContentDialog**——二次确认/选择面板一律做同层 overlay。
- `AppWindow` 的单位是**物理像素**：默认尺寸必须按 DPI 换算（150% 下直接写 1400 得到半屏窗口）；最小尺寸走 `WM_GETMINMAXINFO` 子类化（`MINMAXINFO` 只声明 `ptMinTrackSize`，全写触发 CS0649）。
- 提权进程：WinRT `FileOpenPicker` 打不开 → `Win32FilePicker`；UIPI 拦 explorer 拖放 → `ChangeWindowMessageFilterEx` 放行三条消息 + 旧式 `WM_DROPFILES`；子类化窗口过程可叠加（按 HWND 记账、只转发不卸载）。
- `WriteableBitmap.PixelBuffer` 的 `CopyTo` 走 CsWinRT 的 `WindowsRuntimeBufferExtensions`；图标经 `GetDIBits` 负高度读 32bpp 保 alpha（`Bitmap.FromHbitmap` 丢 alpha）。
- `Foreground` 绑 null 会切断依赖属性继承链 → 文字不可见（拆互斥 TextBlock，其一不设 Foreground 沿用默认）。
- **可见性判据写在会被父级整体折叠的子树里等于没写**（D59）——这类失效不报错。

## 七、发布与安装产物

- 🔴 **bin 能跑 ≠ publish 能跑**：unpackaged 工程删掉 `EnableMsixTooling` 后，`*.xbf` / `<主模块名>.pri` / `Assets\*.ico` **不会进 publish 输出**，装出来 `Microsoft.UI.Xaml.dll` `0xc000027b` 秒崩。防线：csproj `CopyWinUIResourcesToPublishDir` + 构建期断言。资源包名必须是 `DelayStart.pri`（主模块名），不是 `resources.pri`。**安装器形态的验收必须装进真实目录跑，不能用 bin 代替。**
- 🔴 **两形态混装（hostfxr）**：apphost 在自己目录看到 `hostfxr.dll` 就把"运行时根"当程序目录 → 框架依赖版报"必须安装 .NET"（哪怕装着 10.0.12）。修法 = 装前 `PrepareToInstall` 清空 `{app}`（只清程序文件、保留 `unins*`、探测先于清理、唯一中止条件 = 精简版 `hostfxr.dll` 删不掉）。
- **Restart Manager 不会自动重启被关进程**（只对调用过 `RegisterApplicationRestart` 的生效）→ 安装时调度端被关，托盘图标下次登录才回来。**已定性维持现状，禁止"管理端启动调度端"**。
- 图标：`<Content>` 管"文件随发布"（运行期 `SetIcon` 用）、`<ApplicationIcon>` 管"写进 PE 资源"（快捷方式/任务栏/卸载图标）——**两者都要有**，漏 `<ApplicationIcon>` 全线空白图标。调度端同理（D63）。
- ICO 容器：Pillow `save(format="ICO")` 全尺寸写 PNG 条目，小尺寸在缩略图等外壳路径会空白 → 手写混合容器（≥96 PNG、16–48 DIB）；多尺寸 ICO **不能"取第一个条目"**（首条 16×16 + `LR_DEFAULTSIZE` = 两次重采样），按目标尺寸挑条目并显式传 cx/cy；托盘底色取 32 而非 `SM_CXSMICON`（无 DPI 声明时恒 16）。
- 🔴 **CI 里调 `gh` 的 job 必须 `actions/checkout`**（2026-09-22 v0.1.0 首次真跑暴露）：`release` job 只 `download-artifact`（想省掉检出），而 `gh release create` 靠当前目录的 `.git` 判定目标仓库 → `failed to run git: fatal: not a git repository`，`--generate-notes` 同样无从取提交历史。修法：加一步 `actions/checkout@v4`，或给 job 设 `GH_REPO` / 命令加 `--repo`。**这个坑能藏很久**：该 job 带 `if: startsWith(github.ref, 'refs/tags/')`，此前两次都是 `workflow_dispatch` 触发、整段被跳过 —— "从来没跑过"和"一直好着"在日志里长得一模一样，只有真走 tag 那条路才暴露。⇒ **新加的触发路径必须实跑一次才算数。**
- 🔴 **`gh run rerun` 用的仍是原 run 那次 commit 的 workflow 文件**：改了 `.github/workflows/*.yml` 之后 `--failed` 重跑，跑的还是**旧定义**（GH 固定沿用原 run 的 `GITHUB_SHA` + `GITHUB_REF`）。要验证 workflow 改动只能重新触发：移动 tag 重推、或新提交推 main 后再打 tag。

## 八、WinUI 3 模板与工程创建

- 🔴 **官方模板只生成 MSIX 打包工程，没有任何 unpackaged 开关**（`Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` 0.0.6-alpha）；生成后需手工改造 csproj 并删 `Package.appxmanifest`。模板缓存是用户级全局的。
- 模板残留清理清单：`Package.appxmanifest` + 徽标 PNG + `.pubxml`、模板页 `Pages/`、`Form1.*`、`Class1`/`UnitTest1`；`Microsoft.Windows.SDK.BuildTools.WinApp` 包 unpackaged 用不上可移除。
- 测试项目 TFM `net10.0-windows` 即可——"必须匹配 WinUI TFM + SelfContained + 预装 Runtime"那套只适用于**直接引用 WinUI 3 项目**的测试，本项目不适用（分层收益）。

## 九、安装器（Inno Setup）

- 🔴 **任何一行不能以 `[` 开头**（含 `[Code]` 段内、含缩进后的 `[`）——ISCC 报 `Invalid section tag`，哪怕那行是合法的 Pascal 数组字面量。
- `TaskDialogMsgBox` 第 5 参 `Shields` 是集合类型，传整数报 `Type mismatch` → 改 `MsgBox` + `MB_YESNOCANCEL`。
- **不要重复声明 `FILE_ATTRIBUTE_DIRECTORY`**——Inno Pascal 自带（`Duplicate identifier`）。
- 🔴 **提权两条（同一根因踩两次，740）**：`DelayStart.exe` manifest = requireAdministrator 而安装器 = lowest → `[Run]` 首启必须 `shellexec`；卸载还原必须 `ShellExec('runas')` + `--result-file` 轮询回读退出码（`ShellExec` 拿不到子进程退出码；`ewWaitUntilTerminated` 不可靠）。`[UninstallRun]` 读不到退出码，还原失败也照删文件 → 不能用。
- **iss 的 `/DSlim` 判据是"有没有定义"**，不要传 `/DSlim=0` 表"否"。
- **中文 .isl 不随官方 Inno 分发**（属用户贡献翻译）→ 随仓库分发 `installer\languages\ChineseSimplified.isl`，iss 写 `compiler:Default.isl,languages\ChineseSimplified.isl`（相对路径按 **.iss 所在目录**解析；垫 Default.isl 消"缺 message"警告；语言文件版本错位只警告不失败）。
- 语言/版本比较别用 `Copy(s,1,27)` 对 31 字符串——恒为假，用 `Pos(...)>0`。
- 「设置→应用」显示名取 `AppVerName` 而非 `AppName`；固定 `AppVerName={#AppName}` 只显示裸名。
- 静默安装不自动勾选任何任务：要桌面图标必须 `/MERGETASKS="desktopicon"`；静默卸载删数据一律按"否"。
- 缺运行时检测多判据：.NET 在 32 位注册表视图（WOW6432Node\dotnet）+ `{pf64}/{pf32}` 目录；Windows App Runtime 查 HKCU/HKLM/HKLM64 并**匹配架构段**。自检出口 `DELAYSTART_RUNTIME_CHECK` / `DELAYSTART_FAKE_MISSING` 可自动化回归。

## 十、本机环境（AI / 工具链）

- **Bash 工具基本不可用**：coreutils 全部 command not found——文件列举用 `Get-ChildItem`、读取用 `Read`、搜索用 `Grep`；**git 只走 PowerShell/cmd，禁止 Bash 工具**（曾三次损坏 .git）。
- PowerShell 命令输出不回显 → 重定向到文件再 `Read`，加 `DONE` 标记确认跑完；外部命令中文输出先设 `[Console]::OutputEncoding = UTF8`。
- 安全策略拦截含完整 `.exe` 路径字面量的命令（LOLBin 规则）；不要用中文列名解析 `tasklist`/`schtasks` 输出。
- 删除文件**一次一个路径**（多路径数组会被安全钩子拼坏）；`Remove-Item` 成功也可能抛错，须以 `Test-Path` 复核为准。
- 同一文件一条消息里并发多个 Edit 会**静默丢改动**——串行编辑，改完用 Grep 搜新串确认落盘。
- 构建前清缓存做对照实验（obj 的 .g.cs 会污染二分结论）。
- 🔴 **拆多笔提交时别用交互式 `git add -p`**（非交互环境会挂）：改用「备份终态 → `git checkout --` 退回 HEAD → 逐笔正向编辑并提交 → 终态 SHA256 与备份比对」。若某笔的改动被提前落到工作区，用精确字符串替换临时撤下、提交后再恢复。
- 🔴 **用 PowerShell 替换源码字符串前先探测行尾与 BOM**：本仓 C# 源文件是 **LF** 行尾，模式串里写 `` `r`n `` 会**静默零匹配**（`String.Replace` 不命中也不报错，改动像没做）。替换后必须回读命中计数（`[regex]::Matches(...).Count`）；写回用 `UTF8Encoding($false)` 并保留原 BOM 状态。
- 安全钩子会误判 cmd 风格语法：`git show --format="%H%n%s"` 被当成 `%VAR%` 环境变量语法拦下；`cmd /c "..."` 在 PowerShell 工具里被直接禁。命令里避免 `%...%` 片段，重定向用纯 PowerShell 写法。

---

## 教训方法论

1. **先取证再改**：报错框是证据不是结论——"读到 A 要求 B、本机只有 C"要直接调一次探针验证（D64 的 DDLM 假铁证）。
2. **涉令牌/权限/完整性的结论，必须测到进程完整性级别**，不能停在"启动成功"（D42→D44→D45 两度翻转）。
3. **单测与构建都绿 ≠ 真机可用**：InvariantGlobalization、publish 缺 XBF、多触发器粒度全是单测照不到的角落——出口条件必须含真机项。
4. **能直接调一次 API 就别推理**；红/绿对照证明用例真的钉住了缺陷。
5. **用户报告的现象先 1:1 复现再修**；修完用"反向放回病根文件"验证因果。
