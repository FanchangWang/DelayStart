# DelayStart — 设计文档（当前方案单一事实来源）

> 本文是**当前方案的单一事实来源**：只写"现在成立什么"，不写论证过程。每个决策的结论与取舍见 [`decisions.md`](decisions.md)，技术陷阱见 [`pitfalls.md`](pitfalls.md)，构建 / 调试 / 打包见 [`development.md`](development.md)。
> 代码注释、提交信息、测试用例引用的编号（`FR-x` / `NFR-x` / `E-x` / `D-x` / `坑 x`）以本文与另两份为准。

---

## 一、产品定位与三原则

**扫描所有自启动位置 → 软禁用（不删数据）→ 由独立的调度进程按延时启动。**

| 原则 | 含义 |
|---|---|
| **全部可逆** | 禁用一律走软禁用标记（`StartupApproved` / `task.Enabled` / UWP `State`），绝不删除注册表值、绝不移动用户文件 |
| **判定可靠** | 延时是"相对登录时刻的绝对时间点"，不是累加；软件必须准确告诉用户"第几秒启动" |
| **不替用户做决定** | 启动失败只提醒、不自动改系统状态（D17）；所有会改变系统行为的动作，出口都在用户手上 |

技术目标：管理端冷启动 < 2s；调度端冷启动 < 0.3s、AOT 产物约 3.4–3.6 MB；全量扫描 < 2s；调度偏差 < ±500ms；调度端空闲内存 < 30MB。

界面仅简体中文（D3）；应用名统一为英文 **DelayStart**（D61）。

---

## 二、扫描与接管范围

| 来源 | 位置 | 可禁用 | 可延时启动 | 备注 |
|---|---|---|---|---|
| 注册表 HKCU Run | `HKCU\...\CurrentVersion\Run` | ✅ | ✅ | |
| 注册表 HKLM Run | `HKLM\...\CurrentVersion\Run` | ✅ | ✅ | |
| 注册表 HKLM 32 位 | `HKLM\Software\WOW6432Node\...\Run` | ✅ | ✅ | 标记写 `StartupApproved\Run32` |
| 用户启动文件夹 | `%APPDATA%\...\Startup` | ✅ | ✅ | `.lnk` / `.url`，解析真实目标与图标 |
| 系统启动文件夹 | `%PROGRAMDATA%\...\Startup` | ✅ | ✅ | |
| 计划任务 | 带 Logon/Boot 触发器、排除 `\Microsoft\` 文件夹（判据带尾分隔符，见 pitfalls 二） | ✅ | ✅ | 禁用粒度细分任务级/触发器级（D67） |
| UWP / Store 应用 | `AppModel\SystemAppData\<PFN>\<TaskId>` | ✅ | ⚠️ 受限 | 见下 |
| 手动添加 | exe / lnk / bat / cmd / ps1（`LaunchTargetTypes` 白名单，**无 .msi**，D47） | — | ✅ | 不进"自启动项"页 |

**UWP 能力边界（必须写进 UI 文案，D4/D45）**：UWP 的自启动由系统 AppModel 调度，第三方无法延后。"延时启动 UWP" 实为 *禁用系统自启 + 到点 COM/外壳激活*；**UWP 进程恒为普通用户身份**（真机实测，中转外壳用谁的令牌都不改结果），编辑器不提供管理员胶囊。

**红线（不做）**：不删任何原始数据；不接管服务/驱动（服务走原生 `delayed-auto`）；不改 Winlogon/Userinit/Shell（只读展示）；不改登录脚本 / GPO 配置；失败后不自动恢复自启动（D17）；无常驻后台服务；不做批量操作（D10）。

---

## 三、功能性需求（FR）

### FR-1 扫描

| 编号 | 需求 |
|---|---|
| FR-1.1 | 一次扫描覆盖第二节全部来源 |
| FR-1.2 | 进入管理端即自动扫描（固定行为）；FR-1.3 提供手动刷新 |
| FR-1.4 | 单条读取失败不影响整次扫描（跳过 + 记日志） |
| FR-1.5 | 软禁用判定走键名三级回退（坑 1） |
| FR-1.6 | "已被接管"用稳定主键匹配，不因同名误判 |
| FR-1.7 | 解析 `.lnk` 真实目标/参数并提取图标 |
| FR-1.8 | 解析 UWP 显示名（SplashScreen → `SHLoadIndirectString` → 兜底包名前缀；MrtCache 反查未实现，属已知偏差） |
| FR-1.9 | `\Microsoft\` 文件夹与 GPO 任务标记只读；FR-1.10 标记目标缺失的"已失效"项 |

### FR-2 软禁用

禁用 = 写软禁用标记（原始数据不变）；启用 = 删标记。标记位置按 scope 精确分流（机制 3/4）；12 字节格式 `[0]=0x03` + FILETIME；写前 `CreateSubKey` 确保子键存在；访问被拒指出**具体条目**与原因（D20 后 ≠ 需要提权）；持久化 `originalState` 供精确恢复（FR-2.7）。

### FR-3 接管

接管 = 软禁用原项 + 生成延时条目 + 持久化配置，任一步失败逆序回滚（FR-3.1）；计划任务由 `SchedulerTaskBootstrap` 在**每次启动**检测、缺失即自动补建（D68，替代原"首启一次"）；手动条目不做任何系统改动。

### FR-4 延时配置

预设快选（默认 `0/5/10/15/20/30/60` 秒，D50）+ 自定义秒数；上限仅输入校验（默认 24h，FR-4.3）；身份可选普通/管理员（UWP 锁普通，D45）；条目级启用开关；同延时内可上下移调序（FR-4.7）；配置原子写（FR-4.8）。

### FR-5 调度执行

登录后 3s 由计划任务拉起（`onlogon` + `delay 0000:03` + `RunLevel=Highest`）；单实例 Mutex（FR-5.2）；时序 = 绝对时间点（FR-5.3，机制 5）；按 `DelaySeconds,SortOrder` 排序（FR-5.4）；逐条 try/catch（FR-5.5）；普通条目降权启动（机制 6，D40 调度端亲自降权）；管理员条目继承令牌不弹 UAC（FR-5.8）；成功判定 = 创建成功 + 1.5s 后复查 `HasExited`（机制 7）；无有效条目静默退出（FR-5.10）；跑完即退（FR-5.11）；实时状态写 `current-run.json`（FR-5.12）+ 归档 `runs\<runId>.json` 保留 30 次（FR-5.13）；源生成 JSON（FR-5.14）。

### FR-6 结果通知与追溯

托盘图标显示进度（预计驻留 < 15s 不显示）；点击弹面板（失焦即关）；**默认仅失败时通知**（FR-6.3，D15）；失败通知合并一条（FR-6.4）；失败角标保留（FR-6.5）；点通知打开管理端运行日志并定位本次运行（FR-6.6，`--goto-log --run=<id>`）；**连续失败 ≥3 次：角标不自动消失、告警条不提供忽略、唯一出口是「移出延时启动」**（FR-6.8/6.9）；**永不自动恢复失败项的自启动**（FR-6.10）。

### FR-7 系统启动项（只读）

服务只读展示 + 一键切"延迟自动启动"（原生 `delayed-auto`）；驱动/Winlogon/登录脚本纯只读；全部不参与延时配置（D5）。

### FR-8 运行日志

按 `runId` 分组；每项显示名称/延时/结果/启动时间/失败原因；保留 30 次；支持跳转定位；调度端自身日志滚动截断。

### FR-9 设置

预设编辑（FR-9.2）；上限（FR-9.3）；托盘保留时长 3/5/10s（FR-9.5）；同次运行内重试 0/1/2 次（FR-9.6）；通知策略三档（FR-9.7）；主题外观（D50 置于第一个分区）；导入/导出/重置（重置前二次确认）。字段与 `Settings.cs` 一一对应。

### FR-10 模拟调度

按真实延时真启动（可加速、可中止、进度视图；时间轴已按 D37 砍除）。

### FR-11 计划任务注册

`TaskService` API 创建/更新（FR-11.3）；`SchedulerTaskBootstrap` 每次启动自检自愈（D68）；卸载清理路径（FR-11.4，`--restore-all`）。

### FR-12 配置迁移与备份

v1（demo）→ v2 迁移补齐 `scope`/`enabled`/`originalState`；导入导出；损坏时保留坏文件副本并重建默认配置。

---

## 四、非功能性需求（NFR）

| 编号 | 指标 |
|---|---|
| NFR-1 | 性能：管理端冷启动 <2s；调度端 <0.3s / ~6MB 上限（实测 3.4–3.6MB）；扫描 <2s；偏差 <±500ms；空闲 <30MB |
| NFR-2 | 可靠性：单条失败不影响其他；配置原子写；崩溃留现场（E9）；Mutex 防并发；配置目录可重建 |
| NFR-3 | 安全与权限：**管理端全程提权**（app.manifest requireAdministrator，D20）；NFR-3.2 **绝不删除用户数据**；普通程序一律降权启动；改动系统状态必有确认；不联网不采集；UAC 拒绝即退出不降级；提权窗口主路径 `[浏览…]`、拖放为增强（R10）；**拒绝"以其他用户身份运行"/OTS**（判据 = 提权后 SID 对比交互会话 SID，不是 `IsInRole`） |

> **NFR-3.2 安全验收（原 9.3）**：全流程操作后三个 `Run` 键**值数量与内容零变化**（`StartupApproved` 标记写入属设计内正常写入，不算改动原值）；启动文件夹文件零变化；未接管任务的 `Enabled` 零变化；卸载/重置后系统回到接管前状态。已按 D34 真机执行通过。

| 编号 | 指标 |
|---|---|
| NFR-4 | 可维护性：Core 无 UI 依赖；系统操作走接口抽象；纯逻辑有单测；公开 API 中文 XML 注释 |
| NFR-5 | 兼容性：Win10 21H2+；100–200% DPI；多显示器面板落主屏右下；明暗主题；仅简体中文 |
| NFR-6 | 分发：管理端 WinUI 3 unpackaged 自包含（R9 已实测提权生效）；调度端纯 Win32 + NativeAOT（D24）；**Inno Setup 安装器**，固定 `%LOCALAPPDATA%\Programs\DelayStart`，`PrivilegesRequired=lowest`（安装零 UAC、卸载弹一次）；**NFR-6.4 卸载必须可逆**：先还原全部接管项再删文件，还原失败中止卸载；安装目录只读（NFR-6.7）；计划任务身份 = 交互用户 + `RunLevel=Highest`，**禁止 SYSTEM**（NFR-6.8） |

---

## 五、异常场景矩阵（E1–E19）

| # | 场景 | 期望行为 |
|---|---|---|
| E1 | 某计划任务读取抛异常 | 跳过 + 记日志，其余照常 |
| E2 | 写 HKLM 标记被拒 | 指出具体条目与原因（受策略保护），不静默失败 |
| E3 | 目标 exe 已卸载 | 标记"已失效"，不参与调度 |
| E4/E5 | 启动后立即退出（码 0 / ≠0） | 成功（拉起已有实例）/ 失败（记退出码） |
| E6 | 降权失败 | **判本条目失败并继续，绝不提权回退**（D40/D20 红线） |
| E7 | 延时已过期（remaining ≤ 0） | 立即启动 |
| E8 | 调度端被重复拉起 | Mutex 拦截，立即退出零副作用 |
| E9 | 调度端被强杀 | `current-run.json` 留现场，管理端识别"未正常完成" |
| E10 | `config.json` 损坏 | 保留坏副本 + 重建默认 + 提示 |
| E11 | 用户手动改回注册表 | 以用户改动为准并解除接管 |
| E12 | 计划任务被外部删除 | 检测到即重建（D68 自检自愈） |
| E13 | 连续 3 次登录失败 | 保持接管；角标常驻 + 告警条 + `[移出延时启动]` |
| E14 | 所有条目被关闭 | 调度端静默退出 |
| E15 | 系统时间被改 | 时序用 `Stopwatch` 单调计时 |
| E16 | GPO 下发的任务 | 只读，入口禁用 |
| E17 | UAC 选"否" | 说明后退出，不降级 |
| E18 | 以其他用户身份运行 / OTS | SID 比对不一致则提示不支持 |
| E19 | 程序目录移动 | 启动时对比安装路径，提示一键 `--reinstall-task` |

---

## 六、架构总览、权限模型与路径布局

**解决方案**：`DelayStart.slnx` + `src/DelayStart.{Core,Management,App,Scheduler}` + `tests/DelayStart.Core.Tests`。依赖方向：

```
App ──> Management ──> Core <── Scheduler（只引用 Core，硬边界）
Tests ──> Core (+ Management)
```

**TFM**：Core/Management `net10.0-windows`（Core 另开 `IsAotCompatible=true` 守门）；App `net10.0-windows10.0.26100.0`（`TargetPlatformMinVersion=10.0.19041.0`，`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`）；Scheduler `net10.0-windows` + `PublishAot=true`。版本基线：WindowsAppSDK 1.8 元包、`TaskScheduler` 2.12.2（🔴 包名是 `TaskScheduler`，不是 `Microsoft.Win32.TaskScheduler`——那是 2016 年的死包）。

**权限模型（D20）**：两个进程全程提权。App 靠 manifest；Scheduler 靠计划任务 `RunLevel=Highest`。UAC 被拒 → 退出不降级；"以其他用户身份运行"用 SID 比对拒绝（E18）。

**路径布局（D23，全部经 `PathService`，禁止硬编码）**：

| 类别 | 路径 | 说明 |
|---|---|---|
| 程序 | `%LOCALAPPDATA%\Programs\DelayStart\` | per-user 安装、**只读** |
| 配置 | `%APPDATA%\DelayStart\config.json` | Roaming，原子写 |
| 日志 | `%LOCALAPPDATA%\DelayStart\logs\{scheduler,manager}.log` | 2MB 轮转 |
| 实时状态 | `%LOCALAPPDATA%\DelayStart\state\current-run.json` | 每次状态变化原子重写 |
| 运行归档 | `%LOCALAPPDATA%\DelayStart\runs\<runId>.json` | 保留 30 次 |

调试覆盖：`DELAYSTART_LOCAL_DIR` / `DELAYSTART_CONFIG_DIR`（仅开发测试用）。计划任务身份必须是交互用户——SYSTEM 下 `%APPDATA%` 解析到 systemprofile 且不报错。

---

## 七、分层与模块设计

### 7.1 分层判据与测试边界

🔴 **Core / Management 按"AOT 兼容性"切分，不按领域**：凡 COM / 反射 / `TaskScheduler` 包相关的扫描接管代码一律在 Management；写进 Core 会被 `IsAotCompatible=true` 在构建期拦截。调度端因此拿到零 COM、零反射的最小集合。

🔴 **Tests 绝不引用 `App`**——一旦引用就要背上 WindowsAppSDK 自包含 + 运行时预装的包袱；本项目测试只引用 Core/Management，秒级、无环境依赖。单元测试禁止触碰真实注册表/文件系统/进程，全部注入假实现，以普通权限运行。

### 7.2 Core 层

`Abstractions/`（IAppConfigStore / IRunStateStore / IProcessLauncher / IClock / ILogSink——只有 `Write` 两个方法，Info/Warn/Error 走扩展方法以规避 CA1716）、`Models/`（含 `ProcessSnapshot`、`StartupFailureReason`，异常消息强制带 EntryId 的 `StartupOperationException`）、`Services/`（PathService / AtomicFileWriter / ConfigService / RunStateService / CommandLineService / LaunchTargetTypes / PowerShellHost / DelayCalculator / ItemKeyBuilder / StartupSortComparer / LaunchResultEvaluator / FailureStreakService / UwpParsingName）、`Serialization/`（JsonContext 源生成 + v1 迁移 DTO）、`Logging/`。

### 7.3 关键机制（编码必须照做）

1. **稳定主键**：`ItemKeyBuilder.Build(source, scope, sourceKey)` 三元组小写；手动条目 `manual:{Guid:N}`。禁止 `(Name, Source)` 二元组判断（坑 6）。
2. **StartupApproved 键名三级回退**：原名 → `+.exe` → 去扩展名，全未命中才算启用（坑 1）。
3. **scope 显式枚举**：`StartupScope { None, Hkcu, Hklm, HklmWow, UserFolder, SystemFolder }`，禁止字符串猜 hive（坑 5）。
4. **软禁用矩阵**：Registry→`StartupApproved\Run`（WOW64→`Run32`）；Folder→`StartupFolder`；ScheduledTask→任务级/触发器级细分（D67：单触发器切任务级；多触发器只切登录/启动触发器且**全部**切、任务级已关时禁用动作一个字节都不动、启用先开任务级）；UWP→`State=0/2`；Manual→无动作。
5. **调度时序**：`remaining = DelaySeconds - elapsed`（绝对时间点）；`Stopwatch` 单调计时；只保证发起顺序（坑 7：同延时值不保证前者完成初始化）。
6. **启动身份**：管理员条目继承令牌直启；普通条目 `GetShellWindow` 外壳令牌 → `DuplicateTokenEx` → `CreateProcessWithTokenW`（D40 方案 7）；**降权链任何一步失败 = 本条目失败并继续，绝不提权回退**；`.lnk`/UWP 经 explorer 外壳委托（UWP 恒降权委托，D45）；`.ps1` 走宿主（pwsh 优先，`-NoProfile -ExecutionPolicy Bypass -File`）。
6a. **uiAccess 目标（D70）**：预检目标 RT_MANIFEST（`LoadLibraryEx` AS_DATAFILE 只读资源）判 `uiAccess="true"`（Quicker 类）——这类目标 CPWT 直启必 740（`TokenUIAccess` 需 SeTcbPrivilege，仅 SYSTEM 有）。改走**降权中转器链**：调度端 A（High）写作业 JSON → CPWT 降权拉起 `DelayStart.LaunchBroker.exe` B（Medium，AOT 单文件，与调度端同目录部署）→ B 对目标 C `ShellExecuteEx`（AppInfo 校验签名+安全位置+清单声明后赋 UIAccess、按调用方身份抬 IL：受限管理员 → High，与手动双击一致）→ B 把 **C 的**启动状态（PID/秒退/Win32 错误）经结果 JSON 回写（写 `.tmp` + 原子改名），调度端轮询读取；B 自身退出码只表达回写是否成功。作业/结果契约 = `BrokerLaunchJob`/`BrokerLaunchResult`（Core，`BrokerJsonContext` 源生成）。
7. **成功判定**：创建成功 + 1500ms 后复查 `HasExited`；退出码 0 = 成功（拉起已有实例的必要宽容）；UWP 无 PID 收到 `null` 快照直接判成功（D28）。
8. **原子写**：`<name>.tmp` → `File.Replace`（目标不存在则 `File.Move(overwrite:true)`）。
9. **单实例**：`Local\DelayStartScheduler` / `Local\DelayStartManager` Mutex，重复触发立即退出零副作用。

### 7.4 数据契约与来源接口

`IStartupSource`（Kind / Scope / DisplayName / RequiresElevation / Scan / Disable / Enable）四个实现按序注册：注册表三项 → 启动文件夹两项 → 计划任务 → UWP；`ScanService` 逐源 try/catch。接管事务性（`TakeoverService`）：**先写配置再禁用系统项**，任一步失败逆序回滚；`Remove` 反向执行并按 `originalState.wasEnabled` 精确还原。

配置（version=2，枚举一律字符串落盘、camelCase）：

```jsonc
{ "version": 2, "items": [ { "id", "name", "path", "args", "delaySeconds", "sortOrder",
  "runAsAdmin", "enabled", "source", "scope", "sourceKey", "sourceDetail",
  "originalState": { "wasEnabled" } } ],
  "settings": { "delayPresets": [0,5,10,15,20,30,60], "defaultPreset": 10, "notifyMode",
                "retryCount", "theme", "lastRunId" } }
```

`maxDelaySeconds` / `trayKeepSeconds` / `showTrayIcon` / 降权两开关**已从模型删除**（不再可配）。运行归档 `RunRecord`：`runId / startedAt / finishedAt / completedNormally / items[{id,name,delay,state,launchedAt,reason,attempts}]`；连续失败次数不落盘，由 `FailureStreakService` 扫 `runs/` 现算（两端共用，调度端无状态）。

### 7.5 管理端（App）设计

页面：Overview（统计 + 调度任务状态卡 + 最近一次开机调度表）/ Items（来源筛选 + 搜索）/ Delay（按延时分组，组 = 子标题 + 各自边框卡片）/ System（只读）/ Runs（最新一组默认展开）/ Settings（外观 → 延时 → 调度）。MVVM 用 CommunityToolkit.Mvvm 源生成器 + `x:Bind`；组合根 `ServiceRegistration` **CLI 与 GUI 共用一个容器**，容器在 CLI 分流前构建；跨页数据用 `ScanCacheService` 来源级过期标记做局部刷新（D42）。

**提权交互约束（R10）**：`FileOpenPicker` 必须 `InitializeWithWindow` 挂 HWND；拖放走旧式 `WM_DROPFILES` + `ChangeWindowMessageFilterEx`（`UipiMessageFilter`），不通则降级纯按钮；禁止"拖出"交互。编辑器（`DelayEditorDialog`）：4 种进入方式共用，目标块 = 只读卡片 / 「程序」+「UWP 应用」分段页签（ToggleButton，禁用"再点取消选中"），参数/工作目录在页签之外按形态统一算可见性（D59 教训：可见性判据不能写在会被折叠的子树里），宽度回归框架默认 548，自定义延时二次确认用同层 overlay（ContentDialog 不能嵌套）。

图标：`IconProvider`（IShellItemImageFactory）→ `IconPixels`（32bpp BGRA，`GetDIBits` 负高度保 alpha）→ App 侧同步灌 `WriteableBitmap`；失败返 null 不抛不重试。

### 7.6 调度端实现

纯 Win32 + NativeAOT（D24）：`SchedulerEngine`（`SetTimer` 逐条绝对时刻点火 + 逐条落盘）、`TrayIconHost`（`Shell_NotifyIconW`）、`PanelWindow`（失焦即关）、`DeElevatedProcessLauncher`（机制 6）、`IconResources`（多尺寸 ICO 按目标尺寸挑条目，托盘底色取 32 非 `SM_CXSMICON`）。**AOT 硬约束**：`OutputType=WinExe`；图标走 `GetManifestResourceStream` 禁 `.resx`；JSON 源生成；绑定手工赋值；`LibraryImport` + `[return: MarshalAs(UnmanagedType.Bool)]`；**不设 `InvariantGlobalization`**（R13）；禁 Reflection.Emit / BinaryFormatter / 动态 COM。手动构造、无 DI 容器。

### 7.7 错误处理与日志

Core 纯逻辑抛标准异常；系统操作统一包 `StartupOperationException`（AccessDenied 语义 = "受 ACL/GPO 保护"而非"需要提权"，消息带条目标识）；ScanService / 调度引擎单条失败不外抛；UI 顶层 `UnhandledException` 兜底；**禁止空 catch**。日志格式 `时间 [级别] [来源] 消息`，中文，单文件 2MB 轮转，启动记录版本与耗时。

---

## 八、调度端交互设计

**结论先行：调度器是基础设施进程，不是应用程序。无主窗口，默认隐形，跑完即退。**

**信息分级（0–4）**：完全静默 → 托盘悬停一行摘要（127 字符上限、不换行；格式 `n/m 已启动 · 下一项 n 秒`，D3）→ 点击弹面板（右下角工作区、只读列表）→ **完成后自动弹出面板**（取代气泡，D71）→ 管理端运行日志。任何升级必须用户主动触发（失败除外）。

**面板 UI v2（D71）**：

- **右上角两枚图标**：**置顶**（默认不置顶，点击 `HWND_TOPMOST` 并高亮，再点取消）与**关闭**。
- 🔴 **启动过程中不自动关闭**（已删除原来的 `WM_ACTIVATE` 失焦即关），只认 ✕。
- **完成后倒计时自动关闭**：全成功 10 秒 / 有失败 60 秒；**鼠标移入暂停**（`TrackMouseEvent(TME_LEAVE)` + `WM_MOUSELEAVE`），移出继续且**不重置**。
- 🔴 **完成态关闭面板（倒计时归零或手动 ✕）= 退出调度器整个应用**，托盘图标随之 `NIM_DELETE` 移除；启动中 ✕ 只 `SW_HIDE` 不退出。
- **按钮**：完成态底部两枚 `打开 DelayStart` + `运行日志`；启动中主按钮 `立即启动剩余 N 项` + 次行 `跳过剩余任务 / 运行日志`。倒计时卡只在完成态出现。
- **绘制**：分段进度条按条目着色（绿/金/灰/红）。启动中**不再有脚下提示行**（原「下一项 N 秒后启动 · 剩余 M 项」与当前项 ETA、统计行重复，D71 删除，`PanelSnapshot.FooterText` 一并移除）。
- 🔴 **刷新源**：GDI 自绘面板没有独立刷新源，重绘只在 `InvalidateRect` 时发生 —— 状态变化（`changed`）之外，启动中的实时秒数（当前项卡片右侧 ETA）由引擎 `RefreshLiveCountdown()` **按秒驱动**（节拍 250ms，用"下一项剩余秒数"做节流签名，变了才重绘）；完成态倒计时由面板自带的 1 秒 `WM_TIMER`(id=2) 驱动。漏了前者就表现为"几秒才跳一次"。

**面板双主题**：跟随管理端设置主题（自动 = 读系统 `AppsUseLightTheme`），浅/深两套调色板在绘制帧解析。

**右键菜单（两套，随状态切换）**：顶部是**灰显不可点的状态头**（`启动中 · 4/8 已启动 · 剩余 3 项` / `启动完成 · 成功 6 · 失败 1`）。启动中 → `打开 DelayStart`、`查看启动进度`、`立即启动剩余任务`、`跳过剩余任务并退出`（无等待条目时后两项置灰；跳过 = **不启动**这些条目，标 `Skipped` 落盘，在日志与时间轴里可见）。启动完毕 → `打开 DelayStart`、`查看运行日志`、**`退出`**（结束调度端进程、托盘消失；此时已无剩余条目，裸退出是安全的）。菜单与面板同名按钮的语义差异见上方入口表。菜单走 `SetForegroundWindow + TrackPopupMenu(TPM_RETURNCMD) + WM_NULL`（KB135788）。**提权门槛**：非管理员令牌（手动双击）静默退出并记日志，调度端唯一合法入口是 `RunLevel=Highest` 计划任务。

**通知约束（为什么完成通知改成面板）**：elevated 进程发不了 `AppNotification`；AOT 用不了 WinRT 通知 API；Win11 气泡不进通知中心、时长由系统控制不可控。⇒ 完成通知的载体改为**面板**（可停留、可查看失败明细、可跳转），气泡只保留给"管理端缺失 / 启动失败"这类报错。

**收尾行为基线（`CompletionPolicy` 的对照基准，D71–D73）**：

| 场景 | 期望行为 |
|---|---|
| 常规完成 · `FailuresOnly` · 全成功 · 面板未显示 | 不弹面板，**同一次 `Tick()` 内**退出 |
| 常规完成 · 面板已显示 | 切完成态 + 起倒计时，关闭后退出（不受 `NotifyMode` 支配） |
| 菜单「跳过剩余任务并退出」 | 不弹面板，`Launching` 一并标 `Skipped`，落盘归档后**立即**退出 |
| 面板「立即启动剩余 N 项」 | 剩余项立即启动，完成后**保留面板** + 起倒计时 |
| 面板「跳过剩余任务」 | 显示完成态 + 起倒计时，**不退出** |

**收尾判据已下沉为 `CompletionPolicy`**（纯函数、可单测，D73）。它接收 `QuitImmediately` / `PanelRequestedCompletion` / `PanelVisible` / `FailedCount` / `NotifyMode`，按优先级短路给出 `ExitMode`：

| 优先级 | 条件 | 结论 |
|---|---|---|
| 1 | `QuitImmediately`（菜单「跳过剩余任务并退出」） | 不等面板，立即退出 |
| 2 | `PanelRequestedCompletion`（用户在面板上点了按钮） | 等面板关闭 |
| 3 | `PanelVisible`（面板此刻已显示） | 等面板关闭 |
| 4 | `NotifyMode = Always` | 等面板关闭 |
| 5 | `NotifyMode = FailuresOnly` 且有失败 | 等面板关闭 |
| 6 | 其余 | 不弹面板，同一次 `Tick()` 内退出 |

🔴 **关键语义：`NotifyMode` 只决定"要不要主动弹面板"，不决定"已显示的面板要不要留"** —— 用户手动打开过面板就该看到结果，否则面板会在全部成功后毫无理由地自己消失（D73）；上表第 3 条就是为此而加。两条"不等面板"的路径速度也不同：`QuitImmediately` 直接 `Quit()`（不等 `Launching` 条目的 1.5 秒复查窗口）；其余情况把 `_quitAt` 拨到当下，由紧随其后的同一次 `Tick()` 判定退出（**不存在"下一拍退"这种状态**）。

**同一个动作在菜单与面板上语义不同**（D72），因此有两套入口：

| 入口 | 「立即启动剩余」 | 「跳过剩余任务」 |
|---|---|---|
| **右键菜单** | 遵循通知策略（该弹就弹、该退就退） | **不弹面板、直接退出**，且**不等** Launching 条目的 1.5 秒复查窗口 —— 正在复查的条目一并标 `Skipped`（原因文案与未执行的等待项一致），落盘 + 归档后立刻 `Quit()`。菜单文案保持 `跳过剩余任务并退出` |
| **面板按钮** | **必定留在面板上给结果**：切完成态 + 起倒计时 | 同上，切完成态 + 起倒计时（**不退出**）；且**只跳 `Waiting`**，`Launching` 照常复查出结果、留在面板上看。面板按钮文案去掉"并退出" → `跳过剩余任务` |

理由：用户已经手动弹出面板在看，点了按钮之后面板反而消失是明显倒退；而菜单那条是"不看结果直接走"。引擎用 `_panelRequestedCompletion` 实现"面板发起 → 必弹"；菜单跳过走 `Finish(quitImmediately: true)` 直接进退出分支，**不经过面板决策**，因此不需要单独的抑制标记。对应两套入口方法：`LaunchRemainingNow` / `LaunchRemainingFromPanel`、`SkipRemaining` / `SkipRemainingFromPanel`，托盘宿主构造时分别接线。

**文案矩阵（面板内）**：全部成功 `启动完成` + `全部 N 项已启动`；部分失败 `完成 · N 项失败` + `成功 X · 失败 Y`（失败明细在中部卡片列第一条）；倒计时行只写 `面板 N 秒后自动关闭`（不重复统计数字）。

**成功判定与失败策略（D17=D）**：软件不自动处理——保持接管 + 提醒持续升级（第 1/2 次可忽略；**≥3 次角标不自动消失、告警条不提供忽略**，唯一出口 `[移出延时启动]`）；连续失败次数由 `FailureStreakService` 现算。状态机 `waiting → launching → done/failed(→重试→failed 最终)`，另有 `waiting → skipped`（仅用户主动触发，不计失败）。

**跨进程状态**：调度端每次状态变化原子重写 `state/current-run.json`（含 pid）；管理端用 `Process.GetProcessById` 探测判"进行中"（D19）。点击通知 → `DelayStart.exe --goto-log --run=<runId>`。

**设置项**：托盘开关 / 通知三档 / 重试次数 / 托盘保留时长；短任务（<15s）不显示图标为固定行为。

---

## 九、编码规范

### 9.1 编译器设置

`Nullable=enable`、`TreatWarningsAsErrors=true`、`EnforceCodeStyleInBuild`、`AnalysisLevel=latest-recommended`；Core 另开 `IsAotCompatible=true`（架构护栏）。CPM 管包版本，取当前最新稳定版作声明下限。

### 9.2 文件组织与现代 C#

一文件一公开类型；file-scoped namespace；UTF-8 **无 BOM**、恰好一个末尾换行；4 空格缩进、单行语句也带大括号。集合表达式、模式匹配、`[GeneratedRegex]`（🔴 AOT 下 `new Regex` 不可靠）、record 表达值语义、默认 `sealed`、不写 public 字段；`Core.Models` 用 sealed class + init（源生成 JSON 往返需要，与 record 建议不同）。

### 9.3 错误处理与日志

禁止空 catch；`UnauthorizedAccessException` → `StartupOperationException(AccessDenied)` 且消息带条目标识（**语义是"受保护"不是"需要提权"**，UI 不提示"以管理员身份运行"）；单条目失败绝不外抛；Win32 句柄 try/finally 释放；日志经构造注入 `ILogSink`，`Warn(ex, "…")` 带异常对象。

### 9.4 P/Invoke 与 NativeAOT

`LibraryImport`（不用 `DllImport`）+ **bool 返回值必须 `[return: MarshalAs(UnmanagedType.Bool)]`**（坑 9：不报错只出错值）；`AllowUnsafeBlocks=true`；`SetLastError=true` 的失败必须 `Marshal.GetLastWin32Error()` 进异常消息；P/Invoke 按 DLL 分文件放 `Interop/`，包装后暴露；COM 互操作只允许出现在 Management。AOT 禁用清单见 7.6 与 pitfalls 五。

### 9.5 WinUI 3 约定

统一 `x:Bind`；ViewModel 用源生成器 `[ObservableProperty]`/`[RelayCommand]`；颜色字号只用资源键；code-behind 只放视图逻辑（9.5）；改系统状态的操作必须有成功/失败反馈；ViewModel 一律构造注入。提权附加约束见 7.5。⚠️ 行模型为不可变普通类时行级绑定用 OneTime；get-only 计算属性绑 OneWay 是 WMC1506 编译错误——联动用 `[NotifyPropertyChangedFor]`（详见 pitfalls 六）。

### 9.6 单元测试

xUnit v3（D21）；命名 `被测方法_场景_期望结果`（测试目录 `.editorconfig` 就近关 CA1707）；Arrange/Act/Assert 三段；条件跳过用 `Assert.SkipWhen`；不引 `Microsoft.NET.Test.Sdk`。

### 9.7 Git 与提交

Conventional Commits（type 英文 + 中文摘要），正文写"为什么"，引用 FR/NFR/D 编号；一次提交一件事；**AI 只生成 commit message，不自动 commit**（用户明示时除外）。

---

## 十、开发与交付流程

**环境**：.NET SDK 10（global.json 钉 10.0.401）、VS 2026（含 WinUI 模板包）、Inno Setup 6（per-user 布局）、中文语言文件随仓库分发（D65）。

**日常命令**：

```powershell
scripts\build.ps1          # Release 全解决方案，0 警告验收
scripts\test.ps1           # = dotnet run --project tests/DelayStart.Core.Tests -c Release（D26：dotnet test 不可用）
scripts\all.ps1            # 编译 → 测试 → 发布同步
scripts\publish.ps1 [-Rid] # AOT 发布调度端并同步进管理端 bin
```

**AOT 发布验收**：产物约 3.4–3.6MB（上限 6MB）；`scheduler.log` 首行记录启动耗时（< 0.3s）。

**打包分发**：`installer\build-all.ps1` 出 2 形态 × 2 架构 = 4 包（自包含 ~60MB / 精简 ~10MB）；单包 `installer\build-installer.ps1 -Rid win-x64 [-Slim]`。iss 参数、硬约束、验证配方见 `installer\README.md` 与 `docs/pitfalls.md` 九。CI：`.github/workflows/release.yml`，tag `v*` + 手动双触发，矩阵 fail-fast:false，arm64 前置检测工具集。

**真机手工验证清单**（要点）：软禁用前后注册表导出对比零变化；接管→重启→按预期时间启动；卸载可逆（还原失败则中止）；DPI 三档；明暗主题；安装/升级/卸载全链路（含 slim 覆盖 full 的混装回归：往安装目录丢 `hostfxr.dll` → 装 → 断言被清）。

**提交前必查**：Release 构建 0 警告 0 错误 + 测试全绿；`git status` 无构建产物；新踩的坑追加进 `docs/pitfalls.md`。
