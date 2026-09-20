# DelayStart — 架构与实现设计

> **文档边界**：本文回答"代码怎么搭"（How）。
> 需求见 `requirements.md`，界面文案见 `design-spec.md`，调度端交互见 `scheduler-design.md`，
> Win32 / 注册表细节见 `api-analysis.md`，编码风格见 `coding-standards.md`。

---

## 一、解决方案结构

```
DelayStart/
├─ DelayStart.slnx                      # XML 格式解决方案（.NET 9+；若不兼容则回退 .sln）
├─ Directory.Build.props                # 全解决方案共享的编译属性
├─ Directory.Packages.props             # 中央包版本管理（CPM）
├─ .editorconfig                        # 代码风格强制
├─ .gitignore
├─ README.md
├─ docs/                                # 本目录：需求与设计文档
├─ src/
│  ├─ DelayStart.Core/                  # ★ AOT 安全：被调度端引用的全部代码
│  ├─ DelayStart.Management/            # ☆ 非 AOT：扫描/接管/计划任务/COM 互操作
│  ├─ DelayStart.App/                   # WinUI 3 管理端
│  └─ DelayStart.Scheduler/             # WinForms + NativeAOT 调度端
└─ tests/
   └─ DelayStart.Core.Tests/            # 单元测试（xUnit v3）
```

### 1.1 为什么 Core 和 Management 要拆开

**这是本架构最重要的一个决定。**

调度端用 NativeAOT 发布，因此**它引用的一切代码都必须满足 AOT 约束**。而自启动扫描所需的三类依赖恰好都不满足：

| 依赖 | 用途 | AOT 问题 |
|---|---|---|
| `Microsoft.Win32.TaskScheduler` | 计划任务读写 | 大量依赖 COM 互操作与反射 |
| `IShellLinkW` / `IShellItemImageFactory` 等 COM 互操作 | `.lnk` 解析、图标提取 | NativeAOT 官方限制："Windows: No built-in COM" |
| 注册表 `RegistryKey` + 反射式 JSON | 扫描与配置 | 注册表本身 OK，反射序列化不行 |

如果按"领域"划分（把扫描和调度放一个 Core），调度端就会被这些依赖拖死，AOT 编译直接失败或运行时崩溃。

**所以按"AOT 兼容性"切分：**

| 项目 | AOT 安全 | 内容 | 被谁引用 |
|---|---|---|---|
| **`DelayStart.Core`** | ✅ 必须 | 数据模型、配置读写（源生成 JSON）、时序计算、路径/参数解析、进程启动（含 `CreateProcessAsUser`）、日志、基础 P/Invoke | App + **Scheduler** + Tests |
| **`DelayStart.Management`** | ❌ 不需要 | 各来源扫描器、软禁用操作、接管/恢复、计划任务注册、`.lnk` 解析、图标提取、服务查询 | App + Tests |

调度端**只引用 Core**，因此它拿到的是一个经过裁剪、零 COM、零反射的最小集合。这是一条**硬边界**：任何往 Core 里放 COM 互操作或反射代码的改动，都会直接打断调度端的发布。

> **守门规则**：Core 项目的 `Directory.Build.props` 中开启 `<IsAotCompatible>true</IsAotCompatible>`，让编译器在构建期就报出 AOT 不兼容的 API 使用。这是防止架构腐化的自动化护栏。

### 1.2 依赖方向

```
        DelayStart.App ──────┬──> DelayStart.Management ──┐
        (WinUI 3)            │                            │
                             └──> DelayStart.Core <────────┘
                                        ▲
        DelayStart.Scheduler ───────────┘
        (纯 Win32 + AOT)

        ⛔ DelayStart.Agent（普通用户代理，D38）—— D40 起已彻底移除：
           slnx 无引用、安装器不再打包、调用链已切到调度端亲自降权。
           仅剩源码目录 src\DelayStart.Agent\ 待手工删除（不参与任何构建）。

        DelayStart.Core.Tests ──> DelayStart.Core (+ Management)
```

- 单向依赖，无循环
- `Core` 不引用任何 UI 框架
- `Management` 只依赖 `Core`
- `Scheduler` **绝不**引用 `Management`
- ⛔ `Agent`（D38）：D40 起移除，不再属于分层（曾为"调度器 ↔ 代理"命名管道 JSON 行协议的两端）
- 🔴 **D40（2026-09-20）**：调度端改为**亲自降权**（外壳令牌 → `DuplicateTokenEx` 主令牌 → `CreateProcessWithTokenW`），代理退出调用链。两版依赖对比：D39 的代理**本身也是经 explorer 委托拉起**，与直接降权依赖同一个外壳 —— 方案 7 不新增依赖，却少一层进程与一条管道。真机对照见 `requirements.md` D40

### 1.3 目标框架

| 项目 | TFM | 关键属性 |
|---|---|---|
| `DelayStart.Core` | `net10.0-windows` | `IsAotCompatible=true`、`Nullable=enable` |
| `DelayStart.Management` | `net10.0-windows` | 普通库 |
| `DelayStart.App` | `net10.0-windows10.0.26100.0`<br>`TargetPlatformMinVersion=10.0.19041.0` | `UseWinUI=true`、`AssemblyName=DelayStart`、`WindowsPackageType=None`（unpackaged）、`ApplicationManifest=app.manifest`、`WindowsAppSDKSelfContained`（取值由 R9 结果定，见 `build-and-test.md` 7.1） |
| `DelayStart.Scheduler` | `net10.0-windows` | `OutputType=WinExe`、`PublishAot=true`、`RuntimeIdentifier=win-x64`（D24=B 纯 Win32，无 UI 框架引用） |
| ~~`DelayStart.Agent`~~ | — | **D40 起移除**：普通用户代理（D38 新增，命名管道服务端，AOT 单文件，零项目引用）被调度端亲自降权取代。已从 slnx / 安装器 / 发布脚本摘除；源码目录 `src\DelayStart.Agent\` 待手工删除 |
| `DelayStart.Core.Tests` | `net10.0-windows` | `IsPackable=false`、`OutputType=Exe`（xUnit v3 硬要求，缺了直接构建失败） |

> **App 的 TFM 与初稿不同（Phase 0 实测修正）**：原写 `net10.0-windows10.0.19041.0`，实际模板产出 **`net10.0-windows10.0.26100.0`**。
> 26100 是 `TargetPlatformVersion`（编译期用的 SDK 版本），**不支持的下限由 `TargetPlatformMinVersion` 控制** —— 已显式设为 `10.0.19041.0`，与 NFR-5.1（Windows 10 21H2+）对齐；它同时决定 `SupportedOSPlatformVersion`，因此 CA1416 会在编译期拦住"误用 Win11 专属 API"。保持模板默认值（而非回改 19041）是因为本机 Windows SDK 就是 26100，且这是微软当前模板的默认组合。

> 所有版本号以创建项目时的最新稳定版为准写入 `Directory.Packages.props`，不照搬 demo 的版本下限。

**版本基线（本机实测，2026-09-19）**：

| 包 | 版本 | 说明 |
|---|---|---|
| `Microsoft.WindowsAppSDK` | **`1.8.260317003`** | WinUI 3 模板的默认值，CPM 中固定。**1.8 是元包**，实际内容由 `Microsoft.WindowsAppSDK.WinUI`（1.8.260224000）/`.Foundation`/`.Base`/`.Runtime` 等子包提供 |
| `Microsoft.Windows.SDK.BuildTools` | **`10.0.26100.7705`** | 同上 |
| `xunit.v3.mtp-v2` | **`4.0.1`** | `xunit.v3.templates` 4.0.1 产出值（短名 `xunit3`）。见 `build-and-test.md` 4.1 |
| `Microsoft.Windows.SDK.BuildTools.WinApp` | ❌ **已移除** | 模板默认引入，它是为"**打包应用**的 `dotnet run`"服务的（winapp CLI + AUMID 注册），unpackaged 用不上 |

**Phase 0 实测的骨架状态（2026-09-19，构建 0 警告 0 错误）**：

| 项 | 实测值 |
|---|---|
| 项目产出 | `DelayStart.dll`→`DelayStart.exe`（App）、`DelayStart.Scheduler.exe`（Scheduler）、`DelayStart.Core.dll`、`DelayStart.Management.dll` |
| App 自包含产物体积 | **134.8 MB / 239 个文件**（`WindowsAppSDKSelfContained=true`），与 `build-and-test.md` 7.1 的 100–150 MB 预估吻合 |
| `app.manifest` 是否嵌进 exe | ✅ **已嵌入**（在 exe 字节流中检出 `requireAdministrator` / `PerMonitorV2` / `longPathAware`）。**这只是"声明进去了"，是否被 Windows 采纳仍待 R9 双击实测** |
| 仓库级设置 | `Directory.Build.props`（LangVersion / Nullable / TreatWarningsAsErrors / EnforceCodeStyleInBuild / AnalysisLevel / InvariantGlobalization）、`Directory.Packages.props`（CPM）均已落地 |

> ⚠️ **`dotnet new` 与 CPM 的先后顺序是个坑**（Phase 0 踩到，详见 `build-and-test.md` 2.1）：
> 若先生成 `Directory.Packages.props`（含 `ManagePackageVersionsCentrally=true`），
> 再跑 WinUI 模板，模板的收尾步骤 `dotnet add package` 会失败（NU1008），
> 生成的 csproj 会带内联 `Version` 而无法还原。

**项目创建一律走 `dotnet new`**，命令清单、模板实测事实与**工具链前置依赖**（VS 组件正确 ID、Inno Setup 现状）见 **`build-and-test.md` 1.2 / 2.1**。其中一条硬限制必须记住：**WinUI 3 模板只生成 MSIX 打包工程，没有 unpackaged 开关**，生成后需按 2.1 的改造清单转成 unpackaged（本项目 D22 要求 unpackaged + 安装器分发）。

**关于测试项目的 TFM**：微软官方文档 *Test apps built with the Windows App SDK and WinUI 3* 要求"测试项目的 TFM 必须与 WinUI 3 项目匹配、加 `WindowsAppSDKSelfContained`、机器上要装 Windows App Runtime"——**本项目不适用这套配置**。

原因是那套要求的前提是**测试项目直接引用 WinUI 3 项目**。本项目的测试项目只引用 `Core` / `Management`（均为普通类库，无 WinUI 依赖），因此：

| 项 | 微软 WinUI 3 测试项目要求 | 本项目 |
|---|---|---|
| TFM | `net10.0-windows10.0.19041.0` | `net10.0-windows` |
| `WindowsAppSDKSelfContained` | 需要 | **不需要** |
| 预装 Windows App Runtime | 需要 | **不需要** |
| `RuntimeIdentifiers` 多 RID | 需要 | **不需要** |

**这是 1.1 节分层策略的额外收益**：UI 与逻辑彻底解耦后，逻辑测试不必背 WindowsAppSDK 的包袱，跑起来是秒级、无环境依赖的。反过来说，**任何试图让测试项目引用 `DelayStart.App` 的改动，都会立刻把这些包袱全部带回来**——不要这么做。

### 1.4 权限模型（D20）

**结论：两个进程全程以管理员权限运行。**

| 进程 | 权限 | 实现 | 理由 |
|---|---|---|---|
| `DelayStart.App` | **必须 elevated** | `app.manifest` → `requestedExecutionLevel level="requireAdministrator" uiAccess="false"` | 核心链路 100% 需要：注册调度器计划任务、HKLM 三项软禁用、系统启动文件夹、系统级计划任务、服务 `delayed-auto` |
| `DelayStart.Scheduler` | **必须 elevated** | 计划任务 `RunLevel=Highest` | 免 UAC 启动"管理员身份"的托管程序 |

**原「按需提权」建议已被否**，完整论证见 `design-spec.md` 6.2。核心判据：**只要有一个必需功能必须提权，按需提权就省不掉那次 UAC**，剩下的只是纯粹的复杂度。

**必须配套实现的三件事：**

1. **UAC 被拒 → 退出，不降级。** 启动时检测 `IsInRole(WindowsBuiltInRole.Administrator)`；若为 false，显示说明后退出（对应 `requirements.md` E17）。降级运行会造成"用户以为禁用成功、实际静默失败"的最坏状态。
2. **提权窗口的拖放**（详见 R10）。主路径是 `[浏览…]` 按钮，**不依赖拖放**；拖放作为增强能力尝试补上。
3. **拒绝「以其他用户身份运行」与 OTS 提权**（NFR-3.8 / E18）。这两类模式下 `%APPDATA%` / `%LOCALAPPDATA%` 指向另一账户，配置读不到。判定用 **SID 比对**，不用 `IsInRole` —— 细则见 1.5 第 6 条。

> **实现注意**：`Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` 在 **UAC 提权**（同一用户 + 管理员令牌）场景下是正确的；只有"以其他用户身份运行"才会错。这两者必须区分开，不要因为担心前者而误封后者。

**App 提权的已知风险（R9）**：社区报告在 `WindowsAppSDKSelfContained=true` 时，`app.manifest` 里的 `requestedExecutionLevel` 可能被忽略（不弹 UAC、也不提权）。**Phase 0 必须实测**，见 R9 的三条降级路径。

### 1.5 路径与数据目录（D23）

**安装目录（per-user，只读）**

```
%LOCALAPPDATA%\Programs\DelayStart\
├─ DelayStart.exe                # 管理端（unpackaged）
├─ DelayStart.Scheduler.exe      # 调度端（NativeAOT）
├─ LICENSE
├─ README.md
└─ third-party-notices.txt
```

**运行时数据（与安装目录完全分离）**

| 类别 | 路径 | 说明 |
|---|---|---|
| 配置 | `%APPDATA%\DelayStart\config.json` | **Roaming**。跨机器可漫游的个人配置 |
| 日志 | `%LOCALAPPDATA%\DelayStart\logs\scheduler.log`<br>`%LOCALAPPDATA%\DelayStart\logs\manager.log` | **Local**。机器相关，不漫游 |
| 实时状态 | `%LOCALAPPDATA%\DelayStart\state\current-run.json` | 调度端每次状态变化原子重写；管理端只读（D19） |
| 运行归档 | `%LOCALAPPDATA%\DelayStart\runs\<runId>.json` | 保留最近 30 次 |

**实现规则（编码时必须照做）：**

1. 🔴 **禁止硬编码路径**。全部路径必须经由 `PathService`（`Core` 层）解析：`InstalledRoot` / `ConfigRoot` / `LocalRoot` / `LogsRoot` / `StateRoot` / `RunsRoot`。任何类库里出现 `@"C:\..."` 或手工拼 `Environment.GetFolderPath` 都算违规。
2. **安装目录只读**。`PathService.InstalledRoot` 仅用于"取自身 exe 路径"（注册计划任务时用），**不得作为任何写入目标**（NFR-6.7）。
3. **调试覆盖开关**：`DELAYSTART_LOCAL_DIR` / `DELAYSTART_CONFIG_DIR` 分别覆盖 Local 根与 Roaming 根；未设置时走 `%LOCALAPPDATA%` / `%APPDATA%`。这两个变量**只用于开发与测试**，正式代码不得依赖。
4. **Roaming 的代价必须兜住**：域环境下漫游配置可能把机器相关的条目列表带到另一台机器。配置加载后必须按 `ItemKey` 校验实际存在性，不存在的条目标记「已失效」且**不参与调度**（复用 E3 逻辑），不得直接报错或静默启动失败。
5. **计划任务身份 = 交互用户**，`LogonType=Interactive` + `RunLevel=Highest`，🔴 禁止 SYSTEM（NFR-6.8）。系统级任务会让 `%LOCALAPPDATA%` 指向 `C:\Windows\System32\config\systemprofile`，配置读不到、日志写错位置。
6. **禁止「以其他用户身份运行」与 OTS 提权**（NFR-3.8 / E18）。判定方式：**提权后的用户 SID 是否等于交互会话用户 SID**（取本会话 `explorer.exe` 的令牌用户）。`IsInRole(Administrator)` 只能判断"是否提权"，判断不出"是不是换了人"。

---

## 二、Core 层设计

### 2.1 目录结构

```
DelayStart.Core/
├─ Abstractions/
│  ├─ IAppConfigStore.cs           # 配置读写
│  ├─ IRunStateStore.cs            # current-run.json / runs/ 读写
│  ├─ IProcessLauncher.cs          # 进程启动抽象（便于测试）⏳ 实现体见 Launch/ 注
│  ├─ IClock.cs                    # 时间抽象（便于测试时序）
│  ├─ ILogSink.cs                  # ★ 只有 Write(LogLevel, …) 两个方法，见下方注
│  ├─ LogLevel.cs                  # enum：Info / Warn / Error
│  └─ LogSinkExtensions.cs         # Info() / Warn() / Error() 便捷扩展方法
├─ Models/
│  ├─ StartupSource.cs             # enum
│  ├─ StartupScope.cs              # enum
│  ├─ StartupEntry.cs              # 扫描结果（内存模型）
│  ├─ DelayedItem.cs               # 延时条目（持久化）
│  ├─ OriginalState.cs
│  ├─ AppConfig.cs                 # 含 CurrentVersion = 2
│  ├─ Settings.cs
│  ├─ RunRecord.cs
│  ├─ RunItemResult.cs
│  ├─ RunItemState.cs              # enum
│  ├─ ProcessSnapshot.cs           # record struct(HasExited, ExitCode)，机制 7 用
│  ├─ ParsedCommandLine.cs         # record struct(Path, Arguments)
│  ├─ NotifyMode.cs                # enum：FailuresOnly / Always / Never（D15）
│  ├─ StartupFailureReason.cs      # enum，异常场景矩阵的机器可读镜像
│  └─ StartupOperationException.cs # ★ 构造参数强制带 EntryId，见下方注
├─ Services/
│  ├─ PathService.cs               # ★ D23 路径布局 + 环境变量覆盖（新增）
│  ├─ AtomicFileWriter.cs          # ★ 机制 8 的唯一实现（新增）
│  ├─ ConfigService.cs             # 加载/校验/迁移/原子保存
│  ├─ RunStateService.cs           # 状态与归档读写
│  ├─ CommandLineService.cs        # 命令行解析 + 参数拼接（★ 纯逻辑，重点测试对象）
│  ├─ LaunchTargetTypes.cs         # ★ D47 目标类型白名单（编辑器与调度端唯一事实来源）
│  ├─ PowerShellHost.cs            # ★ D47 .ps1 宿主解析 + 命令行构造（★ 纯逻辑）
│  ├─ DelayCalculator.cs           # 时序计算（★ 纯逻辑，重点测试对象）
│  ├─ ItemKeyBuilder.cs            # 稳定主键生成（★ 纯逻辑）
│  ├─ StartupSortComparer.cs       # (delay, sortOrder) 排序（★ 纯逻辑）
│  ├─ LaunchResultEvaluator.cs     # 启动成功/失败判定（★ 纯逻辑）
│  ├─ FailureStreakService.cs      # ★ 连续失败聚合（★ 纯逻辑，D31：管理端与调度端共用）
│  └─ SystemClock.cs               # IClock 的生产实现
├─ Launch/
│  ├─ LaunchOutcome.cs             # 启动动作的原始结果
│  ├─ LaunchEvaluation.cs          # record struct(State, Reason, Message)
│  ├─ ProcessLauncher.cs           # ⏳ Phase 4（D27=A）
│  └─ TokenHelper.cs               # ⏳ Phase 4（D27=A）
├─ Logging/
│  └─ FileLogger.cs                # 滚动日志
├─ Interop/                        # ⏳ Phase 4（D27=A），Phase 1 不创建该目录
│  ├─ Kernel32.cs
│  ├─ AdvApi32.cs
│  ├─ WtsApi32.cs
│  └─ UserEnv.cs
└─ Serialization/
   ├─ JsonContext.cs               # JsonSerializerContext 源生成
   ├─ LegacyConfigV1.cs            # v1 迁移 DTO（internal，只在迁移时用）
   └─ LegacyItemV1.cs              # v1 迁移 DTO（internal）
```

> **注 1（F3，Phase 1 修订）**：Core 的 `Interop/` **不再包含 `Shell32.cs`**。原表中那一项是误植 ——
> Core 从不直接调 Shell32（`Process.Start` 由 BCL 封装，`ShellExecute` 走 `ProcessStartInfo.UseShellExecute`），
> Shell 相关 P/Invoke 全部属于 Management 层（见 3.1）。
>
> **注 2（`ILogSink` 的形状）**：接口只有 `Write(LogLevel, string)` 与 `Write(LogLevel, Exception, string)` 两个方法，
> `Info/Warn/Error` 由 `LogSinkExtensions` 提供。原因：把 `Error` 直接做成接口成员会触发 **CA1716**
> （`Error` 是 VB 保留字），而抑制分析器不如改设计 —— 现在的形状**新增日志级别不需要动接口**。
>
> **注 3（`StartupOperationException`）**：构造签名强制要求 `EntryId`，让"失败必须定位到具体条目"
> 从约定变成**编译期约束**（R6 的治理手段）。
>
> **注 4（`FailureStreakService` 为什么在 Core，D31）**：它原先被安排在 Management 层，与两条硬约束
> **直接冲突** —— `scheduler-design.md` 9.2 要求**调度端**的托盘角标按连续失败次数升级，
> 而调度端**只引用 Core**（1.2 分层表）。登录那一刻管理端根本没运行，不可能算出调度端要用的数。
> 该计算是**纯函数**（只吃 `RunRecord` 列表，不碰注册表 / 无 COM / 无反射），AOT 安全，故搬入 Core 两端共用。
> 调度端仍保持**无状态**（每次现算、不维护计数器），与原设计意图一致。详见 `design-spec.md` 6.11.2。

### 2.2 各模块职责

| 模块 | 职责 | AOT 备注 |
|---|---|---|
| `PathService` | 解析 D23 的四个根（安装根 / 配置根 / 本地根 / 状态与归档子目录），支持 `DELAYSTART_LOCAL_DIR` / `DELAYSTART_CONFIG_DIR` 覆盖（测试与绿色版用） | 纯 `Environment` + 字符串，无 IO 副作用。`WritableRoots` **刻意不含安装根** —— 安装目录只读是 D23 的硬约束 |
| `AtomicFileWriter` | 机制 8 的唯一实现：`<name>.tmp` → `File.Replace` / `File.Move` 回退 | 纯 BCL |
| `ConfigService` | 加载 `config.json` → 校验 → 迁移（v1→v2）→ 原子保存 | 用 `JsonContext` 源生成 |
| `RunStateService` | 读写 `state/current-run.json`、归档 `runs/<runId>.json`、清理超过 30 份的旧记录 | 同上 |
| `CommandLineService` | ① 解析注册表值 / 快捷方式参数为 `(Path, Args)`；② 拼接最终命令行 | 纯字符串逻辑，无依赖 |
| `LaunchTargetTypes` | D47 目标类型白名单（`.exe`/`.lnk`/`.bat`/`.cmd`/`.ps1`）。编辑器（选择器白名单、拖放校验）与调度端（启动路由）**共用同一份** —— 各写一份的后果是"选得进来却启不动"（旧 `.msi` 正是如此），且不会报错 | 纯字符串判断，无依赖 |
| `PowerShellHost` | D47 `.ps1` 的宿主解析（`pwsh.exe` 优先 → `powershell.exe` 回落）与命令行构造（`-NoProfile -ExecutionPolicy Bypass -File "<脚本>" <参数>`） | 纯字符串 + `File.Exists` 探测；全部探测入口可注入假探针，测试不依赖本机装没装 PowerShell 7 |
| `DelayCalculator` | `remaining = DelaySeconds - elapsed`；排序；计算调度器驻留时长 | 纯计算，注入 `IClock` |
| `ItemKeyBuilder` | 生成稳定主键 `{source}:{scope}:{sourceKey}` | 纯计算 |
| `LaunchResultEvaluator` | 依据"创建结果 + 1.5 秒后 `HasExited` + 退出码"判定成功/失败；UWP 因无 PID 而收到 `null` 快照时直接判成功（D28） | 纯逻辑，可注入假进程探针 |
| `FailureStreakService` | 扫最近若干份 `runs/*.json`，**现算**连续失败次数与提醒级别（E13）；输出供管理端横幅与调度端角标使用 | 纯函数，只吃 `RunRecord` 列表。**D31 从 Management 搬入**，见注 4 |
| `SystemClock` | `IClock` 的生产实现 | `DateTimeOffset.Now`，测试注入 `FakeClock` 替代 |
| `ProcessLauncher` | 管理员条目继承令牌启动；普通条目降权启动；降权失败回退 | P/Invoke，`LibraryImport`。**Phase 4**（D27=A） |
| `FileLogger` | 按大小滚动的文本日志 | 无第三方日志库（省体积） |

### 2.3 关键机制（编码时必须严格照做）

#### 机制 1：稳定主键

```csharp
// ItemKeyBuilder
public static string Build(StartupSource source, StartupScope scope, string sourceKey)
    => $"{SourceToken(source)}:{ScopeToken(scope)}:{sourceKey.Trim().ToLowerInvariant()}";
```

- 系统条目：由三元组生成，**不存 Guid**，保证重新扫描后仍能匹配
- 手动条目：`manual:{Guid.NewGuid():N}`，因为它没有任何系统来源可锚定
- 用途：判断"该项是否已被接管"。**禁止**用 `(Name, Source)` 二元组（坑 6：同名不同来源会误判）

#### 机制 2：`StartupApproved` 键名三级回退

判断一个注册表/启动文件夹项是否被软禁用，必须依次尝试三个候选名，全部未命中才算"未禁用"：

```
候选 1: 原值名                    "Weixin"
候选 2: 原值名 + ".exe"           "Weixin.exe"
候选 3: 去扩展名的原值名           Path.GetFileNameWithoutExtension("Weixin")
```

因为任务管理器写标记时用的名字不保证与原值名一致。**不做回退会把已禁用的项误报为启用**（坑 1）。

#### 机制 3：`scope` 显式枚举

```csharp
public enum StartupScope { None, Hkcu, Hklm, HklmWow, UserFolder, SystemFolder }
```

**禁止**用字符串 `StartsWith("HKLM")` 猜 hive（坑 5）。恢复时必须精确知道标记该写回哪个 hive，写错是**静默失效**——没有任何报错，用户以为恢复了其实没有。

#### 机制 4：软禁用矩阵

| 来源 + scope | 禁用动作 | 恢复动作 | 标记位置 |
|---|---|---|---|
| Registry + Hkcu | 写 `[0]=0x03` 12 字节 | `DeleteValue` | `HKCU\...\Explorer\StartupApproved\Run` |
| Registry + Hklm | 同上 | 同上 | `HKLM\...\Explorer\StartupApproved\Run` |
| Registry + HklmWow | 同上 | 同上 | `HKLM\...\Explorer\StartupApproved\Run32` ⚠️ |
| StartupFolder + UserFolder | 同上 | 同上 | `HKCU\...\StartupApproved\StartupFolder` |
| StartupFolder + SystemFolder | 同上 | 同上 | `HKLM\...\StartupApproved\StartupFolder` |
| ScheduledTask | `task.Enabled = false` | `task.Enabled = true` | 任务定义本身 |
| Uwp | `State = 0` (DWord) | `State = 2` | `...\AppModel\SystemAppData\<PFN>\<TaskId>` |
| Manual | **无动作** | **无动作** | — |

标记数据格式（12 字节）：

```
[0]     = 0x03 禁用 / 0x02 启用
[1..3]  = 0
[4..11] = BitConverter.GetBytes(DateTime.UtcNow.ToFileTime())   // 小端 FILETIME
```

#### 机制 5：调度时序

```csharp
// DelayCalculator：注意是「绝对时间点」，不是累加
public static TimeSpan Remaining(int delaySeconds, TimeSpan elapsedSinceRunStart)
    => TimeSpan.FromSeconds(delaySeconds) - elapsedSinceRunStart;   // <= 0 → 立即启动
```

- 用 `Stopwatch` 计时（单调时钟），**不用** `DateTime.Now`（受系统时间调整影响，见 E15）
- 排序：`OrderBy(DelaySeconds).ThenBy(SortOrder)`
- 只保证**发起顺序**，不保证前一个已完成初始化（坑 7）—— UI 必须提示有依赖关系时用不同延时值
- 驻留时长（只读展示值）= `TimeSpan.FromSeconds(maxDelay) + trayKeep + 初始化开销估值`

#### 机制 6：启动身份

| 条目 `RunAsAdmin` | 实现 |
|---|---|
| `true` | 调度端自身以 `Highest` 运行 → 直接 `Process.Start`（继承令牌），**不弹 UAC** |
| `false` | `WTSQueryUserToken` → `CreateEnvironmentBlock` → `CreateProcessAsUser`，`lpDesktop = "winsta0\\default"` |
| `false` 但降权失败 | 回退 `Process.Start` + 记日志（宁可启动成管理员，也不要启动失败） |

- 会话 ID 优先用 `WTSGetActiveConsoleSessionId()`，失败再退回 `Process.GetCurrentProcess().SessionId`（坑 8）
- `dwCreationFlags = CREATE_UNICODE_ENVIRONMENT(0x400) | CREATE_NEW_CONSOLE(0x10)`
- `LibraryImport` 声明的 `bool` 返回值必须写 `[return: MarshalAs(UnmanagedType.Bool)]`（坑 9）

#### 机制 7：启动成功判定

```
1. CreateProcess / Process.Start 未抛异常
2. 延时 1500 ms 后复查 HasExited
   ├─ 仍存活                    → ok
   ├─ 已退出 && ExitCode == 0   → ok    （拉起已有实例的正常行为）
   └─ 已退出 && ExitCode != 0   → failed（记录退出码）
```

第 2 步的"退出码 0 算成功"是**必要的宽容**——`msedge.exe` 等大量程序在拉起已有实例后会立刻退出。严格判定会产生大量假失败。

#### 机制 8：原子写

所有配置与状态文件统一走：

```
写同目录 <name>.tmp → Flush + Dispose → File.Replace(tmp, target, backup: null)
```

`File.Replace` 在目标不存在时会抛异常，需先用 `File.Exists` 分支到 `File.Move(tmp, target, overwrite: true)`。

#### 机制 9：单实例

调度端用 `new Mutex(true, @"Local\DelayStartScheduler", out createdNew)` + `createdNew` 判定。已存在时**立即退出，零副作用**（不写状态文件、不发通知）。

管理端用同样的手法但名字不同（`Local\DelayStartManager`），行为改为"激活已有窗口"。

---

## 三、Management 层设计

### 3.1 目录结构

```
DelayStart.Management/
├─ Abstractions/
│  ├─ IStartupSource.cs            # 扫描 + 禁用/启用 的统一接口
│  ├─ IShellLinkResolver.cs
│  ├─ IScheduledTaskGateway.cs     # 计划任务访问抽象 —— 接管编排靠它注入假实现（D33）
│  └─ （IIconProvider 不设抽象 —— 图标提取没有第二实现，组合根直接注册具体类 IconProvider）
├─ Sources/
│  ├─ RegistryStartupSource.cs     # HKCU / HKLM / HKLM-WOW64 三个实例
│  ├─ StartupFolderSource.cs       # 用户 / 系统 两个实例
│  ├─ ScheduledTaskSource.cs
│  └─ UwpStartupSource.cs
├─ Services/
│  ├─ ScanService.cs               # 遍历各 Source，单源失败不影响其余（FR-1.4）
│  ├─ TakeoverService.cs           # 接管 / 移除（失败逆序回滚，FR-3.1）
│  ├─ TaskRegistrationService.cs   # DelayStartScheduler 的创建/更新/删除（FR-11）
│  ├─ ShellLinkResolver.cs         # IShellLinkW 解析目标与参数（FR-1.7）
│  ├─ IconProvider.cs              # ✅ Phase 3（D30）：IShellItemImageFactory，内存缓存 / 失败返 null
│  ├─ ServiceQueryService.cs       # ⏳ Phase 3/5（D30）：服务查询 + delayed-auto
│  └─ SystemStartupInspector.cs    # ⏳ Phase 5（D30）：驱动 / Winlogon / 登录脚本
├─ Interop/
│  ├─ ShellInterfaces.cs           # IShellLinkW / IPersistFile
│  ├─ Shell32.cs                   # ⏳ Phase 3（D30）：SHCreateItemFromParsingName
│  ├─ AdvApi32.cs                  # ⏳ Phase 5（D30）：ChangeServiceConfig
│  └─ Shlwapi.cs                   # SHLoadIndirectString（UWP 显示名，FR-1.8）
└─ Serialization/
   └─ (复用 Core 的 JsonContext)
```

> **Phase 2 范围说明（D30 / D31）**
>
> - 带 `⏳` 的六项**本期不实现**（D30）。它们本期没有消费者（图标是纯 UI 依赖，FR-7 的页面在 Phase 3），
>   且按 `coding-standards.md` 14.1 **进不了单元测试** —— 写了只能靠"编译通过"自证，与 D27 拒绝在
>   Phase 1 写 P/Invoke 启动器是同一条逻辑。验证时**不得**把它们算作已完成项。
> - `FailureStreakService.cs` **已移出本层**（D31），改在 `Core/Services/` —— 理由见 2.1 注 4。
>   原表中它列在 Management 是本项目**唯一一处真的实现不了的设计**（调度端要用它，却拿不到它）。

### 3.2 `IStartupSource` 统一接口

```csharp
public interface IStartupSource
{
    StartupSource Kind { get; }
    StartupScope Scope { get; }
    string DisplayName { get; }
    bool RequiresElevation { get; }

    IReadOnlyList<StartupEntry> Scan();
    void Disable(StartupEntry entry);   // 软禁用
    void Enable(StartupEntry entry);    // 删除标记，恢复默认
}
```

`ScanService` 遍历所有实现，**每个 Source 单独 try/catch**，一个失败不影响其余（FR-1.4）。扩展新来源只需新增一个实现并注册——满足开闭原则。

### 3.3 接管的事务性

`TakeoverService.Takeover(entry, options)` 是有副作用的复合操作，必须保证**失败不留下半成品**：

```
1. 记录 originalState（读当前启用状态）
2. 写配置（新增 DelayedItem）              ─┐
3. 软禁用原自启动项                          ├─ 任一步抛异常 →
4. 确保计划任务存在                          ─┘  回滚已完成的步骤（逆序）+ 报错
```

顺序刻意的：**先写配置再禁用系统项**。反过来的话，若配置写入失败，用户的自启动项已经被禁用了却没有任何记录能恢复它——那是最坏的结果。

`Remove(item)` 反向执行：恢复系统项 → 删配置 → 若已无条目则删除计划任务。

### 3.4 图标提取（✅ Phase 3 已实现，D30）

> **Phase 3 落地补充**（与设计意图的差异）：提取结果不返回 `HBITMAP` 也不编码 PNG ——
> 返回 `IconPixels`（32bpp BGRA 裸像素，自上而下行序，经 `GetDIBits` 负高度读出，
> 避免 `Bitmap.FromHbitmap` 丢 alpha 的经典坑）。App 侧经 `IconRenderer` 一次性灌进
> `WriteableBitmap.PixelBuffer`（同步、无 UI 线程亲和的字节），行与图标同帧出现；
> `SoftwareBitmapSource.SetBitmapAsync` 的逐行 await 闪变被这样消掉。
> UWP 条目直接用 `shell:AppsFolder\<AUMID>` 解析名提取（与 R12 的零 COM 启动约定同源）。
> 提取失败一律返回 `null`（列表以「已失效」标注原因），**不抛异常、不记日志**
> —— `LogLevel` 刻意只有三档，装帧的失败撑不起 Warn。

`SHGetFileInfo` 只能给 32×32 或 16×16，在高 DPI 的 64px 列表行里会糊。改用 `IShellItemImageFactory`：

```
SHCreateItemFromParsingName(path, null, IID_IShellItemImageFactory, out factory)
factory.GetImage(new SIZE(pixelSize, pixelSize), SIIGBF_ICONONLY, out hBitmap)
```

`pixelSize` 由当前 DPI 换算（32 × scale）。得到的 `HBITMAP` 转成 WinUI 的 `ImageSource` 时注意释放原始句柄。

---

## 四、数据模型

### 4.1 枚举

```csharp
public enum StartupSource { Registry, StartupFolder, ScheduledTask, Uwp, Manual }
public enum StartupScope  { None, Hkcu, Hklm, HklmWow, UserFolder, SystemFolder }
public enum RunItemState  { Waiting, Launching, Done, Failed }
public enum NotifyMode    { FailuresOnly = 0, Always = 1, Never = 2 }
```

### 4.2 `StartupEntry`（扫描结果，不入配置）

| 字段 | 类型 | 说明 |
|---|---|---|
| `Id` | `string` | 稳定主键 |
| `Name` | `string` | 显示名（注册表值名 / 文件名 / 任务名 / 应用显示名） |
| `Path` | `string` | 目标路径（`.lnk` 已解析为真实目标；UWP 为 AUMID） |
| `Arguments` | `string` | 参数 |
| `Source` / `Scope` | 枚举 | 来源与作用域 |
| `SourceKey` | `string` | 值名 / 文件名 / 任务完整路径 / TaskId |
| `SourceDetail` | `string` | 展示用的人类可读位置描述 |
| `IsEnabled` | `bool` | 当前是否启用（已考虑三级回退匹配） |
| `IsMissing` | `bool` | 目标文件不存在 |
| `IsProtected` | `bool` | `\Microsoft\*` / GPO 下发 → 只读 |
| `IsTakenOver` | `bool` | 已被本软件接管 |

### 4.3 `DelayedItem`（持久化到 `config.json`）

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `Id` | `string` | — | 稳定主键 |
| `Name` | `string` | — | |
| `Path` | `string` | — | |
| `Arguments` | `string` | `""` | |
| `DelaySeconds` | `int` | `30` | 相对登录时刻的绝对秒数 |
| `SortOrder` | `int` | 递增 | 同延时内的发起顺序 |
| `RunAsAdmin` | `bool` | `false` | |
| `Enabled` | `bool` | `true` | 条目级开关 |
| `Source` / `Scope` / `SourceKey` | | | 用于恢复与匹配；手动条目为 `Manual` / `None` / `""` |
| `SourceDetail` | `string` | `""` | 展示用 |
| `OriginalState` | `OriginalState` | | 接管前的状态 |

```csharp
public sealed class OriginalState
{
    /// <summary>接管前该项在系统中是否处于启用状态。</summary>
    public bool WasEnabled { get; set; }

    /// <summary>接管前的身份标记（如计划任务的 RunLevel），用于精确还原。</summary>
    public string? Extra { get; set; }
}
```

### 4.4 `AppConfig`

```jsonc
{
  "version": 2,
  "items": [ /* DelayedItem[] */ ],
  "settings": {
    "delayPresets": [0, 5, 10, 15, 20, 30, 60],   // D50：5 秒一档，覆盖登录后最拥挤的 30 秒区间
    "defaultPreset": 30,               // 编辑器预选值；必须落在 delayPresets 内（加载时兜底到首项）
    "notifyMode": "FailuresOnly",       // 字符串枚举：FailuresOnly / Always / Never
    "retryCount": 1,                    // 同一次运行内的重试次数（0–5）
    "theme": "FollowSystem",            // FollowSystem / Light / Dark
    "lastRunId": "20260919-084112"      // 管理端横幅用
  }
}
```

> ⚠️ **示例已按实现同步（2026-09-20）**：`maxDelaySeconds` / `trayKeepSeconds` / `showTrayIcon` /
> `preferDeElevatedLaunch` / `fallbackOnDeElevationFailure` 五项**已从模型删除**（用户批复：
> 延时上限不再可配、托盘设置只属调度端、降权语义固定为"一定降权、绝不回退"）。
> 枚举一律以**字符串**落盘（`ConfigService` 用 `JsonStringEnumConverter`），手写配置时不要写数字。

**迁移规则（v1 → v2）**：读取 demo 格式时补齐 `Scope`（从 `source_detail` 字符串推断一次并固化）、`Enabled=true`、`OriginalState`（保守取 `{ WasEnabled: false }`，因为能被接管的项通常已被禁用）。

### 4.5 `RunRecord`

```jsonc
{
  "runId": "20260919-084112",
  "startedAt": "2026-09-19T08:41:12+08:00",
  "finishedAt": "2026-09-19T08:42:15+08:00",
  "completedNormally": true,
  "plannedCount": 4,
  "items": [
    { "id": "registry:hkcu:weixin", "name": "微信", "delay": 30,
      "state": "done", "launchedAt": "2026-09-19T08:41:42+08:00",
      "reason": null, "attempts": 1 }
  ]
}
```

- `RunStateService` 每次状态变化重写 `state/current-run.json`（原子写）
- 结束时写 `runs/<runId>.json`，然后清理超出 30 份的旧文件
- `completedNormally=false` 用于识别"上次调度未正常完成"（E9）
- **连续失败次数不在这里存**，由 `FailureStreakService` 扫最近 N 份 `runs/` 现算

---

## 五、管理端（App）设计

### 5.1 目录结构

```
DelayStart.App/
├─ App.xaml / App.xaml.cs             # 应用入口、DI 容器、单实例
├─ MainWindow.xaml                    # NavigationView 外壳
├─ Views/
│  ├─ OverviewPage.xaml               # 总览（含「最近一次开机调度」表、上次运行横幅）
│  ├─ ItemsPage.xaml                  # 自启动项（按来源筛选）
│  ├─ DelayPage.xaml                  # 延时启动（**仅列表**，时间轴已按 D37=B 删除；D51 起按延时分组，D52 版式 = 子标题 + 每组一张边框卡片）
│  ├─ SystemPage.xaml                 # 系统启动项（只读）
│  ├─ LogPage.xaml                    # 运行日志
│  └─ SettingsPage.xaml               # 设置
├─ Dialogs/
│  ├─ DelayEditorDialog.xaml          # 延时配置编辑器（**子标题 + 卡片**的分节布局；目标程序块 = 只读卡片 / 「程序」+「UWP 应用」两颗分段页签，**手动添加与编辑共用**）
│  ├─ ConfirmRemoveDialog.xaml        # 移除确认（两种变体文案）
│  └─ SimulateDialog.xaml             # 模拟调度（0.5 倍速进度视图，D37=B 后不再有时间轴）
├─ ViewModels/
│  ├─ ShellViewModel.cs
│  ├─ OverviewViewModel.cs
│  ├─ ItemsViewModel.cs
│  ├─ DelayViewModel.cs
│  ├─ DelayGroup.cs                   # 延时分组（组标题 = 延时本身 + 行快照，D51）
│  ├─ SystemViewModel.cs
│  ├─ LogViewModel.cs
│  └─ SettingsViewModel.cs
├─ Controls/
│  ├─ StartupItemRow.xaml             # 列表行（图标 + 双行文本 + 操作）
│  ├─ DelayEditorSection.xaml         # 编辑器内的编号步骤块
│  └─ ResultBanner.xaml               # 上次运行结果横幅 / 常驻告警条
├─ Services/
│  ├─ ServiceRegistration.cs           # ★ 组合根：CLI 与 GUI 共用的唯一容器（D35）
│  ├─ NavigationService.cs
│  ├─ DialogService.cs
│  ├─ SimulationService.cs            # 模拟调度
│  ├─ NotificationService.cs          # 管理端内通知（InfoBar）
│  └─ ActivationRouter.cs             # 处理 --goto-log --run=xxx
└─ Converters/
   └─ BoolToVisibilityConverter.cs    # bool → Visibility（x:Bind 不做该隐式转换）
```

> **`Interop/`（实现期新增，5.1 原表未列）**：提权进程下必须绕行的原生互操作都收在这里 ——
> `UipiMessageFilter`（放行拖放消息，R10）、`FileDropReceiver`（经典 `WM_DROPFILES` 接收，
> 2026-09-19 批复 4）、`Win32FilePicker`（WinRT 选择器在提权进程里打不开）、
> `WindowSizing`（默认宽高与最小尺寸，D49）。三者都用「子类化窗口过程/换消息过滤器 +
> 按 HWND 记账 + 只转发不卸载」这一套写法，**可以叠加**（后装的把先前的过程当成自己的原过程）。

> **组合根为什么放在 `Services/ServiceRegistration.cs` 而不是 `App.xaml.cs`**：
> headless CLI（D32）与 GUI 必须装配**同一批实例** —— 两个 `FileLogger` 同时打开
> `manager.log` 会共享冲突，两个 `ConfigService` 会让"命令行看到的"与"界面看到的"不一致。
> 容器因此在 `Program.Main` 里构建一次，先分流 CLI、再交给 `App`。

### 5.2 MVVM 约定

- 用 **CommunityToolkit.Mvvm**（源生成器版的 `[ObservableProperty]` / `[RelayCommand]`），不手写 `INotifyPropertyChanged`
- 视图用 `x:Bind`，**不用** `{Binding}`（编译期检查 + 性能好）
- ViewModel 只依赖 `Core` / `Management` 的接口，不直接 new 具体的 Source
- 长任务（扫描、接管、导出）一律走 `async` 命令，UI 上有忙碌态，**不阻塞 UI 线程**

### 5.3 启动流程

```
Program.Main
 ├─ 构建 DI 容器（ServiceRegistration.AddDelayStartServices）
 ├─ CliHost.TryExecute(provider, args) ── 命中 headless 子命令 → 直接返回退出码
 └─ Application.Start
     └─ App.OnLaunched
         ├─ 解析命令行（--goto-log --run=<id> → 决定初始页）
         ├─ 单实例检查（已存在 → 激活它并退出）
         ├─ 从容器解析 MainWindow
         └─ 触发首次扫描（FR-1.2，异步，不等它完成就显示窗口）
```

> 🔴 **容器必须在 CLI 分流之前构造**：headless 路径同样依赖 `PathService.EnsureCreated()`
> 建出的目录树，而这条路根本不会进入 `App`。

参数示例：

```
DelayStart.exe --goto-log --run=20260919-084112
```

### 5.4 总览页数据流

| 数据源 | 用途 |
|---|---|
| `AppConfig.Items` | 延时条目数与驻留时长计算 |
| 最近一次扫描结果 | 各来源计数 |
| `state/current-run.json` + `Process.GetProcessById` 探测 pid | 判断"调度进行中"（D19） |
| 最新一份 `runs/*.json` + `FailureStreakService` | 「上次运行结果」横幅 / 常驻告警条（FR-6.7 / FR-6.8） |

---

## 六、调度端设计

结构与流程详见 `scheduler-design.md`，此处只列代码组织。

```
DelayStart.Scheduler/
├─ Program.cs                      # 入口：互斥体 → 配置加载 → 引擎 → 退出
├─ Runtime/
│  ├─ SchedulerEngine.cs           # 主循环：定时 + 发起 + 记录
│  ├─ RunSession.cs                # 单次运行的会话状态
│  └─ TrayHost.cs                  # 托盘图标 + 弹出面板 + 通知
├─ Ui/
│  ├─ ProgressPopup.cs             # 无边框弹出面板（★ 首个验证点）
│  └─ ProgressPopupFallback.cs     # ContextMenuStrip 降级实现
├─ Assets/
│  └─ tray.ico / tray-warn.ico     # 嵌入资源，禁止用 .resx
└─ SchedulerJsonContext.cs         # 源生成 JSON
```

**AOT 硬约束**（写错就发布失败或运行时崩）：

| 项 | 要求 |
|---|---|
| `OutputType` | `WinExe` —— 用 `Exe` 会在登录瞬间闪控制台黑框 |
| 图标 | `Assembly.GetManifestResourceStream`，**禁止 `.resx`** |
| JSON | `JsonSerializerContext` 源生成 |
| 控件绑定 | 手工赋值，**禁止 `DataSource`** 类反射绑定 |
| P/Invoke | `LibraryImport`；`bool` 返回值必须 `[return: MarshalAs(UnmanagedType.Bool)]` |
| 全局化 | 🔴 **不得设 `InvariantGlobalization`**，继承仓库级 `false`。设 `true` 会让 `TaskScheduler` 注册计划任务时抛 `CultureNotFoundException`（见 R13） |
| 禁用 API | `Reflection.Emit`、`BinaryFormatter`、`Marshal.GetTypedObjectForIUnknown`、COM RCW 动态包装 |

> **Phase 4 的第一个动作**必须先验证"无边框弹出面板在 AOT 下能否工作"（`Deactivate` 失焦即关）。不通过则切 `ProgressPopupFallback`，零风险。这是全项目唯一的未知项。

---

## 七、线程模型

| 场景 | 线程 |
|---|---|
| 扫描 | `Task.Run` 并发跑各 Source，结果回 UI 线程 |
| 接管/恢复 | 后台线程（有注册表与文件 IO），UI 显示忙碌态 |
| 调度端主循环 | 单线程 + `Stopwatch` 轮询（间隔 100 ms），无 `Task.Delay` 链 |
| 面板 UI | 调度端只有 UI 线程 + 主循环线程，用 `Control.BeginInvoke` 通信 |
| 计时 | 只用 `Stopwatch`，**禁止** `DateTime.Now` 参与时序计算 |

**禁止**：`async void`（事件处理器除外）、`.Result` / `.Wait()`、UI 线程上的 `Thread.Sleep`。

---

## 八、错误处理与日志

### 8.1 分层策略

| 层 | 策略 |
|---|---|
| `Core` 纯逻辑 | 参数非法 → `ArgumentException`；状态非法 → `InvalidOperationException` |
| 系统操作（注册表/任务/COM） | 捕获 `UnauthorizedAccessException` / `SecurityException` → 包装为 `StartupOperationException`（`StartupFailureReason.AccessDenied`，消息带**具体条目标识**）。管理端已全程提权（D20），此异常正常只来自 ACL / 组策略保护项，**不是**"需要提权" |
| `ScanService` | 每个 Source 单独 try/catch，失败项记为"读取失败"并进日志，**不向上抛** |
| 调度引擎 | 每条目单独 try/catch，失败记入 `RunItemResult`，不中断后续 |
| UI | 顶层 `UnhandledException` 处理器：写日志 + `InfoBar` 提示，不静默崩 |

**禁止空 `catch {}`**。至少要 `Log.Warn(ex, "...")`。

### 8.2 日志

| 文件 | 写入方 | 格式 |
|---|---|---|
| `scheduler.log` | 调度端 | `2026-09-19 08:41:12.345 [INF] [Scheduler] 正在启动 微信（延时 30s）` |
| `manager.log` | 管理端 | 同格式 |

- 单文件上限 2 MB，超出后轮转（`scheduler.log` → `scheduler.1.log`，保留 2 份）
- 启动时记录一次版本、构建时间、进程启动耗时（`Stopwatch`）——这是 NFR-1.2 的验收数据来源
- 不记录完整命令行以外的敏感信息；本项目无敏感数据

---

## 九、关键风险与验证点

按"不验证就不能往下写"的顺序排列。

| # | 风险 | 影响 | 验证方式 | 时机 |
|---|---|---|---|---|
| **R1** | WinForms 无边框窗口在 NativeAOT 下能否正常显示 + 失焦即关 | 决定调度端面板的实现方式 | Phase 4 第一件事：写最小 demo 发布 AOT 实测 | Phase 4 前 |
| **R2** | `Microsoft.Win32.TaskScheduler` 在 WinUI 3 下与 `System.Threading.Tasks.Task` 命名冲突 | 编译错误 | 用 `using TaskSchedulerTask = Microsoft.Win32.TaskScheduler.Task;` alias | Phase 2 |
| **R3** | WinUI 3 unpackaged 下 `app.manifest` 与 `Package.appxmanifest` 的选择 | 提权与 DPI 声明是否生效 | 建项目时确认；unpackaged 用 `app.manifest` | Phase 1 |
| **R4** | `IShellItemImageFactory` 取到的 `HBITMAP` 转 WinUI `ImageSource` 的生命周期 | 图标不显示或 GDI 句柄泄漏 | 小规模实测 + `DeleteObject` | Phase 3 |
| **R5** | Core 层引入 AOT 不兼容 API | 调度端发布失败 | `IsAotCompatible=true` 让编译器在构建期报错 | 持续 |
| **R6** | 计划任务创建/修改被 ACL 或组策略拒绝 | 接管链路中断 | 管理端已全程提权（D20），正常不会因权限失败。仍需捕获 `UnauthorizedAccessException` 并指出**具体条目** | Phase 2 |
| **R7** | 高 DPI 下列表行与卡片错位（原写"时间轴与列表行"，时间轴已按 D37=B 删除） | 界面不可用 | 100/150/200% 三档实测 | Phase 3 / 5 |
| **R8** | `DelayStart.slnx` 与 WinUI 3 项目兼容性 | 无法打开解决方案 | ✅ **已确认（2026-09-19）**：VS 18 可正常打开 `DelayStart.slnx`（含 5 个项目与 XAML 设计器），**无需回退 `.sln`** | **Phase 0 ✅** |
| **R9** | 🔴 **`WindowsAppSDKSelfContained=true` 时 `requireAdministrator` 可能被忽略** | **管理端不弹 UAC 也不提权 → D20 的整个权限模型失效** | ✅ **已通过（2026-09-19 实测）**：unpackaged + 自包含 + manifest 提权，Release 下**在 VS 之外双击 exe，UAC 正常弹出** → manifest **未被忽略**。`WindowsAppSDKSelfContained` 保持 `true`，三条降级路径**均不需启用**（留档备用，见下方）。⚠️ 残留一项：`WindowsPrincipal.IsInRole(Administrator)` 的**代码级**确认待 Phase 1 有代码后补测 | **Phase 0 ✅** |
| **R10** | 🟡 提权窗口接收不到资源管理器的拖放（UIPI） | 「手动添加」的拖放区失效 | 主路径 `[浏览…]` 按钮（`FileOpenPicker` + `InitializeWithWindow`）必须 100% 可用，**不依赖拖放**；拖放作为增强：`ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES/MSGFLT_ALLOW)` + `DragAcceptFiles` + 子类化窗口处理 `WM_DROPFILES`。**不通则拖放区降级为纯按钮，不接受"等待修复"** | Phase 3 |
| **R11** | 🔴 **WinForms + NativeAOT 官方未支持**：SDK 主动拦截（`NETSDK1175`），放行只能靠**内部属性** `_SuppressWinFormsTrimError` | ~~调度端可能发布出"能编译但运行时崩溃"的 exe；D1 的整个技术路线受威胁~~ → **已由 D24 = B 从根源消解** | ✅ **已解决（2026-09-19 D24 批复 B）**：调度端弃用 WinForms，改**纯 Win32 + AOT**，灰色地带不复存在。Phase 0 复验——去掉逃逸属性后**仍 0 警告 0 错误**。**R11 的约束条款继续生效且更强**（禁 `.resx` 反射式资源加载 / `DataGridView` / `RichTextBox` / 动态 COM / `System.Reflection`，IL2xxx 逐个消掉）。剩下"AOT 产物真机能否运行"由 **R1 在 Phase 4** 覆盖托盘 / 面板 / 失焦即关三条路径 | **Phase 0（已结）→ Phase 4（R1 覆盖运行侧）** |
| **R12** | 🔴 **NativeAOT 无 built-in COM**：UWP 项的 COM 激活（`IApplicationActivationManager`）在 demo 里走 `Marshal.GetObjectForIUnknown` + `[ComImport]`，AOT 下**运行时必抛 `PlatformNotSupportedException`**；同时 UWP 激活**拿不到 PID**，机制 7 的"1.5 秒后复查"对它无从执行 | FR-5.9「UWP 项的延时启动」整条链路失效 —— 而 D4 已批复 UWP 延时**要做** | ✅ **已由 D28 = A 从根源消解（2026-09-19）**：UWP 激活改用 `explorer.exe shell:AppsFolder\<AUMID>` —— 纯 `Process.Start` 拉起 explorer，**完全不碰 COM**，AOT 下可用。派生两条硬约定：① `LaunchResultEvaluator.Evaluate` 收到 **`null` 快照（无 PID）时判成功**，不套用退出码规则；② UWP 是**唯一**"延时启动 + 绕过系统启动管理"的来源，UI 文案必须写明（D4） | **Phase 1（Phase 1 已消解，Phase 4 只需实测一次）** |
| **R13** | 🔴 **`InvariantGlobalization=true` 让计划任务注册必然崩溃**（D34 真机实测）：该开关的语义**不是**"不要多语言"，而是**不存在任何 culture** —— 任何 `CultureInfo` 构造都抛 `CultureNotFoundException`。`Microsoft.Win32.TaskScheduler.Trigger` 的静态构造器会 `CreateSpecificCulture("en")` 来格式化任务 XML 的日期 | FR-11 计划任务注册 **100% 不可用**，并**连带 FR-3.1 全部接管动作失败**（第 4 步失败 → 回滚），Phase 2 的核心交付物在真机上完全不能用。⚠️ `--scan` 的枚举路径不受影响（`DescribeTrigger` 不触发该静态构造器），所以只坏"写"不坏"读" | ✅ **已修复（2026-09-19）**：`Directory.Build.props` 改为 `false`。体积代价实测 ≈ 0（Windows 上 .NET 用系统 `C:\Windows\System32\icu.dll`，**不随产物分发**，产物 140.8 MB 不变）→ 原"省体积"论据在 Windows 上本就不成立。⚠️ 该开关**构建期写入 `runtimeconfig.json`**，运行时环境变量翻不回来（`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT` 无效）。误判根源：Phase 0 把它当成"本项目无多语言需求"的性能选项，**从未作为决策走审批** | **Phase 2（已结）** |

#### R9 实测进度（Phase 0，2026-09-19）

**已完成的部分（静态验证）**：

| 检查项 | 结果 |
|---|---|
| 骨架按上述第 1 条生成 | ✅ `src/DelayStart.App`（unpackaged + `WindowsAppSDKSelfContained=true`），TFM `net10.0-windows10.0.26100.0` |
| `app.manifest` 写入 `requireAdministrator` | ✅ 已写（另含 `PerMonitorV2` DPI + `longPathAware`） |
| **`requireAdministrator` 是否真的嵌进了 exe** | ✅ **已嵌入** —— 在 Release 产物 `DelayStart.exe` 字节流中检出 `requireAdministrator` / `PerMonitorV2` / `longPathAware` 三个标记 |
| Release 构建是否通过 | ✅ 0 警告 0 错误 |

**运行侧验证（2026-09-19，由用户在 VS 之外实测）**：

> ✅ **R9 通过**：双击
> `src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\DelayStart.exe`
> → **UAC 提权对话框正常弹出**。
>
> 结论：**"manifest 已嵌进 exe" → "Windows 采纳了 manifest" 这条链路成立**，
> `WindowsAppSDKSelfContained=true` **保持不动**，下面三条降级路径**全部不需要**。
> 已回写 `DelayStart.App.csproj` 的注释。
>
> ⚠️ 残留一项：`WindowsPrincipal.IsInRole(Administrator)` 的**代码级**确认要等 Phase 1 有代码后补测
> （弹了 UAC 已足以判定 R9 通过，代码级确认是加固）。
>
> **置信度说明**：R9 的风险来自社区在 **WinAppSDK 1.0–1.2** 时期的报告，本机
> **WinAppSDK 1.8 + .NET 10** 未复现。用 `bin` 目录里的一个**空壳** exe 无法百分百覆盖
> "将来加了 WinAppSDK 初始化代码后的行为"，因此升版 Windows App SDK 后建议回归一次。

**三条降级路径（当前不需要，留档备用，按推荐顺序）**：

| 路径 | 做法 | 代价 |
|---|---|---|
| **① 去掉自包含**（社区验证最有效） | `WindowsAppSDKSelfContained=false`，改为框架依赖 | 用户机器需预装 Windows App Runtime。**本机已装**（`C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime*` 已确认存在）；分发时由 **D22 的 Inno Setup 安装器**代为部署运行时，用户无感 |
| **② 运行时自提权** | manifest 保持 `asInvoker`，在 `App.OnLaunched` 最早期检测权限，不足则 `Process.Start(new ProcessStartInfo { FileName = exePath, Verb = "runas", UseShellExecute = true })` 重启自己，然后退出当前实例 | 多一次进程启动；**必须传参防止无限重启循环**（如加 `--elevated-retry` 标记位，失败则直接退出） |
| **③ 外部启动器** | 新增极小 AOT 控制台/窗口 exe 作为 `DelayStart.Launcher.exe`，manifest 标 `requireAdministrator`，只负责以提升权限拉起 `DelayStart.App.exe` | 多一个 exe 要维护；快捷方式指向 launcher |

> **R9 不通过就不进入 Phase 1。** 这是唯一会推翻已批复决策（D20）的风险，必须在写第一行业务代码前解决。

---

#### R11 详解：WinForms + NativeAOT 官方不支持，靠内部属性放行

**现象（Phase 0 实测复现）**：`DelayStart.Scheduler` 加上 `PublishAot=true` 后，构建**直接失败**：

```
error NETSDK1175: 启用剪裁时，不支持或不推荐使用 Windows 窗体。
[src\DelayStart.Scheduler\DelayStart.Scheduler.csproj]
```

**来源（读 SDK 源码确认，不是猜的）**：

```
C:\Program Files\dotnet\sdk\10.0.401\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.RuntimeIdentifierInference.targets:305
<NetSdkError Condition="('$(UseWindowsForms)' == 'true') and ('$(PublishTrimmed)' == 'true')
                     and ('$(_SuppressWinFormsTrimError)' != 'true')"
             ResourceName="TrimmingWindowsFormsIsNotSupported" />
```

而 `PublishTrimmed` 是被 `PublishAot` **隐式**打开的：

```
...\Microsoft.NET.Publish.targets
<PublishTrimmed Condition="'$(PublishTrimmed)' == '' And '$(PublishAot)' == 'true'">true</PublishTrimmed>
```

即：`UseWindowsForms=true` + `PublishAot=true` ⇒ `PublishTrimmed=true` ⇒ SDK 主动拦下。

**官方立场**：微软 *Known trimming incompatibilities* 文档把 **WinForms 与 WPF 明确列为 trimming 不支持**，理由是 Windows 上的 Native AOT **没有 built-in COM**，而 WinForms 对 built-in COM marshalling 依赖很重（.NET 10 发布说明同样写着 NativeAOT 对 WinForms/WPF 不可用）。

**现状判定**：`_SuppressWinFormsTrimError` 是 **SDK 自己提供的逃逸口**，但**以下划线开头 = 内部属性，不在公开 API 文档中**。社区实践（.NET 10，中等复杂度 WinForms 项目）显示：加它之后**能编译、能产出可独立运行的 exe**，但**不是官方推荐路径，不保证所有代码路径安全**。已知高危路径：

| 高危路径 | 本项目是否踩 |
|---|---|
| `.resx` + `ComponentResourceManager` 反射式资源加载 | 🔴 **会踩**——托盘图标若用设计器 `.resx` 必然出问题。已在记忆与规范里定死：改 `Assembly.GetManifestResourceStream` |
| `DataGridView` 反射式列类型解析 | 🟢 不用 |
| `RichTextBox`（RichEdit COM 包装） | 🟢 不用 |
| 动态 COM 互操作（`Marshal.GetTypedObjectForIUnknown`、动态 IID） | 🟢 调度端不碰 |
| 系统原生 Shell 右键菜单 | 🟢 不用 |
| 大量 `System.Reflection` | 🟢 不碰 |

~~本项目的调度端是"托盘图标 + 一个小面板"的极小 WinForms 程序，恰好落在社区验证过的可行区间内~~ —— **该判断已作废**。上表前六条是**静态风险面**，而 R11 真正的问题是：即便这几条都躲开，WinForms + AOT 仍是**官方不支持的组合**，走它就得靠内部属性逃逸。

**已决策 → D24 = B：纯 Win32 + AOT（2026-09-19 用户批复）**

调度端 UI 归零重写：不用 WinForms、不用 WinUI 3，直接 `Shell_NotifyIcon` 托盘 + 自绘无边框弹窗，全部 P/Invoke。三条理由（完整版见 `design-spec.md` 6.6）：

1. **A 走不通** —— 官方不支持，靠 `_SuppressWinFormsTrimError`（内部属性）逃逸，等于把关键路径押在微软随时能改名的开关上。**Phase 0 实测：去掉该属性后构建依然 0 警告 0 错误**，说明它本就不必要。
2. **C 解决不了问题还引入新问题** —— **WinUI 3 没有任何托盘 API**（微软有意省略），托盘那层照样得写 `Shell_NotifyIcon`；而它带来 **0.5–0.7s 冷启动，直接违反 NFR-1.2 的 0.3s**，还要背上 WinAppSDK 自包含体积。
3. **B 的量本来就小** —— 托盘图标 + 一行 tooltip + 一个只读列表，几百行 Win32 代码。为省这几百行去背一整个框架，是净负债。

> **对 R11 的最终定论**：R11 列出的**约束继续有效，而且更强** —— 禁 `.resx` 反射式资源加载、禁 `DataGridView` / `RichTextBox` / 动态 COM 互操作 / `System.Reflection`、IL2xxx 必须逐个消掉。纯 Win32 路径下这些**本来就不会出现**，等于主动把风险面砍掉而不是绕开。

> **R11 不阻止 Phase 1–3**（那三个阶段只做 Core / Management / 管理端，不碰调度端）。
> **Phase 4 已解锁**，开工第一件事是写最小 demo 发布 AOT，在真机登录场景实测托盘 / 面板 / 失焦即关三条路径。

---

## 十、实施顺序

| Phase | 内容 | 出口条件 |
|---|---|---|
| **0** | 环境与骨架：先按 `build-and-test.md` 1.2 装齐工具链前置（VS 组件 + WinUI 模板包）；`dotnet new` 生成 4 个项目 + 测试项目（2.1）、`Directory.Build.props`、CPM、`.editorconfig`；**并验证 R9**（自包含 + manifest 提权） | `dotnet build` 全绿；R2 / R3 / R8 已确认；**R9 有明确结论**（决定 7.1 的自包含取值） |
| **1** | `Core` 层：模型、`ConfigService`、`DelayCalculator`、`ItemKeyBuilder`、`CommandLineService`、`LaunchResultEvaluator` + 单元测试。**D27=A：`ProcessLauncher` / `TokenHelper` / `Core/Interop/*` 不在本期** | ✅ 单元测试全绿（128 个） |
| **2** | `Management` 层：4 个 Source 的扫描 + 软禁用 + 恢复、`TakeoverService`、`TaskRegistrationService` | ✅ **已达成（D34）**：三个 `Run` 键经全量快照对比**值数量与内容零变化**（`requirements.md` 9.3 第 1 条），接管 / 移出往返闭环。详见 10.3 |
| **3** | 管理端骨架：导航 + 自启动项页 + 延时启动页，跑通"扫描 → 接管 → 移除"单链路；验证 R10（提权下的文件选择/拖放） | 端到端手工验证通过；「手动添加」的 `[浏览…]` 路径可用 |
| **4** | 调度端：先验 R1（+ R11 的运行侧），再做引擎 + 托盘 + 通知 + 状态文件。**D24 已批复 = B（纯 Win32），UI 归零重写** | 重启实测延时准确 |
| **5** | 总览 / 日志 / 设置 / 系统启动项页 | 全部页面文案对齐 `design-spec.md` |
| **6** | 模拟调度、**Inno Setup 安装器与卸载还原链路**（D22）、真机全面验收 | `requirements.md` 第九节全部勾选（含 9.4 分发验收） |

**Phase 2 与 Phase 4 之间不允许跳过**——调度端依赖 Core 的时序与启动逻辑，而那部分的正确性只能靠 Phase 1 的单元测试保证。

### 10.1 Phase 0 执行记录（2026-09-19）

| 项 | 结论 |
|---|---|
| 骨架 | ✅ `DelayStart.slnx` + 5 个项目（`Core` / `Management` / `App` / `Scheduler` / `Core.Tests`）全部生成并入解决方案 |
| 构建 | ✅ `dotnet build DelayStart.slnx -c Release` → **0 警告 0 错误** |
| 测试 | ✅ **2 个冒烟用例全过**（`dotnet run --project tests/DelayStart.Core.Tests -c Release`，退出码 0）。⚠️ **`dotnet test` 在本项目不可用，已登记 D26**，原因与规范命令见 `build-and-test.md` 4.2 |
| 模板残留清理 | ✅ 已删：MSIX 打包残留（`Package.appxmanifest` + 8 个徽标 PNG + 3 个 `.pubxml` + 嵌套 `.gitignore`）、调度端 WinForms 残留（`Form1.*`）、空模板类（`Class1.cs`×2 / `UnitTest1.cs`）、模板页（`Pages/` 全部 6 个文件，**D25 = A**）。`Assets/AppIcon.ico` 保留 |
| R2（TaskScheduler 命名冲突） | ⏳ 未验证 —— `Management` 尚未引入该包，Phase 2 处理 |
| R3（manifest + PerMonitorV2 生效） | ✅ **通过（人工实测 2026-09-19）**：100% / 150% / 200% 三档 DPI 下界面不糊、不越界 |
| R8（`.slnx` 兼容性） | ✅ **通过（人工实测 2026-09-19）**：VS 18 可正常打开 `DelayStart.slnx`，**无需回退 `.sln`** |
| R9（自包含 + 提权 manifest） | ✅ **通过（人工实测 2026-09-19）**：在 VS 之外双击 Release 产物，**UAC 提权弹窗正常弹出** → `WindowsAppSDKSelfContained=true` 保持不动，三条降级路径均不需启用。⚠️ 残留：`IsInRole(Administrator)` 代码级确认待 Phase 1 补 |
| R11（WinForms + AOT） | ✅ **已由 D24 = B 从根源消解**：调度端改纯 Win32 + AOT，不再引用任何 UI 框架；**移除逃逸属性后构建仍 0 警告 0 错误**。运行侧由 R1 在 Phase 4 覆盖 |
| 9.0 最后一项（VS 组件是否硬依赖） | ✅ **结论：体验项，不是硬依赖**。依据：`XamlCompiler.exe` 与全部 `Microsoft.UI.Xaml.Markup.Compiler.*.targets` 均由 NuGet 包 `Microsoft.WindowsAppSDK.WinUI/1.8.260224000` 的 `buildTransitive/` 提供（本机缓存实测），命令行构建不经过 VS 组件。**唯一残留的混淆因子是该组件当前已安装、无法做隔离对照** |
| **Phase 0 出口条件** | ✅ **全部达成（2026-09-19）**：构建 0 警告 0 错误 + R3 / R8 / R9 三项人工验证通过 + D24 / D25 落地。R2 按计划延后到 Phase 2。**可以进入 Phase 1** |

### 10.2 Phase 1 执行记录（2026-09-19）

**范围**：`architecture.md` 二（Core 层设计）+ `coding-standards.md` 14.1。**不含** P/Invoke（D27=A 推到 Phase 4）。

| 项 | 结论 |
|---|---|
| 交付物 | ✅ `src/DelayStart.Core/` **38 个源文件**：`Models/` 15（含 4 个纯 enum 与 2 个 `record struct`）、`Abstractions/` 7、`Services/` 10、`Launch/` 2、`Serialization/` 3、`Logging/` 1 |
| 新增（原 2.1 表未列） | ✅ `PathService`、`AtomicFileWriter`、`SystemClock`、`LogLevel`、`LogSinkExtensions`、`ProcessSnapshot`、`ParsedCommandLine`、`NotifyMode`、`StartupFailureReason`、`StartupOperationException`、`LaunchEvaluation`、`LegacyConfigV1`、`LegacyItemV1` —— 已回写 2.1 / 2.2（**D29 = F2**） |
| 构建（Core） | ✅ `IsAotCompatible=true` + `TreatWarningsAsErrors=true` 下 **0 警告 0 错误** → **证明 Core 无 IL2026 / IL3050 违规，AOT 边界成立**（R5 守门生效） |
| 构建（全解决方案） | ✅ `dotnet build DelayStart.slnx -c Release` → **0 警告 0 错误**（5 个项目，含 WinUI 3 自包含的管理端） |
| 测试 | ✅ **128 个用例全绿**，`Total: 128, Errors: 0, Failed: 0, Skipped: 0`，退出码 0，**耗时 0.238s**。命令同 D26：`dotnet run --project tests/DelayStart.Core.Tests -c Release` |
| 测试结构 | ✅ 10 个文件 = 7 个测试类（`DelayCalculator` 15 / `ItemKeyBuilder` 15 / `CommandLineService` ~22 / `LaunchResultEvaluator` 10 / `StartupSortComparer` 9 / `PathService` 14 / `ConfigService` 24）+ 3 个假件（`FakeLogSink` / `FakeClock` / `TempDirectory`） |
| 冒烟测试清理 | ✅ 删除 `ScaffoldSmokeTests.cs`（Phase 0 遗留，`build-and-test.md` 9.0 注明"业务用例到位后再删"） |
| **新增 R12（D28 派生）** | 🔴 **NativeAOT 无 built-in COM** → demo 的 `IApplicationActivationManager` + `Marshal.GetObjectForIUnknown` 激活 UWP **运行时必抛 `PlatformNotSupportedException`**。**已由 D28 = A 消解**（改 `explorer.exe shell:AppsFolder\<AUMID>`）。Phase 4 真机实测 `shell:AppsFolder` 在**提权进程**中是否仍可激活（UIPI） |
| 新增约定（写进 2.1 注） | ✅ ① `ILogSink` 只有 `Write(LogLevel, …)`，`Info/Warn/Error` 走扩展方法（规避 **CA1716**，且新增级别不动接口）；② `StartupOperationException` 构造**强制带 `EntryId`** —— 把"失败必须定位到具体条目"（R6）从约定升级为编译期约束 |
| 规范冲突处置 | ✅ `CA1707`（成员名禁下划线）与 `coding-standards.md` 14.2 强制的测试命名 `被测方法_场景_期望结果` 直接冲突 → 在 `tests/DelayStart.Core.Tests/.editorconfig` **就近关闭 CA1707**，生产代码的检查强度不受影响。另修 `.editorconfig` 的 `insert_final_newline=false`（与 14.2 所在的第五节"每文件恰好一个末尾换行"矛盾） |
| 文档修订（D29） | ✅ **F1** `requirements.md` 追踪矩阵实现层 6 行路径纠正（`Core/` → `Management/`）；**F2** 2.1 / 2.2 补 `PathService` 等；**F3** 删 Core 的 `Interop/Shell32.cs`（误植）；**F4** 登记 R12 |
| **Phase 1 出口条件** | ✅ **达成**：单元测试全绿（128/128）+ AOT 守门 0 警告。**可以进入 Phase 2** |

### 10.3 Phase 2 执行记录（2026-09-19）

**范围**：`architecture.md` 三（Management 层设计）+ 机制 2 / 3 / 4。按 **D30** 排除图标、服务查询、系统启动项检查三块。

| 项 | 结论 |
|---|---|
| 交付物（Management） | ✅ `src/DelayStart.Management/` **20 个源文件**：`Abstractions/` 3（`IStartupSource` / `ISchedulerTaskRegistrar` / `IShellLinkResolver`）、`Sources/` 4（注册表 / 启动文件夹 / 计划任务 / UWP）、`Services/` 5（`StartupApprovedStore` / `ShellLinkResolver` / `TaskRegistrationService` / `ScanService` / `TakeoverService`）、`Interop/` 3（`ShellInterfaces` / `ComFactory` / `Shlwapi`）、`Models/` 5 |
| 交付物（Core 新增） | ✅ `FailureStreakService` + 4 个配套模型（**D31**），Core 由 38 → **43 个源文件**。纯函数，`IsAotCompatible` 守门仍 **0 警告** |
| 交付物（App 新增） | ✅ `Program.cs` + `Cli/`×3（**D32**）。`DISABLE_XAML_GENERATED_MAIN` 接管入口，`--restore-all` / `--reinstall-task` / `--takeover` / `--release` / `--scan` 五条命令 |
| 构建（全解决方案） | ✅ `dotnet build DelayStart.slnx -c Release` → **0 警告 0 错误**（5 个项目） |
| 测试 | ✅ **205 个用例全绿**，`Total: 205, Errors: 0, Failed: 0, Skipped: 0`，退出码 0，**耗时 0.260s**（Phase 1 的 128 + 本期新增 77） |
| 测试结构（本期新增） | ✅ 7 个文件 = 4 个测试类（`FailureStreakService` / `StartupApprovedStore` / `ScanService` / `TakeoverService`）+ 3 个新替身（`FakeStartupSource` / `FakeSchedulerTaskRegistrar` / `InMemoryConfigStore`） |
| **R2 验证** | ✅ **已消解**：`Microsoft.Win32.TaskScheduler.Task` 与 `System.Threading.Tasks.Task` 的冲突用 `using TaskSchedulerTask = …` 解决，构建 0 警告。**并且文档里的包名本身是错的**，见下条 |
| 🔴 **包名勘误** | 本文与 `api-analysis.md` 1.4 早先写的包 id `Microsoft.Win32.TaskScheduler` 是**另一个同名旧包**（最新 2.2.0.3，2016 年，只带 `.NETFramework4.0` 资产，**对 .NET 10 不可用**）。真正在维护的是 **`TaskScheduler` 2.12.2**。已按后者落地并回写 `api-analysis.md` 1.4 |
| 新增本机坑 | ⚠️ `[LibraryImport]` 需要 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`，否则 **SYSLIB1062 + CS0227**（`Management` / `App` 两处，是 `build-and-test.md` 坑 9 的完整形态）。⚠️ `[ComImport] class` **不能**显式转接口（**CS0030**），改走 `CoCreateInstance` + `Marshal.GetObjectForIUnknown`（`Interop/ComFactory.cs`）。⚠️ `lambda` 参数命名为 `_` 会让内部的 `_ = x` 变成给参数赋值（**CS0029**）。⚠️ `new App();` 裸调用触发 **CA1806** |
| ⏳ **已知偏差 1** | UWP 显示名解析只做了 **FR-1.8 的第 1 / 2 / 4 步**（`SplashScreen\...\AppName` → `SHLoadIndirectString` → 回退包名前缀），**第 3 步（MrtCache 反查 `PackageFullName`）未实现**。理由：需解码 `resources.pri` 或改 TFM 引入 WinRT 的 `PackageManager`，代价明显高于收益，而第 4 步已给出可读兜底 |
| ⏳ **已知偏差 2** | `ScheduledTaskSource` **整体跳过 `\Microsoft\*` 下的任务**，而不是"展示为只读"。理由：一台干净 Win11 上就有上百个系统任务，全部列出会把用户真正关心的十几项淹掉，且它们按 D20 提权也改不动。这是比 FR-1.9 更强的保证（"不可操作" → "不展示"），但确实是解释上的偏离 |
| **Phase 2 出口条件** | ✅ **已达成（2026-09-19 · D34 真机执行）**：`--scan` 37 项零误判 → `--reinstall-task` 注册成功 → `--takeover` ×4 → `--restore-all`（成功 4 / 失败 0）→ 前后全量快照 diff 中三个 `Run` 键**逐行 IDENTICAL**、其余 30+ 条目未出现于 diff → 回归扫描四项回到「启用」。详见 `build-and-test.md` 9.1 执行状态块 |
| 🔴 **D34 缺陷 1（已修复）** | Phase 0 自行引入 `<InvariantGlobalization>true</InvariantGlobalization>`（**无对应决策**，理由写的是"省体积"）→ `TaskScheduler.Trigger..cctor` 里的 `CreateSpecificCulture("en")` 抛 `CultureNotFoundException`，**计划任务注册 100% 崩溃，并连带接管第 4 步全部回滚**。**单测与构建都发现不了**：单测注入的是 `FakeSchedulerTaskRegistrar`，真实的 `TaskRegistrationService` 从未被执行，构建也是 0 警告。**已改为 `false`**；体积代价实测 ≈ 0（Windows 用系统 `icu.dll`，产物 140.8 MB 不变）。已登记为 **R13** |
| 🔴 **D34 缺陷 2（已修复）** | `Release` / `Rollback` 无条件调 `Enable`（删标记），而 `OriginalState.WasEnabled` **只有写入点、零读取点** —— 实现漏了 `DelayedItem.OriginalState` 注释里明写的"移除接管时据此精确还原（FR-2.7）"。后果：**接管前已被用户禁用的项，移出后会被变成启用**（违反 9.3 第 4 条）。**已修复**：恢复动作按 `WasEnabled` 分支；该字段默认值由 `false` 改为 `true`（安全侧：取"原本会自启动"，否则老配置条目释放后会永久不启动且无从解释）。新增 3 个单测钉住 |

### 10.4 Phase 3 执行记录（2026-09-19，出口达成）

**范围**：`architecture.md` 五（管理端设计）。按 **D35**（容器选型）与 **D36**（本期范围）执行；**D37 = B**：时间轴从延时页与总览页**整体删除**，`TimelineView` 不再存在，D7 的模拟调度改为进度列表。

| 项 | 结论 |
|---|---|
| **D35（DI 容器）** | ✅ **A：`Microsoft.Extensions.DependencyInjection` 10.0.12**。组合根 `Services/ServiceRegistration.cs`，**CLI 与 GUI 共用一个容器** —— 两个 `FileLogger` 打开同一个 `manager.log` 会共享冲突，两个 `ConfigService` 会让两处看到的配置不一致。`CliComposition` 退化为解析薄壳 |
| **D36（本期范围）** | ✅ **A：导出 / 导入、预设延时值编辑都不做**，留 Phase 5 与设置页一起做 |
| 已落地（3a 外壳） | ✅ `ServiceRegistration` / `NavigationService`（**public**，否则 `MainWindow` 构造函数报 CS0051）/ `MainWindow`（NavigationView + TitleBar + Frame）/ `ShellViewModel` / `App` 与 `Program` 改为收 `IServiceProvider`。构建 **0 警告 0 错误**，`--scan` 回归退出码 0 |
| 已落地（3b 自启动项页） | ✅ `ItemsPage` + `ItemsViewModel`（异步扫描、来源级失败提示、空状态）+ `StartupEntryRow` + `Converters/BoolToVisibilityConverter` |
| 已落地（3c 延时编辑器） | ✅ `Dialogs/DelayEditorDialog`（①目标程序 ②延时预设+自定义 ③身份 ④接管对象 + 底部活摘要 + 上限校验），**三种形态齐备**：「加入系统项」/「编辑已有条目」（手动项可改名称与路径，系统项锁定目标）/「手动添加」（`[浏览…]` 文件选择器经 `WindowHandleProvider` 句柄挂载） |
| 已落地（3d 延时启动页） | ✅ `DelayPage` + `DelayViewModel`（失效判定、两种空状态）+ 行内「移出延时」（两种变体文案，取自 `ui-mockup.html` 的 `unlink()`） |
| 已落地（3e 编辑与调序） | ✅ Management 新增 `ConfigEditService`（`ApplyEdit` / `SetEnabled` / `Move` / `AddManual`，全部原子保存）与 `DelayItemValues`（哪些字段生效由条目来源决定：系统项只改延时 / 身份 / 参数）。`Move` 在同延时组内先重编号再交换（历史配置全 0 时交换才会真的动）；`AddManual` 计划任务注册失败时**回滚**新条目。行内 ↑↓（FR-4.7，仅同延时组内有效）、条目级开关（FR-4.6，回灌二次触发用"开关值 == 配置值"判据挡掉） |
| 已落地（3f 图标，D30） | ✅ Management `IconProvider` + `Interop/ShellItemImageFactory.cs`（`SHCreateItemFromParsingName` 一个入口吃路径 / `.lnk` / `shell:AppsFolder\<AUMID>` 三种解析名；`GetDIBits` 负高度读 32bpp 保 alpha；HBITMAP 与 COM 指针 finally 逐层释放）。App 侧 `IconRenderer` 同步灌 `WriteableBitmap`，两页行模板 32 DIP 图标位 + `FontIcon` 占位。成功才缓存，失败返 null 不重试打扰 |
| 已落地（3g 搜索与筛选） | ✅ `ItemsViewModel` 增加 `SearchText`（名称 / 命令行 / 位置子串匹配）+ `SourceFilterIndex`（全部 / 注册表 / 启动文件夹 / 计划任务 / UWP），`Rows` 全量与 `FilteredRows` 视图分离，空状态区分"系统里没有"与"被筛光了" |
| ⚠️ 本机坑（新增，3e–3f 批次） | ⑤ `WriteableBitmap.PixelBuffer` 的 `CopyTo` 走 CsWinRT 的 `WindowsRuntimeBufferExtensions`（`System.Runtime.InteropServices.WindowsRuntime` 命名空间在 .NET Core 由 WinRT.Runtime 提供）；⑥ `x:Bind` 的 `TextBox.Text` 双向绑定需 `UpdateSourceTrigger=PropertyChanged` 才能逐键过滤；⑦ 取反布尔不能在 `x:Bind` 里写表达式，行对象补 `HasNoIcon` 这类镜像属性 |
| 构建与测试 | ✅ 全解决方案 **0 警告 0 错误**；**208 个用例全绿**（3e–3f 批次后复核） |
| ⚠️ 本机坑（新增） | ① XML 注释里出现 `--`（如 `--exact-match`）是**非法 XML**，会让整个 `Directory.Packages.props` 静默失效 → 报 **NU1015**（全部包"未指定版本"）。② 被 XAML / 容器解析的类型必须 **public**（CS0051）。③ `ContentDialog.ShowAsync` 返回 `IAsyncOperation<T>`，**没有 `ConfigureAwait`**（CS1929），直接 await 即可。④ `x:Bind` 不做 `bool → Visibility` 隐式转换，需 `IValueConverter` |
| ✅ **R7（DPI）** | **2026-09-19 用户实测通过**：100% / 150% / 200% 三档下界面不糊、列表行与卡片不错位 |
| ✅ **出口条件（白名单四项）** | **2026-09-19 用户实测通过**：真机走通「扫描 → 接管 → 移出」闭环，白名单四项行为符合 9.3。**Phase 3 出口达成** |
| ⏳ **仍待人工** | **R10 复测**（修复已落地，见 10.5）与任务管理器 / MSCONFIG 的显示核对 —— 可留 Phase 6 |

### 10.5 R10 修复 + Phase 4 调度端执行记录（2026-09-19）

**R10 修复（用户实测"拖进去鼠标变禁止"后落地）**：根因是 **UIPI** —— 中等完整性的 Explorer 向提权（`requireAdministrator`）窗口投递拖放消息被系统静默拦截。修复：新增 `App/Interop/UipiMessageFilter.cs`，在 `MainWindow` 构造期对主窗口句柄调 `ChangeWindowMessageFilterEx` 放行三条消息（`WM_DROPFILES 0x233` / `WM_COPYDATA 0x4A` / `WM_COPYGLOBALDATA 0x49`）；编辑器目标选择区恢复设计稿"拖入文件或点击选择"文案，实现 `DragOver` / `Drop`（接受 `.exe/.lnk/.bat/.cmd/.ps1`，落 `ApplyPickedFile` 同一入口；白名单自 D47 起由 `Core.Services.LaunchTargetTypes` 统一提供）。

**Phase 4（调度端，纯 Win32 + NativeAOT）**：

| 项 | 结论 |
|---|---|
| Core 启动器（D27=A） | ✅ `Core/Interop/TokenHelper.cs`（`WTSGetActiveConsoleSessionToken` → `DuplicateTokenEx` 取交互用户主令牌 + `CreateEnvironmentBlock`）+ `Interop/ProcessLauncher.cs`（降权走 `CreateProcessAsUserW`；`.lnk` 与令牌不可得时回退直接启动；回退语义由 `Settings.PreferDeElevatedLaunch` / `FallbackOnDeElevationFailure` 驱动）。`Core.csproj` 补 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`（`[LibraryImport]` + 固定缓冲需要） |
| 调度端骨架 | ✅ `Scheduler/NativeMethods.cs`（消息循环 / 窗口类 / GDI 文本 / `SetTimer`；`GetMessageW` 返回 -1 视为致命，0 退出，>0 派发）/ `TrayIconHost.cs`（`Shell_NotifyIconW`，隐藏消息窗承载回调）/ `PanelWindow.cs`（失焦即关：`WM_ACTIVATEAPP` / `WM_KILLFOCUS`；`WM_DPICHANGED` 重排）/ `IconResources.cs`（嵌入的两枚 ICO 运行期载入） |
| 调度引擎 | ✅ `SchedulerEngine.cs`：按 `StartupSortComparer` 排序 → `SetTimer` 逐条绝对时刻点火 → `IRunStateStore.WriteCurrent` 逐条落盘（管理端总览可见进度）→ 失败按 D17（保持接管 + 下次登录原延时重试，无自动处置）→ `FailureStreakService` 连击计数随归档写入。托盘 tooltip 与面板实时显示"第 x/n 项 · 剩余 y 秒" |
| 入口 | ✅ `Program.cs`：手动构造（无 DI 容器，AOT 保持零反射）；`OutputType=WinExe`（无控制台闪烁） |
| 图标资源 | ✅ `Assets/Scheduler.ico` + `SchedulerWarning.ico`（脚本生成，嵌入资源） |
| 🔴 AOT 链接坑 | `dotnet publish` 报 **LNK1181 找不到 `advapi32.lib`**：本机 VS 2026 的 vcvarsall 未自动带出 Windows SDK 的 `um/ucrt` 库路径。修复：`DelayStart.Scheduler.csproj` 显式 `<AdditionalNativeLibraryDirectories>` 注入 `C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\{um,ucrt}\x64`（硬编码本机唯一 SDK 版本，换机需改） |
| ✅ 产物 | **AOT 单文件 3.4 MB**（目标 ≤ 6 MB，NFR 达标），`0 警告 0 错误` |

### 10.6 Phase 5 执行记录（2026-09-19）

| 项 | 结论 |
|---|---|
| 服务 / 驱动查询 | ✅ Management `ServiceQueryService`（`OpenSCManager` + `EnumServicesStatusExW`，**零 COM**，AOT 友好）+ `Models/ServiceInfo.cs`；启动类型文案与"延迟自动启动"标注来自注册表 `Start` / `DelayedAutoStart` |
| Winlogon / 登录脚本 | ✅ Management `SystemStartupInspector`（**全静态只读**；Shell/Userinit/VMApplet 三处劫持高发点 + 组策略登录脚本 HKLM/HKCU 两侧；权限不足按"无条目"降级）+ `Models/ReadOnlyEntry.cs`（顶层 record —— 曾是嵌套类型，XAML `x:DataType` 引用嵌套类型麻烦，提出） |
| 四个页面 | ✅ `OverviewPage`（统计卡 + FR-6.7 横幅矩阵 + **D37 模拟调度进度列表**：10 倍速 `DispatcherTimer`，行状态 等待中→已启动）/ `SystemPage`（四个 Expander 分区只读展示）/ `RunsPage`（FR-8 按次分组，失败项标红 + 失败原因）/ `SettingsPage`（FR-9 全项：预设解析升序去重、上限、重试 0–5、通知策略、托盘、降权两开关；**校验失败整体不落盘**） |
| 🔴 本机坑（Phase 5） | ⑧ **`Page` 不是 `ObservableObject`** —— 页面自身属性（如 `SimulateButtonText`）不能 `Mode=OneWay`（WMC1506），这类"页面派生文案"一律放 ViewModel；⑨ **x:Bind 表达式里写不了字符串字面量三元式**（`{x:Bind Flag ? '是' : '否'}`）—— pass-1 `XamlCompiler.exe` **直接崩溃退出码 1 且不落任何错误详情**（output.json 停在旧时间戳），换计算属性解决；⑩ 行模型是不可变普通类时行级绑定用 **OneTime**（默认省略 Mode），写 `Mode=OneWay` 会批量触发 WMC1506，在 `TreatWarningsAsErrors` 下即编译失败 |
| 构建与测试 | ✅ 全解决方案 **0 警告 0 错误**；**208 用例全绿** |

### 10.7 Phase 6 执行记录（2026-09-19，代码完成）

| 项 | 结论 |
|---|---|
| Inno Setup | ✅ `installer/DelayStart.iss` + `installer/README.md`：固定路径 `{localappdata}\Programs\DelayStart`（`PrivilegesRequired=lowest` + `DisableDirPage`）、`InitializeUninstall` 同步跑 `--restore-all` + `ewWaitUntilTerminated` + 非 0 **中止卸载**（🈲 `[UninstallRun]` 读不到退出码）、默认保留配置与日志（无 `[UninstallDelete]`，完成页提示目录）。本机未装 Inno Setup，**编译与安装/卸载链路留用户验收** |
| 管理端导航收口 | ✅ 六模块全接入（总览 / 自启动项 / 延时启动 / 系统启动项 / 运行日志 / 设置），`NavigationService` 六标签齐全，`MainWindow` 菜单不再有"分阶段增长"注释前置条件 |
| ⏳ 待人工验收 | ① R10 拖放复测；② Inno 安装 → 升级 → 卸载链路（`--restore-all` 拦截路径）；③ 模拟调度与运行日志页真机核对；④ MSCONFIG / 任务管理器显示核对 |

### 10.8 D40–D47 执行记录（2026-09-20）

**D40–D45：调度端亲自降权 + UWP 启动链**（提交 `5d418ae`）

| 项 | 结论 |
|---|---|
| 🔴 降权机制（D40） | ✅ 新增 `Scheduler/DeElevatedProcessLauncher.cs`：`GetShellWindow` → `OpenProcessToken(TOKEN_DUPLICATE)` → **`DuplicateTokenEx` 转主令牌** → `CreateProcessWithTokenW`（`lpDesktop` 留 NULL）。`NativeMethods` 补必要的 P/Invoke；`AgentProcessLauncher.cs` 与整个 `DelayStart.Agent` 工程删除（slnx / `.iss` / `publish.ps1` 同步摘除，D38/D39 的双进程方案作废）。真机七方案对照：直接交 explorer **进程令牌**必 `Win32Error=5`；`CreateProcessAsUserW`=1314；COM `Shell.Application.ShellExecute` **不降权**（子进程仍 0x3000 High） |
| 🔴 UWP 解析名（D41） | ✅ `Core/Services/UwpParsingName.cs`（裸 AUMID → `shell:AppsFolder\…`，大小写不敏感、幂等）+ `Management/Services/UwpAppIdResolver.cs`（`Repository\Packages` → `PackageRootFolder` → `AppxManifest.xml` 建 `StartupTask.TaskId → Application.Id` 映射）。**根因是注册表子键名不是 AppId**：修前 `…!PantherBarTask` 解析不了，外壳回退打开"文档"目录 |
| 跨页刷新（D42） | ✅ `App/Services/ScanCacheService` 增来源级过期标记（`Invalidate` / `IsStale` / `ClearStale`）：接管 / 移出 / 禁用后置脏，来源页载入命中即重扫该来源。删掉 D38/D39 遗留的 `\DelayStart` 任务文件夹清理代码 |
| 总览结果列（D43） | ✅ `OverviewPage.xaml` 结果列拆两份互斥 `TextBlock` —— 真因是**画刷**：`Foreground` 是可继承属性，转换器把 `false` 绑成 `null` 会切断继承链，成功项因此不可见 |
| 🔴 UWP 身份（D44 → D45） | ✅ 最终定性：**UWP 进程恒为普通用户身份**（打包应用进程的令牌由系统 / 激活服务决定，中转外壳用谁的令牌都不改结果）。故调度端 UWP 一律降权委托、`RunAsAdmin` 只记说明不判失败；编辑器对 UWP 不给「管理员」胶囊。⚠️ D43 ②/D44 的机制假设均已被推翻，历史记录保留在 `requirements.md` |

**D46–D47：手动添加 UWP + 目标类型白名单**

| 项 | 结论 |
|---|---|
| UWP 应用目录（D46-2） | ✅ `App/Services/UwpAppCatalog.cs`：`PackageManager.FindPackagesForUser("")` → `GetAppListEntriesAsync()`，只列**有应用清单条目**的包（≈ 开始菜单里能点开的那些）。显示名三级回退（条目 → 包 → 包族名），判据复用 `UwpNameResolver.LooksUnresolved`；单个包失败只跳过它。🔴 `AppListEntry` 在投影里**不作为可命名类型暴露**（写类型名报 CS0234），只能 `var` 迭代 |
| 选择面板（D46-1） | ✅ `DelayEditorDialog` 新增同层覆盖层 `UwpPickerOverlay`（含筛选框 / 图标列表 / 14 行提示）。**不能叠第二个 ContentDialog**，故与自定义延时确认面板同一套路。选中后存**解析名**（`UwpParsingName.Build`）而非裸 AUMID —— 存裸 AUMID 会被调度端当普通 exe 判"目标不存在" |
| 图标（D46-3） | ✅ 复用 `IconProvider` + `shell:AppsFolder\…` 解析名：`Task.Run` 后台批量提 `IconPixels`，回 UI 线程建 `WriteableBitmap`（与列表页同一套 `IconRenderer`） |
| 参数框（D46-4） | ✅ **保留但置为禁用态**（`IsEnabled=false`）并附一行说明 —— 外壳委托不转发参数 |
| 🔴 白名单收敛（D47） | ✅ 新增 `Core/Services/LaunchTargetTypes.cs` 作为**唯一事实来源**：`.exe / .lnk / .bat / .cmd / .ps1`（**移除 `.msi`** —— 非 PE 映像，`CreateProcess` 报 `193 ERROR_BAD_EXE_FORMAT`）。`CommandLineService.ExecutableExtensions` 同步补 `.ps1` |
| 🔴 `.ps1` 宿主（D47） | ✅ 新增 `Core/Services/PowerShellHost.cs`：`pwsh.exe` 优先（`%ProgramFiles%\PowerShell\7` → `7-preview` → `PATH` → `%LOCALAPPDATA%\Microsoft\WindowsApps` 执行别名），回落 `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`；命令行固定 `-NoProfile -ExecutionPolicy Bypass -File "<脚本>" <参数>`。调度端**降权与管理员两条路都走宿主** —— `ShellExecute` 对 `.ps1` 没有"打开"动词（默认"编辑"），管理员条目改用 `UseShellExecute=false` 继承提权令牌 |
| 构建与测试 | ✅ 全解决方案 **0 警告 0 错误**；**256 个用例全绿**（+44：`LaunchTargetTypesTests` / `PowerShellHostTests`）；Scheduler AOT 单文件 **3.6 MB** 发布通过并同步进 App bin |

### 10.9 D48–D51 执行记录（2026-09-20，真机反馈后）

**D48：编辑器目标区（用户 bug 1 + UWP 工作目录）**

| 项 | 结论 |
|---|---|
| 🔴 「更换」分流 | ✅ `DelayEditorDialog` 的「更换」与"拖入文件，或点击选择程序"合并为 `OnChangeTarget`：目标已是 `shell:AppsFolder\…` 解析名 → 打开 **UWP 应用列表**；否则 → 打开文件选择器。真因是解析名不是文件路径，**文件选择器永远选不到 UWP 应用**，用户视角就是"UWP 条目换不了"。判据复用 `_identityLockedToNormal`（与 D45 的身份锁定同源，在 `RefreshTargetKind` 里与目标形态同步） |
| 双向切换 | ✅ 「或选择一个 UWP 应用」这张卡片**双向工作**：目标不是 UWP → 指向 UWP 列表；目标已是 UWP → 文案改指文件选择器（`或改为选择一个普通程序`）。**没有这条回程就会出现"换了 UWP 后再也换不回普通程序"**（此时「更换」只回 UWP 列表）。两者都由 `ApplyTargetCapabilities` 统一刷新 |
| 工作目录（UWP） | ✅ **UWP 没有"工作目录"概念**：打包应用由系统激活器拉起，当前目录由激活机制决定、调用方无从指定，应用也不允许依赖它（数据走 `ApplicationData`、资源走 `ms-appx`）。故工作目录框与参数框一样**置灰 + 一行说明**（`ApplyUwpState` 并入 `ApplyTargetCapabilities`，可重复调用以支持目标在 UWP / 普通程序之间来回换） |

**D49：管理端默认宽高**

| 项 | 结论 |
|---|---|
| 默认尺寸 | ✅ `App/Interop/WindowSizing.ApplyInitialSize`：**1400×900 DIP**（`AppWindow.ResizeClient` + `Move`），按 `GetDpiForWindow` 换算成像素、最多占工作区 **92%**、相对 `DisplayArea.GetFromWindowId(…, Nearest)` 居中。🔴 `AppWindow` 的单位是**物理像素**，本机 3840×2160 / 150% 下不换算会得到一个"只有半屏大"的窗口 |
| 最小尺寸 | ✅ `WindowSizing.EnforceMinimum` 接原生 `WM_GETMINMAXINFO`（**960×600 DIP**，每次按当前显示器 DPI 重算）。原生 WinUI 没有最小尺寸 API（PowerToys 用的是 WinUIEx 的 `WindowEx`），本项目不引该包，改为与 `FileDropReceiver` 同款的子类化；两条链各自记账、依次转发，可共存。⚠️ `MINMAXINFO` 用显式布局**只声明 `ptMinTrackSize`** 一项：全写出来会有 4 个"只由系统填"的字段触发 **CS0649**，而 `TreatWarningsAsErrors` 下那是编译失败 |
| 参考对象 | PowerToys `Settings.UI/SettingsXAML/MainWindow.xaml`（`MinWidth/MinHeight=480`）+ `Helpers/WindowHelper.cs`（`settings-placement.json` 记住位置/大小）。**"记住上次尺寸"本轮不做**（会新增一个落盘文件，见 D49 的 C 选项） |

**D50 / D51：设置页与延时启动页**

| 项 | 结论 |
|---|---|
| 预设延时默认值（D50） | ✅ `Settings.DelayPresets` 默认改 **`0 / 5 / 10 / 15 / 20 / 30 / 60`**，`ConfigService.NormalizePresets` 的两处空值兜底同步；编辑器空列表兜底改为直接读 `new Settings().DelayPresets`（不再另抄一份清单）。已落盘的列表**不做迁移**，原样保留 |
| 设置页分区顺序（D50） | ✅ `SettingsPage.xaml` 顺序改为 **外观 → 延时 → 调度**（「外观」作为第一个子标题） |
| 延时页分组（D51） | ✅ 新增 `App/ViewModels/DelayGroup.cs`（延时值 + 行快照 + 组标题/条目数），`DelayViewModel.Groups` 由**已排序**的 `Rows` 顺序切片得出（同一延时的行必然相邻；用 `GroupBy` 反而会丢掉"组按延时升序"这个既有保证）。`DelayPage.xaml` 改为 `ItemsControl` 套 `ItemsControl`：表头只保留一份、组内行模板与列宽不变（列宽仍逐列相同） |
| 构建与测试 | ✅ 全解决方案 **0 警告 0 错误**；**256 个用例全绿** |

### 10.10 D52–D54 执行记录（2026-09-20，分组版式与编辑器 tab）

| 项 | 结论 |
|---|---|
| 分组边框按组（D52） | ✅ `DelayPage.xaml` 去掉最外层那张"包住所有组"的大卡片，改为**每组一个 `Border`**：组模板 = `StackPanel`（子标题纯文本 + 条目数 → 组卡片）。画刷 / 圆角沿用卡片默认值（不新增样式）。表头左内边距 28 = 组卡片 `Padding` 8 + 行 `Padding` 20，保证标题与行内容逐列对齐 |
| 编辑器 ① 块改 tab（D53） | ✅ `DelayEditorDialog` 用 `SelectorBar`（`程序` / `UWP 应用`）替代原来的"单块 + 双入口卡片"；code-behind 以 `_uwpTabActive` 为单一状态源，`TargetPath` / `ItemName` / `Arguments` / `WorkingDirectory` 全部按当前页签取值 —— **两个页签各存各的目标**（`_filePath` / `_uwpPath`），D48 那套"互相覆盖同一个字段"的结构消失。拖放落文件时 `SelectTab(false)` 自动切回「程序」页签。身份不再是"锁死"而是"按页签归一"：`_wantAdmin` 单独记一份，UWP 页签下 `RunAsAdmin` 恒为 false，切回「程序」页签时用户之前的选择还在（`PaintIdentity` 只重画，`SyncIdentityPills` 仅在管理员胶囊该出现/该消失时重建） |
| 删掉活摘要（D53） | ✅ 删 `SummaryText` / `UpdateSummary` 及其 5 处调用（`ApplyPickedFile` / `ApplyPickedUwpTarget` / `OnDelayPillChecked` / `OnCustomDelayChanged` / `SetDelay`） |
| UWP 页签字段（D53） | ✅ 页签内**不摆**命令行与工作目录 —— 原先的"禁用态 + 两行说明"整体删除（`ArgumentsHintText` / `WorkingDirHintText` / `ApplyTargetCapabilities` 一并删掉），页签底部换成一行说明「UWP 用系统外壳激活：始终普通身份，参数不转发、也没有工作目录」 |
| UWP 只读条目（D54） | ✅ `UseReadOnlyTarget` 按 `_systemIsUwp` 决定：UWP 来源时名称 / 命令行 / 工作目录**全部 `Collapsed`**（非 UWP 系统条目的工作目录仍可编辑，bug#5 不变）。只读卡片的路径行对 UWP 显示 `UWP 应用 · shell:AppsFolder\<AUMID>`，但**提交用的 `_readOnlyPath` 仍取原始值**（显示文案与数据分离） |
| 构建与测试 | ✅ 全解决方案 **0 警告 0 错误**；**256 个用例全绿**；🔴 `SelectorBar` / `SelectorBarItem`（WinAppSDK 1.5+，本项目 1.8）可用，但 **XAML 解析期 `SelectionChanged` 会早于其余字段就绪触发**（第一项 `IsSelected="True"`），必须用 `_initialized` 挡住，否则 NRE |

### 10.11 D55–D59 执行记录（2026-09-20，弹窗宽度 / 分节卡片 / 删 ④ / 字段可见性修正）

| 项 | 结论 |
|---|---|
| 弹窗宽度回归框架默认（D55） | ✅ 删掉 `ContentDialog.Resources` 里的 `ContentDialogMaxWidth = 700` 覆盖，宽度交给框架。🔴 框架值实测自 WinUI 包内 `lib\net6.0-windows10.0.17763.0\Microsoft.WinUI\Themes\generic.xaml`：`ContentDialogMinWidth=320` / **`ContentDialogMaxWidth=548`** / `ContentDialogMaxHeight=756` / `ContentDialogTitleMaxHeight=56` / `ContentDialogPadding=24`（旧注释写的"540 限制"是错的）。同文件也确认模板里已有 `ContentScrollViewer`。内容区滚动上限 520 → **560**：宽度收窄后内容更高，560 + 外框（标题 ~27 + 内边距 48 + 按钮区 ~56 ≈ 143）= 703，仍在 756 以内 |
| 分节卡片 + 去序号（D56） | ✅ 三个功能块统一为「纯文本子标题（`EditorSectionHeaderStyle`：14px SemiBold，与设置页 `SectionHeaderStyle` 同视觉）→ 一张边框卡片」；`①②③` 序号删除。卡片内的子区域（拖放区 / 已选目标 / UWP 入口）改用 `SubtleFillColorSecondaryBrush` 浅底 + 无描边，避免框套框 |
| 页签换成分段按钮（D56） | ✅ `SelectorBar` → 两颗等宽 `ToggleButton`（`Background="Transparent"` + `BorderThickness="0"`，选中态由 Fluent 默认视觉给出 accent 实心：`generic.xaml` 的 `ToggleButtonBackgroundChecked = AccentFillColorDefaultBrush`）。🔴 必须处理 `ToggleButton` 的"再点一下取消选中"：新增 `OnProgramTabUnchecked` / `OnUwpTabUnchecked` → `RestoreTabSelection()` → `ApplyTabState()` 重画两颗，否则会出现"两颗都没选中、面板却停在那页"的矛盾状态；`ApplyTabState` 内改写 `IsChecked` 仍由 `_suppressSync` 挡重入 |
| 删除原 ④ 模块（D57） | ✅ 删除 `ImpactHeaderText` / `InfoTypeText` / `InfoWriteText` / `InfoImpactText` 四个控件与三处构造赋值（加入系统项 / 编辑手动项 / 手动添加） |
| 工作目录不再自动填（D58） | ✅ `ApplyPickedFile` 收掉两个恒真参数（`fillName` / `fillWorkingDirectory`）与"回填程序所在目录"那段，只保留"显示已选文件 + 按需回填显示名称"，三处调用点同步 |
| 🔴 字段可见性缺陷修复（D59） | ✅ **上一轮 D53 引入的缺陷**：`NameBox` / `ArgumentsBox` / `WorkingDirBox` 被放进「程序」页签面板（`ProgramTabPanel`）之后，只读形态（系统条目）会整体折叠该面板 —— `UseReadOnlyTarget` 里对这三项设的 `Visibility` **全部无效**，"接管系统条目时能填参数与工作目录"（bug#5）静默失效且不报任何错。修法：`ArgumentsBox` / `WorkingDirBox` 移到**页签之外**（目标卡片的直接子项），新增 `ApplyTargetFieldVisibility()` 按 `_manualForm` + `UwpMode` 统一算三项可见性（名称仅"手动 + 「程序」页签"；参数与工作目录**除 UWP 外一律显示**），`ApplyTabState` 每次切页签都调用。🔴 教训：**可见性判据不能写在"会被父级整体折叠的子树"里** —— 这类失效不报错，只能靠逐形态过一遍字段发现 |
| 文案单一来源 | ✅ 拖放区"支持 .exe / …"改由 `LaunchTargetTypes.DisplayList` 赋值（`InitializeCommon` 里写一次），不再硬编码在 XAML —— 白名单与界面文案一起漂移的隐患消除 |
| 构建与测试 | ✅ 全解决方案 **0 警告 0 错误**；**256 个用例全绿**（本轮只动 App 层 XAML 与 code-behind，测试用例数不变） |

### 10.12 D60 执行记录（2026-09-20，安装包矩阵：2 形态 × 2 架构）

| 项 | 结论 |
|---|---|
| 架构核查（先查再定，不凭印象） | ✅ ① NativeAOT 在 .NET 9+ **官方支持 win-x86**（.NET 8 时代才只有 x64/Arm64）—— 与最初判断相反，所以 x86 被砍的理由**不是**"不支持"；② Windows App SDK 1.8 包内 `runtimes-framework/` 下 **x64 / x86 / arm64 / arm64ec 运行时齐全**；③ 注册表扫描**已全部显式 `RegistryView.Registry64`**（含 `Software\WOW6432Node\...\Run` 显式子键）→ x86 进程不会漏掉 64 位启动项；④ 调度端结构体全走 `Marshal.SizeOf<T>()` → 天然架构无关 |
| 🔴 x86 的唯一硬伤 | `ServiceQueryService` 的 `const int entrySize = 8 + 8 + 9 * 4 + 4`（= 56）是 **x64 布局硬编码**，x86 下应为 `4 + 4 + 36` = 44。步长错 12 字节 → 读到垃圾指针 → 在 `PtrToStringUni` 上 AV，而 AV 属**损坏状态异常，外层 catch 拦不住**。用户批复砍掉 x86 → **不改行为，只补 🔴 警示注释**，写明日后支持时改用 `Marshal.SizeOf<结构体>()` |
| arm64 的构建位置 | ✅ 本机 `vswhere -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64` **返回空**（VS 2026 Community 18.10.1 只装了 x86.x64）→ **本地只出 x64，arm64 交 CI**。workflow 里加了同款检测作为前置步骤，避免在链接阶段抛看不懂的 LNK 错 |
| csproj / props 参数化 | ✅ `Scheduler.csproj` 的 `RuntimeIdentifier` 加 `Condition="'$(RuntimeIdentifier)' == ''"`（可命令行覆盖）；`AdditionalNativeLibraryDirectories` 的 `\x64` → `$(_WinSdkLibArch)`，按 `win-arm64` 切到 `\arm64`，并加 `Exists` 条件（SDK 目录缺失时跳过而非报路径错）；`App.csproj` 同步钩子的 `win-x64` → `$(RuntimeIdentifier)`；`Directory.Build.props` 增 `<Version>0.1.0</Version>` 作**版本号单一来源** |
| iss 参数化 | ✅ 全部走 `/D`：`AppVersion` / `Rid` / `Slim`（**判据是"有没有定义"**，用 `#ifdef` 而不是值比较）/ `PublishDir` / `SchedulerDir` / `TargetArch` / `WinAppRuntimeUrl`。`OutputBaseFilename=DelayStart-Setup-<版本>-<Rid>[-slim].exe`；`ArchitecturesAllowed` 由 `TargetArch` 给（x64 包 `x64compatible`、arm64 包 `arm64`） |
| 构建脚本 | ✅ 新增 `installer\build-installer.ps1`（单包：publish 管理端 → publish 调度端 → ISCC）与 `installer\build-all.ps1`（矩阵，任一组合失败即以非零码退出供 CI 判定）；`publish.ps1` 的 `win-x64` 硬编码改 `-Rid` 参数。⚠️ 脚本内**刻意不做递归删除**来清理输出目录 —— 目录按 `artifacts\publish\<rid>\<形态>` 分开本就不会互相污染，而递归删除会触发本机安全策略对批量删除的拦截 |
| 精简版运行时探测点 | 🔴 **实测纠正**：slim 的 `DelayStart.runtimeconfig.json` 里 framework **只有 `Microsoft.NETCore.App 10.0.0`**，没有 `Microsoft.WindowsDesktop.App` → 精简版要的是 **.NET Runtime 而非 Desktop Runtime**（WinUI 3 这一层不要求后者）。iss 因此探测 `...\sharedfx\Microsoft.NETCore.App`（x64 / arm64 / x86 三条路径都查），可同时覆盖"只装 Runtime"与"装了 Desktop Runtime"的机器。`Microsoft.WindowsAppRuntime.Bootstrap.dll` / `.Bootstrap.Net.dll` 随包发布 → unpackaged 框架依赖的启动初始化有落点 |
| 🔴 Inno 两条硬约束（实测踩坑） | ① **任何一行不得以 `[` 开头**（含 `[Code]` 段内、含缩进后的 `[`）：ISCC 在需要 section 头的位置看到行首 `[` 就报 `Invalid section tag`，哪怕那行是合法的 Pascal 数组字面量 —— 首次 slim 编译即败于此（第 184 行的 `['打开下载页', ...]`）。② `TaskDialogMsgBox` 的第 5 个参数 `Shields` 是集合类型 `TMsgBoxShields`，传整数 `0` 报 `Type mismatch`（6.7.3 实测），而自定义按钮标签又必须写数组字面量（撞上第 ① 条）→ 运行时提示改用 `MsgBox` + `MB_YESNOCANCEL`。另：`Copy(Names[i], 1, 27)` 拿 27 个字符去和 31 字符的串比较 → **恒为假**，改用 `Pos(...) > 0`（否则装了运行时也会误报缺失） |
| 实测产物（win-x64） | ✅ 自包含：publish 217.0 MB / 536 文件 → 安装包 **60.4 MB**（ISCC 25.6 s）；精简版：publish 41.7 MB / 72 文件 → 安装包 **9.8 MB**（ISCC 4.7 s）。`DelayStart.Scheduler.exe` 不在 App publish 目录里是**正常的** —— 它由 iss 第二条 `[Files]` 从 AOT publish 目录单独取 |
| CI | ✅ `.github\workflows\release.yml`：tag `v*` + `workflow_dispatch` 双触发；矩阵 rid × 形态 = 4 包；`fail-fast: false`；ARM64 工具集前置检测；标签优先作版本号；Release 用 `gh release create --generate-notes --verify-tag` |
| 构建与测试 | ✅ 全解决方案 **0 警告 0 错误**；**256 个用例全绿**。⚠️ 首次构建失败于 `MSB3021 / MSB3027`：正在运行的 `DelayStart.exe`（PID 55180 —— 真机验收留下的实例）锁住了 `bin\...\win-x64\DelayStart.Management.dll`，关闭后立即通过 → **构建前先关掉正在运行的管理端** |
| 待真机验收 | ⏳ 两个 x64 安装包的安装 / 升级 / 卸载（含精简版缺运行时的提示与 `--restore-all` 卡口）、精简版在**未装 Windows App Runtime** 的机器上能否给出可读错误。arm64 包无本地验收，首次发布前需在 arm64 设备上过一遍 9.8 清单 |

### 10.13 D61 执行记录（2026-09-21，首次真机安装的四类缺陷 + 应用名统一）

用户在真机上装了 `DelayStart-Setup-0.1.0-win-x64.exe` 与 `...-slim.exe`，回报三个现象。逐条查到根因（全部**先取证再改**，没有一条是猜的）：

| 现象 | 根因（证据） | 修法 |
|---|---|---|
| 🔴 装完从开始菜单运行：**UAC 之后进程秒退，窗口不出现** | **事件日志实锤**：Application Error 1000 —— `出错模块 Microsoft.UI.Xaml.dll 3.1.8.0`、`异常代码 0xc000027b`、`Faulting 应用程序路径 ...\Programs\DelayStart\DelayStart.exe`；WER 1001 组合里 `combase.dll` + `0x80004005`。`Compare-Object` 比对 publish 与 install 目录**只差 3 个 Inno 文件**（即 iss 没漏拷），再比对 **bin 与 publish：publish 少 11 个 App 自己的文件** —— `App.xbf` / `MainWindow.xbf` / `Views\*.xbf`（6 个）/ `Dialogs\DelayEditorDialog.xbf` / `DelayStart.pri` / `Assets\AppIcon.ico` | `DelayStart.App.csproj` 增 `CopyWinUIResourcesToPublishDir`（`AfterTargets="Publish"`，通配补齐 XBF + `<AssemblyName>.pri` + `Assets\**`）；`build-installer.ps1` 加**构建期守门**：断言这 4 类文件存在且 `*.xbf` ≥ 2，缺一个就 `throw`。🔴 探针诊断过一次 **MSB4095**（`Message` 的 `Text` 里写了 `@{...}` 而非 `@(...)` 做项变换）→ 改成静态文案 |
| 🔴 `.publish` 为什么一直没被发现 | 历次真机验收（含 R9 的提权弹窗验证）**都是在 `bin\Release\...\win-x64\` 里双击做的**，而 bin 目录**有**这些文件（build 阶段产出）→ 缺陷只在"走 publish 的安装包"这条路径上存在，本地开发路径永远复现不到。**结论：安装器形态的验收必须装在真实安装目录里跑，不能拿 bin 代替** | 已写进 `build-and-test.md` 7.1 |
| 🔴 装完勾选「启动」点确定：报 **740 请求的操作需要提升** | `DelayStart.exe` 的 `app.manifest` 是 `requireAdministrator`；安装器 `PrivilegesRequired=lowest` → `[Run]` 默认的 `CreateProcess` 路径必然 `ERROR_ELEVATION_REQUIRED`（740） | `[Run]` 加 `Flags: ... shellexec`，由外壳按 manifest 弹 UAC |
| 🔴 装完**没有桌面快捷方式**，且图标是空白的 | `[Icons]` 只建了开始菜单项（D60 的疏漏）。**顺手查出更根本的一层**：`App.csproj` **从未设 `<ApplicationIcon>`** —— 只有 `<Content Include="Assets\AppIcon.ico" />`（那是给 `AppWindow.SetIcon("Assets/AppIcon.ico")` 设**窗口**图标用的）→ exe 自带 .NET 默认图标，开始菜单 / 桌面快捷方式 / 任务栏 / Alt-Tab /「应用和功能」卸载图标（`UninstallDisplayIcon` 指向本 exe）全是通用空白图标 | 补 `Name: "{autodesktop}\{#AppName}"`（`lowest` 模式下 = 当前用户桌面）；补 `<ApplicationIcon>Assets\AppIcon.ico</ApplicationIcon>`。🔴 **`<Content>` 管"文件随程序发布"，`<ApplicationIcon>` 管"图标写进 exe 资源" —— 两者都要有** |
| 🔴 卸载路径同因失效（**用户尚未遇到，排查时发现**） | `InitializeUninstall` 用 `Exec(...)` 拉起 `--restore-all` —— 同样 740 → **还原动作根本不会发生**，而 `Exec` 返回 False 只会走到"警告后照常卸载"分支 → 直接违反 D22「非 0 中止卸载」 | 改 `ShellExec('runas', ...)` 提权拉起。代价：**ShellExec 拿不到退出码** → 约定 `--result-file <路径>` 由程序回写退出码（`CliHost.WriteResultFile`），卸载器 `Sleep(250)` × 240 轮询回读。⚠️ 刻意不用 `ewWaitUntilTerminated`：提权启动能否拿到进程句柄不确定，"文件出现了没有"才是确定性判据。三种失败（起不来 / 超时 / 退出码非 0）一律按"还原未完成"处理 |
| 🔴 精简版双击：**已完成 .NET 10.0.12 与 WindowsAppRuntime 1.8 却双双报"缺失"** | 注册表实探：`.NET` 的安装记录在 **32 位视图** —— `HKLM\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App`（`HKLM64` 下只有一条 `x64\sharedhost`，因为 .NET 安装器是 32 位进程）；`Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x64__...` 在 **HKCU** 的包仓库里，`HKLM` 下同名键只有 3 条系统内置项。原实现**两项都只查 `HKLM64`** → 必然双误报 | 每项改为**多判据任一命中**：.NET 查 ① `{pf64}`/`{pf32}` 下 `dotnet\shared\Microsoft.NETCore.App\10.*` 目录（`FindFirst` 通配，主判据）② 32 位视图三条架构子键 ③ 64 位视图同三条；Windows App Runtime 查 `HKCU`/`HKLM`/`HKLM64` 三处并**按架构匹配** `_x64__` / `_arm64__`（新增 `/DWinAppRuntimeArch`，由 `-Rid` 推导） |
| 怎么保证检测不再误报 | 检测结果原先只在**要人点确定**的 `MsgBox` 里，无法自动化验证 | 加环境变量自检出口 `DELAYSTART_RUNTIME_CHECK=<文件>`：写结果文件后 `Result := False` 直接退出、**不安装**。实测本机 `dotnet=1 winappruntime=1 pf64=C:\Program Files` → **两项误报已修复**（改前两项都是 0） |
| 应用名（D61-①） | 用户指令：应用名只用英文 `DelayStart`（改判 D6 的双名方案） | `MainWindow.xaml` 的 `Window.Title` + `TitleBar.Title`、iss 的 `AppName`/`AppVerName`/`[Icons]`/`[Run]`、`CliHost` 帮助抬头、`TaskRegistrationService` 的任务描述 —— 中文名退回文档 |
| 收尾验证 | — | ✅ 全解决方案 **0 警告 0 错误**、**256 用例全绿**；两个 x64 包重新产出：自包含 **60.7 MB / 547 文件**（= 原 536 + 被补回的 11），精简版 **9.8 MB**，压缩日志确认 `App.xbf` / `MainWindow.xbf` / `DelayStart.pri` / `Assets\AppIcon.ico` / `Views\*.xbf` 均在包内 |

> 🔴 **本节最该记住的一条**：`bin` 能跑 ≠ `publish` 能跑。WinUI 3 的 unpackaged 工程删掉 `EnableMsixTooling` 之后，XBF / PRI / Content 资源**不会进 publish 输出**；只有"装到真实安装目录再运行"才能暴露它。

### 10.14 D62 执行记录（2026-09-21，第二轮真机：精简版自动启动 / 桌面图标任务 / 卸载数据处置 / 应用图标）

用户在真机上重装了两个 x64 包，回报 4 项。第 ① 项是**上一轮修复留下的半截活**（提示做了、处置没做），②③④ 是行为增补：

| 现象 / 诉求 | 根因 | 修法 |
|---|---|---|
| 🔴 精简版装完**自动运行**，程序弹 `You must install or update .NET to run this application`（`Required: 'Microsoft.NETCore.App', version '10.0.0' (x64)`） | D60/D61 只做了**安装前**的运行时检测与提示；用户在提示框里选「否＝先装程序、稍后补装」后，`[Run]` 的 postinstall 项**照旧出现在完成页并被执行** → 拉起的就是 apphost 的缺运行时错误框，指不到安装器、也没有解法。**提示与处置必须配套** | `[Run]` 加 `Check: RuntimeReadyForApp`（精简版 = 两个运行时都齐；自包含恒真）→ 缺运行时那一项**整项消失**；`CurStepChanged(ssPostInstall)` 给一次可读说明（列出缺什么 + 两条下载链接 + 说明为什么没自动启动 + 可选直接打开下载页）。新增诊断开关 `DELAYSTART_FAKE_MISSING`，把这条分支在装了运行时的开发机上也能走通 |
| 这条分支本地怎么验 | 开发机装了 .NET 10 与 Windows App Runtime → `MissingRuntimeList()` 恒为空，缺运行时那条路**永远走不到** | 自检出口（`DELAYSTART_RUNTIME_CHECK`）扩展两行：`ready=` = `[Run]` 那个 `Check` 的取值、`forced=` = 是否被诊断开关强制。实测：正常运行 `ready=1 / forced=0`；`DELAYSTART_FAKE_MISSING=1` 时 `ready=0 / forced=1` —— **闸门可自动化回归** |
| 桌面快捷方式改成安装时可选 | D61 是按用户要求**强制创建**，用户随后要求给勾选框 | `[Tasks] Name: "desktopicon"` + `[Icons]` 的桌面项挂 `Tasks: desktopicon`，默认勾选。⚠️ 静默安装不自动勾选任何任务，需 `/MERGETASKS="desktopicon"`（已写进 README） |
| 卸载时可选删除配置与日志 | 旧行为是"只删程序目录、完成页提示手动删数据"（D22 的安全默认） | `CurUninstallStepChanged(usUninstall)` 用 `SuppressibleMsgBox(..., MB_YESNO, IDNO)` 询问；选「是」`DelTree` 掉 `%APPDATA%\DelayStart` 与 `%LOCALAPPDATA%\DelayStart`，删不干净（文件被占用）时在完成页如实汇报。🔴 **时机选 `usUninstall` 而非 `InitializeUninstall`**：前者在"确认卸载"之后触发，用户反悔时数据还在；且 `--restore-all` 已在 `InitializeUninstall` 跑完，顺序天然正确。🔴 **静默卸载一律按「否」**（`SuppressibleMsgBox` 的 Default 参数），删用户数据不能在用户没看见提示时发生 |
| 应用图标换成用户提供的 `delay.png` | 原 `AppIcon.ico` 不是用户想要的那张 | 源图入库 `assets/icon/delay.png`（256×256 RGBA，带 alpha）；新增 `tools/make-icon.py`（PEP 723 单文件脚本，依赖内联声明，`uv run tools/make-icon.py`）生成 10 档 `AppIcon.ico`（16/20/24/32/40/48/64/96/128 用 **BMP/DIB 32bpp BGRA + AND 掩码**，256 用 PNG 条目）。🔴 **Pillow 的 `Image.save(format="ICO", sizes=[...])` 对所有尺寸都写 PNG 条目** —— PNG-in-ICO 虽在 Vista+ 支持，但小尺寸走 PNG 时部分外壳路径（缩略图、第三方文件对话框）会取不到图标而显示空白，故手写 ICO 容器。iss 同步加 `SetupIconFile`（安装程序自身与卸载入口都显示应用图标） | 

收尾验证：

- 全解决方案 **0 警告 0 错误**，**256 用例全绿**；
- iss 两种形态（自包含 / 精简版）ISCC 编译 **0 错误 0 警告**（自包含形态最初有两条 `[Hint] Variable 'MISSING'/'ERRORCODE' never used` —— 变量声明也要跟着 `#ifdef` 一起裁掉）；
- 运行时闸门自检：`ready=1 / forced=0`（正常）、`ready=0 / forced=1`（`DELAYSTART_FAKE_MISSING=1`）；
- 两包重新产出（体积见 `installer/README.md`），图标：从新 exe 与 `AppIcon.ico` 提取的像素一致。

> ⚠️ **图标设计提示（非缺陷）**：源图是「白色圆环 + 绿色指针」，透明区只在圆环之外/之内。因此在**浅色背景**（浅色任务栏、Explorer 白色列表）上白色圆环基本不可见，只剩绿色指针。深色环境下观感正常。若要浅色下也完整，需要给圆环换深色 —— 属设计取舍，未擅自改图。
> —— 该提示已随 D63 换图作废：现用源图是**蓝色描边时钟 + 青色沙漏**，明暗两种背景下都完整可读（`ds-zoom-light.png` / `ds-zoom-dark.png` 真机预览比对过）。

### 10.15 D63 执行记录（2026-09-21，第三轮真机：图标 / 默认延时 / 显示名 / 首启注册计划任务）

用户重装后回报五项。这一轮有一条**行为变更**（⑤）和四类图标 / 文案 / 默认值调整：

| 现象 / 诉求 | 根因 | 修法 |
|---|---|---|
| ① 换掉源图后**重新生成图标** | — | `tools/make-icon.py` 扩展为一次产出**三份**（管理端 `AppIcon.ico` + 调度端 `Scheduler.ico` / `SchedulerWarning.ico`），新增 `--roles app,tray` 与角标合成 |
| ② `DelayStart.Scheduler.exe` 与**托盘图标**要不要一起换 | 调度端 csproj **只有 `EmbeddedResource`、没有 `<ApplicationIcon>`** → exe 带的是 .NET 默认图标（与 D61 在管理端踩的是同一个坑）；托盘两枚还是 Phase 4 期间的占位图 | 补 `<ApplicationIcon>Assets\Scheduler.ico</ApplicationIcon>`；两枚托盘图标改用新图并加红色告警角标。🔴 **`IconResources` 必须跟着改**：它原先"取 ICO 第一个条目"，而新图标是 10 档多尺寸容器（首条 16×16），配上 `LR_DEFAULTSIZE` 会被放大到 32 再被外壳缩回 16 —— 白搭两次重采样。改为**按托盘尺寸挑条目**（优先 ≤ 32 的最大档，都没有则最小档）并把 cx/cy 显式传下去，`LR_DEFAULTSIZE` 常量随之删除 |
| ③ 设置-延时**默认选中 10 秒**（原 30 秒） | 老默认值是早期拍的，登录后最拥挤的区间其实是前 30 秒 | `Settings.DefaultPreset` 30 → 10。⚠️ 与 D50 的预设列表同理：**已落盘的 `config.json` 原样保留**（用户改过的值不该被默认值覆盖），只影响新装 / 未改过该项的配置 |
| ④ "设置 → 应用"里显示 `DelayStart 0.1.0`（slim 档还有 `-slim` 后缀） | 🔴 **那一栏取的是 `AppVerName`，不是 `AppName`**。Inno 的缺省值是 `AppName + " version " + AppVersion` 之类的拼接，D60 起又按形态拼了后缀 | `AppVerName={#AppName}`（=裸 `DelayStart`）；版本号仍由 `DisplayVersion` 承载，形态后缀只留在产物文件名里。真机注册表实测佐证：改动前 `HKCU\...\Uninstall\{...}_is1` 的 `DisplayName` 正是 `DelayStart 0.1.0` |
| ⑤ 装完首次运行应**自动注册计划任务** | D22 一直写着"首启幂等注册"，但**从来没有任何代码在启动时调它** —— 只有总览页的开关（D3）与 CLI 两个入口。不注册则延时启动完全不生效，而用户得先知道有个开关、还得知道要拨开 | 新增 `Management/Services/FirstRunBootstrap`，在 `App.OnLaunched` 解析主窗口**之前**执行（晚于窗口创建的话，总览页会先显示"未创建"再自己跳成"已创建"）。安装器不做这件事：`PrivilegesRequired=lowest`，注册任务要提权，而管理端首启时已是管理员 |

**⑤ 的设计要点（这是本轮唯一的行为变更，值得单独记住）**：

- 🔴 **只在首次运行做一次**，判据是新增的 `Settings.SchedulerTaskInitialized`。总览页那个开关表达的是**用户意图**，自动动作只允许发生在他表达意图**之前** —— 若每次启动都"发现没注册就补上"，用户关掉开关的意愿会被无声推翻，而且他永远关不掉。这条不变量有专门的单元测试（`EnsureSchedulerTask_AlreadyInitialized_LeavesTaskAlone`）。
- **注册失败不落标记** → 下次启动重试（首启时被组策略临时拦下这类情况）。用户自己开过开关时 `IsRegistered()` 直接命中，只补标记不重复注册（`CreateOrUpdate` 本就幂等，这里是省一次无谓调用）。
- ⚠️ **一次性迁移效应**：`schedulerTaskInitialized` 是后加字段，升级上来的老配置没有它 → 下一次启动会补做一次自动注册。此前有意关掉开关的用户会被重新打开一次（仅此一次）。接受这个代价的理由：字段不存在时，根本无法区分"没做过"和"用户关掉了"。
- 该类**不抛异常**：它跑在启动路径上，任何失败只记日志；总览页的开关会如实显示"未创建"，用户仍能手动打开。

**顺带修掉的一处测试替身缺陷**：`InMemoryConfigStore.Copy` 对 `Settings` 是**浅拷贝**（共享同一个实例），于是"Save 抛异常 ⇒ 标记不该落盘"这条断言**假通过** —— 被测代码改的是内存里那个对象，替身看到的当然也是改过的。已改为逐字段深拷贝（`AppConfig.Items` 的元素是不可变记录，列表浅拷贝够用）。这与该替身自己写的注释（"真实 `ConfigService` 每次都从磁盘重新解析，调用方拿到的永远是一份独立快照"）一致。

**图标的两个工程细节**：

- 🔴 **PNG 条目阈值从 256 下调到 96**。原先只有 256 档用 PNG，96/128 走 DIB —— 那两张就占 105 KB，整份文件 154,811 B 里的一大半，且只服务"大图标 / 超大图标"这类 Vista+ 的现代外壳路径。改后**单份 71,530 B（−54%）**；小尺寸（16~48，会走缩略图 / 老式列表 / 第三方文件对话框）**仍是 DIB**，那才是 PNG-in-ICO 真正的兼容风险区。调度端要嵌两枚，收益按两份算。
- **角标手绘**：红色圆 + 白色描边 + 白叹号，先按 4 倍画布绘制再缩到目标尺寸（16px 上直接画圆与斜线会有锯齿）；叹号是圆角矩形 + 圆点，不用字体 —— 不依赖系统装了哪种字体，也不受 Pillow 内置位图字体的尺寸限制。角标直径取图标边长的 50%。

**托盘为什么是 32px 而不是 `SM_CXSMICON`**：调度端没有 DPI 感知声明，`SM_CXSMICON` 在本进程里恒为 16 —— 拿它当目标尺寸等于在 150%/200% 缩放下发一个 16px 位图给外壳放大。取 32 的实际效果是 100% 由外壳缩到 16（1:2 整数比，干净）、150% 缩到 24、200% 直接用满 32。

收尾验证：

- 全解决方案 **0 警告 0 错误**，**263 用例全绿**（256 + 7 条新增的 `FirstRunBootstrapTests`）；
- 图标：三份 ico 的条目结构逐条核对（10 档、16~64 为 DIB、96/128/256 为 PNG）；16/20/24/32/48 在明暗两种背景下放大 8 倍肉眼比对，角标在 16px 上仍可辨认；
- iss 两种形态编译通过；产物体积见 `installer/README.md`。

### 10.16 D64 执行记录（2026-09-21，第四轮真机：slim 报缺运行库 = 两种形态混装）

用户报"**slim 装完之后 `DelayStart.exe` 运行还提示缺少运行库**"。框的内容是 apphost 的
`You must install or update .NET to run this application. Required Microsoft.NETCore.App version 10.0.0 x64`，
且列出的 `.NET location` 指向**程序安装目录本身**。用户另外给了一条决定性线索：**卸载全包、重装 slim 就不报**。

**定性：不是缺运行时，是两种形态装进了同一个目录。**

| 环节 | 事实 |
|---|---|
| 复现 | 在一份**框架依赖**产物目录里放上 `hostfxr.dll` + `hostpolicy.dll`（取自自包含产物）→ 立刻复现同一句报错；把这两个文件删掉 → 恢复 |
| 机制 | .NET 的 apphost **只要在自己目录里看到 `hostfxr.dll`，就把"运行时根"当作程序目录本身**，转而去 `<程序目录>\shared\Microsoft.NETCore.App\10.x` 找共享框架 —— 而自包含布局是**平铺**的、那里没有 → 报"必须安装 .NET"，**哪怕机器上装着 10.0.12**（报错框还会列出一份 x86 的 10.0.12 来自证"确实装了"） |
| 命中条件 | 用户做的那个顺序：先装 full（自包含）→ 再装 slim。两形态 `AppId` 与安装目录都相同，Inno 覆盖安装**只覆盖同名文件、不清理对方形态的残留** → 静默混装。反向（slim → full）无害：slim 的文件集是 full 的子集 |
| 为什么报错框会误导 | 它自带 `aka.ms/dotnet-core-applaunch?missing_runtime=true&...` 链接，会把**已经装了 .NET** 的人再引去装一次 .NET —— 所以只能从根上修，文案救不回来 |

**先被否掉的两条假设**（都做过实验，写在这里避免后人重走）：

1. **"本机缺 DDLM → Windows App Runtime 解析不到"** —— 看着像铁证：从 `DelayStart.dll` 的自动初始化器里读出要求 `Microsoft.WinAppRuntime.DDLM.8000.806.2252.0-x6…`，而本机包仓库里只有 `8000.675.1142.0` 与 `8000.770.947.0`。但直接写探针调 `MddBootstrapInitialize2`，返回 **`HRESULT = 0x00000000`（成功）** → DDLM 版本不匹配**不是**硬前提。结论：**"读到 A 要求 B、而本机只有 C" 是证据，不是结论** —— 能直接调一次就别推。
2. **"检测误报"** —— 安装器的自检出口给出 `dotnet=1 / winappruntime=1 / ready=1`。

**修法（D64-1=A）**：`[Code] PrepareToInstall` 在复制文件之前**递归清空 `{app}`**。

- **只清程序文件**：配置在 `%APPDATA%`、日志在 `%LOCALAPPDATA%`、计划任务在目录外 —— 用户数据与接管状态一律不动；`unins*`（旧卸载器）刻意保留，本次安装失败时用户仍能卸干净；删除前再核一次路径必须等于固定安装目录（`DisableDirPage=yes` 挡不住命令行 `/DIR=` 覆盖）。
- 🔴 **为什么必须放在 `PrepareToInstall`**：Inno 文档保证它**早于** Setup 的"文件占用检查"（`CloseApplications` / Restart Manager）执行。而安装器是 `lowest` 权限，**删不掉提权进程占用的文件**（管理端与调度端都是提权跑的）。顺序不会互相抢，理由很实：Windows 上没带 `FILE_SHARE_DELETE` 打开的文件**根本删不掉**（`DeleteFile` 返回 False），所以凡是被运行中程序占着的文件**必然还在原地**，一定被随后那次占用检查发现；我们提前清掉的只是本来就没人在用的文件。
- 🔴 **唯一中止安装的情况**：精简版发现 `hostfxr.dll` 删不掉（＝自包含形态的管理端还在运行）。此时中止优于装出一个起不来的程序；**探测放在清理之前**，所以中止时一个文件都还没删、旧安装保持完整，用户关掉程序重跑即可，不会卡在半删状态。
- **诊断开关** `DELAYSTART_SKIP_PURGE`（跳过清理与探测），沿用 D61/D62 那套"环境变量出口，不设就不生效"的做法。

**修法（D64-2=A）**：`MissingRuntimeLinks()` + `OpenRuntimeDownloads()`，安装前提示与安装后提示共用 —— **只列、只打开实际缺的那几项**（两项都缺则 .NET 在前）。原实现无条件全列两个地址、点「是」却只开 .NET 下载页，而现实中最常见的恰恰是"装着 .NET、只缺 Windows App Runtime"（本机即如此）→ 用户被引去装一个已经装好的东西。

**顺带否掉的方案**：让 slim 把 Windows App Runtime 也自包含（`WindowsAppSDKSelfContained=true`）→ App 产物 42 MB → **142 MB**（安装包约 10 MB → 40 MB），只为省掉一次框架包依赖，性价比不成立。

🔴 **本轮发现、但决定不处理的缺口（2026-09-21 用户定性：什么都不做）**：`CloseApplications=yes` / `RestartApplications=yes` 会把占用文件的程序关掉，但 **Restart Manager 只会自动重启调用过 `RegisterApplicationRestart` 的程序**（Inno 文档原文），而两个 exe 都没调用 → 安装时被关掉的**调度端不会自己回来**，托盘图标要到下次登录才恢复（计划任务只在登录时触发），本次登录尚未到点的延时条目随之下次登录补上（与"关机早于延时到点"同一套语义，不丢数据）。这与 D64 修的缺陷无关 —— 任何一次升级都要替换运行中的调度端 exe。

**决定（2026-09-21，用户指令）：维持现状，不做任何补救。** 🔴 **并明确禁止"管理端启动调度端"这条修法** —— 调度端进程的生死只由计划任务决定，管理端不代管。本文档此前"倾向 ① 管理端拉起"的建议**已作废**；另两条（安装器补一次提权调用 / 调度端自己调 `RegisterApplicationRestart`）一并放弃，别再提。

**验收 / 回归**：见 `build-and-test.md` 7.8（含"往安装目录扔一个伪造残留 → 装 → 断言被清掉"的一行命令式回归，以及中止分支怎么造）。

收尾验证：

- iss **两种形态编译通过、零警告零错误**（Inno 的 `[Hint]`/`[Warning]` 也计入验收）；
- 产物重建：自包含 **60.8 MB**（547 文件 / 218.6 MB）、精简版 **10.0 MB**（83 文件 / 42.0 MB）；
- 清理逻辑的判定顺序、路径防呆、`unins*` 保留、中止前不删文件四条**逐条读码复核**；
- ✅ **2026-09-21 用户真机复测通过**：① 装 full → 启动正常；② **装 slim 覆盖 full → 启动正常**（＝ D64-1 生效的直接证据：残留 `hostfxr.dll` 已被装前清空）；③ 在 slim 目录里手工丢回一个 full 的 `hostfxr.dll` → **立刻复现"必须安装 .NET"**（＝ 反向确认这个文件就是唯一病根，也说明修复效果来自清理而非环境巧合）。

