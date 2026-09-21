# 安装器（Inno Setup）构建说明

> 决策背景见 `docs/decisions.md`（D22 / D60–D65）；踩坑细节见 `docs/pitfalls.md` 九。

## 一键构建

```powershell
.\installer\build-all.ps1                                  # win-x64 的 full + slim（实测约 1 分钟）
.\installer\build-all.ps1 -Rids win-x64,win-arm64          # 指定架构（arm64 需 MSVC ARM64 工具集，本机未装 → 交 CI）
.\installer\build-all.ps1 -SlimOnly / -FullOnly            # 只出一种形态
.\installer\build-installer.ps1 -Rid win-x64 -Slim         # 单包（调试用）
```

| 参数（build-installer.ps1） | 默认 | 说明 |
|---|---|---|
| `-Rid` | `win-x64` | `win-x64` / `win-arm64` |
| `-Slim` | 关 | 出精简版（框架依赖）。两形态的调度端都是 NativeAOT |
| `-Version` | 读 `Directory.Build.props` | 版本号唯一来源，脚本注入 `/DAppVersion` |
| `-Configuration` | `Release` | |

`build-all.ps1`：`-Rids`（逗号分隔）、`-FullOnly/-SlimOnly`、`-Version` 透传；任一组合失败即非零退出（CI 靠它判定）。

## 产物（落 `dist\`，已 gitignore）

| 文件 | 形态 | 体积 | 用户需预装 |
|---|---|---|---|
| `DelayStart-Setup-<版本>-win-x64.exe` | 自包含 | ~60.8 MB | 无 |
| `DelayStart-Setup-<版本>-win-x64-slim.exe` | 精简版 | ~10.0 MB | **.NET Runtime**（非 Desktop Runtime，slim 的 runtimeconfig 只有 `Microsoft.NETCore.App`）+ Windows App Runtime 1.8 |

调度端 exe 不在 App publish 目录里是正常的——iss 第二条 `[Files]` 从 AOT publish 目录单独取。

## 前置条件

| 项 | 说明 |
|---|---|
| .NET SDK 10 | `global.json` 钉 `10.0.401` |
| Inno Setup 6 | winget 装的是 per-user 布局（`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`），脚本依次探三个候选路径 |
| 中文语言文件 | **随仓库分发** `installer\languages\ChineseSimplified.isl`（官方 Inno 不带简体中文，D65）。缺失时脚本提前报错 |
| ARM64 工具集 | 仅 arm64 包需要；本机未装，本地只出 x64 |

## iss 编译参数（脚本传入）

`/DAppVersion`、`/DRid`、`/DSlim`（**判据是"有没有定义"**，别传 `/DSlim=0`）、`/DPublishDir`、`/DSchedulerDir`、`/DTargetArch`（`x64compatible`/`arm64`）、`/DWinAppRuntimeUrl` + `/DWinAppRuntimeArch`（与 Rid 同步，检测连架构一起匹配）。

## 设计要点速查

| 项 | 做法 |
|---|---|
| 安装路径 | 固定 `%LOCALAPPDATA%\Programs\DelayStart`，`DisableDirPage` + `PrivilegesRequired=lowest`（安装零 UAC，卸载弹一次） |
| 应用名 | `AppName` = `AppVerName` = 裸 **`DelayStart`**（「设置→应用」读的是 `AppVerName`；版本号走 `DisplayVersion`，形态后缀只留在产物文件名） |
| 快捷方式 | `[Icons]` 开始菜单 + 桌面（挂 `Tasks: desktopicon`，默认勾选；静默安装需 `/MERGETASKS="desktopicon"`）；图标来自 exe 内嵌资源（`<ApplicationIcon>`） |
| 装前清空安装目录 | `[Code] PrepareToInstall` 递归清 `{app}`（只清程序文件，保留 `unins*`；放在占用检查之前；唯一中止 = 精简版 `hostfxr.dll` 删不掉）。防两形态混装（D64） |
| 调度任务 | **不归安装器管**（lowest 权限）；管理端每次启动 `SchedulerTaskBootstrap` 自检自愈（D68） |
| 卸载可逆 | `InitializeUninstall` 用 `ShellExec('runas')` 拉起 `--restore-all --result-file <临时文件>` 轮询退出码，非 0 中止卸载（可强跳但二次确认） |
| 卸载数据 | `usUninstall` 时问一次是否删配置与日志（默认否；静默=否） |
| 缺运行时 | 三判据检测 + 架构匹配；提示 + 下载链接不阻断；缺任一项时完成页「启动 DelayStart」整项消失（`Check: RuntimeReadyForApp`） |
| 诊断开关 | `DELAYSTART_RUNTIME_CHECK` / `DELAYSTART_FAKE_MISSING` / `DELAYSTART_SKIP_PURGE` |

## 回归验证配方

```powershell
# 运行时检测（不安装）
$env:DELAYSTART_RUNTIME_CHECK = "$env:TEMP\rt.txt"
.\dist\DelayStart-Setup-0.1.0-win-x64-slim.exe /VERYSILENT /SUPPRESSMSGBOXES
Get-Content "$env:TEMP\rt.txt"      # dotnet=1 / winappruntime=1 / ready=1 / forced=0
# 缺运行时分支：再加 $env:DELAYSTART_FAKE_MISSING='1' → ready=0 / forced=1

# 装前清空（D64）：先伪造残留再装，断言被清
$app = "$env:LOCALAPPDATA\Programs\DelayStart"
New-Item -ItemType File -Path "$app\hostfxr.dll" -Force | Out-Null
.\dist\DelayStart-Setup-0.1.0-win-x64-slim.exe /VERYSILENT /SUPPRESSMSGBOXES
Test-Path "$app\hostfxr.dll"        # 期望 False；Test-Path "$app\DelayStart.exe" 期望 True
# 中止分支：另一进程占住 hostfxr.dll 再装 → 安装停在"准备安装"页且未删任何文件
# 观察删除明细：/LOG="%TEMP%\ds-install.log"，搜「安装目录已清理」「残留文件删不掉」
```

## 图标

```powershell
uv run tools\make-icon.py    # assets\icon\delay.png → AppIcon.ico + Scheduler.ico + SchedulerWarning.ico（--roles app/tray 可单出）
```

三份产物分别供管理端 exe / 调度端 exe + 托盘两态使用；换图后**必须重新构建**（exe 图标是编译期嵌入的）。混合 ICO 容器与托盘挑条目的坑见 `docs/pitfalls.md` 七。

## CI（`.github/workflows/release.yml`）

推 `v*` 标签自动建 Release，或手动 `workflow_dispatch`。矩阵 `rid × 形态` = 4 包，`fail-fast: false`；arm64 组先跑工具集检测。标签触发的版本号以标签为准；手动触发可填 `version`，都留空读 `Directory.Build.props`。
