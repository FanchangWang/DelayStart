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
| `DelayStart.App` | `net10.0-windows10.0.19041.0` | `UseWinUI=true`、`WindowsPackageType=None`（unpackaged）、自包含 |
| `DelayStart.Scheduler` | `net10.0-windows` | `OutputType=WinExe`、`PublishAot=true`、`UseWindowsForms=true` |
| `DelayStart.Core.Tests` | `net10.0-windows` | `IsPackable=false` |

> 所有版本号以创建项目时的 NuGet / SDK 最新稳定版为准写入 `Directory.Packages.props`，不照搬 demo 的版本下限。

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
| 系统操作（注册表/任务/COM） | 捕获 `UnauthorizedAccessException` / `SecurityException` → 包装为 `StartupOperationException`（带"需要管理员权限"语义） |
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
| **R6** | 计划任务创建需要管理员权限 | 首次接管时弹 UAC（预期行为，但需明确的失败路径） | 无权限时给出明确提示与提权入口 | Phase 2 |
| **R7** | 高 DPI 下时间轴与列表行错位 | 界面不可用 | 100/150/200% 三档实测 | Phase 3 / 5 |
| **R8** | `DelayStart.slnx` 与 WinUI 3 项目兼容性 | 无法打开解决方案 | 若 VS 2026 报错则回退 `.sln` | Phase 1 |

---

## 十、实施顺序

| Phase | 内容 | 出口条件 |
|---|---|---|
| **0** | 环境与骨架：创建 4 个项目 + 测试项目、`Directory.Build.props`、CPM、`.editorconfig` | `dotnet build` 全绿；R2 / R3 / R8 已确认 |
| **1** | `Core` 层：模型、`ConfigService`、`DelayCalculator`、`ItemKeyBuilder`、`CommandLineService`、`LaunchResultEvaluator` + 单元测试 | 单元测试全绿 |
| **2** | `Management` 层：4 个 Source 的扫描 + 软禁用 + 恢复、`TakeoverService`、`TaskRegistrationService` | 注册表导出对比验证"零数据损坏" |
| **3** | 管理端骨架：导航 + 自启动项页 + 延时启动页，跑通"扫描 → 接管 → 移除"单链路 | 端到端手工验证通过 |
| **4** | 调度端：先验 R1，再做引擎 + 托盘 + 通知 + 状态文件 | 重启实测延时准确 |
| **5** | 总览 / 日志 / 设置 / 系统启动项页 | 全部页面文案对齐 `design-spec.md` |
| **6** | 模拟调度、打包、真机全面验收 | `requirements.md` 第九节全部勾选 |

**Phase 2 与 Phase 4 之间不允许跳过**——调度端依赖 Core 的时序与启动逻辑，而那部分的正确性只能靠 Phase 1 的单元测试保证。
