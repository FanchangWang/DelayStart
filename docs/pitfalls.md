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
- 启动文件夹**只扫 `*.lnk`**（`.url` 是 Internet 快捷方式，调度端无启动路由 → 扫出来必然启动失败）；`.lnk` 目标解析用 `IShellLinkW` + `IPersistFile`。

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
- 🔴 **CI 里调 `gh` 的 job 必须 `actions/checkout`**（2026-09-22 v0.1.0 首次真跑暴露）：`release` job 只 `download-artifact`（想省掉检出），而 `gh release create` 靠当前目录的 `.git` 判定目标仓库 → `failed to run git: fatal: not a git repository`，`--generate-notes` 同样无从取提交历史。修法：加一步 `actions/checkout@v4`，或给 job 设 `GH_REPO` / 命令加 `--repo`。（D86 起该 job 不再用 `--generate-notes` 改 `--notes-file`，但检出仍不可省：make-release-notes.ps1 要读仓库里的 CHANGELOG.md。）**这个坑能藏很久**：该 job 带 `if: startsWith(github.ref, 'refs/tags/')`，此前两次都是 `workflow_dispatch` 触发、整段被跳过 —— "从来没跑过"和"一直好着"在日志里长得一模一样，只有真走 tag 那条路才暴露。⇒ **新加的触发路径必须实跑一次才算数。**
- 🔴 **`gh run rerun` 用的仍是原 run 那次 commit 的 workflow 文件**：改了 `.github/workflows/*.yml` 之后 `--failed` 重跑，跑的还是**旧定义**（GH 固定沿用原 run 的 `GITHUB_SHA` + `GITHUB_REF`）。要验证 workflow 改动只能重新触发：移动 tag 重推、或新提交推 main 后再打 tag。
- 🔴 **`shell: pwsh` 步骤里不能写 bash 风格 `${SOME_ENV}`**（2026-09-23 v0.2.0 首跑踩坑）：`${GITHUB_REF_NAME}` 在 pwsh 里是 **PowerShell 变量**（不存在 → 恒空串，报 `Cannot bind argument to parameter ... empty string`），不是环境变量；必须写 `$env:GITHUB_REF_NAME`。同文件里 bash 步骤（未写 `shell:` 的 ubuntu 默认）用 `${GITHUB_REF_NAME}` 才是对的 —— 两种风格混排时极易看错。
- 🔴 **发布脚本的"产物核对"清单必须包含用户会去双击的那个 exe**（2026-09-23）：`scripts/publish.ps1` 原先列了 13 项 —— 调度端 / UIAccess 中转器 / 守卫四件套 / 通知中转器四件套，**唯独没有管理端本体 `DelayStart.exe`**。脚本 `exit 0`、清单一片 OK，用户照着清单找主程序却找不到，于是得出"脚本编译不出 exe"的错误结论（它一直在 `src\DelayStart.App\bin\Release\{AppTfm}\{Rid}\` 下，从未缺席）。⇒ **入清单的判据是"用户会不会去找它"，而不是"它是不是本条流水线新产出的产物"**；同时脚本结尾要直接打印"去哪个目录双击"。另注意 `publish.ps1`（开发期）**不产出 App 的 publish 目录**，可分发目录与安装包归 `installer\build-installer.ps1`（`artifacts\publish\{rid}\{flavor}\` + `dist\`）—— 两者职责别混。
- **沙箱程序黑名单拦 `reg.exe` 不会让 NativeAOT 发布失败**（2026-09-23 本机实测）：`dotnet publish` 调度端时，ILCompiler 的工具链探测会拉起 `reg.exe`，被安全策略拦下并在**外层 shell** 抛 `PROGRAM BLOCKED BY SECURITY POLICY`；但 MSBuild 把它当可选探测，`Generating native code` 照常完成、`exit 0`、产物齐全。⇒ 见到这条阻断消息，先去**读脚本自身的日志与退出码**，别据此判定构建失败（对照：同机上 `dotnet build src\DelayStart.App -c Release -r win-x64 --no-incremental` 全程无此拦截）。

## 八、WinUI 3 模板与工程创建

- 🔴 **官方模板只生成 MSIX 打包工程，没有任何 unpackaged 开关**（`Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` 0.0.6-alpha）；生成后需手工改造 csproj 并删 `Package.appxmanifest`。模板缓存是用户级全局的。
- 模板残留清理清单：`Package.appxmanifest` + 徽标 PNG + `.pubxml`、模板页 `Pages/`、`Form1.*`、`Class1`/`UnitTest1`；`Microsoft.Windows.SDK.BuildTools.WinApp` 包 unpackaged 用不上可移除。
- 测试项目 TFM `net10.0-windows` 即可——"必须匹配 WinUI TFM + SelfContained + 预装 Runtime"那套只适用于**直接引用 WinUI 3 项目**的测试，本项目不适用（分层收益）。

## 九、安装器（Inno Setup）

- 🔴 **任何一行不能以 `[` 开头**（含 `[Code]` 段内、含缩进后的 `[`）——ISCC 报 `Invalid section tag`，哪怕那行是合法的 Pascal 数组字面量。
- `TaskDialogMsgBox` 签名共 **6 参**：`(Instruction, Text, Typ, Buttons, ButtonLabels, ShieldButton)`，**没有验证复选框**（Inno 未把 Win32 任务对话框的 verification checkbox 暴露给 Pascal；2026-09-23 以官方文档源 `ISHelp/isxfunc.xml` + ISCC 6.7.3 编译实证）。第 6 参 `ShieldButton` = 哪个按钮显示盾牌图标（0 = 无）—— 旧记录"第 5 参 Shields 是集合类型"系误记。要静默默认值用 `SuppressibleTaskDialogMsgBox`（末尾多一个 `Default` 参）。
- 🔴 **`TaskDialogMsgBox` 也没有默认按钮参数，默认焦点恒在第一个按钮（IDYES）**（2026-09-23 官方文档源实证 + 用户真机截图确认）。要把非破坏性操作设为默认，只能把它**排到 ButtonLabels 第一位** —— 首版卸载对话框把「删除配置并卸载」排第一，默认焦点落在破坏性操作上，用户实测发现后改为「保留」排第一（D85）。
- **不要重复声明 `FILE_ATTRIBUTE_DIRECTORY`**——Inno Pascal 自带（`Duplicate identifier`）。
- 🔴 **提权相关的两条做法（D61 真机踩过两次 740）**：当年 `DelayStart.exe` 的 manifest 是 `requireAdministrator` 而安装器是 `lowest`，于是 ① `[Run]` 首启必须带 `shellexec`（默认的 CreateProcess 路径 740，表现为"勾了启动、点确定弹报错框，程序根本没起来"）；② 卸载还原必须 `ShellExec('runas')` + `--result-file` 轮询回读退出码（`ShellExec` 拿不到子进程退出码），且**不能用 `[UninstallRun]`**（读不到退出码，还原失败也照删文件）。**D82 把 manifest 改成 `asInvoker` 之后 740 的成因已消失；这两条做法仍然保留不动** —— 它们是真机验证过的路径，改它们等于往卸载流程里塞进两条没跑过的分支（UAC 被拒、父子退出码转发），而收益只是少一次文件往返。
- **iss 的 `/DSlim` 判据是"有没有定义"**，不要传 `/DSlim=0` 表"否"。
- **中文 .isl 不随官方 Inno 分发**（属用户贡献翻译）→ 随仓库分发 `installer\languages\ChineseSimplified.isl`，iss 写 `compiler:Default.isl,languages\ChineseSimplified.isl`（相对路径按 **.iss 所在目录**解析；垫 Default.isl 消"缺 message"警告；语言文件版本错位只警告不失败）。
- 语言/版本比较别用 `Copy(s,1,27)` 对 31 字符串——恒为假，用 `Pos(...)>0`。
- 「设置→应用」显示名取 `AppVerName` 而非 `AppName`；固定 `AppVerName={#AppName}` 只显示裸名。
- **静默安装沿用任务的默认勾选态，不会"自动全不勾"**（2026-09-23 实测纠正）：`[Tasks]` 不写 `Flags` 时默认勾选 → GUI 与静默都创建桌面图标；要静默排除用 `/MERGETASKS="!desktopicon"`。静默卸载删数据只认显式 `/DELETEDATA`，未传一律保留（D84）。
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

## 十三、通知中转器（NotifyBroker）

- 🔴 **跨进程 JSON 契约的默认值必须自洽，"空字符串默认值"是哑弹**（2026-09-22）：`NotifyToastJob.Launch` 的属性默认值是 `string.Empty`，常量 `ScheduleDoneLaunch` 从未接成默认值；写作业方按注释"靠契约默认值"只填 Title/Message → 发出的 toast `launch=""` → **点击通知毫无反应**，而发送侧全程绿灯（作业落盘、`Show()` 成功、日志无异常）。修复分四层：① 契约默认值改 = `ScheduleDoneLaunch`（自洽）；② 写作业方**显式**赋值（自文档化，不依赖注释承诺）；③ broker 对空 `Launch` 记 Warn（纵深防御，不阻断发送）；④ 单测锁默认值（`NotifyToastJobTests`）。教训：契约里"能被静默取到的空值"都会等到链路最远端（用户指尖）才炸，且炸得无声无息 —— 默认值要么不存在（必填、缺了报错），要么就是正确值。

---

## 十四、调度周期与节假日数据（FR-15）

- 🔴 **`DataTemplate` 里的 `x:Bind` 够不到页面的 `ViewModel`**（2026-09-23）：`DataTemplate` 的绑定上下文是**行对象**，写 `{x:Bind ViewModel.IsBusy}` 直接编译失败（`x:DataType` 是 `vm:CycleRow`）。要绑页面级状态，只能 ① 把状态挂到行对象上（本项目采用：`CycleRow.CanDelete` / `EditToolTip`），或 ② 用 `ElementName` 绑定。**结论：凡是"行要不要置灰 / 显示什么"的判断，一律在行对象里算完**（顺带也避免了判定分叉 —— 界面自己再算一遍就会出现"按钮亮着但点了报错"）。
- 🔴 **同一 `Grid` 行的两个 `InfoBar` 会互相压住**（2026-09-23）：延时页顶部有"配置坏了"和"次年数据没到"两条横幅，各自写 `Grid.Row="1"` 时后一条直接盖住前一条（`InfoBar` 不是流式布局）。要么给它们**不同的行**，要么像现在这样放进同一个 `StackPanel`（该行高度是 `Auto`）。
- ⚠️ **`ContentDialog.Title` 吃 `object`，可以塞 `StackPanel` 做"标题 + 副标题"**（2026-09-23）：面板要显示"3 个条目正在使用它"这类影响面说明时，不必新造控件 —— `Title = StackPanel { TextBlock(标题), TextBlock(12px 次要色) }` 即可（`ContentDialog` 没有 Subtitle 属性）。
- 🔴 **默认值写对方向是设计决定，不是随手填**（2026-09-23）：周期引用的兜底一律往**宽松**走（认不出的周期 id → 回落「每天」），绝不往**严格**走（→ 永不启动）。同理，法定数据缺失时降级成星期近似，而不是"判定不出来就不跑"。判定链上任何一个"失败方向"选错，用户看到的现象都是"我的程序今天没启动"，而且不报错。
- ⚠️ **`CA1822` 会把"不读实例状态"的属性判成该 `static`**（2026-09-23）：本仓库 `TreatWarningsAsErrors=true`，所以 `public DateOnly Today => ...` 这种纯计算属性会直接把构建打红。**别为了加个便利属性破坏"同一快照"的设计** —— 该属性该由持有快照的类（`CycleInfoProvider`）提供，而不是由服务再取一次时间。
- ⚠️ **新建源码文件前先确认目标目录**（2026-09-23）：一次 `Write` 把三个新文件写到了仓库根下的 `Management/`（而不是 `src/DelayStart.Management/`），**编译时表现为"类型找不到"**（因为根目录不在任何 csproj 的 `**/*.cs` 范围内），而不是"文件写错了地方"。文件建完后用 `git status` 扫一眼未跟踪目录，比在报错里找原因快得多。
- 🔴 **`[ObservableProperty]` 只为自己生成的属性发通知，get-only 派生属性要手工补**（2026-09-23 用户真机实测）：`StatusText` 是 `[ObservableProperty]`，而 `HasError => StatusText.Length > 0` 只是普通计算属性 —— 少了 `partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasError));` 这一行，后果**不是"提示难看"，而是所有 `Fail()` 全部静默**：`Message` 绑定的文本确实更新了，但 `IsOpen` 绑定的 `HasError` 永远停在 `false`，整条 `InfoBar` 从不出现。用户在设置页看到的就是"每个按钮点了都没反应"。**审查清单：凡是把 `IsOpen` / `Visibility` / `IsEnabled` 绑到 `xxx => 某字段.某判断` 的地方，都必须有对应的通知补发**（`partial void OnXxxChanged` 或显式 `OnPropertyChanged`）。
- 🔴 **`stc:SettingsCard` 的 `Content` 区会断 `DataContext` 继承链**（2026-09-23）：在**同一个** `ItemsControl` + `DataTemplate` 结构下，`DelayPage` / `ItemsPage` 的行内按钮用 `sender is Button { DataContext: DelayRow row }` 一直好用；但同样的写法放进 `SettingsCard` 的 `Content` 之后，`DataContext` 取不到行，处理器里的 `if` 直接落空 —— **静默什么都不发生**。⚠️ 卡片标题 / 描述显示正常**不能**当作内容区 `DataContext` 正确的证据：那些走 `x:Bind` 编译期引用，与 `DataContext` 无关。对策：给按钮 `Tag="{x:Bind}"`（编译期绑到 item，不经过继承链），取行时 `Tag` 优先、`DataContext` 兜底。
- 🔴 **把"忙"标志直接绑到 `IsEnabled` 是反相错误**（2026-09-23）：`IsEnabled="{x:Bind ViewModel.IsHolidayUpdateBusy}"` 读起来通顺，语义却恰好相反 —— 忙的时候能点、闲的时候点不了。`x:Bind` 不支持 `!`，所以要么加一个派生属性（`IsHolidayUpdateIdle => !IsHolidayUpdateBusy`）并在源属性变化时补通知。凡是"按下会进长时间操作"的按钮都该按这个套路配一个 `xxxIdle`；顺带**必须给出进行中的可见反馈**（本项目加了"正在检查并下载…"的 `InfoBar`）—— 网络操作最坏要十几秒，没有反馈就等于"按钮坏了"。
- ⚠️ **自绘控件的"只读模式"忘了设 = 假交互**（2026-09-23）：`WeekdayGrid` 是同一套按钮网格的两种用法（可编辑 / 只读展示），只读靠 `IsReadOnly="True"` 关命中测试，但没有任何地方会提醒你漏设。漏设时格子看起来**完全可点**、点下去却不改变任何东西（连选中态都不变，因为 `IsReadOnly` 也挡着 `DaysChanged`）—— 用户只会以为自己没点对。**同一个控件承担两种身份时，"只读"必须由调用方显式声明；能用一行文字表达的"纯展示"，就不要用可交互控件**（本轮最终把弹窗里的七宫格换成了一行文字）。
- 🔴 **"点了没反应"要先怀疑"反馈通道断了"，而不是"事件没绑上"**（2026-09-23）：本轮 4 个无响应现象（重新下载 / 立即更新 / 导出 / 导入）**事件全都正常触发了**，是三条反馈通道各断一环 —— ① 错误文案的 `IsOpen` 不更新；② 行对象取不到导致静默 return；③ 按钮被反相的 `IsEnabled` 锁死。定位顺序：**先在处理器第一行写日志确认进没进 → 再看取值是否为空 → 最后看 UI 绑定的通知源**。
- 🔴 **`JsonElement.TryGetProperty` 是大小写敏感的，而源生成上下文默认不敏感**（2026-09-23）：用前者做"这是哪种格式"的判别式，会造出"反序列化能过、但格式认不出来"的错位 —— 文件被当成不认识的格式拒收，而且看不出原因。两个 `JsonSerializerContext` 都开了 `PropertyNameCaseInsensitive = true`，判别式也必须同口径（本项目用 `HolidaySourceConverter.TryGetProperty` 遍历属性名做 `OrdinalIgnoreCase` 比较）。
- ⚠️ **测试里用 `ToUpperInvariant()` 伪造"键名大小写"会把 `true`/`false` 也变成 `TRUE`/`FALSE`**（2026-09-23）：那已经不是合法 JSON 了，用例断言到的其实是"解析失败"，而不是"大小写不敏感"（真实表现：用例报 `Expected: Ok, Actual: Unrecognized`，看起来像实现有 bug）。**只替换键名**（逐个 `Replace("\"key\"", "\"KEY\"")`），值一律保持原样。
- 🔴 **同一份外部数据有两个入口时，转换只能有一份实现**（2026-09-23 用户批复）：下载通道与「从文件导入」原先各写一套解析 —— 下载路径认 `days: [{date,isOffDay}]`，导入路径只认自己的 `workdays`/`restDays`。后果不是"重复代码"，而是**同一份文件在两条路径上得到相反结局**：用户从上游直接下载的 `2026.json` 能自动下载落盘，却不能导入（报"条目数 0 少于 20"）。抽成 `HolidaySourceConverter` 之后两边共用。⚠️ 推广：**凡是"让用户自己先把文件转成内部格式"的设计，都要先问一句"他拿什么工具转"** —— 答不上来就说明该由程序转（离线机器用 U 盘传数据这件事本身，就说明他手上不该再多一道工序）。

---

## 十五、COM 互操作（系统对话框）

- 🔴 **接口 cast 失败 = 进程静默消失**（2026-09-23 用户报"设置页导出点击闪退"）：`IFileOpenDialog` 与 `IFileSaveDialog` 是**平行接口**（两者都只继承 `IFileDialog`，彼此没有继承关系）。拿 `FileSaveDialog` 的实例去 cast `IFileOpenDialog`，QueryInterface 返回 `E_NOINTERFACE`，CLR 据此抛 `InvalidCastException` —— 它抛在事件处理器里没人接住，结果是**整个进程没了**：没有"未响应"、没有事件日志里的一句人话，只有"点一下闪退"。本机 `Marshal.QueryInterface` 实测：

  ```
  FileSaveDialog   →  IFileOpenDialog  (D57C7288-D4AD-4768-BE02-9D969532D960)   0x80004002  E_NOINTERFACE
  FileSaveDialog   →  IFileDialog      (42F85136-DB7E-439C-85F1-E4075D135FC8)   0x00000000  OK
  FileSaveDialog   →  IFileSaveDialog  (84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB)   0x00000000  OK
  FileOpenDialog   →  IFileOpenDialog                                            0x00000000  OK
  FileOpenDialog   →  IFileDialog                                                0x00000000  OK
  ```

  ⇒ **两个对话框 coclass 都 cast 到共同接口 `IFileDialog`**（那也是我们实际用到的全部方法所在：声明到 `SetFilter` 即它的完整 vtable；`IFileOpenDialog` 特有的 `GetResults` / `GetSelectedItems` 声不声明都别调）。

- 🔧 **排查手法：先用 PowerShell 把 IID 逐对试一遍，比猜快得多**（同上，2026-09-23）：建实例 → 取 `IUnknown` → `Marshal.QueryInterface` 看 HRESULT。不需要启动程序、不需要弹窗、不会卡住 UI：

  ```powershell
  $inst = [Activator]::CreateInstance([type]::GetTypeFromCLSID([Guid]'C0B4E2F3-BA21-4773-8DBA-335EC946EB8B'))
  $unk  = [System.Runtime.InteropServices.Marshal]::GetIUnknownForObject($inst)
  $iid  = [Guid]'D57C7288-D4AD-4768-BE02-9D969532D960'; $ptr = [IntPtr]::Zero
  'HR=0x{0:X8}' -f [System.Runtime.InteropServices.Marshal]::QueryInterface($unk, [ref]$iid, [ref]$ptr)
  # 0x00000000 = 支持；0x80004002 = E_NOINTERFACE，cast 它必炸
  ```

  ⚠️ 这类"cast 错接口"的 bug **单测查不出来**（真实 COM 对象只在真机上，测试里没有），只能靠真机点一遍 —— 所以凡是**新接入一个 shell COM 组件**（对话框 / 快捷方式 / 任务栏），验收清单里必须有一次"点它"。

## 十六、测试替身（Fakes）

- 🔴 **给 `AppConfig` 加字段时，必须同步改 `InMemoryConfigStore.Copy`**（2026-09-23 实测）：那个替身的 `Copy` 是**逐字段手写**的深拷贝，FR-15 给 `AppConfig` 加 `Cycles` 时漏了这一行。后果不是"少测一个字段"，而是**所有经该替身的周期用例都跑在空周期表上** —— 新加的重名校验"拦不住"、`AddCycle` 写进去的周期在 `Save` 时凭空消失；而判据自身的纯函数用例**全是绿的**（它不经过替身），第一眼看上去像是被测代码写错了方向。
  - **症状识别**：`AddCycle(...)` 不抛异常 + `Snapshot().Cycles.Count == 0` + 判据单测全过 ⇒ 先去看替身的 `Copy`，而不是去查被测代码。
  - 同一条提醒在 `CopySettings` 的 remarks 里已经写过一次（2026-09-21，`Settings` 那批字段）；`AppConfig` 自己的集合字段**同样适用**，别以为"只有 Settings 要注意"。
  - 更根本的做法：让替身只保留**一处**"全字段拷贝"的写法（或对新字段做编译期提醒），别让每个新字段都依赖"记得回来加一行"。

## 十七、后台任务、网络降级与节流（FR-15 自动检查）

- 🔴 **超时是"降级触发器"，不是"终局"**（2026-09-23 真机）。三个地址的降级链里，`raw.githubusercontent.com` 在国内稳定 15 秒超时（本机实测：两个 jsdelivr 镜像 1.1 / 1.5 秒返回 200），而原实现把"自己的 15 秒到点"当成终局直接 `return` —— **降级链在最常见的失败形态上完全不生效**，用户拿到的是"开关开着、什么都没有"。
  - 为什么偏偏漏了它：`HttpRequestException`（HTTP 状态码不对）与"空内容"都 `continue` 了，唯独超时没有 —— 因为"自己超时"与"外部取消"要分开报告这件事占住了注意力，顺手把 `continue` 写成了 `return`。
  - 判据：**凡"换个地址 / 换个策略再试"的循环，先把所有失败形态列出来逐个确认走向**，别只测理想失败路径。
  - 回归用例：`HolidayCalendarUpdateServiceTests.FirstAddressTimingOut_FallsThroughToTheNextAddress`（假 `HttpMessageHandler` 让首个 host 抛 `TaskCanceledException`）。

- 🔴 **节流不能拿"失败"记账**（同一次真机）。原实现"发请求**之前**先落时间戳"（本意是防"每次开机都卡一下网络"），代价是**一次失败 = 静默 7 天**。现在按结局分开：成功 / 上游尚未公布 = 7 天，失败 = 1 小时。
  - 通用规则：**节流的对象是"重复的无效开销"，不是"用户想要的功能"**。当节流键是"上次尝试时间"时，必须同时记住"上次的结局"，否则一次抖动就否决了整周的功能。
  - **旧格式必须向后兼容**：升级上来的机器上留着旧版本的**单行** ISO 时间戳（没有结局行），一律按"失败"读 —— 宁可多查一次，也不要让它被当成"上周查过了"继续安静。

- 🔴 **节流记录必须能表达"数据不在了"**（2026-09-23 第二次真机）。把「已获取」与「上游未公布」合并成一个"成功"，就丢掉了唯一能自愈的线索：下载确实成功过，那份文件后来在本地被改了名（`2026.json` → `a2026.json`），读盘校验按"文件名年份与内容不符"把它拒掉，而节流拿着"成功"记账 —— 自动检查再也不会去补（`manager.log` 里只有一行"距上次尝试不足最小间隔，2026-09-30 之后才会再试"），年份行则显示"未下载"。用户看到的是"开关是开的，就是没反应"，与"功能根本没实现"完全一样。
  - 通用规则：**节流的键除了"上次的结局"，还要能表达"那个结局是否还成立"**。写盘成功换来的安静期，前提是"那份数据仍在可用位置"；前提一旦不成立（被改名 / 删掉 / 损坏），安静期立刻作废。
  - 落点：`HolidayCheckOutcome` 三档（`Updated` / `NotPublished` / `Failed`），`IsDue` 对 `Updated` 返回 `TimeSpan.Zero` —— 调用方只在"本地没有可用数据"时才会问节流，这两个条件合起来正好就是"记录说已获取、而它不在"。
  - 中间那代的 `ok` 标记（把「已获取」与「未公布」混在一起）按 `Updated` 读：数据真在本地时调用方根本问不到节流，最坏只是多查一次；反过来会让"文件被改名"的机器继续静默 7 天。
  - 顺带：**用户看得见的那句话也要跟着改**。同一个状态下年份行说"未下载"，而设置页副标题若照旧说"上次自动检查：已获取 2026 年数据"，两句话就是在互相打脸；`HolidayAutoCheckService.Describe()` 为这一档单独出一句"报告已获取、但本地现在读不到它（可能被改名或删除）"。
  - 回归用例：`HolidayCheckThrottleTests.UpdatedRecord_ButDataIsGone_IsDueNow`。

- 🔴 **后台任务的状态必须往"共享对象"上报**（同一次真机）。自动检查是启动后 fire-and-forget 的，而设置页的进度条只绑在 ViewModel 自己的标志上 —— 于是"启动时到底在没在下载"，**界面上无处可查**，用户唯一能得出的结论是"这功能是假的"。修法：单例 `HolidayUpdateStatus`（`INotifyPropertyChanged`）作为唯一进度源，两个触发方（页面按钮 / 启动检查）都往它上报。
  - ⚠️ 后台线程发通知**必须切回 UI 线程**（构造时抓 `DispatcherQueue`）：从线程池线程直接发 `PropertyChanged` 不是"偶尔不刷新"，是当场抛 `RPC_E_WRONG_THREAD`。
  - ⚠️ 单例状态 + 瞬态 ViewModel 的订阅必须在页面 `Unloaded` 里摘掉，否则每进一次页面就往单例上多挂一个处理器（连页面一起不释放）。
  - 判定信号：**一个后台任务，如果用户在界面上问不出"它跑没跑"，那它就是缺陷** —— 不是"功能没做"，而是"做完了也看不见"。

- ⚠️ **`$"""…"""` 里的花括号要升格**（同日，写回归用例时踩到）：单 `$` 的原始插值串里 `{` 一律开启插值，想输出 JSON 的花括号得写 `$$"""…{{表达式}}…"""`（双 `$` 把定界符升格成 `{{ }}`），否则 CS9006「插值原始字符串字面量的开头没有足够的 `$` 字符」。手写 JSON 夹具时最容易踩。

## 十八、运行日志的可解释性（FR-15.26）

- 🔴 **"不执行"也必须在日志里留痕**（2026-09-23 用户要求）。周期不匹配的条目按原设计是"不进计划 = 不上报、不统计、不通知"，逻辑自洽但**用户视角是一片空白**：两条设成「周一至周五」的条目，周六登录后什么都没启动，而运行日志里**连一行记录都没有**（计划为空 → 走 FR-5.10 静默退出，压根不写归档）。他能得到的唯一结论是"这东西坏了"。
  - 修法：`SchedulePlan.BuildWithSkipped` 把"今天不在周期内"的启用条目单列出来（`ScheduleOutcome.SkippedToday`），调度端把它们追加成 `Skipped` 日志行；**全部被跳过时照样写一份归档 + `current-run.json`**（只写归档会让总览页的"最近一次运行"停在上一次，与运行日志页说两套话）。
  - 判据：**任何"有条件地什么都不做"的功能，都要回答"用户怎么知道今天本来就该什么都不做"**。功能正确 ≠ 行为可解释。
  - 次序铁律：被 `Enabled=false` 关掉的条目**不在此列** —— 算进去等于把"用户自己关的"报成"周期决定今天不启动"，运行日志立刻变成误导。

- ⚠️ **运行侧与日志侧别靠下标对齐**（同日在做上面的改动时发现）。原代码是：

  ```csharp
  Items = plan.Select(entry => new RunItemResult { … }).ToList(),
  …
  _items.Add(new SchedulerRuntimeItem { Result = _record.Items[index], … });
  ```

  即"运行项的第 i 个 ↔ 日志项的第 i 个"。往日志里追加**不在运行列表里**的条目（周期跳过项正是这种）之前，必须先把计划项收成一个局部 `planned` 列表、两侧各自引用它 —— 否则将来某次"把追加写在建 `_items` 之前"，运行结果就会静默落到错误的行上（日志说 A 成功、实际启动的是 B），**任何一处都不会报错**。

## 十九、界面符号与字宽（UI 文案）

**症状**：运行日志「结果」列里「成功」「失败」「跳过」三种文字的左边参差不齐，怎么调都调不齐。

**踩过的错**：看到 ◌ 比 ✓ 窄，就给它补一个空格 —— **方向就补错了，越补越歪**。逐字符试探字宽是个填不满的坑。

**根因**：这几个符号**根本不在同一套度量里**。

| 字符 | 码位 | 大致字宽 |
|---|---|---|
| ✓ | U+2713 | ~1 em |
| ✗ | U+2717 | ~1 em |
| ◌ | U+25CC | < 1 em |
| · | U+00B7 | < 1 em |
| ✅ / ❌ | U+2705 / U+274C | 1.2–1.4 em，且**彩色** |

emoji 那行还有两个额外问题：① WinUI 会把它们 fallback 到 **Segoe UI Emoji**（另一个字体，基线与侧边距都可能不同）；② 彩色 emoji 夹在一列灰/红文字里，视觉噪声远大于它带来的信息。

**做**：状态列一律**纯中文**（`成功` / `失败` / `跳过` / `启动`）—— 都是两个汉字，天然等宽；要表达差别用**颜色**（如 `IsFailed` 标红），不要用字形。

**为什么不该"先量一下、再挑一个同宽的符号"**：

1. 度量**随字体变化**（`Segoe UI` 与 `Segoe UI Variable` 不是同一份 `hmtx`），换主题、换系统就可能重新不齐；
2. 本机测字宽的成本意外地高 —— PowerShell 的 `Add-Type`（"compiles and loads .NET code"）与 `[Reflection.Assembly]::Load*`（"execute arbitrary .NET code"）**都被安全策略拦**（见第十节），managed 分发的 `python` **不带 `tkinter`**，最后只剩"自己读 TTF 的 `cmap`/`hmtx` 表"这条重路；
3. 就算量准了，**收益也只是"看起来齐"** —— 而拿掉符号是零成本、且与字体无关的解。

---

## 二十、Win32 句柄不是身份（HWND 复用）

**症状**：往窗口里拖文件**偶尔完全没反应** —— 不报错、不写日志、鼠标也没有 🚫，就是什么都不发生。反复开关延时编辑器之后更容易撞上。

**根因**：`FileDropReceiver.EnableSingle` 的幂等判据是"**这个 HWND 值我见过吗**"（`OriginalProcs.ContainsKey(hwnd)`），而不是"这个窗口现在的过程是不是我们的"。**HWND 会被系统回收复用**：旧窗口销毁、句柄值被分配给新窗口，于是新窗口在表里"命中"、被直接跳过子类化。

为什么表现得像"没接线"：`DragAcceptFiles(hwnd, true)` 在早退**之前**已经执行，`WS_EX_ACCEPTFILES` 是真的设上了 —— 资源管理器照发 `WM_DROPFILES`，而该窗口的过程不是我们的，消息落进默认处理，`FilesDropped` 永远不会触发。整条路径**零异常、零日志**。

**做**：判据换成"**现在的状态对不对**"——`GetWindowLongPtrW(hwnd, GWLP_WNDPROC)` 是不是自己的过程指针；不是就（重新）记录原过程并子类化。那张按 HWND 记账的表只能当**缓存**看，它是否还成立必须**当场核对**（2026-09-24 批复 28 / D106）。

**顺序本身就是语义**：先问"现在对不对"，再决定要不要动手；"我做过没有"不能代替"现在对不对"。

⚠ **同一个写法在别处可能是故意的**：`WindowSizing.EnforceMinimum` 里那道 `ContainsKey` 门防的是**另一件事** —— 本项目有两个窗口过程子类化器，删掉那道门会让后调用者把自己的过程再压一层、而它的"原过程"记录指向对方，**过程链成环 → 栈溢出**。它只在启动时对主窗口调一次、主窗口 HWND 终身不变，所以无害。两条代码长得一模一样，改之前先想清楚自己在改哪一条。

---

## 二十一、`x:Bind` 与 CA1822：常量成员不能写成表达式体

**症状**：`public string Summary => "……";` 这类"值本身是常量"的 ViewModel 成员，构建直接失败（本仓库 `TreatWarningsAsErrors=true`）：

```
error CA1822: 成员"Summary"不访问实例数据，可标记为 static
```

**根因**：分析器（`AnalysisLevel=latest-recommended`）看到表达式体不碰 `this` 就建议 `static`；而 `x:Bind` **绑不到静态属性**（`{x:Bind ViewModel.X}` 走的是实例成员）。两边都满足不了。

**做**：写成**带初始值的自动属性** —— `public string Summary { get; } = "……";`。它有后备字段，不算"不访问实例数据"，分析器不报；同时仍是实例成员，`x:Bind` 正常可用（2026-09-24 批复 28 / `AboutViewModel`）。

同一条也适用于 `RuntimeInformation.FrameworkDescription`、`Environment.IsPrivilegedProcess` 这类"启动后不会再变"的值：**取一次存进自动属性**，别每次渲染调一遍 API。

## 二十二、计划任务「每次重写」会重置登录触发器（守卫沉默失效）

**症状**：开机登录 → 打开管理端，守卫当天一次都没跑；任务计划程序里「上次运行时间」是管理端重写任务的时刻、「下次运行时间」卡在昨天。guard.log 自上次手动运行后零记录。

**根因**：守卫触发器是 `LogonTrigger + InitialDelay`（登录后 N 分钟、一次性）。登录事件在会话里**只能消费一次**；`CreateOrUpdate` 每次都用全新 `LogonTrigger` 替换定义，而此时登录事件已过去，新触发器在本会话内永远等不到下一次登录 → 该次巡检被静默丢弃。周期模式同理（重复序列锚在触发器激活上）。

**做（D112 / F1）**：写入前**逐字段比对现有任务定义**（action 路径与工作目录、Principal 三项、触发器 Delay/Repetition.Interval/UserId、六项 Settings、`RegistrationInfo.Description`），全部一致就跳过 `RegisterTaskDefinition`、保留已武装的触发器；只有定义确实变了（档位调整 / 安装目录迁移 / 任务被改坏）才重写。D74「缺失即补建 / 关闭即删除」语义全部保留。**判定"一致"只比我们显式写入的字段，不比未设置的默认值**（库版本差异会让默认值漂移，误判成需要重写）。

**反模式**：把 `RegisterTaskDefinition(CreateOrUpdate)` 的"幂等"理解成"重写下次运行时间等统计不丢"——**错**：重写会重建触发器、并覆盖运行统计（D112 已证伪旧注释）。

**验证**：同会话内第二次 `SyncWithSettings` 必须返回 `NoChange`（不是 `Updated`）；真机验收：登录后开管理端，到点守卫仍应巡检一次，且任务计划程序「下次运行时间」不再卡在过去。

---

## 二十三、调度计划任务「每次重写」同样会重置登录触发器（F1 调度端镜像）

**症状**：`\DelayStart\Scheduler` 每次启动被 `SchedulerTaskBootstrap` 无条件重写（`CreateOrUpdate`），任务计划程序里「上次运行时间」等统计被覆盖成重写时刻、「下次运行时间」随之错位；虽不像守卫那样静默吞掉巡检，但统计失真且无谓触碰系统状态。

**根因**：调度任务触发器也是 `LogonTrigger + InitialDelay`（登录后 N 秒、一次性）。`CreateOrUpdate` 每次都重建触发器，登录事件已过去，新触发器本会话内等不到下一次登录。调度端是固定规则、不随设置变化，相同定义下每次启动都不该重写它。

**做（D113 / F1 调度端镜像）**：写入前**逐字段比对现有任务定义**（action 路径与工作目录、Principal 三项、触发器 Delay/UserId、六项 Settings、`RegistrationInfo.Description`），全部一致就跳过 `RegisterTaskDefinition`、保留已武装的触发器与运行统计；只有缺失或定义确实变了（安装目录迁移 / 任务被改坏）才重写。判定"一致"只比显式写入的字段，不比未设置的默认值（库版本差异会让默认值漂移，误判成需要重写）。

**反模式**：把 `RegisterTaskDefinition(CreateOrUpdate)` 的"幂等"理解成"重写下次运行时间等统计不丢"——**错**：重写会重建触发器、并覆盖运行统计（D112 已证伪旧注释，此处同源）。

**验证**：`EnsureSchedulerTask_UpToDateDefinition_SkipsRewriteAndLogsNoRebuild` 必须成立——已存在且定义一致时日志出现"已是最新，无需重建"、且 `WriteCount` 不增长；真机验收：登录后开管理端，任务计划程序「上次运行时间」不再被重写时刻覆盖。

---

## 二十四、任务计划程序把账户名落成 SID，库读回却是裸名 —— 账户字段比对必须归一化

**症状**：F1 修复（D112 / D113）真机首验：管理端每次启动都打「调度计划任务定义已变更，已自动重新创建」/「已注册守卫计划任务」，**从不出现**「跳过重写」——逐字段比对形同虚设，触发器仍被每次重置。

**取证**：`Export-ScheduledTask` 导出 XML，`<Principal><UserId>S-1-5-21-…</UserId></Principal>` 是 **SID**；再用同一个库（TaskScheduler 2.12.2）读回同一任务：`Principal.UserId = "guyue"`（**裸账户名，丢了域前缀**；完整名 `MSI\guyue` 在 `Principal.Account` 属性里）。而写入时传的是 `WindowsIdentity.GetCurrent().Name` = `MSI\guyue`。其余字段（Delay、Repetition、ExecutionTimeLimit=PT0S、Settings 六项、Action 路径/工作目录、Description）读写往返全部保真。

**根因**：任务计划程序服务在注册时把 Principal 账户名解析成 SID 存储；库读回时把 SID 反解成**不带域的**账户名。写 `MSI\guyue` → 读 `guyue`，`OrdinalIgnoreCase` 字符串比对永远不等 → `IsDefinitionUpToDate` 每次判"变更" → 每次重写，F1 被整体架空。单测与 Fake 全绿照不到这里——这是系统服务端的规范化行为（方法论 3 的又一例）。

**做（D114 真机首验修正）**：账户字段（`Principal.UserId`、触发器 `UserId`）一律经 `ScheduledTaskGateway.SameAccount` 比较：先字符串等值短路；不等时两侧都 `Translate` 成 SID 再比；任一侧无法映射（`IdentityNotMappedException` / `ArgumentException`）按不等处理。用例锁在 `ScheduledTaskGatewayTests`（用 Everyone / BUILTIN\Administrators 等 WellKnown 账户，不依赖本机用户名）。

**做（通用）**：
- 凡与任务计划程序交换的账户字符串，**不要假设形态**：全名（`MSI\guyue`）、裸名（`guyue`）、SID（`S-1-5-…`）三种都可能出现，取决于哪个组件在哪个环节做了规范化。
- 比对前归一化成 SID，别比较原始字符串。
- 诊断这类问题要**双视角**：`Export-ScheduledTask` 看 XML 落盘形态 + 用同一版本的库读回 API 层形态，缺一不可。

**验证**：构建 0 警告 0 错误；`scripts/test.ps1` 652/0（新增 `SameAccount_NormalizesSidAndNameForms` 8 例）；真机复跑应出现「定义未变化，跳过重写」且不再出现「定义已变更」。

---

## 二十五、把「谓词为真 ⇒ 加入」改写成「谓词为真 ⇒ continue」时忘了取反（D140）

**症状**：自启动项的四个子页面，条目混在一起 —— 注册表页显示的是其余三个来源的条目，一条注册表项都没有；父菜单「自启动项」显示**空列表**，副标题却写着「全部来源 · 共 N 项」（2026-10-02 用户实测）。

**取证**：那段谓词被手写了**三遍** —— 列表（`Apply`）、扫描失败文案、`BuildSubtitle`（副标题计数）。其中一份是：

```csharp
// 改写前：谓词为真 ⇒ 加入
if (SourceFilter is not { } kind || entry.Source == kind) { Rows.Add(...); }

// 改写成：谓词为真 ⇒ 跳过     ← 取反漏了
if (SourceFilter is not { } filter || entry.Source == filter) { continue; }
```

**为什么静态检查抓不到**：括号配平、类型合法、构建 0 警告、784 个单测全绿。另外两份是**正确**的，所以副标题数字与列表行数本来就不一致 —— 只是没人对过账。

**做**：判据收归 `Core/Services/SourceFilterPolicy`，并写成**肯定式**（`IsVisible` = 该显示），调用点必须写 `if (!IsVisible(...)) continue;` —— **取反出现在眼前**，而不是藏在谓词里等人去推。

**做（通用）**：
- 判断一段谓词对不对，要**求值**（「它到底留了哪些」），不要只**读形状**（「这行看着像过滤」）。我栽在这里：逐条读过它，然后对用户说「过滤逻辑正确」。
- 同一段判据出现第二次就该收归唯一实现。第三份漂移只是时间问题。
- 用例必须**穷举**枚举里的每一个值并断言「留下的正好是它自己」，只测一两个组合正是漏掉它的原因。

---

## 二十六、只验「结果对」不验「通知次数」，差量同步等于白做（D141）

**症状**：刷新本页时，**有新增条目是增量插入的，有移除条目却整屏重建**（2026-10-02 用户实测）。

**取证**：`SyncInPlace` 的删除阶段是

```csharp
while (current.Count > desired.Count) current.RemoveAt(current.Count - 1);
```

**按位置硬砍尾部**。删中间 3 项时它删掉的是尾部同样 3 项 —— **内容仍然是对的**（所以 20.5 万组随机对拍全过），但分叉点之后每一格都要 `Replace`。而 `ObservableCollection` 的每个 `Add`/`Remove`/索引赋值都会让 WinUI `ListView` **销毁或新建一个行容器**，`Move` 才不会。新增在尾部不触发这一步 —— 于是正好是用户看到的那个不对称。

**做**：① 删除阶段按「不该再出现」从尾部往前扫、凑够就停；② 补 `Insert` 分支（新行根本不存在 + 确实还缺项 + 被顶掉的旧行后面还要 ⇒ 插入而不是覆盖，插入后旧行会自己落到正确位置）。

**做（通用）**：
- 验差量同步要验**两件**事：结果对、集合通知次数接近最小。第二件才是用户看得见的那个。
- 期望值必须是**理想值**，不能把当前实现的输出抄成期望值 —— 抄了这条用例就永远不会红，也就毫无意义。
- 不断言自己说不出理由的数字（倒序重排的最优 Move 数是纯算术细节）；改断言那个**真的要紧**的性质（「重建次数为 0」）。
- 中间反复调了三版，每版都靠随机对拍重新确认。第一版把「只有一行内容变了」从 1 次变成 21 次 —— 那是**最高频**的改动（状态翻转 / 换图标），拿它换删除的优化是亏的。**先量各形态的改动次数，再决定往哪边优化。**

---

## 二十七、按行号「覆盖」代码，括号配平时代码完全合法

**症状**：点「运行调度」，调度端确实起来了，管理端却紧跟着弹「调度未接管」；同时「按钮永不复位」那条修复**根本没生效**。

**取证**：上一轮加观察循环时脚本用了 `$l[$hit + 2] = ...`（**覆盖**），而那一行原本是 `else`：

```csharp
if (await WaitForSchedulerAsync()) {
    _toast.ShowSuccess("调度已启动", …);
        _ = WatchSchedulerExitAsync();   // ← 原来这里是 else
}
IsSchedulerRunning = false;               // ← 现在无条件执行
```

括号仍配平（`else` 块的 `}` 顶替了 `try` 的 `}`），**构建绿、单测全绿**，而失败分支变成成功之后必跑。`IsSchedulerRunning = false` 紧跟着把刚加的观察循环也掐掉了。

**做（通用）**：
- 批量改文件时，**按行号只允许插入**；覆盖必须先断言那一行的内容。
- ⚠️ 字段初始化器（`public X Y { get; set; } = …;`）**不经过属性** —— 所以「在 setter 里挂钩子」这类做法覆盖不到它。要统一入口就改成手工属性 + 构造末尾走属性赋值。
- 成对结构（`if/else`、`try/catch`、`switch` 的每个分支）在改写后**逐支核对**，这类错误在括号配平时代码完全合法。

---

## 二十八、接口返回「计数」而调用方需要「是哪几条」（二次审计 S1）

**症状**：守卫同步过期路径时，整批只要有 1 条成功，**全部 N 条都报成功**。该 `Detail` 进守卫归档并计入副标题「已纠正 N 项」—— 用户以为路径已经跟上，而调度端仍在跳过那个程序。

**取证**：

```csharp
var applied = _configEdit.ResyncPaths(resyncs);          // int：只有"成功几条"
var done = applied > 0 && resyncs.Any(applied => applied.ItemId == resync.ItemId);
//              ^^^^^^^^^ int ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ lambda 参数遮蔽了它
```

两个错误叠加：**lambda 参数 `applied` 遮蔽了外层的 `int applied`**；**`Any` 查的是 `resyncs` 自己**，而 `resync` 取自 `resyncs` ⇒ **必然匹配自身 ⇒ 恒真**。

**做**：`ResyncPaths` 返回 `IReadOnlySet<string>`，调用方 `Contains(resync.ItemId)`。**改签名而不是在调用方打补丁** —— 接口的表达力不足会被调用方用「猜」来补，而「猜」不会报错；压制症状只会让下一个调用方重蹈覆辙。

**做（通用）**：
- 返回 `int` / `bool` 这类**聚合值**的接口，一旦调用方需要知道「哪几条」，就该换成集合。症状是调用方开始对着入参反查。
- **两个极端恰好都对**的缺陷最容易潜伏：这里 `applied == 0` 与「整批都成功」都给出正确答案，**只有部分成功才暴露**。写用例要专门造中间态。
- ⚠️ **测试名承诺 ≠ 测试覆盖**：有一个用例叫 `…ButItemGoneFromConfig…DoesNotClaimSuccess`，注释写着「随后把它从配置里删掉」，函数体却从头到尾没删过任何东西 —— 它重复了上一个用例。**这类假绿比没有测试更危险**，因为它让人以为那条路验过了。
- 造「判定与写入之间状态变了」的夹缝时注意：测试替身的深拷贝可能不够。`InMemoryConfigStore.Copy` 对 `Items` 是**浅拷贝**，`DelayedItem` 实例在所有副本之间共享 —— 就地改会让每一份副本都跟着变，必须**整体替换列表**。

---

## 二十九、采信子 agent / 二次意见的结论而不自己追证据链

**症状**：我把一条子 agent 给的结论原样写进了审计说明，方向是**反的**。

**取证**：那条结论说「`InvalidateAfterCorrections` 只标脏当前来源，会让其它来源页面显示过期状态，应改成标脏全部」。二次审计推翻它，理由是 `CoveredScopes → AddUnprocessedScopes 并入 failures → failureArray → TargetPathResync.cs:87-89 用它过滤` 这条链保证：**管理端带筛选时，守卫只可能纠正该来源的条目**。我逐环节核实后确认二次审计是对的，**不改**。

**做（通用）**：
- 子 agent 与二次审计都是**线索来源**，不是结论来源。采信之前把它的证据链**逐环节自己读一遍** —— 成本是几分钟，收益是不把错的结论写进文档、也不照着它做一次负收益的改动。
- 写进审计文档的每一条「已判定为 X」，都要能当场给出 `文件:行号` 的依据。给不出来就标明「未验证」，别让下一个排查的人沿错误前提去推 —— 本轮就因此误诊过一次（D140 被误诊成指纹门）。
- 与第二十五、二十六是同一类错误的三个面：**认形状而不求值**、**只验一半**、**引用而不验证**。

---

## 三十、Inno Pascal Script 的三处编译期错误，静态检查一个都照不出来

**症状**：给 `.iss` 加了一段 `[Code]`（提权 `taskkill` + 带重试的清目录），**静态检查全绿就提交了**，并如实写了"未经编译验证"。用户跑 `installer\build-installer.ps1` 直接炸，三个错误全在我自己身上。

**取证**（`build-installer.ps1:270` 抛的那句 `ISCC 编译失败（exit 2）` 里只有第一行有信息量）：

| 报错 | 真实成因 |
|---|---|
| `Error on line 635 ... Column 33: colon (':') expected` | 写了 `function KillDelayStartProcesses;`。**Inno 的 Pascal Script 里 `function` 必须带返回类型**（函数名之后的 `:`），不返回值的该写 `procedure`。报错的列号正是那个分号所在位置 —— 也就是它在等 `:` 的地方。 |
| 随后在调用处报类型不匹配 | `ShellExec` 末参是 `var ErrorCode: Integer`，我声明成了 `String`，`ErrCode := '0'` 也随之错。签名以官方为准：`ShellExec(const Verb, Filename, Params, WorkingDir: String; const ShowCmd: Integer; const Wait: TExecWait; var ErrorCode: Integer): Boolean`。 |
| `Error on line 772 ... Column 1: Identifier expected` | **772 行是既有的 `procedure CurStepChanged`，不是我新写的代码。** 真实原因在上游：删旧代码时用 `RemoveRange($hit, 3)` 按固定行数删三行，第三行是 `PrepareToInstall` 的收尾 `end;`，被一起吃掉了。后续解析器在下一个声明处才炸。 |

**做（通用）**：
- 🔴 **`.iss` 的静态检查几乎等于没有**：括号配平、单引号配平、"没有未使用变量"这些我都过了，三个错误一个都不影响它们 —— 被删掉的 `end;` 恰恰让 `begin`/`end` 依然配平，所以配平检查给的是**假绿**。`.iss` 只能靠 ISCC 真编译，**别把"静态过了"写成"验证过了"**。
- **报错行号会落在受害者身上，不在肇事者身上**。看到 `Identifier expected` 指向一段你没碰过的既有代码，第一反应应该是"我上面少了闭合"，而不是"这行有问题"。本轮那三个错误里有两个是这种形态。
- **按固定行数批量删代码是这轮的实际肇因**。第三行的内容当时检查过（断言是 `end;`）却仍在删除范围内 —— 检查的是"这三行是什么"，不是"我该不该删这三行"。删多行前先想清楚**最后一行是不是别人的收尾**。
- ISCC 可以在几秒内给出真结论，本机就有（`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`）。**手上没有 ISCC 时应当明说"环境不具备验证条件"**，而不是照样提交 —— 差别只在于一句话，但代价是让用户替你跑一遍。

## 三十一、Inno `[Code]` 里的"看着像标准库"的名字，一半不存在

**症状**：给卸载结果文件改名，想用"临时文件"API 拼一个随机名，凭印象写了三个函数名。ISCC 立刻报 `Unknown identifier 'GetTempFileName'`。

**取证**（2026-10-03，用一个十几行的探针 `.iss` 逐个试，ISCC 6.7.3）：

| 名字 | `[Code]` 里 | 备注 |
|---|---|---|
| `Random(Int64)` | ✅ 可用 | **每次进程启动重新播种** —— 连跑三次输出三组完全不同的数，所以它做得出不可预测的文件名 |
| `CreateDir` | ✅ 可用 | |
| `GetTempFileName('.txt')` | ❌ `Unknown identifier` | 只有编译期（ISPP）侧有 |
| `GetTickCount` | ❌ `Unknown identifier` | 想给随机名再加一份熵，用不了 |
| `FormatDateTime(...)` | ❌ `Unknown identifier` | 同上，时间戳拼不进去 |

另外一条：**`procedure InitializeSetup();` 会报 `Invalid prototype`** —— 因为该事件点的原型是 `function InitializeSetup(): Boolean`，声明成 procedure 就算原型不符（声明成 `procedure InitializeUninstall();` 反而是对的）。

- 🔴 **别按"文档里好像有"的名字写**。ISHelp 的 chm 讲的是整个 Inno 语言（含预处理期），**`[Code]` 的 Pascal Script 只是它的子集**；名字在文档里出现过 ≠ 在 `[Code]` 里能编过。
- 🔴 **探针 `.iss` 是这里最便宜的手段**：写一个只有 `[Setup]` + 一个空 `[Code]` 的十几行脚本，`ISCC.exe /O<临时目录> <探针.iss>`，几秒给出真结论。切勿改真的 `.iss` 去"试试看能不能编过"。
- **要验证的从来不只是"能不能编过"**：本机有 ISCC 时，跑 `installer/build-installer.ps1` 走一次真编译（注意它要 **pwsh**，Windows PowerShell 5.1 会被 `#requires -Version 7` 挡下），比任何静态检查都强 —— 上一条"静态检查约等于没有"说的正是这件事的另一半。

---

## 三十二、把「用户可写位置」当成可信来源（本项目的配置与安装目录都在其中）

**症状**：安全审计把「配置位于用户可写目录 `%LOCALAPPDATA%\DelayStart\config\app.json`，
其中的 `Path` / `Arguments` / `RunAsAdmin` 由文件内容决定，而 High 调度端照着启动」标成
**high**；同族的还有「`.ps1` 的 PowerShell 宿主探测把 `PATH` 与
`%LOCALAPPDATA%\Microsoft\WindowsApps` 排在前两位」（`Core/Services/PowerShellHost.cs`）。
看起来是一条完整的无 UAC 本地提权链。

**结论**：**不修，也不试图加防护。** 本项目的**配置目录与安装目录都是当前用户可写的**，
`config\app.json` 里的 `Path` / `Arguments` / `RunAsAdmin` 由文件内容决定，
所以它**不是信任边界**。而且**给它加防护也没有意义**：
- **ACL 挡不住同用户进程** —— 能改这个文件的进程本来就是那个用户，给它加「仅当前用户 + SYSTEM」
  等于没加；
- **加密挡不住有权改该用户文件的人** —— 密钥要么在同一个用户上下文里（连同密文一起可取），
  要么得靠 DPAPI 绑用户（同样同用户可解）。

**取证（2026-10-03 实测）**：

```
> icacls C:\Users\<用户>\AppData\Local\Programs\DelayStart
  NT AUTHORITY\SYSTEM:(I)(OI)(CI)(F)
  BUILTIN\Administrators:(I)(OI)(CI)(F)
  <当前用户>:(I)(OI)(CI)(F)
```

当前用户对**安装目录**是 `(F)` 完全控制 ⇒ 同用户的 Medium 进程**可以直接覆写
`DelayStart.Scheduler.exe`**。这条路（换掉整个 High 进程的代码）**严格强于**改 JSON
里的启动目标 —— 所以「篡改配置」这个动作**没有引入任何新的攻击面**：
能改配置的攻击者本来就已经能做更坏的事。反过来给配置加 ACL 也不会让安装目录变得不可写，
只是把同一个洞从一处挪到另一处。

**同族**：PowerShell 宿主探测顺序（①`%ProgramFiles%\PowerShell\7` → ②`…7-preview`
→ ③`PATH` → ④`%LOCALAPPDATA%\Microsoft\WindowsApps` → ⑤`%SystemRoot%\…\powershell.exe`），
③④ 确为用户可写位置，与上面同一条。真正收紧要限定 `%ProgramFiles%` / `%SystemRoot%`，
那会碰 D128 的启动链，**须单独立项**，不在「顺手修一下」的范围内。

**与 D110 的关系**：D110 自己写了「掌握 HKLM 写入 + 计划任务注册 + 常驻托盘 +
注册协议处理器 ⇒ 已越过 PUA 判定的高风险特征线」。本条是那条判断的**延续** ——
按这个产品形态（per-user 安装、无常驻服务、靠计划任务以 `Highest` 跑起来），
配置放在用户目录是必然的，不是疏漏。相应地，`design.md` 的 **NFR-6.7 与路径布局表
把「安装目录只读」改成了「程序运行时不向安装目录写任何东西」** ———
原措辞容易被误读成 ACL 只读，而实际上那个目录继承 `%LOCALAPPDATA%` 的用户可写 ACL。

**反面教训（这条本身就是教训）**：security-reviewer 标 high 用的是「配置文件应当防篡改」
的通用模型，**没有结合本项目的 per-user 安装形态**。审计时**必须先查清安装目录的 ACL
再定级**：本机一条 `icacls` 就能定性，而它把结论从「高危漏洞」翻成了
「产品形态的固有暴露面」。定级前少跑一条命令，报告就会指向一个不存在的修复。
这也正是 `pitfalls.md` 二十九「引用而不验证」的另一个面：连**通用安全模型**也要拿本机事实校一遍。

---

## 三十三、把"已经告诉过用户"记在**别人的数据结构**里（不改字段 ≠ 零代价，D148）

**形态**：守卫要实现"同一条失效只通知一次"。最省事的做法是复用已有的扫描基线
`guard\baseline.json` —— 它里面有每条目的 `IsMissing`，正好能回答"上一轮还好着吗"，
于是**一个持久化字段都不用新增**。

**后果（用户看得见的形态）**：手动添加的 exe 被卸载后，守卫**从此一条通知都不会发**。
用户看到的是「延时启动」页那一行失效徽标，和一份完全正常的 `guard.log`，
而他卸载程序这件事本身**没有任何迹象**。更糟的是这不是配置问题、不是权限问题、
不是偶发 —— 它是这一类条目的**永久**行为，重装、重新接管、改设置都不会改变。

**为什么会这样**：基线是"上一次**扫描**快照"，它的条目全部来自扫描结果。
而手动条目（`Source=Manual`）在系统里没有锚点，七个来源实例里根本没有它 ——
它**从来就不在基线里**。于是差集判据的第一道门（"上轮基线含该项吗"）永远不通过。
"不新增字段"这个约束本身没错，它错在**把判据的载体选窄了**，然后让功能去适应载体。

**同一次改动里被载体逼出来的还有两样**：

- `StaleKind.SourceLost` 的"一律算新的"特判 —— 源丢失的条目下一轮就不在基线里了，
  没有 `IsMissing` 可翻转，只能靠"上轮在不在"这一支硬兜；
- `GuardService.RunOnce(advanceBaseline: false)` —— 管理端两页也跑巡检，
  它们会把基线推进掉，于是要加一个参数让它们"别推进"。
  那是把一条**结构约束降级成了调用约定**：每个新调用点都得记得传对，漏一个就重现
  "用户卸载程序后先打开管理端、那条唯一的通知被吃掉"。

**规则**：一份持久化状态回答<b>一个问题</b>，就配一个文件。
"上次扫描到了什么"（基线）与"哪些已经通知过用户"（通知状态）是两个问题，
哪怕后者能从前者推出来 —— 能推出来的那份**覆盖不全**（手动条目就是缺口），
而且推导规则会随判定口径变化而漂移。

**正解（同一个改动的最终形态）**：状态独立落盘 `guard\notify-state.json`，
按**条目主键**记账，与条目从哪来无关。判据随之塌缩成"主键在不在已通报集合里"，
两档特判与开关参数一起消失。顺带得到一条更强的所有权保证：
**谁有资格写这份状态由文件归属决定**（只有守卫进程构造那个 filter），
不需要在每次调用时传一个布尔量。

**通用形态**：加字段之前先问一句"这个状态回答的是不是同一个问题"。
载体与问题错配的代价，不会体现在"要不要改 schema"上，
而会体现在**某一类用户输入上永久静默** —— 而那一类通常正是最少被测到的。

---

## 教训方法论

1. **先取证再改**：报错框是证据不是结论——"读到 A 要求 B、本机只有 C"要直接调一次探针验证（D64 的 DDLM 假铁证）。
2. **涉令牌/权限/完整性的结论，必须测到进程完整性级别**，不能停在"启动成功"（D42→D44→D45 两度翻转）。
3. **单测与构建都绿 ≠ 真机可用**：InvariantGlobalization、publish 缺 XBF、多触发器粒度全是单测照不到的角落——出口条件必须含真机项。
4. **能直接调一次 API 就别推理**；红/绿对照证明用例真的钉住了缺陷。
5. **用户报告的现象先 1:1 复现再修**；修完用"反向放回病根文件"验证因果。
6. **"顺序对" ≠ "布局对"**：与 Win32 结构体打交道时，先核对 SDK 头文件的 packing（`pshpack1.h` / `poppack.h`）与本机实测 `sizeof`，再谈字段取值。顺序、偏移、大小是三件独立的事；用同一 HRESULT 复现所有字段取值组合，就是布局错的信号。
7. **安全定级前先量安装目录的 ACL**：`icacls %LOCALAPPDATA%\Programs\<本程序>` 一条命令就能判定
   "用户可写的配置"到底是不是一条新攻击面 —— 若当前用户对**程序目录**也有 `(F)`，
   那么它严格强于改配置，加固等于把同一个洞挪位置（本节三十二）。
