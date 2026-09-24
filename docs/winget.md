# 上架 winget（Windows 包管理器社区源）

本文件是 DelayStart **手工提交到 winget 社区源的教程**：提交什么、文件怎么写、按哪几步做。
决策依据见 `decisions.md` **D110 / D111**；发版流程见 `installer/README.md`。

---

## 1. 提交什么

- **安装包**：`DelayStart-Setup-0.3.2-win-x64-slim.exe` 与 `…-win-arm64-slim.exe`（Release 资产，URL 版本固定）。
- **形态只发 slim**：15 MB（full 是 217 MB）。winget 场景下用户必然有网络，缺的运行时交给包管理器按依赖装 —— 比让用户去安装器结尾的下载链接里自己找更直接。
- **两个包依赖**（写进 manifest，winget 会自动先装）：

  | 包                                 | 谁需要         | 说明                           |
  | --------------------------------- | ----------- | ---------------------------- |
  | `Microsoft.DotNet.Runtime.10`     | 仅 slim      | slim 不含 .NET 运行时             |
  | `Microsoft.WindowsAppRuntime.1.8` | **两种形态都需要** | WinUI 3 要的 MSIX 框架包，安装器从来不管它 |

- **不提交 full 的两个包**：同架构摆两个 installer 时 winget 只取第一个，行为不直观。

---

## 2. 文件放哪

社区源里 `PackageIdentifier` 直接决定路径（**大小写敏感**，文件名必须与 identifier 逐字一致）：

```
manifests/f/FanchangWang/DelayStart/0.3.2/
├── FanchangWang.DelayStart.yaml                  ← version
├── FanchangWang.DelayStart.installer.yaml        ← installer
├── FanchangWang.DelayStart.locale.zh-CN.yaml     ← defaultLocale（DefaultLocale 指向它）
└── FanchangWang.DelayStart.locale.en-US.yaml     ← 可选，但推荐
```

必须**多文件**：singleton manifest 在社区源里被禁止。四个文件的 `ManifestVersion` 都用仓库当前推荐的 **1.12.0**。

---

## 3. 四个文件怎么写

> 推荐先用 `wingetcreate new <url1> <url2>` 生成骨架（它会读安装包、算哈希），再按下面的模板逐字段核对补齐 —— 生成器**不会**自动写好 `Scope`、`Dependencies` 和 `AppsAndFeaturesEntries`。

### 3.1 `FanchangWang.DelayStart.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json
PackageIdentifier: FanchangWang.DelayStart
PackageVersion: 0.3.2
DefaultLocale: zh-CN
ManifestType: version
ManifestVersion: 1.12.0
```

### 3.2 `FanchangWang.DelayStart.installer.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
PackageIdentifier: FanchangWang.DelayStart
PackageVersion: 0.3.2
Platform:
  - Windows.Desktop
MinimumOSVersion: 10.0.19041.0
InstallerType: inno
Scope: user
Dependencies:
  PackageDependencies:
    - PackageIdentifier: Microsoft.DotNet.Runtime.10
      MinimumVersion: 10.0.0
    - PackageIdentifier: Microsoft.WindowsAppRuntime.1.8
      MinimumVersion: 1.8.0
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/FanchangWang/DelayStart/releases/download/v0.3.2/DelayStart-Setup-0.3.2-win-x64-slim.exe
    InstallerSha256: <发版后填，64 位十六进制>
    AppsAndFeaturesEntries:
      - DisplayName: DelayStart
        Publisher: FanchangWang
        DisplayVersion: 0.3.2
        ProductCode: '{48BC0E34-50D1-4D82-908C-4A5C11AC5690}_is1'
  - Architecture: arm64
    InstallerUrl: https://github.com/FanchangWang/DelayStart/releases/download/v0.3.2/DelayStart-Setup-0.3.2-win-arm64-slim.exe
    InstallerSha256: <发版后填>
    AppsAndFeaturesEntries:
      - DisplayName: DelayStart
        Publisher: FanchangWang
        DisplayVersion: 0.3.2
        ProductCode: '{48BC0E34-50D1-4D82-908C-4A5C11AC5690}_is1'
ManifestType: installer
ManifestVersion: 1.12.0
```

字段说明：

- `InstallerType: inno` —— 必须写具体类型，不能写泛化的 `exe`；winget 靠它决定静默安装参数（`/SILENT` 一类），写错就变成"交互式安装"而验证失败。
- `Scope: user` —— 安装器是 `PrivilegesRequired=lowest`、装在 `%LOCALAPPDATA%\Programs\DelayStart`，是用户级安装。
- `MinimumOSVersion: 10.0.19041.0` —— 与各 csproj 的 `TargetPlatformMinVersion` 一致（NFR-5.1：Windows 10 21H2+）。
- `Dependencies` —— 见第 1 节。只写**包标识**，不要写下载地址。
- `DisplayName` / `DisplayVersion` —— 与安装器写进注册表的值一致（显示名是裸 `DelayStart`，版本是 `0.3.2`）；写上是为了把"版本匹配"钉死，避免版本号不一致时陷入升级循环。
- `ProductCode` —— 填 Inno 的卸载注册表键名，即 `AppId` + `_is1`，本项目为 `{48BC0E34-50D1-4D82-908C-4A5C11AC5690}_is1`。
  ⚠️ YAML 里必须加引号：值以 `{` 开头，裸写会被当成 flow mapping。
- 不写 `InstallerLocale` —— 安装器只有中文一种界面，声明它没有收益。

### 3.3 `FanchangWang.DelayStart.locale.zh-CN.yaml`

```yaml
# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json
PackageIdentifier: FanchangWang.DelayStart
PackageVersion: 0.3.2
PackageLocale: zh-CN
Publisher: FanchangWang
PublisherUrl: https://github.com/FanchangWang
PublisherSupportUrl: https://github.com/FanchangWang/DelayStart/issues
PackageName: DelayStart
PackageUrl: https://github.com/FanchangWang/DelayStart
License: MIT
LicenseUrl: https://github.com/FanchangWang/DelayStart/blob/main/LICENSE
Copyright: Copyright (c) FanchangWang
ShortDescription: 让开机自启动项按设定延时逐个启动，不挤在一起抢资源
Description: |-
  DelayStart 接管你选定的自启动项，在登录后按每个条目设定的延时依次启动，
  避免十几个程序同时抢占磁盘与 CPU 造成登录后长时间卡顿。支持注册表 Run 键、
  启动文件夹、计划任务与 UWP 应用四类来源，可给条目设置调度周期
  （每天 / 工作日 / 法定节假日等），并记录每次调度结果。
Moniker: delaystart
Tags:
  - startup
  - autostart
  - boot
  - performance
ReleaseNotesUrl: https://github.com/FanchangWang/DelayStart/releases/tag/v0.3.2
Documentations:
  - DocumentLabel: 使用说明
    DocumentUrl: https://github.com/FanchangWang/DelayStart#readme
ManifestType: defaultLocale
ManifestVersion: 1.12.0
```

### 3.4 `FanchangWang.DelayStart.locale.en-US.yaml`

同 3.3，把 `PackageLocale` 换成 `en-US`，字段用英文；`ShortDescription` / `Description` 必填。

---

## 4. 手工提交流程

### 步骤 0 · 一次性准备

```powershell
winget --version                                   # 需要 1.6 以上
winget install --id Microsoft.WingetCreate -e      # 清单生成器
```

提权 PowerShell 里打开本地清单支持（**这一步要管理员**）：

```powershell
winget settings --enable LocalManifestFiles
```

再把 `microsoft/winget-pkgs` fork 到自己的账号（`FanchangWang`）下。

### 步骤 1 · 先出安装包

打 `v0.3.2` 标签 → 等 Release workflow 出四个包 → 在 **Release 正文的附件说明表**里直接抄两个 slim 的 **SHA256**（`make-release-notes.ps1` 已经算好列出来了，不用自己算）。

> 顺序不能反：`InstallerSha256` 必须与资产逐字节一致，包重传过一次哈希就变了。

### 步骤 2 · 生成骨架

```powershell
# 工作目录随意，比如 %TEMP%\winget
wingetcreate new `
  https://github.com/FanchangWang/DelayStart/releases/download/v0.3.2/DelayStart-Setup-0.3.2-win-x64-slim.exe `
  https://github.com/FanchangWang/DelayStart/releases/download/v0.3.2/DelayStart-Setup-0.3.2-win-arm64-slim.exe
```

按第 3 节的模板逐字段补齐：`Scope` / `Dependencies` / `AppsAndFeaturesEntries` / `DefaultLocale` 与中文 locale 文件基本都要手工写。目录名用 `0.3.2`，文件名与 identifier 逐字一致。

### 步骤 3 · 本地校验

```powershell
winget validate --manifest .\FanchangWang.DelayStart\0.3.2
```

### 步骤 4 · 本地试装（**会真的装到本机**）

```powershell
winget install --manifest .\FanchangWang.DelayStart\0.3.2
winget list --id FanchangWang.DelayStart       # 列得出来 = 已装检测正常
winget uninstall --id FanchangWang.DelayStart  # 顺带验卸载
```

> ⚠️ 试装前先卸掉或备份本机日常用的 DelayStart，避免装出两份状态。
> 若 `winget list` 列不出来（说明 `ProductCode` / 显示名与注册表对不上），用 `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall /s /f DelayStart` 看真实键名再改 manifest。

### 步骤 5 · 提 PR

把四个文件推到 fork 的 `manifests/f/FanchangWang/DelayStart/0.3.2/`，向 `microsoft/winget-pkgs` 开 PR：

- 标题：`New package: FanchangWang.DelayStart version 0.3.2`
- 正文按仓库 PR 模板填写（说明已本地 `winget validate` + `winget install --manifest` 通过）
- **一个 PR 只放这一个版本、且只放 manifest 文件** —— 夹带 `README.md`、`doc/`、拼写文件的改动会被直接打回

### 步骤 6 · 跟进

PR 上会自动跑 6 个检查（清单校验 → URL 与 SmartScreen 信誉 → 域名 → 内容策略 → 哈希与杀毒扫描 → 标准用户静默安装 + 卸载）。失败会挂标签，对照 `ValidationFailureGuide.md` 处理。

**被指派回你之后 7 天内必须响应**，超时机器人自动关 PR。

---

## 5. 提交后可能遇到的问题

| 现象 | 处置 |
|---|---|
| 静默卸载被"UAC 弹窗阻塞进度"判失败 | 卸载器里 `--restore-all` 走 `ShellExec('runas')`（不还原就不删，宁可中止卸载）。失败分支有 `SuppressibleMsgBox(Default=IDYES)` 兜底，卸载最终仍会完成。真被拦下再单独评估改法 —— **不要顺手改**，那一路是 D61/D84/D85 逐轮真机验过的 |
| 安全扫描命中（PUA 一票否决） | 政策不看应用是否正当，只要命中就不能收。按 `Validation.md` 的申诉路径处理（向 Microsoft Defender 提交文件复检 + 附 PR 链接），重新触发用 `@wingetbot run` |
| 卸载后 `%APPDATA%\DelayStart` 与 `%LOCALAPPDATA%\DelayStart` 还在 | 这是设计（默认保留用户数据），不是缺陷。静默卸载可用 `/DELETEDATA` 清除，但 manifest 里**不**传它 |
| 干净机器上装完程序起不来 | winget 客户端自身（1.12 起 App Installer 是 WinUI 3）就依赖 `Microsoft.WindowsAppRuntime.1.8`，验证环境里必然有它；真实用户那边靠 manifest 的 `Dependencies` 自动补 |

---

## 6. 首次合并之后：自动化

首次 PR 合并后再谈自动化。届时在 **`release.yml` 里新增一个 job**（`needs: release`）跑 `komac update --submit`（或 `vedantmgoyal9/winget-releaser`，底层就是 komac）。

三个必须记住的点：

1. **不能靠 `on: release: [published]` 触发** —— 本仓库的 Release 是 `GITHUB_TOKEN` 建的，GitHub 防递归规定这种事件不会再触发别的 workflow。所以要走**同一个 workflow 内的后续 job**。
2. 向 winget-pkgs 提 PR 需要 **classic PAT**（`public_repo`），**细粒度 PAT 不支持**；存成仓库 secret（如 `WINGET_TOKEN`）。
3. 提 PR 用的是 **fork**，fork 与上游的同步由 komac 负责；fork 所在账号默认取仓库 owner（`FanchangWang`）。
