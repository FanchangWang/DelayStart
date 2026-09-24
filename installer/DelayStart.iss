; DelayStart.iss —— Inno Setup 6 安装脚本（D22 决策 / D60 多形态多架构扩展 / D61·D62 真机修复）
;
; ★ 正式构建请用 installer\build-installer.ps1（它先 publish 两个产物、再从
;   Directory.Build.props 取版本号，最后调用本脚本）。手工编译见下方缺省值。
;
; 编译参数（全部可选，缺省值 = 本地 win-x64 自包含）：
;   /DAppVersion=0.1.0            安装程序版本（唯一来源：Directory.Build.props 的 <Version>）
;   /DRid=win-x64                 目标架构，仅用于产物命名
;   /DSlim                        传入即"精简版"（框架依赖，不含运行时）——
;                                 ⚠️ 判据是"有没有定义"，不要传 /DSlim=0 表达"否"，不传即可
;   /DPublishDir=<绝对路径>        管理端 publish 目录
;   /DSchedulerDir=<绝对路径>      调度端 publish 目录
;   /DTargetArch=x64compatible    允许安装的架构（arm64 包传 arm64）
;   /DWinAppRuntimeUrl=<url>      Windows App Runtime 下载链接（按架构不同）
;   /DWinAppRuntimeArch=x64       框架包名字里的架构段（x64 / arm64），与 {#Rid} 同步
;
; 产物：dist\DelayStart-Setup-<版本>-<Rid>[-slim].exe
;
; 🔴 本文件有一处 Inno 的硬约束：**任何一行都不能以 `[` 开头**（含 [Code] 段内、
;    含缩进后的 `[`）—— ISCC 的解析器在需要 section 头的位置看到行首 `[`
;    就报 "Invalid section tag"，即使那行是合法的 Pascal 数组字面量。
;    所以数组字面量必须跟在别的记号后面同一行（见 InitializeSetup 末尾）。
;
; D22 决策要点（详见 docs/requirements.md 决策表）：
;   · 固定安装路径 %LOCALAPPDATA%\Programs\DelayStart，不允许用户改（计划任务按固定路径注册，
;     用户自定义路径会让升级后的任务指向旧位置）。
;   · 不做 MSIX。首启的调度任务注册是幂等的，由程序自己完成，安装器只放文件。
;   · 卸载必须可逆：InitializeUninstall 里跑 --restore-all，退出码非 0 直接中止卸载
;     （🔴 不能写在 [UninstallRun] —— 那里读不到退出码，失败也照删文件）。
;   · **默认保留**配置与日志；D62 起在卸载时询问用户是否一并清除（默认"否"）。
;
; D61（2026-09-21 第一轮真机）：应用名去中文、[Run] 加 shellexec（740）、
;   卸载改 ShellExec('runas') + --result-file 回读退出码、精简版运行时检测修误报。
;
; D63（2026-09-21 第三轮真机 / 用户反馈）：
;   ① "设置 → 应用"里的显示名回归裸 `DelayStart`（原为 `DelayStart <版本>[-slim]`）——
;      见 [Setup] 里 AppVerName 的说明。
;   ② 计划任务不归安装器管（NFR-6.5）：管理端**每次启动**检测、缺失即自动补建
;      （2026-09-21 改判，SchedulerTaskBootstrap；原 D63 为仅首启注册一次）。安装器仍然只放文件。
;
; D62（2026-09-21 第二轮真机）：
;   ① 精简版在**缺运行时**的机器上不再自动启动（[Run] 的 Check 直接隐藏该项），
;      改为安装结束时给出可读说明 + 下载链接 —— 原先用户选了"先装程序"，装完
;      自动拉起的还是那个 apphost 的 ".NET not found" 报错框，等于白提示一次。
;   ② 桌面快捷方式改为**可选任务**（D61 时是强制创建），安装向导里给勾选框。
;   ③ 卸载时询问"是否一并删除配置与日志"，默认否；静默卸载一律按"否"处理。
;   ④ SetupIconFile 指向应用图标，安装程序自身与卸载入口都显示 DelayStart 图标。
;
; D64（2026-09-21 第四轮真机 / 用户批复 D64-1=A、D64-2=A）：
;   ① **装之前清空安装目录**（[Code] 的 PrepareToInstall）。两种形态（自包含 / 框架依赖）
;      共用同一个 AppId 与同一个安装目录，覆盖安装只覆盖同名文件、**不清理对方形态的残留**
;      —— 实测"先装 full 再装 slim"会把 full 的 hostfxr.dll / coreclr.dll 留在目录里，
;      而 .NET 的 apphost **只要在自己目录里看到 hostfxr.dll，就把"运行时根"当成程序目录**，
;      于是精简版报 "You must install or update .NET"（哪怕机器上装着 10.0.12，报错里
;      还会列出一份 x86 的 10.0.12 来自证"真的装了"）。清掉的只有程序文件：配置在
;      %APPDATA%、日志在 %LOCALAPPDATA%、计划任务在目录外，**不动用户任何数据**。
;      🔴 清理放在 PrepareToInstall 里是有原因的 —— Inno 文档保证它早于 Setup 的
;      "文件占用检查"（CloseApplications / Restart Manager）执行；而安装器是 lowest
;      权限，删不掉**提权进程**占用的文件（管理端与调度端都是提权跑的），真正还在运行的
;      占用者只能交给 Restart Manager 关掉 —— 🔴 但**它不会把它们自动重启回来**（重启只对
;      调用过 RegisterApplicationRestart 的程序生效，我们没调），详见 [Setup] 段那段说明。
;      🔴 唯一会**中止安装**的情况：精简版发现 hostfxr.dll 删不掉（＝自包含形态的管理端
;      还在运行）。此时中止比装出一个起不来的程序好；且这一步在清理之前，中止时尚未
;      删除任何文件，旧安装保持完整可用。
;   ② 缺运行时的下载入口**按缺哪项决定**（原来两个地址无条件全列、点「是」也只开 .NET
;      下载页）—— 只缺 Windows App Runtime 的用户被引去装一个已经装好的 .NET，装完
;      回来还是缺，等于把"误导"从报错框搬到了安装器里。
;
; D84（2026-09-23 用户批复，共 5 点）：
;   ① 桌面快捷方式恢复**默认勾选**（D62 当时的 unchecked 与用户预期相反）：GUI 与静默
;      安装都会创建；静默想排除用 /MERGETASKS="!desktopicon"。
;   ② 卸载的"是否删除配置与日志"从 MsgBox 改为**自定义勾选框对话框**（AskDeleteUserData），
;      默认不勾 = 保留。Inno 卸载向导不支持插入自定义页，故用自绘模态窗体。
;   ③ 静默卸载支持 **/DELETEDATA**：传参即删配置与日志，未传 = 保留（绝不静默删数据）。
;   ④ 还原失败分支三处 MsgBox 改 **SuppressibleMsgBox** —— 普通 MsgBox 在静默卸载时
;      不会被自动跳过，自动化卸载会卡死在无人值守的弹窗上。
;   ⑤ 顺手修复还原失败分支的 Result 逻辑 bug：原写法点「否」也继续卸载；改为默认中止、
;      显式选「强行卸载」才放行（静默默认 = 中止，宁可不卸不可把用户锁死在接管状态）。
;
; D85（2026-09-23 用户批复，卸载交互定形）：
;   ① D84 的自绘勾选框对话框废弃，改 **TaskDialogMsgBox 原生任务对话框**（AskUninstallOptions）：
;      三按钮「保留配置并卸载（默认焦点）/ 删除配置并卸载 / 取消」——TaskDialogMsgBox
;      没有验证复选框参数（签名 6 参，官方文档源 + ISCC 实证），勾选框不可用；
;      也没有默认按钮参数 —— 默认焦点恒在第一个按钮，故「保留」必须排第一
;      （首版把「删除」排第一，2026-09-23 用户实测发现默认焦点落错后修正）。
;      文案（同日批复）：Instruction「卸载 DelayStart」+ 正文两行实际展开的
;      配置目录 / 日志目录路径。
;   ② 对话框**前置到 InitializeUninstall 开头**（还原之前）：点「取消」= 终止卸载且
;      接管项一个没动。
;   ③ WizardStyle 加 **dynamic**：向导与任务对话框跟随系统深浅色（Inno 6.6+ 深色模式）。

#define AppName "DelayStart"
; D6（2026-09-24 用户批复）：发布者由裸 `DelayStart` 改为 `FanchangWang`。
;   理由：winget 包标识前缀是 `FanchangWang.DelayStart`（D110），而 manifest 的 `Publisher`
;   必须与"设置 → 应用"里显示的实际发布者一致 —— 三处口径就此统一。
;   ⚠️ 这一项只喂给 ARP 的**发布者**列：老用户升级后会看到发布者名由 DelayStart 变成
;   FanchangWang（纯显示，无功能影响）。AppId 刻意不动 —— 见 D111。
#define AppPublisher "FanchangWang"
#define AppExe "DelayStart.exe"
#define SchedulerExe "DelayStart.Scheduler.exe"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Rid
  #define Rid "win-x64"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
#endif
#ifndef SchedulerDir
  #define SchedulerDir "..\src\DelayStart.Scheduler\bin\Release\net10.0-windows\win-x64\publish"
#endif
#ifndef TargetArch
  #define TargetArch "x64compatible"
#endif
#ifndef DotNetUrl
  #define DotNetUrl "https://dotnet.microsoft.com/download/dotnet/10.0"
#endif
#ifndef WinAppRuntimeUrl
  #define WinAppRuntimeUrl "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe"
#endif
; 应用图标：既给安装程序自身（SetupIconFile），也是卸载入口的图标来源。
; 相对路径按本脚本所在目录（installer\）解析。
#ifndef AppIconFile
  #define AppIconFile "..\src\DelayStart.App\Assets\AppIcon.ico"
#endif
; Windows App Runtime 框架包全名形如 Microsoft.WindowsAppRuntime.1.8_<版本>_<架构>__8wekyb3d8bbwe，
; 检测时要连架构一起匹配 —— arm64 机器上只装了 x64 框架包时，arm64 版程序照样起不来。
#ifndef WinAppRuntimeArch
  #define WinAppRuntimeArch "x64"
#endif

; 形态后缀**只用于产物文件名**（DelayStart-Setup-<版本>-<Rid>-slim.exe）——
; 两种形态的 AppId 相同、装在同一个目录，用户不该同时装两个。
; 🔴 它刻意不参与 AppVerName：那一项决定"设置 → 应用"列表里的显示名，
;    而那里只该出现 DelayStart（D63）。
#ifdef Slim
  #define FlavorSuffix "-slim"
#else
  #define FlavorSuffix ""
#endif

[Setup]
; 🔴 AppId 必须是**合法 GUID**（D122，2026-09-25 修正）。原值
;   `{8F4C0B6A-2C1D-4E3B-9A5F-DELAYSTART001}` 的末段是 13 个字符且含 L/Y/S/T
;   等非十六进制字符 —— 它不是 GUID，只能靠"首字符非数字"这个歪打正着的规则
;   区分大小写，而 winget 侧要求 ProductCode 与实际注册表键逐字对应。
;   ⚠️ **改 AppId 会被 Inno 当成一个全新产品**（卸载注册表键 = AppId + "_is1"），
;   旧版的卸载器将不再被新版识别。因此本项目规定：**跨此版本必须先卸载旧版**
;   （D122 / D121 —— 下一个版本同时是配置不兼容版本，本来就要先卸载）。
;   外层 {} 是 Inno 转义字面量花括号，内层才是真正的 GUID。
AppId={{48BC0E34-50D1-4D82-908C-4A5C11AC5690}
AppName={#AppName}
AppVersion={#AppVersion}
; 🔴 "设置 → 应用"（原"应用和功能"）里的**显示名**取的就是 AppVerName，不是 AppName。
;    缺省值是 `AppName + " version " + AppVersion`，所以不显式写就会看到
;    "DelayStart 0.1.0"；D60 起还拼过形态后缀变成 "DelayStart 0.1.0-slim"（2026-09-21 实测）。
;    D63 起固定为裸 AppName —— 版本号由 DisplayVersion 单独承载（系统自己会显示），
;    形态后缀只留在产物文件名里。
AppVerName={#AppName}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
; 固定路径：禁止 "为所有用户安装"，也不允许改目录
PrivilegesRequired=lowest
; 架构防呆：x64 包的 x64compatible 允许 arm64（arm64 可模拟运行 x64）；
; arm64 包传 arm64，装到别的架构上会被直接拒绝。
ArchitecturesAllowed={#TargetArch}
; 安装程序自身 / 卸载入口的图标 = 应用图标（D62）
SetupIconFile={#AppIconFile}
OutputBaseFilename=DelayStart-Setup-{#AppVersion}-{#Rid}{#FlavorSuffix}
OutputDir=..\dist
Compression=lzma2
SolidCompression=yes
; D85：modern dynamic = 跟随 Windows 系统深浅色（Inno 6.6+ 的安装器/卸载器深色模式，
; 含任务对话框样式化）。用户没有自定义向导图，深浅色切换无副作用。
WizardStyle=modern dynamic
UninstallDisplayIcon={app}\{#AppExe}
; 关闭系统重启提示：本软件没有锁文件，重启无关紧要
RestartIfNeededByRun=no
; D64-1：安装前 [Code] 会尽力清空 {app} 里的旧文件；但管理端与调度端都是**提权**运行的，
; 非提权的安装器删不掉它们占用的文件 —— 那部分交给 Windows Restart Manager：它会列出
; 正在占用待替换文件的应用，征得用户同意后关掉它们。
; 🔴 两条必须知道的边界（都是查 Inno 文档确认的，别照直觉假设）：
;   ① `RestartApplications` **只对调用过 Windows `RegisterApplicationRestart` API 的程序
;      生效**（Inno 文档原文），而 DelayStart 与调度端都没有调用 → 装完后它们**不会**被
;      自动重启回来。实际后果：调度端在安装时被关掉的话，托盘图标要到**下次登录**才会
;      回来（它的计划任务只在登录时触发），本次登录尚未到点的延时条目也随之下次登录补上。
;      ⚠️ 这不是 D64 引入的：任何一次升级都要替换调度端 exe，而它一直在运行。
;   ② CloseApplications 的"文件占用"检查发生在 [Code] 的 PrepareToInstall **之后**，
;      两者不会互相抢：被占用的文件我们的清理删不掉，它必然还在原地等着 Restart Manager 发现。
CloseApplications=yes
RestartApplications=yes

[Languages]
; D65：中文语言文件**随仓库分发**（installer\languages\ChineseSimplified.isl）。
; 🔴 不能用 "compiler:Languages\ChineseSimplified.isl"。官方 Inno Setup 安装器**不带**简体中文 .isl
;    —— 它属于"用户贡献翻译"，要从 https://jrsoftware.org/files/istrans/ 单独下载
;    （上游：github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation）。
;    所以那句只在"本机碰巧手工往 Inno 安装目录里放过这个文件"的机器上成立：
;    2026-09-21 GitHub Actions 首次运行即因此失败 —— ISCC exit 2，日志停在
;    "Reading file: C:\Program Files (x86)\Inno Setup 6\Languages\ChineseSimplified.isl"。
; 相对路径按**本 .iss 所在目录**解析（实测确认，与 ISCC 的当前工作目录无关），故写成
; installer\languages\ 下的仓库文件 —— 任何机器、任何 Inno 版本都不会再找不到。
; 前面垫一个 compiler:Default.isl 是给"语言文件与编译器版本不完全对齐"兜底：
; 缺哪个 message 就静默回落英文；不垫底的话 ISCC 会打印
; "Warning: A message named "X" has not been defined ... Will use the English message"。
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,languages\ChineseSimplified.isl"

[Tasks]
; 桌面快捷方式是可选项（可被 /MERGETASKS 排除），但**默认勾选**（2026-09-23 用户批复，
; D84）—— 不写 Flags 时 Inno 的默认就是勾选，GUI 与静默安装（/SILENT /VERYSILENT）
; 都会创建桌面图标。静默时想不建：/MERGETASKS="!desktopicon"（详见 installer\README.md）。
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
; 管理端：自包含（≈217 MB / 536 文件）或框架依赖（≈15 MB）整个放入
; ⚠️ D61：publish 目录里必须含 App.xbf / MainWindow.xbf / Views\*.xbf / Dialogs\*.xbf /
;    DelayStart.pri / Assets\AppIcon.ico —— 少了它们程序一启动就在 Microsoft.UI.Xaml.dll
;    里 0xc000027b 秒崩。这些文件原本不进 publish，由 App.csproj 的
;    CopyWinUIResourcesToPublishDir 补上；若这里装出来的程序崩溃，先查那 11 个文件在不在。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 守卫 DelayStart.Guard.exe（D75）：与管理端同落点（{app} 根目录）、同形态，且
; build-installer.ps1 把它的 publish 输出 **-o 进了管理端 publish 目录**，
; 因此由上面那条通配符一并部署 —— 这里刻意不重复写一行（重复只会让两处指令各自漂移）。
; 🔴 若改动 build-installer.ps1 里守卫的 -o 目标，这条覆盖关系就断了，记得同步这里。
;    守卫**不走 AOT**（它经 Management 调 COM，见 docs/pitfalls.md 五 R12），所以不放在 {#SchedulerDir} 那两条里。
; 通知中转器 DelayStart.NotifyBroker.exe（N1，2026-09-22 批复）：调度完成通知的代发进程，
;    由调度端降权拉起。与守卫同款处理 —— 非 AOT、形态跟随管理端、publish 输出 -o 进
;    管理端 publish 目录，由上面的通配符一并部署，这里同样刻意不重复写一行。
; 调度端：AOT 单文件，放在安装根（调度任务按此路径注册）。
; ⚠️ 调度端两种形态都用 AOT —— 它不依赖 .NET 运行时，精简版用户因此只需补两个运行时而不是三个。
Source: "{#SchedulerDir}\{#SchedulerExe}"; DestDir: "{app}"; Flags: ignoreversion
; UIAccess 中转器（D70）：调度端降权链的第二跳，预检到 uiAccess="true" 目标时由调度端降权拉起；
; 缺失时该类条目判失败（调度端有明确日志），不影响其他条目。
Source: "{#SchedulerDir}\DelayStart.LaunchBroker.exe"; DestDir: "{app}"; Flags: ignoreversion
; （D40：普通用户代理 DelayStart.Agent 已删除 —— 普通条目由调度端亲自降权启动，不再需要第二个 exe；
;   D70 又引入了中转器 DelayStart.LaunchBroker，但它只服务 uiAccess 目标，与当年代理不是一回事）

[Icons]
; 开始菜单恒建；桌面项由 [Tasks] 的 desktopicon 控制（D62）。
; {autoprograms} / {autodesktop} 在 lowest 安装模式下都落在**当前用户**的目录里，
; 与"程序装在 %LOCALAPPDATA%"的定位一致，不会去碰 all users。
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; 首启勾选项：装完直接打开管理端（调度任务由程序首启幂等注册，不归安装器管）。
; 🔴 必须带 shellexec：安装器是 lowest 权限，而管理端需要一个**用户可见的**启动路径
;    （UAC 弹窗要挂在用户点的那一下上）。D82 起管理端清单是 asInvoker，提权由它自己在
;    入口申请（`Program.Main` 的提权门），所以"740（ERROR_ELEVATION_REQUIRED）"
;    这个老问题已经不存在了 —— 但 shellexec 保留：它让启动经过外壳，
;    与双击快捷方式走的是同一条路，行为一致、好排查。（旧注释说"按 manifest 弹 UAC"，
;    D82 后 UAC 是程序自己发的，不由外壳代劳。）
; 🔴 D62 的 Check：精简版在缺运行时的机器上**直接隐藏这一项**。否则用户点了"启动"，
;    得到的是 apphost 那句 "You must install or update .NET to run this application"
;    —— 一个没有任何上下文、也指不到安装器的报错框。改由 CurStepChanged 给可读说明。
Filename: "{app}\{#AppExe}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent shellexec; Check: RuntimeReadyForApp

[Code]
// ⚠️ 不要在这里重复声明 FILE_ATTRIBUTE_DIRECTORY —— Inno 的 Pascal 自带这个常量
//    （重复声明报 "Duplicate identifier"，2026-09-21 实测踩过一次）。

var
  // 卸载是否同时删除配置与日志（D84 立项 / D85 定形，2026-09-23 批复）。取值来源：
  //   · GUI 卸载 = AskUninstallOptions 任务对话框的三按钮（保留配置并卸载〔默认〕/ 删除配置并卸载 / 取消）；
  //   · 静默卸载 = 命令行 /DELETEDATA 参数（未传 = 保留，绝不静默删数据）。
  DeleteUserData: Boolean;
  // usUninstall 阶段删除数据的结果说明：'' = 没删（保留）；非空 = usPostUninstall 显示。
  UserDataNote: String;

#ifdef Slim
// ---------------------------------------------------------------------------
// 精简版（框架依赖）的运行时检测（D60-3，D61 修正判据，D62 补齐后续动作）
//
// 精简版不含任何运行时，缺运行时程序会直接起不来。检测放在 InitializeSetup，
// 目的是**在安装前**就告知，而不是等用户双击图标拿到一个没有上下文的报错。
// 🔴 不阻断安装：用户完全可以选择先装程序、稍后补装运行时 —— 但那时必须
//    **跳过自动启动**（见 [Run] 的 Check），否则等于白提示一次（D62 的教训）。
//
// 🔴 D61 教训：最初两项检测**各错一次**，在已经装了 .NET 10 Runtime 与
//    Windows App Runtime 1.8 的机器上双双误报"缺失"。根因是查错了注册表位置：
//    ① .NET 记录落在 **32 位视图**（WOW6432Node）；只查 HKLM64 读不到。
//    ② Windows App Runtime 是**按用户**注册的框架包，在 HKCU；HKLM 下同名键
//       只有几条系统内置项。只查 HKLM64 读不到。
//    误报比不检测更糟：用户明明装了运行时，却被劝去下载安装包。
// ---------------------------------------------------------------------------

// ── 判据 ① 共享框架目录：文件在不在，才是运行时能不能用的最终事实 ──────────
// 用通配找 10.* 子目录，因此不必预先知道具体版本号（10.0.12、10.0.13… 都认）。
function DotNetSharedFxDirHasMajor10(const SharedFxRoot: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(SharedFxRoot + '\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// ── 判据 ②③ 注册表：两种视图都要读 ────────────────────────────────────────
// 2026-09-21 实测本机：数据在
//   HKLM\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\...
// 而 64 位视图下只有一条 x64\sharedhost —— .NET 安装器是 32 位进程，写的是 32 位视图。
function DotNetSharedFxHasMajor10(RootKey: Integer; const SubKey: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegGetSubkeyNames(RootKey, SubKey, Names) then
    Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    // 主版本必须匹配：NET 9 的运行时跑不了 net10.0 的目标
    if Pos('10.', Names[I]) = 1 then
    begin
      Result := True;
      Exit;
    end;
end;

// ⚠️ 查的是 **Microsoft.NETCore.App**（.NET Runtime），不是 Microsoft.WindowsDesktop.App。
//    依据是精简版 publish 出来的 DelayStart.runtimeconfig.json 实测（2026-09-20）：
//    framework 只有 "Microsoft.NETCore.App" 10.0.0 —— WinUI 3 这一层并不要求 Desktop Runtime。
//    而 Desktop Runtime 的安装包里**含** NETCore Runtime，所以查 NETCore.App 能同时覆盖
//    "只装了 .NET Runtime" 与 "装了 Desktop Runtime" 两种机器，不会误报。
function HasDotNetRuntime(): Boolean;
begin
  Result :=
    // ① 文件系统（主判据）：{pf64} 恒指 64 位 Program Files
    DotNetSharedFxDirHasMajor10(ExpandConstant('{pf64}\dotnet\shared\Microsoft.NETCore.App'))
    or DotNetSharedFxDirHasMajor10(ExpandConstant('{pf32}\dotnet\shared\Microsoft.NETCore.App'))
    // ② 32 位注册表视图（HKLM 常量 = 安装器所在视图，实测数据就在这里）
    or DotNetSharedFxHasMajor10(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App')
    or DotNetSharedFxHasMajor10(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\arm64\sharedfx\Microsoft.NETCore.App')
    or DotNetSharedFxHasMajor10(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x86\sharedfx\Microsoft.NETCore.App')
    // ③ 64 位注册表视图（HKLM64）：部分安装方式（MSI / VS 附带）写这里
    or DotNetSharedFxHasMajor10(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App')
    or DotNetSharedFxHasMajor10(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\arm64\sharedfx\Microsoft.NETCore.App')
    or DotNetSharedFxHasMajor10(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x86\sharedfx\Microsoft.NETCore.App');
end;

// ── Windows App Runtime：MSIX 框架包 ──────────────────────────────────────
// 注册路径：<Hive>\SOFTWARE\Classes\Local Settings\...\AppModel\Repository\Packages\
//             Microsoft.WindowsAppRuntime.<版本>_<包版本>_<架构>__<发布者哈希>
// 🔴 实测（2026-09-21）：框架包在 **HKCU**（按用户注册），HKLM 下同名键只有 3 条
//    系统内置项 —— 只查 HKLM 会误报缺失。安装器以当前用户身份运行，HKCU 可读。
function PackagesRepoHasWindowsAppRuntime(RootKey: Integer): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegGetSubkeyNames(RootKey,
       'SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages',
       Names) then
    Exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    // ⚠️ ① 用 Pos 而不是 Copy —— 包名后缀长度不定，固定长度前缀比较写错一个字符
    //       就会恒为假（装了运行时也误报缺失）。
    //    ② 架构段必须一起匹配：arm64 机器上只有 x64 框架包时，arm64 程序照样起不来。
    if (Pos('Microsoft.WindowsAppRuntime.1.8', Names[I]) > 0)
       and (Pos('_{#WinAppRuntimeArch}__', Names[I]) > 0) then
    begin
      Result := True;
      Exit;
    end;
end;

function HasWindowsAppRuntime(): Boolean;
begin
  Result :=
    PackagesRepoHasWindowsAppRuntime(HKCU)     // 主判据：按用户注册
    or PackagesRepoHasWindowsAppRuntime(HKLM)  // 32 位视图（预置包 / 组策略部署）
    or PackagesRepoHasWindowsAppRuntime(HKLM64) // 64 位视图
end;

// ── 诊断开关：强制把两项检测当作"缺失" ────────────────────────────────────
// 存在的理由：开发机装了运行时，"缺运行时"那条分支在本地永远走不到 —— 提示文案、
// [Run] 是否隐藏、安装结束提示都只能靠人肉在干净机器上验一遍。
// 设了 DELAYSTART_FAKE_MISSING 就让 MissingRuntimeList 恒报缺，从而能在装了
// 运行时的机器上走完整条分支（自检文件里 ready 会变成 0）。
// 🔴 不设这个环境变量时完全不生效，正常用户永远碰不到。
function ForceMissingRuntime(): Boolean;
begin
  Result := GetEnv('DELAYSTART_FAKE_MISSING') <> '';
end;

// 两项分开判断 —— D64-2 起"打开哪个下载页"要按缺哪项决定，不能再合并成一个布尔。
function MissingDotNet(): Boolean;
begin
  Result := ForceMissingRuntime() or (not HasDotNetRuntime());
end;

function MissingWinAppRuntime(): Boolean;
begin
  Result := ForceMissingRuntime() or (not HasWindowsAppRuntime());
end;

// 缺失清单：'' = 运行时齐全。安装前提示与安装后提示共用，避免两处文案不同步。
function MissingRuntimeList(): String;
begin
  Result := '';
  if MissingDotNet() then
    Result := Result + '  · .NET 10 Runtime' #13#10;
  if MissingWinAppRuntime() then
    Result := Result + '  · Windows App Runtime 1.8' #13#10;
end;

// 只列**缺失项**的下载地址（D64-2）。
// 原先无论缺哪项都把两个地址全列、点「是」还只开 .NET 下载页 —— 只缺 Windows App
// Runtime 的用户（本机实测就是这种：.NET 10.0.12 装着、缺的只是框架包）被引去装
// 一个已经装好的东西，装完回来仍然缺。
function MissingRuntimeLinks(): String;
begin
  Result := '';
  if MissingDotNet() then
    Result := Result + '  .NET 10 Runtime：' #13#10 +
                        '    {#DotNetUrl}' #13#10;
  if MissingWinAppRuntime() then
    Result := Result + '  Windows App Runtime 1.8：' #13#10 +
                        '    {#WinAppRuntimeUrl}' #13#10;
end;

// 打开下载页：缺哪项开哪项；两项都缺则 .NET 在前。
procedure OpenRuntimeDownloads();
var
  ErrorCode: Integer;
begin
  if MissingDotNet() then
    ShellExec('open', '{#DotNetUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  if MissingWinAppRuntime() then
    ShellExec('open', '{#WinAppRuntimeUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function InitializeSetup(): Boolean;
var
  Missing: String;
  Answer: Integer;
  SelfCheckFile: String;
  SelfCheckText: String;
begin
  Result := True;

  // ── 自检出口：把两项检测结果写进文件后**直接退出，不安装** ──────────────
  // 用法（本地 / CI 回归"检测逻辑有没有误报"）：
  //   $env:DELAYSTART_RUNTIME_CHECK = "$env:TEMP\rt.txt"
  //   .\dist\DelayStart-Setup-<版本>-win-x64-slim.exe /VERYSILENT
  // 存在的理由：2026-09-21 首次真机安装时，两项检测**各误报一次**（查错了注册表
  // 位置），而检测结果只出现在一个要人点确定的 MsgBox 里 —— 没有这个出口就无法
  // 自动化验证，只能靠人肉装一遍。诊断入口是环境变量，不设就不生效。
  SelfCheckFile := GetEnv('DELAYSTART_RUNTIME_CHECK');
  if SelfCheckFile <> '' then
  begin
    SelfCheckText :=
      'dotnet=' + IntToStr(Ord(HasDotNetRuntime())) + #13#10 +
      'winappruntime=' + IntToStr(Ord(HasWindowsAppRuntime())) + #13#10 +
      // ready = [Run] 自动启动项的可见条件（1 = 会显示"启动 DelayStart"勾选框）。
      // 设 DELAYSTART_FAKE_MISSING 时这里必须变 0 —— 那是"缺运行时"分支的自动化验证点。
      'ready=' + IntToStr(Ord(MissingRuntimeList() = '')) + #13#10 +
      'forced=' + IntToStr(Ord(ForceMissingRuntime())) + #13#10 +
      'pf64=' + ExpandConstant('{pf64}') + #13#10 +
      'needdll=' + AddBackslash(ExpandConstant('{pf64}\dotnet\shared\Microsoft.NETCore.App')) + '-10.x' + #13#10;
    SaveStringToFile(SelfCheckFile, SelfCheckText, False);
    Result := False; // 自检模式绝不落地任何文件
    Exit;
  end;

  Missing := MissingRuntimeList();
  if Missing = '' then
    Exit; // 运行时齐全，静默继续

  // 🔴 这里刻意用 MsgBox 而不是 TaskDialogMsgBox，两个坑都绕开：
  //    ① 本处需要 SuppressibleMsgBox 的静默默认值（TaskDialogMsgBox 没有可指定
  //       Default 的形式 —— 另有 SuppressibleTaskDialogMsgBox 可替代，见 D85 注）；
  //    ② 用自定义按钮标签就必须写数组字面量，而 `[` 出现在行首会被 ISCC
  //       当成 section 标记（"Invalid section tag"）。
  //    ⚠️ 2026-09-23 勘误：本注释曾写"第 5 个参数 Shields 是 TMsgBoxShields 集合，
  //       传整数 0 会报 Type mismatch"—— 是误记。TaskDialogMsgBox 实际签名 6 参：
  //       (Instruction, Text, Typ, Buttons, ButtonLabels, ShieldButton)，
  //       第 6 参 ShieldButton 是"哪个按钮显示盾牌图标"（Integer，0 = 无）。
  //    SuppressibleMsgBox 与 MsgBox 签名兼容，静默安装时不再弹框、直接返回 Default
  //    （这里是 IDNO＝继续安装，自动化装包不会被卡住）。
  Answer := SuppressibleMsgBox(
    '本安装包不含运行时，当前系统缺少：' #13#10 + Missing + #13#10 +
    '缺少运行时时程序无法启动。' #13#10 #13#10 +
    '选「是」打开下载页（只打开上面缺的那几项）；选「否」先装程序、稍后补装' #13#10 +
    '（装完不会自动启动程序）；选「取消」放弃安装。' #13#10 #13#10 +
    '下载地址：' #13#10 +
    MissingRuntimeLinks(),
    mbConfirmation, MB_YESNOCANCEL, IDNO);

  case Answer of
    IDYES:
      OpenRuntimeDownloads();
    IDCANCEL:
      Result := False;
  end;
end;
#endif

// ═══════════════════════════════════════════════════════════════════════════
// D64-1：安装前清空安装目录
//
// 两种形态共用同一个 AppId 与同一个安装目录，覆盖安装只覆盖同名文件 —— 于是
// "先装 full 再装 slim" 会在目录里留下 full 的 hostfxr.dll / coreclr.dll，而 .NET 的
// apphost 只要在自己目录里看到 hostfxr.dll，就把"运行时根"当成程序目录本身，转而去
// <程序目录>\shared\Microsoft.NETCore.App\10.x 找共享框架 —— 那里当然没有（自包含布局
// 是平铺的），于是弹 "You must install or update .NET"，**哪怕机器上装着 10.0.12**。
// 2026-09-21 在本机按同样顺序 1:1 复现（只把 hostfxr.dll + hostpolicy.dll 放进目录即可复现）。
// ═══════════════════════════════════════════════════════════════════════════

// 当前编译的是不是精简版（两种形态共用本脚本，靠 ISPP 的 /DSlim 区分）。
// 做成函数而不是把 #ifdef 散在判断里：PrepareToInstall 两种形态走同一段代码。
function IsSlimFlavor(): Boolean;
begin
#ifdef Slim
  Result := True;
#else
  Result := False;
#endif
end;

// 诊断开关：跳过清理。仅在排查"清目录是否与某个安装场景冲突"时用，正常用户碰不到。
function SkipPurge(): Boolean;
begin
  Result := GetEnv('DELAYSTART_SKIP_PURGE') <> '';
end;

// Inno 自己的卸载器（unins000.exe / .dat / .msg）。**刻意保留不删**：
// 本次安装结束时新卸载器会覆盖它们；而万一安装中途失败（磁盘满、断电、用户取消），
// 留着旧卸载器用户还能把烂摊子卸干净。
function IsInnoUninstallerFile(const FileName: String): Boolean;
begin
  Result := CompareText(Copy(FileName, 1, 5), 'unins') = 0;
end;

// 尽力清空目录内容，返回删掉的文件数。
// 🔴 单个文件删不掉（被占用）**不中止**，而且这里天然安全：Windows 上没带
//    FILE_SHARE_DELETE 打开的文件**根本删不掉**（DeleteFile 返回 False），所以凡是
//    Restart Manager 接下来要关心的文件（＝被运行中的程序占着的那几个）必然还在原地，
//    一定被它的"文件占用"检查发现 —— 我们提前清空的只是那些本来就没人在用的文件。
//    唯一需要"删不掉就中止"的是精简版遇上 hostfxr.dll —— 那一条在 PrepareToInstall 里
//    **提前单独探测**。
// 🔴 分两遍走：边枚举边删条目会让 FindNext 漏项（NTFS 上会跳），所以第一遍只删文件、
//    第二遍才递归进子目录。
function PurgeDirBestEffort(const Dir: String): Integer;
var
  FindRec: TFindRec;
  Full, Name: String;
begin
  Result := 0;

  if FindFirst(Dir + '\*', FindRec) then
  begin
    try
      repeat
        Name := FindRec.Name;
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        begin
          if not IsInnoUninstallerFile(Name) then
          begin
            Full := Dir + '\' + Name;
            if DeleteFile(Full) then
              Result := Result + 1
            else
              Log('残留文件删不掉（被占用），留给 Restart Manager：' + Full);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  if FindFirst(Dir + '\*', FindRec) then
  begin
    try
      repeat
        Name := FindRec.Name;
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          // 通配符枚举会带上这两个伪目录项
          if (Name <> '.') and (Name <> '..') then
          begin
            Full := Dir + '\' + Name;
            Result := Result + PurgeDirBestEffort(Full);
            // 空目录顺手删掉；里面还有删不掉的文件时它会失败，无所谓。
            RemoveDir(Full);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// 安装前清理。返回非空字符串 = 中止安装，并把它作为错误信息显示在"准备安装"页上。
//
// 🔴 只清**我们自己的固定安装目录**：DefaultDirName 虽然固定、DisableDirPage=yes 也挡不住
//    命令行 /DIR= 覆盖，所以这里再核一次路径，不一致就一个文件都不碰。
// 🔴 必须放在 PrepareToInstall 里：Inno 文档保证它早于 Setup 的"文件占用检查"执行。
//    安装器是 lowest 权限，删不掉提权进程（管理端 / 托盘里的调度端）占用的文件，
//    这些占用者随后由 Restart Manager 负责关掉；顺序若反过来，会留下
//    "RM 已经关掉了程序、我们才开始清理"的空档（那时程序目录可能仍被判定为占用）。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppDir, ExpectedDir, StaleHost: String;
  Count: Integer;
begin
  Result := '';

  AppDir := ExpandConstant('{app}');
  ExpectedDir := ExpandConstant('{localappdata}\Programs\{#AppName}');

  if CompareText(AppDir, ExpectedDir) <> 0 then
  begin
    Log('跳过安装目录清理：{app} 指向 ' + AppDir + '，不是固定目录 ' + ExpectedDir);
    Exit;
  end;

  if SkipPurge() then
  begin
    Log('DELAYSTART_SKIP_PURGE 已设置，跳过安装目录清理与残留探测（诊断用）');
    Exit;
  end;

  if not DirExists(AppDir) then
    Exit; // 全新安装，没有可清理的东西

  // ── 精简版：先确认 hostfxr.dll 删得掉，删不掉就中止 ──────────────────────
  // 它是"上一次装的是自包含形态"的指纹，也是唯一会让精简版**起不来**的残留。
  // 它被占用 = 自包含形态的管理端还在运行（调度端不加载它）。
  // 🔴 探测放在清理之前：中止时尚未删任何其他文件，旧安装保持完整可用，
  //    用户只要关掉程序重跑安装程序即可，不会卡在半删状态。
  if IsSlimFlavor() then
  begin
    StaleHost := AppDir + '\hostfxr.dll';
    if FileExists(StaleHost) then
    begin
      DeleteFile(StaleHost);
      if FileExists(StaleHost) then
      begin
        Result :=
          '上一次安装残留的运行时文件正在被占用，无法移除：' #13#10 +
          '  ' + StaleHost + #13#10 #13#10 +
          '这个文件会让本次安装得到一个无法启动的程序（.NET 宿主会误以为运行时' #13#10 +
          '就在程序目录里，于是报"必须安装 .NET"）。' #13#10 #13#10 +
          '请先完全退出 DelayStart（如果任务栏托盘里还有 DelayStart 图标，也请一并退出），' #13#10 +
          '然后重新运行安装程序。';
        Exit;
      end;
    end;
  end;

  Count := PurgeDirBestEffort(AppDir);
  Log('安装目录已清理：删除 ' + IntToStr(Count) + ' 个文件（' + AppDir + '）');
end;

// 安装结束后（仍是安装进程内，[Run] 的 postinstall 项尚未执行）：
// 精简版若仍缺运行时，给一次可读的说明 + 下载入口，并说明**为什么**没有自动启动。
// 🔴 用 SuppressibleMsgBox：静默安装不该被提示框卡住。
procedure CurStepChanged(CurStep: TSetupStep);
#ifdef Slim
var
  Missing: String;
#endif
begin
// ⚠️ 变量声明也要跟着 #ifdef：自包含形态下这段逻辑整体被裁掉，变量留着会让
//    ISCC 报 "[Hint] Variable 'MISSING' never used" —— 本项目的验收标准是零警告。
#ifdef Slim
  if CurStep = ssPostInstall then
  begin
    Missing := MissingRuntimeList();
    if Missing = '' then
      Exit;
    if SuppressibleMsgBox(
        'DelayStart 已安装，但本机仍缺少以下运行时，程序暂时无法启动：' #13#10 +
        Missing + #13#10 +
        '（这正是"安装完成后启动"没有出现的原因 —— 此时拉起程序只会弹出一个' #13#10 +
        ' 没有上下文、也指不出解决方法的报错框。）' #13#10 #13#10 +
        '装好下面缺失的运行时后，从开始菜单/桌面启动 DelayStart 即可：' #13#10 +
        MissingRuntimeLinks() + #13#10 +
        '现在就打开下载页吗？',
        mbConfirmation, MB_YESNO, IDNO) = IDYES then
      OpenRuntimeDownloads();
  end;
#endif
end;

// 非精简版（自包含）永远为真；精简版要求两个运行时都在。
// ⚠️ 这个函数必须在两种形态下都存在 —— [Run] 的 Check 参数无条件引用它。
function RuntimeReadyForApp(): Boolean;
begin
#ifdef Slim
  Result := (MissingRuntimeList() = '');
#else
  Result := True;
#endif
end;

// ── 静默卸载的命令行参数探测（D84）──────────────────────────────────────────
// Inno 的 IsUninstallerSilent 只回答"是不是静默"，不提供自定义参数读取；
// {param:...} 常量也只面向安装向导。所以自己扫 ParamStr：约定参数为 /DELETEDATA
//（大小写不敏感，带不带值都不认 —— 我们只需要一个开关）。
function CmdLineHasParam(const Param: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount() do
    if CompareText(ParamStr(I), '/' + Param) = 0 then
  begin
    Result := True;
    Exit;
  end;
end;

// ── 卸载前的「配置与日志处置」任务对话框（D85，2026-09-23 用户批复 🅐）──────────
// 原生任务对话框 + 三按钮取代勾选框：TaskDialogMsgBox **没有**验证复选框参数 ——
// Inno 未把 Win32 任务对话框的 verification checkbox 暴露给 Pascal（签名共 6 参：
// Instruction/Text/Typ/Buttons/ButtonLabels/ShieldButton，2026-09-23 以官方文档源
// ISHelp\isxfunc.xml + ISCC 6.7.3 编译双重实证）。三个按钮语义互斥、点了即定，
// 不存在"勾了忘点"。
// 🔴 主题跟随系统：[Setup] WizardStyle=modern dynamic，任务对话框自动深浅色（6.6+）。
// 🔴 返回 True = 继续卸载（DeleteUserData 已置位）；False = 点了「取消」，终止卸载。
// 🔴 默认焦点在「保留配置并卸载」—— TaskDialogMsgBox 无默认按钮参数，默认焦点
//    恒在第一个按钮（IDYES），故「保留」必须排第一（2026-09-23 用户实测纠错）。
// 🔴 只在非静默卸载调用；静默卸载没有交互面，走 /DELETEDATA 参数。
function AskUninstallOptions(): Boolean;
var
  Answer: Integer;
begin
  DeleteUserData := False;

  // MB_YESNOCANCEL 时第三个按钮（取消）的标签可省略 —— 省略即用系统默认"取消"。
  // ⚠️ 数组字面量必须写在行中间：行首的 `[` 会被 ISCC 当成 section 标记。
  // 🔴 TaskDialogMsgBox **没有默认按钮参数**（签名 6 参里没有 DefaultButton，
  //    2026-09-23 官方文档源 isxfunc.xml 实证），默认焦点恒在**第一个按钮**（IDYES）。
  //    所以「保留配置并卸载」必须排第一 —— 破坏性的「删除」绝不能是默认焦点
  //    （D85 补丁：首版把「删除」排第一，用户实测发现默认焦点落错了）。
  Answer := TaskDialogMsgBox(
      '卸载 {#AppName}',
      '配置目录：' + ExpandConstant('{userappdata}\DelayStart') + #13#10 +
      '日志目录：' + ExpandConstant('{localappdata}\DelayStart'),
      mbInformation,
      MB_YESNOCANCEL, ['保留配置并卸载', '删除配置并卸载'],
      IDNO);

  // IDYES = 保留（第一按钮，默认焦点）；IDNO = 删除（带盾牌图标，标记破坏性操作）；
  // IDCANCEL = 终止卸载。
  if Answer = IDNO then
    DeleteUserData := True;
  Result := Answer <> IDCANCEL;
end;

// 卸载前的恢复动作（D22 / FR-2.7 / 9.3）。
// 🔴 必须在 InitializeUninstall 里同步等待并检查退出码：
//    --restore-all 失败说明还有条目没能还原成系统默认状态，此时删掉管理端
//    用户就永远失去"移出延时"的入口了 —— 所以中止卸载，让用户先手动处理。
//
// 🔴 D61：**不能再用 Exec**。DelayStart.exe 的 manifest 曾经是 requireAdministrator，
//    而卸载器是 PrivilegesRequired=lowest，Exec（CreateProcess）会以 740
//    （ERROR_ELEVATION_REQUIRED）失败 —— 还原动作根本不会发生，卸载却照常进行，
//    这是 D22 明令禁止的"用户毫不知情地永久失去自启动"。
//    改用 ShellExec('runas') 弹一次 UAC，代价是**拿不到退出码**；
//    因此约定程序用 --result-file 把退出码写到文件里，这里回读。
//
// ⚠️ D82 之后 manifest 改成了 asInvoker（提权由程序入口自己申请），"740"这个成因
//    已经消失，理论上这里也能退回 Exec + waituntilterminated 直接拿退出码。
//    **但不改**：这段是 D61 真机验证过的路径，改了等于把风险塞进卸载流程
//    （UAC 被拒、父子进程退出码转发两条分支都没在真机上跑过），而收益只是少一次
//    文件往返。卸载路径的正确性远比它的优雅重要。
function InitializeUninstall(): Boolean;
var
  AppExe: String;
  ResultFile: String;
  ResultCode: Integer;
  ShellError: Integer;
  Waited: Integer;
  Text: AnsiString;
begin
  Result := True;

  // ── 配置与日志处置（D85：对话框前置到还原之前）────────────────────────────
  // 放在最前 = 用户点「取消」时接管项一个都没被动过，"取消"名副其实。
  // 静默卸载不弹窗：/DELETEDATA 传参即删，未传一律保留（绝不静默删用户数据）。
  if UninstallSilent() then
    DeleteUserData := CmdLineHasParam('DELETEDATA')
  else
  begin
    if not AskUninstallOptions() then
    begin
      Result := False;
      Exit;
    end;
  end;

  AppExe := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(AppExe) then
    Exit; // 文件都不在了（手工删除过），无从恢复，照常卸载

  // 结果文件放 {tmp}（当前用户的临时目录）：提权后的进程还是同一个用户，写得进去。
  ResultFile := ExpandConstant('{tmp}\DelayStart-restore-result.txt');
  DeleteFile(ResultFile);

  if not ShellExec(
      'runas',
      AppExe,
      '--restore-all --result-file "' + ResultFile + '"',
      ExpandConstant('{app}'),
      SW_SHOWNORMAL,
      ewNoWait,
      ShellError) then
  begin
    // UAC 被拒（用户点了"否"）/ 起不来。还原没发生，但默认放行卸载 ——
    // 程序已损坏时强行中止只会把用户锁死。
    // 🔴 D84：必须用 SuppressibleMsgBox —— 普通 MsgBox 在静默卸载时**不会被自动跳过**，
    //    自动化卸载（CI / 脚本清理）会卡死在这个无人值守的弹窗上。Default=IDYES：
    //    静默时按"继续卸载"走（与上面"不锁死用户"原则一致）。
    if SuppressibleMsgBox(
        '无法以管理员身份启动恢复程序（ShellExecute 错误 ' + IntToStr(ShellError) + '）。' #13#10 +
        '⚠ 已接管的条目不会被还原，如需还原请先修复程序再卸载。' #13#10 #13#10 +
        '继续卸载吗？',
        mbConfirmation, MB_YESNO, IDYES) = IDNO then
      Result := False;
    Exit;
  end;

  // 轮询等结果文件（最长约 60 秒：还要算上用户看 UAC 弹窗的时间）。
  // 🔴 不用 ewWaitUntilTerminated：提权启动能否拿到进程句柄并不确定，
  //    而"文件出现了没有"是确定性判据。
  Waited := 0;
  while (Waited < 240) and (not FileExists(ResultFile)) do
  begin
    Sleep(250);
    Waited := Waited + 1;
  end;

  if not FileExists(ResultFile) then
  begin
    // 没有结果文件 = 用户取消了 UAC，或程序没跑到写文件那一步。
    // 两者都意味着"还原没完成"，按失败处理（给用户知情权，默认放行；D84 静默默认也放行）。
    if SuppressibleMsgBox(
        '恢复程序没有返回结果（可能取消了 UAC 提权，或程序已损坏）。' #13#10 +
        '⚠ 已接管的条目不会被还原，如需还原请先修复程序再卸载。' #13#10 #13#10 +
        '继续卸载吗？',
        mbConfirmation, MB_YESNO, IDYES) = IDNO then
      Result := False;
    Exit;
  end;

  if not LoadStringFromFile(ResultFile, Text) then
  begin
    // 理论上到不了这里（刚确认文件存在）。读不了就放行，不拿用户的卸载冒险。
    Exit;
  end;

  DeleteFile(ResultFile);
  // 解析失败按 1（失败）处理 —— 宁可多问一次，也不要静默删掉一个还原失败的安装。
  ResultCode := StrToIntDef(Trim(String(Text)), 1);

  if ResultCode <> 0 then
  begin
    // 🔴 D84 修复两件事：
    //    ① 逻辑 bug：原写法 `if MsgBox(...) = IDYES then Result := True` 在 Result
    //       已是 True 时是空操作 —— 用户点「否」也照样继续卸载，与"卸载已中止"的
    //       文案直接相悖。改为**默认中止**，显式选「强行卸载」才放行。
    //    ② 静默卸载：换 SuppressibleMsgBox，Default=IDNO —— 还原失败说明还有接管项
    //       没还原，静默时无人能拍板"强行卸载"，按 D22 原则中止（宁可不卸，不可把
    //       用户锁死在接管状态）。
    Result := False;
    if SuppressibleMsgBox(
        '恢复自启动项时出现问题（退出码 ' + IntToStr(ResultCode) + '）。' #13#10 +
        '为避免数据丢失，卸载已中止。请打开程序把「延时启动」页的条目逐一移出，' #13#10 +
        '或使用命令行 DelayStart.exe --restore-all 查看具体失败原因。' #13#10 #13#10 +
        '仍要强行卸载（跳过恢复，自启动项保持接管状态）吗？',
        mbConfirmation, MB_YESNO, IDNO) = IDYES then
      Result := True;
  end;
end;

// 配置与日志的默认策略：**保留**（%APPDATA%\DelayStart 与 %LOCALAPPDATA%\DelayStart），
// 这样卸载重装后延时列表还在。D62 曾在此时点 MsgBox 询问；D84 立项、D85（2026-09-23 批复）
// 定形为 InitializeUninstall 开头的任务对话框（GUI，三按钮）/ /DELETEDATA 参数（静默），
// 这里只执行结果。
//
// 🔴 时机选 usUninstall 而不是 InitializeUninstall：
//    ① usUninstall 在"确认卸载"之后才触发 —— 用户如果在确认页反悔，不会已经删了数据；
//    ② 此时 --restore-all 已经跑完（它在 InitializeUninstall 里），顺序天然正确。
// 🔴 删除条件只认 DeleteUserData：GUI 没选「删除配置并卸载」、静默没传 /DELETEDATA，
//    都一律保留 —— 删用户数据这种事绝不能在用户没看见选项的情况下发生。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RoamingDir: String;
  LocalDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // ── 协议处理器（D79）：只清理，不注册 ─────────────────────────────────
    // 注册发生在运行时 —— 管理端每次启动、守卫发通知前都会幂等写一遍
    // （ShellRegistrationService，写 HKCU）。安装器不参与注册（程序可能从不启动，
    // 而注册需要知道 exe 的最终路径；这里只负责**卸载时清干净**）：
    // 该键指向 {app}\{#AppExe}，卸载后留着就是一个指向已删除文件的 handler ——
    // 用户点通知中心里的旧通知只会拿到一个系统报错框。
    // ⚠️ 必须连子键一起删（shell\open\command 就在它下面），RegDeleteKey 只删空键。
    // ⚠️ 时机放 usUninstall 而不是 usPostUninstall：用户若在确认页取消卸载，
    //    本过程根本不会触发，键原封不动。
    if RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\delaystart') then
      Log('已删除协议处理器：HKCU\Software\Classes\delaystart');

    RoamingDir := ExpandConstant('{userappdata}\DelayStart');
    LocalDir := ExpandConstant('{localappdata}\DelayStart');

    if not DeleteUserData then
      Exit;

    DelTree(RoamingDir, True, True, True);
    DelTree(LocalDir, True, True, True);

    if DirExists(RoamingDir) or DirExists(LocalDir) then
      UserDataNote :=
        '⚠ 配置与日志未能完全删除（文件可能仍被占用）。请手动检查：' #13#10 +
        '  ' + RoamingDir + #13#10 +
        '  ' + LocalDir
    else
      UserDataNote := '配置与日志已一并删除。';
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if UserDataNote = '' then
      SuppressibleMsgBox(
        '配置与日志已保留：' #13#10 +
        '  %APPDATA%\DelayStart（延时列表与设置）' #13#10 +
        '  %LOCALAPPDATA%\DelayStart（日志与运行记录）' #13#10 #13#10 +
        '如需彻底清除，请手动删除上述目录。',
        mbInformation, MB_OK, IDOK)
    else
      SuppressibleMsgBox(UserDataNote, mbInformation, MB_OK, IDOK);
  end;
end;
