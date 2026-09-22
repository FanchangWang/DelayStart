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
- 🔴 **`app.manifest` 的注释是 XML，同样不能出现连续两个减号**（2026-09-22 D82 踩到）：把 `--goto-log` / `--goto-startup` 这类命令行字面量写进清单注释，`mt.exe` 直接以 `c1010070 Failed to load and parse the manifest`（exit 31）失败，而**报错只指向文件、不指出行** —— 看起来像"清单整体损坏了"，实际只是注释里的两个减号。去掉前缀（写成 `goto-log`）即过。
  - 📌 **取证经过（做法值得照抄）**：报错时同时怀疑注释里的 emoji（非 BMP 字符，见十一），于是做**单变量**实验 —— 只把 emoji 放回去、保持"无连续减号"，再构建一次：**通过**。⇒ 连续减号是 `c1010070` 的唯一成因；emoji 在**构建期**是安全的。这条实验同时修正一个旧判断：管理端的清单**并非**"走 WinAppSDK 规范化、不受影响"，它和守卫当初那份一样被 `mt.exe` 原样解析（`Microsoft.WindowsAppSDK.SelfContained.targets` 里那条 `mt.exe` 命令的输入之一就是 `app.manifest`），只是 emoji 这一项恰好没触发失败。全程戒 emoji 的成本是零，仍照旧戒。
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
- 🔴 **提权相关的两条做法（D61 真机踩过两次 740）**：当年 `DelayStart.exe` 的 manifest 是 `requireAdministrator` 而安装器是 `lowest`，于是 ① `[Run]` 首启必须带 `shellexec`（默认的 CreateProcess 路径 740，表现为"勾了启动、点确定弹报错框，程序根本没起来"）；② 卸载还原必须 `ShellExec('runas')` + `--result-file` 轮询回读退出码（`ShellExec` 拿不到子进程退出码），且**不能用 `[UninstallRun]`**（读不到退出码，还原失败也照删文件）。**D82 把 manifest 改成 `asInvoker` 之后 740 的成因已消失；这两条做法仍然保留不动** —— 它们是真机验证过的路径，改它们等于往卸载流程里塞进两条没跑过的分支（UAC 被拒、父子退出码转发），而收益只是少一次文件往返。
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

## 十一、自启动项守卫（Guard）

- 🔴 **"应用把自启动项写回启用"是常态而非异常**：软禁用（`StartupApproved` 标记 / 任务触发器）只让系统不去启动它，**原 `Run` 值 / 任务定义一个字节都没动**。于是应用自身升级、重装、或它的"开机自启"开关被重新点开时，会顺手把标记删掉 / 重写（`StartupApproved` 的"启用"就是 `DeleteValue`）—— 用户看到的现象是"接管失效了"，且没有任何提示。⇒ 守卫必须**每一轮都按扫描结果重新纠正**，不能只在接管那一刻做一次。
  - **判据只有一条**：本次扫描结果里该条目的 `IsEnabled`（`GuardCorrectionPolicy`）。🔴 **禁止另写一份"是否被写回"的判据表** —— 四个来源各自的实现已经把这件事算准了（注册表三级回退、计划任务"任务开关 **且** 至少一个自启动触发器启用"（D67）、UWP 看 `State`），第二份判据必然与之漂移。最典型的漂移后果：被接管的多触发器任务**每一轮**都被误判成"被写回"，日志被假纠正记录淹没，"纠正过什么"从此不可信。
  - **写成功 ≠ 写对了**：纠正后必须**复读该来源确认**（`GuardService.ReReadDisabled`），确认失败要报 `Warn` 而不是当作成功。静默吞掉会让用户以为"守卫在保护我"，而实际上没有。
  - 不计数、不设阈值、不做对抗升级：接管是用户明确的期望，被写回就纠回来，与次数无关。
- ⛔ **已作废的两条（D79，2026-09-22）：原生模态提示框及其清单**。守卫原来的通报载体是 comctl32 的 `TaskDialogIndirect`，由此派生过两条教训 ——「它只存在于 comctl32 v6，缺激活上下文直接 `EntryPointNotFoundException`（不是降级成旧样式，是崩）」与「`TASKDIALOGCONFIG` / `TASKDIALOG_BUTTON` 是 1 字节对齐（`Pack = 1`），对齐写错只返回 `E_INVALIDARG(0x80070057)`、不弹不崩」。**这两条随 `NoticeDialog.cs` / `NativeMethods.cs` / `app.manifest` 一起删除而失效**（守卫现在不调任何原生 UI API，也不再有清单文件，因此"清单里不得加 `requestedExecutionLevel`"那条伴随约束同样作废）。
  - 保留它们的历史理由是那条**通用**判据 ——"所有字段取值组合都得到同一个 HRESULT ⇒ 问题在布局不在取值"（见本文末《教训方法论》第 6 条）。真要与 Win32 结构体打交道时仍按它做。
  - 同理作废：**`app.manifest` 里禁止 emoji / 非 BMP 字符**（SxS 激活上下文生成失败 ⇒ 双击无窗口无日志、退出码 1、Application 日志 `SideBySide` Id=59）。守卫的清单已不存在；这条仍适用于**任何**原样嵌入清单的工程（管理端那份走 WinAppSDK 规范化，不受影响）。
- 🔴 **未打包应用发系统通知，AUMID 必须先"存在"**（D79，2026-09-22）：`ToastNotificationManager.CreateToastNotifier(aumid)` 认的不是进程而是一个字符串。不传参的重载对未打包进程直接抛"元素未找到"；传了 AUMID 但系统里没有该标识时通知**静默不显示**。唯一的存在方式 = 开始菜单里有一个把它写进 `System.AppUserModel.ID` 的快捷方式（用 `IShellLinkW` + `IPropertyStore`，属性键 `{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3} / 5`）。
  - ⚠️ **AUMID 本身不显示给用户**：用户看到的标题/图标来自那个**快捷方式**。所以"通知标题显示 DelayStart"与"通知能弹出来"是同一件事的两个面，不是一个字符串参数就能解决。
  - 🔴 注册路径**必须幂等且绝不抛异常**：管理端每次启动 + 守卫发通知前各调一次；失败只降级成"这次通知弹不出来"，不能让管理端起不来或让守卫巡检失败。
  - 🔴 **同一 `Tag` + `Group` 的通知互相替换**（本实现 `guard-change` / `delaystart`）：不设它们，周期档位下每轮"有变化"都会在通知中心堆一条，用户会直接关掉通知权限。
  - ⚠️ 通知是**尽力而为**的：被用户关掉 / 专注助手 / 组策略禁用都会不显示 —— **不算巡检失败**，巡检结果必须始终落在 `guard.log`。
  - 🔴 卸载必须清键：`HKCU\Software\Classes\delaystart` 要 `RegDeleteKeyIncludingSubkeys`（`RegDeleteKey` 只删空键，而它下面有 `shell\open\command`），否则留下一个指向已删除 exe 的 handler。
- 🔴 **`--goto-startup` / `--goto-log` 必须以 `--` 前缀排除在 CLI 分流之外**（2026-09-22 发现）：`Program.Main` 无条件先调 `CliHost.TryExecute(args)`，而那两个参数以 `--` 开头会被当成**未知子命令** → CLI 返回 2 并直接退出，**GUI 从不启动**。通知点击、`--stale` 定位全走这两个参数，所以症状是"点了通知什么都没发生"。判据是 `if (!gotoLog && !gotoStartup && CliHost.TryExecute(...))`。这条在协议链路打通之前一直潜伏（没有调用方传过它们）。
- ⚠️ **守卫 TFM 带平台版本（`net10.0-windows10.0.26100.0`），产物路径比调度端多一段**：发 WinRT 通知需要平台版本才能拿到投影（`Microsoft.Windows.SDK.NET.dll` / `WinRT.Runtime.dll`，管理端 bin 里本来就有）。所以它的 bin 是 `bin\<Cfg>\net10.0-windows10.0.26100.0\<rid>\`，而调度端是 `net10.0-windows\<rid>\`（纯 Win32，不带平台版本）。**照调度端的路径去搬守卫产物会找不到文件。**
- 🔴 **守卫 exe 进驻 App bin 要整组四个文件，不是只拷 exe**：`PathService.GuardExecutablePath` = `AppContext.BaseDirectory + "DelayStart.Guard.exe"`，管理端启动时的 `GuardTaskBootstrap` 会把守卫计划任务的 action 指到这里。守卫**非 AOT**，它的 exe 只是 apphost —— 少了 `DelayStart.Guard.runtimeconfig.json`（声明框架依赖）或 `deps.json`，双击会报"找不到运行时/依赖"，**而编译期毫无提示**。四个文件是 `exe` + `dll` + `runtimeconfig.json` + `deps.json`（其余依赖 `Core`/`Management`/`TaskScheduler`/`EventLog` 本来就在 App bin 里）。
  - ⚠️ **构建顺序是硬约束**：同步靠 `DelayStart.App.csproj` 的 `CopyGuardBuildOutput`（`AfterTargets="Build"`，**拉**式钩子），所以 `dotnet build Guard` 必须排在 `build App` 之前。2026-09-22 的原始缺陷正是顺序反了 + 钩子没覆盖守卫：`publish.ps1` 一路 `[OK]`，**App bin 里却始终没有 `DelayStart.Guard.exe`** —— 计划任务是一条指向空文件的死任务。现在 `publish.ps1` 把 App bin 里的守卫四件纳入产物核对，缺件即 `exit 1`。
- **守卫基线必须原子写、坏基线必须降级**：`baseline.json` 走 `AtomicFileWriter`（先写 `.tmp` 再替换）—— 守卫被强杀留下半截 JSON 时，下一次巡检会把**全部条目报成"新增"**（满屏假情报）。读不到 / 解析失败一律返回 `null`（等价"首次运行 ⇒ 不通报新增"）：差集测不出来只是少一次提示，拿坏基线去测会制造噪声。同理，**本轮整体失败的来源，其条目在下一次基线里要沿用旧值** —— 否则来源恢复时会把一整批老条目报成"新增"。

## 十二、提权与跨进程唤起（D82）

- 🔴 **跨完整性级别不能拿内核对象当信号**：管理端运行在**高完整性**，而 Shell 按协议（点系统通知 → `delaystart:`）拉起的那个进程是**中完整性**。这类对象默认带 `NO_WRITE_UP`（只挡写、不挡读），所以中完整性进程**打不开**高完整性事件、更 `Set()` 不了它。更阴的是**失败是静默的**：`EventWaitHandle.TryOpenExisting(name, out _)` 申请的正是写权限（`Synchronize | Modify`），拿不到时返回 **false** —— 与"对象不存在"是同一个返回值。症状因此是"实例明明在跑，点通知照样弹 UAC"（D82 要修的那个）。⇒ 唤起信号改用**文件**：写它不需要跨级别权限（`ui-request.json` 落在用户目录、标签是中完整性，两边都写得进去），实例侧 `FileSystemWatcher` 收。顺带解掉"事件过期即失"这个老问题（文件留在盘上，投递失败也不会丢单）。
- 🔴 **只读打开要显式申请 `SYNCHRONIZE`，托管 API 表达不了**：判断"还有没有实例在跑"必须只申请读权限，而 `EventWaitHandle` 的托管重载写死了 `Synchronize | Modify`；`EventWaitHandleRights` 枚举也不在 `System.Threading`（那是 .NET Framework 时代的放置位置，本解决方案引用的框架里没有，会直接 CS0103）。⇒ 下沉到 `OpenEventW(EVENT_SYNCHRONIZE)`（`App/Interop/InstanceProbe`）。🔴 **必须紧接着取 `Marshal.GetLastWin32Error()`** 区分"不存在(2)"与"访问被拒(5)"：`AccessDenied` 是"只读探测"这个前提**唯一**能在现场看到的判据，折成一个 `bool` 就永远看不见。误判方向是安全的 —— 按"没有实例"走只会白弹一次 UAC，落点不丢（被拉起的子进程在同级别里必然探测成功）。
- 🔴 **`FileSystemWatcher` 不要设 `Filter`，按完整路径比对**：请求文件是**原子写**的（`ui-request.json.tmp` → 改名 / 替换），而"改名事件里过滤器比对的是旧名还是新名"没有可靠约定 —— 设了过滤器就有概率让**整条点击静默丢失**。目录里直接只有这一个文件（其余都是子目录，`IncludeSubdirectories=false` 时不受影响），全收下来再比 `FullPath` 既便宜又不漏，还顺手免疫 `.tmp` 的噪声事件。
- 🔴 **装文件监听必须早于建"存活标记"**：外面（提权门）的顺序是"先探到实例存活（看标记）→ 再把请求写成文件"，所以**标记晚于监听存在**才成立。反了会出现"探到了实例、但实例还没开始看文件"的丢单窗口；反之（监听已装、标记未建）最坏只是对方白弹一次 UAC，由子进程自己纠正。`App.OnLaunched` 里的顺序就是这条约束的落地。
- 🔴 **提权门必须挡在所有业务分支之前，且自己的标记要先摘掉**：`--elevation-attempted`（防"重拉自己"死循环用）同样以连续两个减号开头，留在参数里会被 `CliHost` 当成未知子命令（退出码 2、界面根本起不来）—— 与十一那条同源。顺序：认领定位参数 → 提权门 → CLI。
- ⚠️ **提权重拉自己会开一个新的控制台窗口**：`runas` 拉起的子进程由 AppInfo / consent 创建，拿不到父进程的控制台，于是 CLI 输出落在一个新窗口里。`requireAdministrator` 时代也是这样，**不是 D82 引入的**。GUI 路径因此**不等待**子进程（否则父进程要陪用户开到关窗，任务管理器里多一个看不见的进程），CLI 路径才等待并透传退出码。

---

## 教训方法论

1. **先取证再改**：报错框是证据不是结论——"读到 A 要求 B、本机只有 C"要直接调一次探针验证（D64 的 DDLM 假铁证）。
2. **涉令牌/权限/完整性的结论，必须测到进程完整性级别**，不能停在"启动成功"（D42→D44→D45 两度翻转）。
3. **单测与构建都绿 ≠ 真机可用**：InvariantGlobalization、publish 缺 XBF、多触发器粒度全是单测照不到的角落——出口条件必须含真机项。
4. **能直接调一次 API 就别推理**；红/绿对照证明用例真的钉住了缺陷。
5. **用户报告的现象先 1:1 复现再修**；修完用"反向放回病根文件"验证因果。
6. **"顺序对" ≠ "布局对"**：与 Win32 结构体打交道时，先核对 SDK 头文件的 packing（`pshpack1.h` / `poppack.h`）与本机实测 `sizeof`，再谈字段取值。顺序、偏移、大小是三件独立的事；用同一 HRESULT 复现所有字段取值组合，就是布局错的信号。
