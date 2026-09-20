# 安装器构建说明（D22 / D60 / D61 / D62 / D63 / D64）

## 一键构建

```powershell
# 本机默认：win-x64 的自包含 + 精简版两个包（实测约 1 分钟，不含首次 AOT 编译）
.\installer\build-all.ps1

# 指定架构（arm64 需要 MSVC 的 ARM64 工具集，见下方前置条件）
.\installer\build-all.ps1 -Rids win-x64,win-arm64

# 只出一种形态
.\installer\build-all.ps1 -SlimOnly
.\installer\build-all.ps1 -FullOnly

# 单包（调试用）
.\installer\build-installer.ps1 -Rid win-x64 -Slim
```

产物落 `dist\`（已在 `.gitignore`）：

| 文件 | 形态 | 实测体积 | 用户需预装 |
|---|---|---|---|
| `DelayStart-Setup-<版本>-win-x64.exe` | 自包含 | **60.8 MB**（内含管理端 218.6 MB / 547 文件 + 调度端 3.66 MB） | 无 |
| `DelayStart-Setup-<版本>-win-x64-slim.exe` | 精简版（框架依赖） | **10.0 MB**（内含管理端 42.0 MB / 83 文件 + 调度端 3.66 MB） | .NET 10 **Runtime** + Windows App Runtime 1.8 |

> ⚠️ 精简版要的是 **.NET Runtime**，不是 Desktop Runtime —— 依据是它 publish 出来的
> `DelayStart.runtimeconfig.json` 里 framework 只有 `Microsoft.NETCore.App 10.0.0`（实测）。
> 安装器按 `Microsoft.NETCore.App` 探测，因此"只装了 .NET Runtime"和"装了 Desktop Runtime"
> 两种机器都能识别。

> 🔴 **publish 目录必须含 XAML 产物与资源包（D61）**：`App.xbf` / `MainWindow.xbf` /
> `Views\*.xbf` / `Dialogs\*.xbf` / `DelayStart.pri` / `Assets\AppIcon.ico`。
> 它们**不会**被 `dotnet publish` 自动带上（unpackaged 转换删掉 `EnableMsixTooling` 的后果），
> 少了这些文件装出来的程序一启动就在 `Microsoft.UI.Xaml.dll` 里 `0xc000027b` 秒崩，
> 而同一份产物在 `bin` 里双击却完全正常。
> 两道防线：`DelayStart.App.csproj` 的 `CopyWinUIResourcesToPublishDir` 负责补文件，
> `build-installer.ps1` 在 publish 之后断言、缺一个就中止构建。
> 资源包名必须是 **`DelayStart.pri`（主模块名）**，不是 `resources.pri`。

## 前置条件

| 项 | 说明 |
|---|---|
| .NET SDK 10 | `global.json` 钉在 `10.0.401`（`rollForward: latestFeature`） |
| Inno Setup 6 | `winget install --id JRSoftware.InnoSetup`。⚠️ winget 装的是 **per-user** 布局：`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`，**不在** `Program Files`。`build-installer.ps1` 会依次探三个候选路径 |
| 中文语言包 | Inno 自带的 `Languages\ChineseSimplified.isl`，已确认存在于 per-user 布局下 |
| ARM64 工具集 | 仅 arm64 需要：VS 组件 `Microsoft.VisualStudio.Component.VC.Tools.ARM64`。🔴 本机 2026-09-20 实测**未装** → 本地只出 x64，arm64 由 CI 出 |

## 构建脚本参数

### `build-installer.ps1`（单包）

| 参数 | 默认 | 说明 |
|---|---|---|
| `-Rid` | `win-x64` | `win-x64` / `win-arm64` |
| `-Slim` | 关 | 出精简版（框架依赖）。两种形态都用 NativeAOT 调度端 —— 它不依赖 .NET 运行时 |
| `-Version` | 读 `Directory.Build.props` | 覆盖版本号 |
| `-Configuration` | `Release` | |

### `build-all.ps1`（矩阵）

| 参数 | 默认 | 说明 |
|---|---|---|
| `-Rids` | `win-x64` | 逗号分隔；CI 传 `win-x64,win-arm64` |
| `-FullOnly` / `-SlimOnly` | — | 只出一种形态 |
| `-Version` | — | 透传 |

若有任意一个组合失败，脚本以非零码退出（CI 靠它判定）。

## iss 编译参数（由脚本传入）

| 参数 | 缺省 | 说明 |
|---|---|---|
| `/DAppVersion` | `0.1.0` | 安装程序版本 |
| `/DRid` | `win-x64` | 仅用于产物命名 |
| `/DSlim` | 未定义 | **判据是"有没有定义"**，不要传 `/DSlim=0` 表达"否" |
| `/DPublishDir` | 本地 build 路径 | 管理端 publish 目录 |
| `/DSchedulerDir` | 本地 publish 路径 | 调度端 publish 目录 |
| `/DTargetArch` | `x64compatible` | arm64 包传 `arm64` |
| `/DWinAppRuntimeUrl` | x64 直链 | arm64 传 arm64 直链 |
| `/DWinAppRuntimeArch` | `x64` | Windows App Runtime 框架包名里的架构段（`_x64__` / `_arm64__`）。**与 `-Rid` 同步**，检测时连架构一起匹配 —— arm64 机器上只装了 x64 框架包时 arm64 程序照样起不来 |

## 🔴 写 Inno 脚本会踩的三条硬约束（都实测过）

1. **任何一行都不能以 `[` 开头** —— 含 `[Code]` 段内、含缩进后的 `[`。ISCC 的解析器在需要
   section 头的位置看到行首 `[` 就报 `Invalid section tag`，哪怕那行是合法的 Pascal 数组字面量。
   对策：数组字面量跟在别的记号后面同一行，或干脆避开数组字面量。
2. **`TaskDialogMsgBox` 的第 5 个参数 `Shields` 是集合类型 `TMsgBoxShields`** —— 传整数 `0`
   报 `Type mismatch`（6.7.3 实测），而自定义按钮标签又必须写数组字面量（撞上第 1 条）。
   精简版的运行时提示因此改用签名更简单的 `MsgBox` + `MB_YESNOCANCEL`。
3. **不要重复声明 `FILE_ATTRIBUTE_DIRECTORY`** —— Inno 的 Pascal **自带**这个常量，再写一遍报
   `Duplicate identifier`（2026-09-21 实测）。同类 Win32 常量优先假设"已经有"。

另外：**不要为了"目录更干净"在构建脚本里递归删除 publish 目录** —— 输出目录按
`artifacts\publish\<rid>\<形态>` 分开本就不会互相污染；递归删除还触发过本机安全策略对批量删除的拦截。

## 🔴 两条"提权"坑：为什么 `[Run]` 和卸载都要用 ShellExec（D61）

`DelayStart.exe` 的 manifest 是 `requireAdministrator`，而安装器是 `PrivilegesRequired=lowest`。
**同一个原因踩了两次**，都表现为 `740 ERROR_ELEVATION_REQUIRED`：

| 位置 | 错误做法 | 症状 | 正确做法 |
|---|---|---|---|
| `[Run]` 首启勾选运行 | 默认（`CreateProcess`） | 勾选"启动"点确定 → 弹报错框，程序根本没起来 | `Flags: ... shellexec`，由外壳按 manifest 弹 UAC |
| `[Code] InitializeUninstall` 的 `--restore-all` | `Exec(...)` | 还原动作**根本不会发生**，卸载却照常进行 —— 直接违反 D22 | `ShellExec('runas', ...)` 提权拉起 |

⚠️ 卸载那处的代价：**`ShellExec` 拿不到子进程退出码**。所以约定程序用
`--result-file <路径>` 把退出码回写进文件，卸载器**轮询**该文件（`Sleep(250)` × 240 ≈ 60 s，
把用户看 UAC 的时间也算进去）。**不要**改用 `ewWaitUntilTerminated` —— 提权启动能否拿到进程句柄
并不确定，"文件出现了没有"才是确定性判据。三种拿不到结果的情况（ShellExec 起不来 / 轮询超时 /
退出码非 0）都按"还原未完成"处理，给用户知情的放行或中止选择。

## 图标（D61 / D62 / D63）

图标是**一张源图 → 三份多尺寸 ico**，全程走脚本，不依赖 IDE：

```powershell
uv run tools\make-icon.py          # 三份一起（默认）；也可 --roles app / --roles tray
```

| 环节 | 位置 |
|---|---|
| 源图 | `assets\icon\delay.png`（**正方形、带 alpha** 的 PNG，推荐 256×256；当前即 256×256 RGBA） |
| 产物 ① | `src\DelayStart.App\Assets\AppIcon.ico` —— 管理端（10 档：16/20/24/32/40/48/64/96/128/256） |
| 产物 ② | `src\DelayStart.Scheduler\Assets\Scheduler.ico` —— 调度端 exe 图标 + 托盘（正常态） |
| 产物 ③ | `src\DelayStart.Scheduler\Assets\SchedulerWarning.ico` —— 托盘「完成但有失败」的红色告警角标态（D31） |

| 使用点 | 机制 |
|---|---|
| 管理端 exe 内嵌图标 | `DelayStart.App.csproj` 的 `<ApplicationIcon>Assets\AppIcon.ico</ApplicationIcon>` —— 决定开始菜单 / 桌面快捷方式 / 任务栏 / Alt-Tab /「设置 → 应用」的图标 |
| 管理端窗口图标 | `AppWindow.SetIcon("Assets/AppIcon.ico")`（`MainWindow.xaml.cs`），文件靠 `<Content Include="Assets\AppIcon.ico" />` 随程序发布 |
| 调度端 exe 内嵌图标 | `DelayStart.Scheduler.csproj` 的 `<ApplicationIcon>Assets\Scheduler.ico</ApplicationIcon>`（D63 补：此前只有 `EmbeddedResource`，exe 带的是 .NET 默认图标） |
| 托盘两枚 | 同工程的 `EmbeddedResource` + `IconResources` 运行时解析（⚠️ 与 `<ApplicationIcon>` 是**两件事**：一个管"图标写进 PE 资源"，一个管"文件随程序发布给运行时自己读"，**两者都要有**） |
| 安装程序 / 卸载入口 | iss 的 `SetupIconFile={#AppIconFile}` |

🛠 **为什么自己拼 ICO 容器，而不用 `PIL.Image.save(format="ICO", sizes=[...])`**：Pillow 的 ICO 插件对
**所有**尺寸都写 PNG 条目。PNG-in-ICO 虽在 Vista+ 支持，但小尺寸走 PNG 时部分外壳路径（缩略图、某些
第三方文件对话框）会取不到图标而显示空白。脚本做混合容器：**≥ 96 用 PNG，16~48 用 BMP/DIB（32bpp
BGRA + AND 掩码）**。阈值取 96 而不是 256 是 D63 的量：96/128 两张 DIB 要 105 KB，而它们只服务
"大图标 / 超大图标"这些现代外壳路径 —— 改后单份从 **154,811 B 降到 71,530 B**（−54%）。

🔴 **调度端托盘不是"取 ICO 第一个条目"**（D63 改）：`IconResources` 现在**按目标尺寸挑条目**
（优先 ≤ 32 的最大档），并把 cx/cy 显式传给 `CreateIconFromResourceEx`。原先"取首个条目 + 
`LR_DEFAULTSIZE`"的写法在多尺寸容器上会拿到 16×16、被系统放大到 32、再被外壳缩回 16 —— 白搭两次
重采样。托盘底色取 **32** 而不是 `GetSystemMetrics(SM_CXSMICON)`：调度端没有 DPI 感知声明，那个值
恒为 16，在 150%/200% 下等于发一个 16px 位图给外壳放大。

告警角标是**手绘**的（红色圆 + 白描边 + 白叹号，先按 4 倍画布绘制再缩到目标尺寸；叹号是圆角矩形 +
圆点，不用字体 → 不依赖系统装了哪种字体），直径取图标边长的 50%。

⚠️ 换图标 = 替换 `assets\icon\delay.png` → 重跑脚本 → **重新构建**（管理端 exe 的图标是编译期
嵌进去的，调度端的还要重新 publish；不重新构建不会变）。

## 设计要点（对应 `docs/requirements.md` D22 / D60 / D61 / D62 / D63 / D64）

| 项 | 做法 |
|---|---|
| 安装路径 | 固定 `%LOCALAPPDATA%\Programs\DelayStart`，`DisableDirPage=yes` + `PrivilegesRequired=lowest`，用户不可改 |
| 应用名（D61） | 安装脚本里**只此一处**名字 = `AppName` = **`DelayStart`**（英文，不带中文）。开始菜单与桌面快捷方式同名，窗口标题同步 |
| 🔴「设置 → 应用」的显示名（D63） | 那一栏取的是 **`AppVerName`，不是 `AppName`** —— Inno 缺省值形如 `AppName + " version " + AppVersion`，D60 起又按形态拼了后缀，真机实测注册表里就是 `DelayStart 0.1.0`（slim 档 `DelayStart 0.1.0-slim`）。D63 起固定 `AppVerName={#AppName}` → 只显示裸 `DelayStart`。版本号仍由 `DisplayVersion` 承载，**形态后缀只留在产物文件名里** |
| 架构防呆 | `ArchitecturesAllowed`：x64 包用 `x64compatible`（arm64 可模拟），arm64 包用 `arm64` |
| 快捷方式（D61） | `[Icons]` 建**开始菜单 + 桌面**两项，都用 `{autoprograms}` / `{autodesktop}` —— `lowest` 模式下都落当前用户目录，不碰 all users。⚠️ 原脚本漏了桌面项 |
| 桌面图标改为可选任务（D62） | 桌面项挂 `Tasks: desktopicon`（`[Tasks]` 里定义，描述「创建桌面快捷方式」，**默认勾选** —— Inno 不写 `unchecked` 就是勾选）。⚠️ **静默安装不自动勾选任何任务**：`/VERYSILENT`（或 `/SILENT`）要出桌面图标必须显式加 `/MERGETASKS="desktopicon"` |
| 快捷方式图标（D61） | 图标来自 **exe 内嵌资源**（`UninstallDisplayIcon` 也指向它）。所以 `App.csproj` 必须设 `<ApplicationIcon>Assets\AppIcon.ico</ApplicationIcon>` —— 只声明 `<Content>` 是不够的（那只管"文件随程序发布"），漏设会让所有快捷方式与「应用和功能」显示**通用空白图标** |
| 安装程序图标（D62） | iss 加 `SetupIconFile={#AppIconFile}`（缺省 `..\src\DelayStart.App\Assets\AppIcon.ico`）→ 安装程序自身与卸载入口都显示 DelayStart 图标。ico 由 `tools/make-icon.py` 从 `assets/icon/delay.png` 生成，见下方「应用图标」一节 |
| 调度任务注册（D63 修订） | **不归安装器管**（它只有 `lowest` 权限，注册任务需要管理员）。落点是**管理端第一次启动**：`FirstRunBootstrap` 自动注册并发落一个"已初始化"标记，之后再不插手。🔴 只在首次运行做一次 —— 总览页那个开关（D3：开 = 注册，关 = 删除）表达的是用户意图，自动动作只允许发生在他表达意图**之前**，否则用户关掉开关的意愿会被无声推翻且永远关不掉。安装器仍提供「立即运行 DelayStart」的勾选（`[Run] ... postinstall shellexec`），UAC 只弹这一次 |
| 默认延时（D63） | `Settings.DefaultPreset` = **10 秒**（原 30）。⚠️ 与 D50 的预设列表同理：**已落盘的 `config.json` 原样保留**，默认值只影响新装 / 未改过该项的配置 |
| 卸载可逆 | `[Code] InitializeUninstall` 用 `ShellExec('runas')` 拉起 `--restore-all --result-file <临时文件>`，轮询回读退出码，非 0 **中止卸载**（可强行跳过但需二次确认） |
| 为什么不用 `[UninstallRun]` | 那里读不到退出码，恢复失败也会照删文件 |
| 为什么不用 `Exec` | 740（提权目标 + lowest 安装器），见上方"两条提权坑" |
| 精简版运行时检测（D60 / D61 修正） | `InitializeSetup` 三项判据任一命中即算装了：① `{pf64}`/`{pf32}` 下 `dotnet\shared\Microsoft.NETCore.App\10.*` 目录（主判据）；② **32 位注册表视图**的 .NET 共享框架子键；③ **64 位视图**同路径。Windows App Runtime 查 `HKCU`/`HKLM`/`HKLM64` 三处包仓库并**匹配架构段**。缺失时提示 + 给下载链接，**不阻断安装** |
| 🔴 缺运行时就不再自动启动（D62） | `[Run]` 挂 `Check: RuntimeReadyForApp`（精简版要求两个运行时都齐；自包含恒真）→ 缺任一项时完成页的「启动 DelayStart」**整项消失**，改由 `CurStepChanged(ssPostInstall)` 给一次带下载链接的说明，并说明**为什么**没有自动启动。<br>🔴 原行为是"提示做了、处置没跟上"：用户选「先装程序、稍后补装」之后照样被拉起，得到 apphost 那句 `You must install or update .NET to run this application` —— 指不到安装器、也没有解法 |
| 检测自检出口（D61 / D62 扩展） | 环境变量 `DELAYSTART_RUNTIME_CHECK=<文件>` → 只写结果、直接退出、不安装。D62 增两行：**`ready=`**（= `[Run]` 那个 `Check` 的取值）/ **`forced=`**；再配 `DELAYSTART_FAKE_MISSING=1` 可在已装运行时的机器上强制走完"缺运行时"整条分支（实测见下方验证一节） |
| 两形态同 AppId | 装在同一个目录（用户不该同时装两个）——🔴 **代价是覆盖安装不会清理对方形态的文件**，见下一行 |
| 🔴 装前清空安装目录（D64-1） | `[Code] PrepareToInstall` 递归清空 `{app}`（**只清程序文件**：配置在 `%APPDATA%`、日志在 `%LOCALAPPDATA%`、计划任务在目录外，不动用户任何数据）。<br>病根：两形态同目录 + 覆盖安装只覆盖同名文件 → "先装 full 再装 slim" 会留下 full 的 `hostfxr.dll`，而 **.NET 的 apphost 只要在自己目录看到 `hostfxr.dll` 就把"运行时根"当成程序目录**，转而去 `<程序目录>\shared\Microsoft.NETCore.App\10.x` 找共享框架（自包含布局是平铺的、那里没有）→ 精简版报 `You must install or update .NET`，**哪怕机器上装着 10.0.12**（2026-09-21 本机 1:1 复现：只把 `hostfxr.dll` + `hostpolicy.dll` 放进目录即可复现）。<br>🔴 清空放在 `PrepareToInstall` 是有原因的：Inno 文档保证它早于 Setup 的"文件占用检查"（Restart Manager）执行；而安装器是 `lowest` 权限，删不掉**提权进程**（管理端 / 托盘里的调度端）占用的文件 —— 这些占用者交给 Restart Manager 关掉（`CloseApplications=yes` / `RestartApplications=yes`，两项都是 Inno 默认值，脚本里显式写出来）。🔴 **但 Restart Manager 不会把它们自动重启回来** —— 实测文档原文：`RestartApplications` 只对调用过 `RegisterApplicationRestart` 的程序生效，DelayStart 与调度端都没有调用。所以调度端在安装时被关掉的话，托盘图标要到**下次登录**才回来（它的计划任务只在登录时触发）。<br>🔴 唯一**中止安装**的情况：精简版发现 `hostfxr.dll` 删不掉（= 自包含形态的管理端还在跑）。此时中止优于装出一个起不来的程序，且这一步在清理之前 → **中止时尚未删任何文件，旧安装保持完整**。异常路径下 `unins*`（旧卸载器）刻意保留 |
| 缺运行时的下载入口（D64-2） | `InitializeSetup` / `CurStepChanged` 共用 `MissingRuntimeLinks()` + `OpenRuntimeDownloads()`：**只列、只打开缺的那几项**（两项都缺则 .NET 在前）。原实现无论缺什么都把两个地址全列、点「是」还只开 .NET 下载页 —— 本机实测正是"装着 .NET 10.0.12、只缺框架包"这种，用户被引去装一个已经装好的东西 |
| 配置与日志（D62 修订） | 默认保留（无 `[UninstallDelete]`）。卸载时在 `CurUninstallStepChanged(usUninstall)` 问一次「是否一并删除配置与日志」，选「是」才 `DelTree` 两个目录；🔴 **静默卸载一律按「否」**（`SuppressibleMsgBox` 的 Default 参数）。选「否」或删不干净（文件被占用）时，完成页给出手动清除指引 |
| 版本号 | 唯一来源 `Directory.Build.props` 的 `<Version>`，由脚本注入 `/DAppVersion` |

## 验证精简版的运行时检测

检测结果原先只在要人点确定的 `MsgBox` 里，没法自动化验证 —— 而它在首次真机安装时**两项各误报一次**。
现在可以这样回归（**不会安装任何东西**）：

```powershell
$env:DELAYSTART_RUNTIME_CHECK = "$env:TEMP\rt.txt"
.\dist\DelayStart-Setup-0.1.0-win-x64-slim.exe /VERYSILENT /SUPPRESSMSGBOXES
Get-Content "$env:TEMP\rt.txt"
# dotnet=1               ← 1 = 判定为已安装
# winappruntime=1
# ready=1                ← D62：= [Run] 那个 Check 的实际取值（1 = 完成页会出现「启动 DelayStart」）
# forced=0               ← D62：是否被 DELAYSTART_FAKE_MISSING 强制
# pf64=C:\Program Files
```

**"缺运行时"那条分支怎么在本机验**（本机装了运行时，`MissingRuntimeList()` 恒为空 → 那条路走不到）：

```powershell
$env:DELAYSTART_RUNTIME_CHECK = "$env:TEMP\rt2.txt"
$env:DELAYSTART_FAKE_MISSING  = '1'
.\dist\DelayStart-Setup-0.1.0-win-x64-slim.exe /VERYSILENT /SUPPRESSMSGBOXES
Get-Content "$env:TEMP\rt2.txt"      # dotnet=1 / winappruntime=1 / ready=0 / forced=1
```

实测两种取值：不设 → `ready=1 forced=0`；设了 → `ready=0 forced=1`（闸门关闭）。
想看**带提示的真机效果**（不自动化）：设 `DELAYSTART_FAKE_MISSING=1` 后正常双击运行该安装包 —— 安装前的
缺运行时对话框、装完的说明框、完成页上消失的启动勾选框会一起出现；退出安装即可，不留任何文件。

## 验证 D64-1 的"装前清空安装目录"

病根场景是**两种形态装进同一个目录**。回归时不必真的装两遍，往安装目录里放一个"上一次形态
的残留"就能验（安装目录里的东西全是程序文件，放进去不影响别的）：

```powershell
$app = "$env:LOCALAPPDATA\Programs\DelayStart"
# 1) 伪造一个自包含形态的残留（精简版最怕的就是它）
New-Item -ItemType File -Path "$app\hostfxr.dll" -Force | Out-Null
New-Item -ItemType File -Path "$app\coreclr.dll" -Force | Out-Null
"$app\OldFlavorOnly.dll" | Set-Content "$app\OldFlavorOnly.dll"

# 2) 装一遍（silent）
.\dist\DelayStart-Setup-0.1.0-win-x64-slim.exe /VERYSILENT /SUPPRESSMSGBOXES

# 3) 残留必须全没了，程序文件必须都在
Get-ChildItem $app -Filter 'hostfxr.dll'      # 期望：没有输出
Get-ChildItem $app -Filter 'coreclr.dll'      # 期望：没有输出
Test-Path "$app\OldFlavorOnly.dll"            # 期望：False
Test-Path "$app\DelayStart.exe"               # 期望：True
```

想复现**中止**那条分支（`hostfxr.dll` 被占用）：在另一个进程里把它占住（`[IO.File]::Open($p,
'Open','ReadWrite','None')` 且不放句柄），再跑同一条安装命令 —— 期望安装**在"准备安装"页停下并显示
中文说明**，且此时 `{app}` 里的其他文件**一个都没被删**（探测先于清理）。

🔴 观察安装器到底删了什么：加 `/LOG="%TEMP%\ds-install.log"`，搜 `安装目录已清理`（会带上删除的
文件数）与 `残留文件删不掉`（被占用、留给 Restart Manager 的文件）。

两个诊断开关（不设就不生效，正常用户碰不到）：

| 环境变量 | 作用 |
|---|---|
| `DELAYSTART_SKIP_PURGE=1` | 跳过"清空安装目录 + hostfxr 残留探测"。用于排查"清目录是否与某个安装场景冲突" |
| `DELAYSTART_RUNTIME_CHECK` / `DELAYSTART_FAKE_MISSING` | 见上一节（运行时检测） |

托盘程序会不会挡住安装？调度端 exe 装的时候正在运行、其文件被锁 —— 这**不是** D64 引入的问题
（任何一次升级都要替换它），交给 Windows Restart Manager：它会列出正在占用待替换文件的应用、
征得同意后关掉。

🔴 **但它不会把被关掉的程序自动重启回来**：Inno 文档明写 `RestartApplications` 只对调用过 Windows
`RegisterApplicationRestart` API 的程序生效，而 DelayStart 与调度端都没有调用。实际后果是 ——
安装时调度端被关掉后，**托盘图标要到下次登录才回来**（它的计划任务只在登录时触发），本次登录
尚未到点的延时条目也随之下次登录一并补上（与"关机早于延时到点"是同一套语义）。若用户拒绝关闭，
Inno 会走到它自己的「重试 / 忽略 / 放弃」提示，最坏结果是保留旧调度端 exe（程序仍可用，只是调度端没升级）。

🔴 **这是已知缺口，2026-09-21 用户已定性：什么都不做、维持现状**；并**明确禁止**"管理端启动调度端"
这条修法 —— 调度端进程的生死只由计划任务决定，管理端不代管。

✅ **真实场景验收（2026-09-21 用户真机复测通过）**：装 full → 启动正常；**装 slim 覆盖 full →
启动正常**（＝ 装前清空生效）；在 slim 目录里手工丢回一个 full 的 `hostfxr.dll` → 立即复现
"必须安装 .NET"（＝ 反向确认病根就是这一个文件）。

## GitHub Release（`.github/workflows/release.yml`）

两种触发：推 `v*` 标签（自动建 Release）或手动 `workflow_dispatch`（只出 artifact）。

矩阵 = `rid(win-x64, win-arm64) × 形态(full, slim)`，共 4 个包，`fail-fast: false`（一个架构失败不拖累其余）。
arm64 那两组会先跑一步 **ARM64 工具集检测**：runner 上缺组件时立刻给出可读结论，而不是等链接阶段报 LNK 错。

标签触发的版本号以**标签**为准（`v0.2.0` → `0.2.0`），手动触发可填 `version`，都留空则读 `Directory.Build.props`。
