# Agents.md — AI 编码代理入口

> 本文档是 AI 编码代理（和新人）进入本仓库的**第一站**：先建立约束意识，再动手。
>
> 文档体系：[`docs/design.md`](docs/design.md) 是**当前方案的单一事实来源**（代码与它冲突时以它为准，并立即修正另一方）；[`docs/decisions.md`](docs/decisions.md) 记录每个决策的结论与取舍；[`docs/pitfalls.md`](docs/pitfalls.md) 是踩坑大全（**写相关代码前逐条看完**）；[`docs/development.md`](docs/development.md) 面向人，讲构建/调试/打包。

---

## 〇、30 秒速览

| 项 | 内容 |
|---|---|
| 项目 | **DelayStart** —— 扫描 Windows 全部自启动位置 → 软禁用（不删数据）→ 独立轻量调度进程按延时启动 |
| 三原则 | **全部可逆**、**判定可靠**、**不替用户做决定**（违反任何一条即架构性错误） |
| 技术栈 | .NET 10 + WinUI 3（管理端，unpackaged）· 纯 Win32 + NativeAOT（调度端）· **.NET 10 + 非 AOT**（守卫端）· xUnit v3（测试） |
| 构建 | `.\scripts\build.ps1` → Release **0 警告 0 错误**（`TreatWarningsAsErrors=true`） |
| 测试 | `.\scripts\test.ps1` → **481 个用例全绿**；🔴 **不要用 `dotnet test`**（见 D26） |
| 状态 | 功能完整（含守卫、通知中转器），进安装包阶段；守卫 / D82 / **N1–N12 通知与面板拆分**均待真机验收；文档与代码同步 |

---

## 一、🔴 硬约束（违反即构建失败或发布失败）

1. **依赖方向单向**：`App → Management → Core`、`Scheduler → Core`、`LaunchBroker → Core`、`NotifyBroker → Core`、`Tests → Core + Management`。
   - `Scheduler` / `LaunchBroker` 绝不引用 `Management` —— NativeAOT 发布直接失败。
   - `Tests` 绝不引用 `App` —— 会背上 WindowsAppSDK 自包含的包袱。
2. **`DelayStart.Core` 必须 AOT 兼容**（`IsAotCompatible=true` 构建期守门）：零 COM、零反射。往 Core 放 `TaskScheduler` 包、`IShellLinkW` 之类的东西 = 打断调度端发布。
3. **调度端是纯 Win32**（D24）：不引用任何 UI 框架，托盘 / 面板全走 P/Invoke；禁止引入 WinForms / WPF。
4. **0 警告构建**：出现警告即失败，这是故意的。
5. **调度端计划任务禁止 SYSTEM**（NFR-6.8）：必须 `LogonType=Interactive` + `RunLevel=Highest`。SYSTEM 下 `%APPDATA%` 会解析到 systemprofile，配置读不到、日志写错位置**且不报错**。
6. **应用名只用英文 `DelayStart`**（D61）；中文名「延时启动管理器」仅出现在文档里。
7. **可逆优先**：任何改变系统状态的操作必须有配对的还原路径；失败必须可见，禁止静默吞异常。

---

## 二、代码地图（先找到地方，再动手）

**`src/DelayStart.Core`** —— 纯逻辑与契约，调度端与管理端共用：

| 要改什么 | 去哪 |
|---|---|
| 调度计划（过滤 / 排序 / 到点计算） | `Services/SchedulePlan.cs` |
| 收尾判据（弹不弹面板 / 退不退出） | `Services/CompletionPolicy.cs` |
| 「跳过剩余」可跳判定 | `Services/SkipPolicy.cs` |
| 同一次运行内的重试判定 | `Services/RetryPolicy.cs` |
| 延时计算（绝对时刻语义） | `Services/DelayCalculator.cs` |
| 条目稳定主键 | `Services/ItemKeyBuilder.cs` |
| 路径布局（唯一来源，禁止硬编码） | `Services/PathService.cs` |
| 配置读写与迁移 | `Services/ConfigService.cs` + `Models/AppConfig.cs` |
| 实时状态 / 运行归档 | `Services/RunStateService.cs` |
| 失败连击计算 | `Services/FailureStreakService.cs` |
| JSON 源生成 | `Serialization/JsonContext.cs` |
| 日志落地 | `Logging/FileLogger.cs` |
| 启动结果判定 | `Services/LaunchResultEvaluator.cs` + `Launch/` |
| uiAccess 作业 / 结果契约 | `Launch/BrokerContracts.cs` |
| 目标 → 进程名推导（防双启动） | `Services/LaunchTargetResolver.cs` |
| "同目标已在跑 ⇒ 跳过"判定 | `Services/DuplicateLaunchPolicy.cs` |
| 守卫写回纠正判据 | `Services/GuardCorrectionPolicy.cs` |
| 守卫新增检测（基线差集） | `Services/GuardNewItemPolicy.cs` |
| 守卫失效 / 孤儿检测 | `Services/GuardStalePolicy.cs` |
| 守卫触发规则（档位 → 触发器） | `Services/GuardSchedulePlan.cs` |
| 提权自检（供守卫入口 / 调度端） | `Services/ElevationCheck.cs` |
| 跨进程启动参数 / 协议常量（CLI token、`delaystart://`） | `Launch/AppActivation.cs`（🔴 守卫与管理端**共用**，改一处即改契约） |

**`src/DelayStart.Management`** —— 一切 COM / 系统 API / 计划任务：

| 要改什么 | 去哪 |
|---|---|
| 扫描编排（逐源 try/catch） | `Services/ScanService.cs` |
| 某个来源的扫描 / 禁用 / 启用 | `Sources/RegistryStartupSource.cs` · `StartupFolderSource.cs` · `ScheduledTaskSource.cs` · `UwpStartupSource.cs`（🔴 计划任务那个**不带** `Startup` 后缀） |
| 来源实例工厂（管理端与守卫共用的七个实例） | `Services/StartupSourceFactory.cs` |
| 接管与还原（事务性） | `Services/TakeoverService.cs` |
| 软禁用标记读写（三级回退） | `Services/StartupApprovedStore.cs` |
| 计划任务注册 / 更新 | `Services/TaskRegistrationService.cs` |
| 计划任务自检自愈（每次启动） | `Services/SchedulerTaskBootstrap.cs` |
| `.lnk` 解析 / 图标提取 | `Services/ShellLinkResolver.cs` · `Services/IconProvider.cs` |
| UWP TaskId → AppId | `Services/UwpAppIdResolver.cs` |
| 系统启动项检查（服务 / 驱动 / Winlogon） | `Services/SystemStartupInspector.cs` |
| 守卫巡检编排（扫描 → 纠正 → 检测 → 基线） | `Services/GuardService.cs` |
| 守卫基线读写（原子写） | `Services/GuardBaselineStore.cs` |
| 守卫任务注册 / 更新 / 删除 | `Services/GuardTaskRegistrar.cs` |
| 守卫任务自检自愈（每次启动） | `Services/GuardTaskBootstrap.cs` |
| 从配置移除 / 转为手动（清理失效项，**不动系统**） | `Services/ConfigEditService.cs`（`Remove` / `ConvertToManual`） |
| 跨进程 UI 定位请求文件（读后即删） | `Services/UiRequestChannel.cs` |
| 系统通知的身份登记（开始菜单 AUMID + `delaystart:` 协议） | `Services/ShellRegistrationService.cs`（🔴 幂等、**绝不抛异常**） |
| 快捷方式属性存储互操作（`IPropertyStore` / `PropVariant`） | `Interop/PropertyStoreInterfaces.cs`（🔴 `PropVariant` 是 `Explicit` 布局，`PropVariantClear` 必须调） |

**`src/DelayStart.Scheduler`** —— 调度端（NativeAOT，无 DI，手工构造）：

| 要改什么 | 去哪 |
|---|---|
| 调度循环 / 时序 / 收尾 / 落盘 | `SchedulerEngine.cs` |
| 进行中状态（条目运行时状态） | `SchedulerRuntimeItem.cs` |
| 托盘图标与右键菜单 | `TrayIconHost.cs` |
| 面板绘制（GDI 自绘，「钉」+ 失焦关闭） | `PanelWindow.cs` |
| 降权启动（外壳令牌 → CPWT；含 `LaunchAuxiliary` 通用降权拉起入口） | `DeElevatedProcessLauncher.cs` |
| Win32 声明（🔴 注意模块归属） | `NativeMethods.cs` |
| 图标资源按尺寸取用 | `IconResources.cs` |

**`src/DelayStart.Guard`** —— 守卫端（D74，**非 AOT**、无 DI、手工构造、**无 `app.manifest`**）：

| 要改什么 | 去哪 |
|---|---|
| 入口自检 / 巡检编排 / 通知文案（标题·摘要·条目名）/ 落点选择 | `Program.cs` |
| 系统通知（Toast XML + `Tag`/`Group` + AUMID） | `GuardToast.cs` |
| 日志（`guard.log`） | `GuardLog.cs` |

🔴 通报载体是**系统通知**（D79），不是原生弹框 —— 原来的 `NoticeDialog.cs` / `NativeMethods.cs` / `app.manifest` 已整体删除。要动这块先读 `decisions.md` D79/D80 与 `pitfalls.md` 十一的 AUMID 条目。

🔴 **守卫 TFM 带平台版本**（`net10.0-windows10.0.26100.0`，发 WinRT 通知要投影），产物在 `bin\<Cfg>\net10.0-windows10.0.26100.0\<rid>\` —— 比调度端（`net10.0-windows\<rid>\`）多一段，搬产物别照抄调度端路径。

**`src/DelayStart.LaunchBroker`** —— `Program.cs`：uiAccess 目标的降权中转器（D70）。

**`src/DelayStart.NotifyBroker`** —— `Program.cs`：通知中转器（N1/D83，**非 AOT**、只引用 Core、无 `app.manifest`）。读调度端写的一次性作业 JSON（`NotifyToastJob`，schema 在 `Core` 的 `BrokerJsonContext`）→ 发系统通知（Tag=`schedule-done`，点击落 `delaystart://runs-log`）→ 驻留 ~500ms 退出。TFM 带平台版本（同守卫），产物在 `bin\<Cfg>\net10.0-windows10.0.26100.0\<rid>\`。

**`src/DelayStart.App`** —— 管理端：页面 `Views/*.xaml`、视图模型 `ViewModels/*.cs`、组合根 `Services/ServiceRegistration.cs`（**CLI 与 GUI 共用一个容器，容器在 CLI 分流之前构建**）、跨进程定位 `Services/UiTargetNavigation.cs`、对话框 `Dialogs/DelayEditorDialog.xaml`、提权相关互操作 `Interop/`（含 `InstanceProbe.cs`：跨完整性级别的"实例还活着吗"探测，D82）。

> 🔴 **没有独立的「失效条目」页面**（D81）：失效行内联在 `Views/DelayPage.xaml`，由 `ViewModels/DelayRow.cs` 的 `IsNormal` / `IsStale` / `CanConvertToManual` 三个 bool 驱动显隐（`x:Bind` 不支持 `!` 取反，别在 XAML 里做转换器）。

**`tests/DelayStart.Core.Tests`** —— 32 个测试类 + `Fakes/` 假实现。🔴 **单元测试禁止触碰真实注册表 / 文件系统 / 进程**，全部注入假实现。⚠️ 跑测试用 `dotnet run --project tests/DelayStart.Core.Tests -c Release`，**`dotnet test` 在此工程下报"零个测试"**（MTP 自执行形态）。

---

## 三、命令速查

```powershell
.\scripts\build.ps1        # 编译全解决方案（Release，0 警告验收）
.\scripts\test.ps1         # 单元测试（D26 约定：dotnet run，不用 dotnet test）
.\scripts\publish.ps1      # 发布四个 exe（调度端 / 中转器走 AOT publish，守卫 / 管理端走 build）+ 把三个同级 exe 同步进管理端 bin
.\scripts\all.ps1          # 一条龙：build → test → publish
.\installer\build-all.ps1  # 安装包矩阵（自包含 / 精简 × x64 / arm64）
```

- 手动等价：`dotnet build DelayStart.slnx -c Release` / `dotnet run --project tests/DelayStart.Core.Tests -c Release`
- 🔴 排查 XAML 编译问题必须 `dotnet clean` + `--no-incremental` —— obj 里的 `.g.cs` 增量缓存会让"改了没生效"和"真的没生效"看起来一样。
- 🔴 构建前先关掉正在运行的 `DelayStart.exe`，否则报 `MSB3021/3027`。
- **验收口径**：Release 0 警告 0 错误 + 481 个用例全绿。

---

## 四、改代码前必读（按任务类型）

| 你要做的事 | 先读这些 |
|---|---|
| 动调度端时序 / 收尾 / 面板 | `design.md` 八 · `decisions.md` D71–D73 · `pitfalls.md` 二（倒计时与退出耦合） |
| 动扫描 / 接管 / 计划任务 | `design.md` 二、七.3–7.4 · `pitfalls.md` 一、二（键名回退 / 禁用粒度 / 身份） |
| 写 P/Invoke | `design.md` 9.4 · `pitfalls.md` 五（模块归属 / bool 封送） |
| 改 XAML 或 ViewModel | `design.md` 9.5 · `pitfalls.md` 六（WMC1506 / pass-1 崩溃） |
| 改安装器或 iss | `pitfalls.md` 九 · `installer/README.md` |
| 新增 NuGet 包 | 硬约束 2 · `pitfalls.md` 五（先发一次 AOT 看 IL2026/IL3050） |
| 动启动链路 / 降权 / 唤起 | `design.md` 7.3 机制 6 / 6a · 六（权限模型）· `decisions.md` D40、D70、**D82** · `pitfalls.md` 十二（跨完整性级别：文件当信号 / 只读探测） |
| 动守卫（Guard） | `design.md` 十一 · `decisions.md` D74–D82 · `pitfalls.md` 十一（写回行为模式 / **AUMID 与系统通知** / bin 四件套同步 / `--goto-*` 必须排除在 CLI 分流之外） |
| 动调度收尾 / 通知 / 面板 | `design.md` 八 + FR-14（**FR-14.1 = N1–N12 锚点表**）· `decisions.md` D71–D73（历史）、**D83**（通知与面板分离 + NotifyBroker） |
| 改任何 `app.manifest` | `pitfalls.md` 四（🔴 注释里出现**连续两个减号** = `mt.exe` 报 c1010070 且不指位置）· 十一（emoji / 非 BMP 字符 = 加载期激活上下文失败）· `decisions.md` D82；🔴 改完**必须真启动一次 exe**，构建绿与单测绿都照不到。⚠️ 守卫已无清单（D79），只剩管理端与调度端 |
| 改文档 | 见下方「文档地图」的更新义务 |

---

## 五、编号体系（可追溯性）

代码注释、提交信息、测试用例都用这套编号互相引用：

| 前缀 | 含义 | 定义处 | 举例 |
|---|---|---|---|
| `FR-x.y` / `NFR-x.y` | 功能 / 非功能需求 | `docs/design.md` 三、四 | `FR-5.3`、`NFR-1.2` |
| `E-x` | 异常场景矩阵 | `docs/design.md` 五 | `E13` |
| `R-x` | 技术风险（已全部闭环） | `docs/decisions.md` 附表 | `R11` |
| `D-x` | 决策点（D1–D83） | `docs/decisions.md` | `D17` |
| `坑 x` | 技术陷阱（编号 1–10 沿用） | `docs/pitfalls.md` | `坑 1` 键名三级回退 |

规则：实现某 `FR` 时在代码注释里引用它；修某 `E` 场景时在提交信息里引用它；**新踩的坑必须追加进 `pitfalls.md`**。

---

## 六、文档地图与更新义务

| 文档 | 回答什么问题 | 什么时候必须改 |
|---|---|---|
| `docs/design.md` | 当前方案单一来源：需求（FR/NFR/E）、架构与关键机制、调度端交互、编码规范、开发流程 | 需求 / 机制 / 交互行为发生变化 |
| `docs/decisions.md` | 每个决策的结论与取舍（D1–D82）+ R1–R13 风险去向 | 出现新的取舍（追加编号），或推翻旧决策（并入取代它的条目，**编号保留**） |
| `docs/pitfalls.md` | 技术陷阱：Win32 / 注册表 / 计划任务 / UWP / 降权 / AOT / WinUI 3 / 安装器 | 踩到新坑，或旧坑被修掉 / 定性变化 |
| `docs/development.md` | 面向人：环境、构建测试、调试、发布打包、真机验收 | 环境要求 / 命令 / 流程变化 |
| `README.md` | 面向用户：项目介绍、安装、快速上手、FAQ | 用户可见行为或安装方式变化 |

---

## 七、协作约定

- 提交遵循 Conventional Commits（type 英文 + 中文摘要），正文写"为什么"，引用 FR/NFR/D 编号；**一次提交一个逻辑变更**。
- 🔴 **AI 只生成 commit message，不自动 commit**（用户明确指示时除外）。
- 行尾统一 LF（见 `.gitattributes`），`.sln` 例外 CRLF；源文件 UTF-8 **无 BOM**、恰好一个末尾换行。
- UI 文案改动**不需要**维护独立文案副本（design-spec / 原型 HTML 已删除，代码即事实）。
- 编码规范细节见 `design.md` 九；测试命名 `被测方法_场景_期望结果`。
- 构建 / 测试产物（`bin` / `obj` / `artifacts` / `TestResults`）不入库。

---

## 八、不要做什么（反模式清单）

以下每一条都是踩过的坑，别重犯：

- ❌ 让 `Scheduler` / `LaunchBroker` 引用 `Management`，或往 `Core` 放 COM / 反射 / 计划任务包。
- ❌ 删除注册表原始值、移动用户文件 —— 只能写软禁用标记。
- ❌ 降权链失败后"提权回退"重试（必须判该条目失败并继续）。
- ❌ 用 `(Name, Source)` 二元组判断"是不是同一项" → 用 `ItemKeyBuilder` 三元组。
- ❌ 用字符串猜 registry hive → `StartupScope` 必须显式枚举。
- ❌ 对非 INPC 属性写 `Mode=OneWay`，或在 `x:Bind` 里写三元表达式 / `bool→Visibility`。
- ❌ 空 `catch`、静默吞异常、失败后不写日志。
- ❌ 让单元测试碰真实注册表 / 文件系统 / 进程，或让 `Tests` 引用 `App`。
- ❌ 给 Core / Scheduler 设 `InvariantGlobalization`（计划任务注册必崩）。
- ❌ 用 `dotnet test`（报"零个测试"）、或在 XAML 排查时用增量构建下结论。
- ❌ 安装器形态用 `bin` 代替真实安装目录验收（**bin 能跑 ≠ publish 能跑**）。
- ❌ 在 `app.manifest` 里写 emoji / 非 BMP 字符（🔴 之类）—— SxS 解析器判成非法 XML，**进程直接起不来**且构建/单测全绿（`pitfalls.md` 十一）。⚠️ 守卫已无清单（D79），这条现在只针对管理端 / 调度端。
- ❌ 在 `app.manifest` 的**注释里**写连续两个减号（`--goto-log` 这种字面量）—— `mt.exe` 报 `c1010070 Failed to load and parse the manifest` 且**不指出行号**，看起来像清单整体坏了（D82 实测的唯一成因；emoji 在构建期反而是安全的，见 `pitfalls.md` 四）。
- ❌ 把管理端的提权门放到 CLI / GUI 分流**之后**，或忘了先摘掉 `--elevation-attempted` 再交给 `CliHost` —— 前者让业务以非提权身份运行（违反 D20），后者以连续两个减号开头会被判成未知子命令（exit 2、界面起不来）。顺序：认领定位参数 → 提权门 → CLI → GUI（D82）。
- ❌ 让 `--goto-startup` / `--goto-log` / `--stale` 走到 `CliHost`（它们以 `--` 开头会被当成未知子命令 → exit 2、GUI 从不启动）。
- ❌ 只把 `DelayStart.Guard.exe` 拷进 App bin 就算部署（非 AOT，必须 exe+dll+runtimeconfig+deps 四件套）。
- ❌ 让 `publish.ps1` 里 `build Guard` / `build NotifyBroker` 排在 `build App` 之后（同步钩子是"拉"式，顺序反了 App bin 里就没有对应 exe）。
- ❌ 在收尾判定（`CompletionPolicy`）里读 `NotifyMode`，或让收尾自动弹面板 —— D83 起"面板是面板，通知是通知"：通知归 `NotifyDecision`，面板只跟随用户的手（N4）。
- ❌ 给 `NotifyBroker` 开 AOT / 让它引用 `Management` / 让它自行注册 AUMID —— 它是"读作业 JSON → 发通知 → 退出"的哑进程（N1/D83）。
