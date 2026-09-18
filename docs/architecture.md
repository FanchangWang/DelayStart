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
        (WinForms AOT)

        DelayStart.Core.Tests ──> DelayStart.Core (+ Management)
```

- 单向依赖，无循环
- `Core` 不引用任何 UI 框架
- `Management` 只依赖 `Core`
- `Scheduler` **绝不**引用 `Management`

### 1.3 目标框架

| 项目 | TFM | 关键属性 |
|---|---|---|
| `DelayStart.Core` | `net10.0-windows` | `IsAotCompatible=true`、`Nullable=enable` |
| `DelayStart.Management` | `net10.0-windows` | 普通库 |
| `DelayStart.App` | `net10.0-windows10.0.19041.0` | `UseWinUI=true`、`WindowsPackageType=None`（unpackaged）、`ApplicationManifest=app.manifest`、`WindowsAppSDKSelfContained`（取值由 R9 结果定，见 `build-and-test.md` 7.1） |
| `DelayStart.Scheduler` | `net10.0-windows` | `OutputType=WinExe`、`PublishAot=true`、`UseWindowsForms=true` |
| `DelayStart.Core.Tests` | `net10.0-windows` | `IsPackable=false`、`OutputType=Exe`（xUnit v3 要求） |

> 所有版本号以创建项目时的最新稳定版为准写入 `Directory.Packages.props`，不照搬 demo 的版本下限。

**版本基线（本机实测，2026-09-19）**：

| 包 | 版本 | 说明 |
|---|---|---|
| `Microsoft.WindowsAppSDK` | **`1.8.260317003`** | WinUI 3 模板的默认值，CPM 中固定 |
| `Microsoft.Windows.SDK.BuildTools` | **`10.0.26100.7705`** | 同上 |
| `xunit.v3` | 创建测试项目时取最新稳定版 | 见 `build-and-test.md` 4.1 |

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
│  ├─ IProcessLauncher.cs          # 进程启动抽象（便于测试）
│  ├─ IClock.cs                    # 时间抽象（便于测试时序）
│  └─ ILogSink.cs
├─ Models/
│  ├─ StartupSource.cs             # enum
│  ├─ StartupScope.cs              # enum
│  ├─ StartupEntry.cs              # 扫描结果（内存模型）
│  ├─ DelayedItem.cs               # 延时条目（持久化）
│  ├─ OriginalState.cs
│  ├─ AppConfig.cs
│  ├─ Settings.cs
│  ├─ RunRecord.cs
│  ├─ RunItemResult.cs
│  └─ RunItemState.cs              # enum
├─ Services/
│  ├─ ConfigService.cs             # 加载/校验/迁移/原子保存
│  ├─ RunStateService.cs           # 状态与归档读写
│  ├─ CommandLineService.cs        # 命令行解析 + 参数拼接（★ 纯逻辑，重点测试对象）
│  ├─ DelayCalculator.cs           # 时序计算（★ 纯逻辑，重点测试对象）
│  ├─ ItemKeyBuilder.cs            # 稳定主键生成（★ 纯逻辑）
│  ├─ StartupSortComparer.cs       # (delay, sortOrder) 排序（★ 纯逻辑）
│  └─ LaunchResultEvaluator.cs     # 启动成功/失败判定（★ 纯逻辑）
├─ Launch/
│  ├─ ProcessLauncher.cs           # 提权/降权启动
│  ├─ TokenHelper.cs               # WTSQueryUserToken / CreateProcessAsUser
│  └─ LaunchOutcome.cs
├─ Logging/
│  └─ FileLogger.cs                # 滚动日志
├─ Interop/
│  ├─ Kernel32.cs
│  ├─ AdvApi32.cs
│  ├─ WtsApi32.cs
│  ├─ UserEnv.cs
│  └─ Shell32.cs                   # 仅 Process.Start 相关的必要部分
└─ Serialization/
   └─ JsonContext.cs               # JsonSerializerContext 源生成
```

### 2.2 各模块职责

| 模块 | 职责 | AOT 备注 |
|---|---|---|
| `ConfigService` | 加载 `config.json` → 校验 → 迁移（v1→v2）→ 原子保存 | 用 `JsonContext` 源生成 |
| `RunStateService` | 读写 `state/current-run.json`、归档 `runs/<runId>.json`、清理超过 30 份的旧记录 | 同上 |
| `CommandLineService` | ① 解析注册表值 / 快捷方式参数为 `(Path, Args)`；② 拼接最终命令行 | 纯字符串逻辑，无依赖 |
| `DelayCalculator` | `remaining = DelaySeconds - elapsed`；排序；计算调度器驻留时长 | 纯计算，注入 `IClock` |
| `ItemKeyBuilder` | 生成稳定主键 `{source}:{scope}:{sourceKey}` | 纯计算 |
| `LaunchResultEvaluator` | 依据"创建结果 + 1.5 秒后 `HasExited` + 退出码"判定成功/失败 | 纯逻辑，可注入假进程探针 |
| `ProcessLauncher` | 管理员条目继承令牌启动；普通条目降权启动；降权失败回退 | P/Invoke，`LibraryImport` |
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
│  ├─ IIconProvider.cs
│  └─ IScheduledTaskGateway.cs
├─ Sources/
│  ├─ RegistryStartupSource.cs     # HKCU / HKLM / HKLM-WOW64 三个实例
│  ├─ StartupFolderSource.cs       # 用户 / 系统 两个实例
│  ├─ ScheduledTaskSource.cs
│  └─ UwpStartupSource.cs
├─ Services/
│  ├─ ScanService.cs               # 并发调度各 Source，汇总结果
│  ├─ TakeoverService.cs           # 接管 / 移除（含失败回滚）
│  ├─ TaskRegistrationService.cs   # DelayStartScheduler 计划任务的创建/更新/删除
│  ├─ ShellLinkResolver.cs         # IShellLinkW 解析目标与参数
│  ├─ IconProvider.cs              # IShellItemImageFactory 按尺寸取图标
│  ├─ ServiceQueryService.cs       # 系统服务查询 + delayed-auto 切换
│  ├─ SystemStartupInspector.cs    # 驱动 / Winlogon / 登录脚本（只读）
│  └─ FailureStreakService.cs      # 聚合 runs/ 计算连续失败次数
├─ Interop/
│  ├─ Shell32.cs                   # SHCreateItemFromParsingName / SHGetFileInfo
│  ├─ ShellInterfaces.cs           # IShellLinkW / IPersistFile / IShellItemImageFactory
│  ├─ AdvApi32.cs                  # ChangeServiceConfig
│  └─ Shlwapi.cs                   # SHLoadIndirectString
└─ Serialization/
   └─ (复用 Core 的 JsonContext)
```

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

### 3.4 图标提取

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
    "delayPresets": [0, 10, 30, 60, 120],
    "maxDelaySeconds": 86400,          // 0 = 不限制；仅输入校验用
    "notifyMode": 0,                    // 0 仅失败 / 1 总是 / 2 从不
    "retryCount": 1,                    // 同一次运行内的重试次数
    "trayKeepSeconds": 3,
    "showTrayIcon": true,
    "preferDeElevatedLaunch": true,     // 普通用户条目优先降权启动
    "fallbackOnDeElevationFailure": true,
    "lastRunId": "20260919-084112"      // 管理端横幅用
  }
}
```

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
│  ├─ OverviewPage.xaml               # 总览（含时间轴、上次运行横幅）
│  ├─ ItemsPage.xaml                  # 自启动项（按来源筛选）
│  ├─ DelayPage.xaml                  # 延时启动（列表 / 时间轴双视图）
│  ├─ SystemPage.xaml                 # 系统启动项（只读）
│  ├─ LogPage.xaml                    # 运行日志
│  └─ SettingsPage.xaml               # 设置
├─ Dialogs/
│  ├─ DelayEditorDialog.xaml          # 延时配置编辑器（4 步布局）
│  ├─ ConfirmRemoveDialog.xaml        # 移除确认（两种变体文案）
│  ├─ SimulateDialog.xaml             # 模拟调度
│  └─ AddManualDialog.xaml            # 手动添加
├─ ViewModels/
│  ├─ ShellViewModel.cs
│  ├─ OverviewViewModel.cs
│  ├─ ItemsViewModel.cs
│  ├─ DelayViewModel.cs
│  ├─ SystemViewModel.cs
│  ├─ LogViewModel.cs
│  └─ SettingsViewModel.cs
├─ Controls/
│  ├─ TimelineView.xaml               # 时间轴（总览与延时页共用）
│  ├─ StartupItemRow.xaml             # 列表行（图标 + 双行文本 + 操作）
│  ├─ DelayEditorSection.xaml         # 编辑器内的编号步骤块
│  └─ ResultBanner.xaml               # 上次运行结果横幅 / 常驻告警条
├─ Services/
│  ├─ NavigationService.cs
│  ├─ DialogService.cs
│  ├─ SimulationService.cs            # 模拟调度
│  ├─ NotificationService.cs          # 管理端内通知（InfoBar）
│  └─ ActivationRouter.cs             # 处理 --goto-log --run=xxx
└─ Converters/
```

### 5.2 MVVM 约定

- 用 **CommunityToolkit.Mvvm**（源生成器版的 `[ObservableProperty]` / `[RelayCommand]`），不手写 `INotifyPropertyChanged`
- 视图用 `x:Bind`，**不用** `{Binding}`（编译期检查 + 性能好）
- ViewModel 只依赖 `Core` / `Management` 的接口，不直接 new 具体的 Source
- 长任务（扫描、接管、导出）一律走 `async` 命令，UI 上有忙碌态，**不阻塞 UI 线程**

### 5.3 启动流程

```
App.OnLaunched
 ├─ 解析命令行（--goto-log --run=<id> → 决定初始页）
 ├─ 单实例检查（已存在 → 激活它并退出）
 ├─ 构建 DI 容器
 ├─ 创建 MainWindow
 └─ 触发首次扫描（FR-1.2，异步，不等它完成就显示窗口）
```

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
| 全局化 | `<InvariantGlobalization>true</InvariantGlobalization>` |
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
| **R2** | `Microsoft.Win32.TaskScheduler` 在 WinUI 3 下与 `System.Threading.Tasks.Task` 命名冲突 | 编译错误 | 用 `using TaskSchedulerTask = Microsoft.Win32.TaskScheduler.Task;` alias | Phase 1 |
| **R3** | WinUI 3 unpackaged 下 `app.manifest` 与 `Package.appxmanifest` 的选择 | 提权与 DPI 声明是否生效 | 建项目时确认；unpackaged 用 `app.manifest` | Phase 1 |
| **R4** | `IShellItemImageFactory` 取到的 `HBITMAP` 转 WinUI `ImageSource` 的生命周期 | 图标不显示或 GDI 句柄泄漏 | 小规模实测 + `DeleteObject` | Phase 3 |
| **R5** | Core 层引入 AOT 不兼容 API | 调度端发布失败 | `IsAotCompatible=true` 让编译器在构建期报错 | 持续 |
| **R6** | 计划任务创建/修改被 ACL 或组策略拒绝 | 接管链路中断 | 管理端已全程提权（D20），正常不会因权限失败。仍需捕获 `UnauthorizedAccessException` 并指出**具体条目** | Phase 2 |
| **R7** | 高 DPI 下时间轴与列表行错位 | 界面不可用 | 100/150/200% 三档实测 | Phase 3 / 5 |
| **R8** | `DelayStart.slnx` 与 WinUI 3 项目兼容性 | 无法打开解决方案 | 若 VS 2026 报错则回退 `.sln` | Phase 0 |
| **R9** | 🔴 **`WindowsAppSDKSelfContained=true` 时 `requireAdministrator` 可能被忽略** | **管理端不弹 UAC 也不提权 → D20 的整个权限模型失效** | **Phase 0 第一件事就是实测**：建最小 WinUI 3 unpackaged 项目 + 自包含 + manifest 提权，Release 下双击 exe，确认弹出 UAC 且 `WindowsPrincipal.IsInRole(Administrator)` 为 true。**三条降级路径见下方** | **Phase 0** |
| **R10** | 🟡 提权窗口接收不到资源管理器的拖放（UIPI） | 「手动添加」的拖放区失效 | 主路径 `[浏览…]` 按钮（`FileOpenPicker` + `InitializeWithWindow`）必须 100% 可用，**不依赖拖放**；拖放作为增强：`ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES/MSGFLT_ALLOW)` + `DragAcceptFiles` + 子类化窗口处理 `WM_DROPFILES`。**不通则拖放区降级为纯按钮，不接受"等待修复"** | Phase 3 |

#### R9 详解：这是本轮唯一可能推翻 D20 的技术风险

**现象**：多个社区报告（StackOverflow 75175782、WindowsAppSDK Discussion #3038）指出，WinUI 3 unpackaged 应用在 `WindowsAppSDKSelfContained=true` 时，`app.manifest` 的 `requestedExecutionLevel` 可能不生效——exe 图标上没有盾牌、双击不弹 UAC、进程仍是中等完整性。其中有分析认为：**自包含模式生成"免注册 WinRT 条目"的过程可能错误地写入了信任信息**，从而吞掉了 manifest 的提权声明。

**背景**：WinAppSDK **1.1 起已官方移除**"不能以管理员身份运行"的限制（官方博客原文：*Development, administration, and system management tools can now leverage the full power of Windows App SDK*）。所以**问题不是"不支持提权"，而是"自包含模式可能吞掉 manifest 声明"**。这些报告多来自 2022 年（WinAppSDK 1.0–1.2），当前版本是否已修复**必须实测，不能靠推断**。

**Phase 0 实测步骤**（三条路，按优先级试）：

1. 建最小 WinUI 3 unpackaged + 自包含项目，`app.manifest` 写 `requireAdministrator`
2. **Release 构建**，**在 VS 之外直接双击 exe**（在 VS 里调试会继承 VS 的权限，测不准）
3. 判定：出现 UAC 盾牌 / 弹 UAC 对话框 / `IsInRole(Administrator) == true` → **通过**

**三条降级路径**（按推荐顺序）：

| 路径 | 做法 | 代价 |
|---|---|---|
| **① 去掉自包含**（社区验证最有效） | `WindowsAppSDKSelfContained=false`，改为框架依赖 | 用户机器需预装 Windows App Runtime。**本机已装**（`C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime*` 已确认存在）；分发时由 **D22 的 Inno Setup 安装器**代为部署运行时，用户无感 |
| **② 运行时自提权** | manifest 保持 `asInvoker`，在 `App.OnLaunched` 最早期检测权限，不足则 `Process.Start(new ProcessStartInfo { FileName = exePath, Verb = "runas", UseShellExecute = true })` 重启自己，然后退出当前实例 | 多一次进程启动；**必须传参防止无限重启循环**（如加 `--elevated-retry` 标记位，失败则直接退出） |
| **③ 外部启动器** | 新增极小 AOT 控制台/窗口 exe 作为 `DelayStart.Launcher.exe`，manifest 标 `requireAdministrator`，只负责以提升权限拉起 `DelayStart.App.exe` | 多一个 exe 要维护；快捷方式指向 launcher |

> **R9 不通过就不进入 Phase 1。** 这是唯一会推翻已批复决策（D20）的风险，必须在写第一行业务代码前解决。

---

## 十、实施顺序

| Phase | 内容 | 出口条件 |
|---|---|---|
| **0** | 环境与骨架：先按 `build-and-test.md` 1.2 装齐工具链前置（VS 组件 + WinUI 模板包）；`dotnet new` 生成 4 个项目 + 测试项目（2.1）、`Directory.Build.props`、CPM、`.editorconfig`；**并验证 R9**（自包含 + manifest 提权） | `dotnet build` 全绿；R2 / R3 / R8 已确认；**R9 有明确结论**（决定 7.1 的自包含取值） |
| **1** | `Core` 层：模型、`ConfigService`、`DelayCalculator`、`ItemKeyBuilder`、`CommandLineService`、`LaunchResultEvaluator` + 单元测试 | 单元测试全绿 |
| **2** | `Management` 层：4 个 Source 的扫描 + 软禁用 + 恢复、`TakeoverService`、`TaskRegistrationService` | 注册表导出对比验证"零数据损坏" |
| **3** | 管理端骨架：导航 + 自启动项页 + 延时启动页，跑通"扫描 → 接管 → 移除"单链路；验证 R10（提权下的文件选择/拖放） | 端到端手工验证通过；「手动添加」的 `[浏览…]` 路径可用 |
| **4** | 调度端：先验 R1，再做引擎 + 托盘 + 通知 + 状态文件 | 重启实测延时准确 |
| **5** | 总览 / 日志 / 设置 / 系统启动项页 | 全部页面文案对齐 `design-spec.md` |
| **6** | 模拟调度、**Inno Setup 安装器与卸载还原链路**（D22）、真机全面验收 | `requirements.md` 第九节全部勾选（含 9.4 分发验收） |

**Phase 2 与 Phase 4 之间不允许跳过**——调度端依赖 Core 的时序与启动逻辑，而那部分的正确性只能靠 Phase 1 的单元测试保证。
