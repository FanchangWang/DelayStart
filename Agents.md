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
| 测试 | `.\scripts\test.ps1` → **643 个用例全绿**；🔴 **不要用 `dotnet test`**（见 D26） |
| 状态 | 功能完整（含守卫、通知中转器、FR-15 调度周期），进安装包阶段；守卫 / D82 / **N1–N12 通知与面板拆分** / **FR-15 真机验收**均待真机验收；文档与代码同步 |

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
8. **离线可判定（NFR-x）**：`Scheduler` / `Guard` / `LaunchBroker` / `NotifyBroker` **零网络代码**；节假日数据的下载只允许出现在 `DelayStart.Management`（`HolidayCalendarUpdateService`），且只在用户主动触发、或设置页「自动检查更新」开着且当年数据缺失时发生。判定输入只能来自本地文件。

---

## 二、代码地图（先找到地方，再动手）

**`src/DelayStart.Core`** —— 纯逻辑与契约，调度端与管理端共用：

| 要改什么 | 去哪 |
|---|---|
| 调度计划（过滤 / 排序 / 到点计算） | `Services/SchedulePlan.cs`（🔴 `BuildWithSkipped` 才是完整判定：`Entries` = 计划、`SkippedToday` = 今天不在周期内的启用条目，调度端靠后者写运行日志 FR-15.26；`Build` = 取 `.Entries`） |
| 周期 id → （种类 + 星期）解析 | `Services/ScheduleCycleResolver.cs`（内置 5 档与自定义档的唯一解析点） |
| 「今天跑不跑」判定 + 降级标志 | `Services/ScheduleRulePolicy.cs`（🔴 管理端与调度端**同一份**，管理端绝不自算） |
| 星期位掩码（周一 = bit0） | `Models/WeekdaySet.cs` + `WeekdaySets`（🔴 与 `DayOfWeek` 的换算只走这里） |
| 周期模型 / 内置周期 id | `Models/ScheduleCycle.cs`（`BuiltinCycleIds` 含展示顺序 `Ordered`） |
| 内置周期**名称** + 周期名判重判据 | `Models/CycleNames.cs`（🔴 界面与落盘**共用同一份** `IsTaken`；管理端也要拦"与内置档同名"，所以它必须住在 Core，不能留在界面层） |
| 法定日历模型 | `Models/HolidayCalendar.cs`（`IsWorkday` 在未覆盖年份**抛异常**，调用方必须降级） |
| 法定日历读写与校验 | `Services/HolidayCalendarStore.cs`（`MinimumEntryCount = 20`） |
| 节假日归一化落盘格式 | `Serialization/HolidayCalendarDocument.cs` + `HolidayJsonContext` |
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
| 节假日下载与归一化写盘 | `Services/HolidayCalendarUpdateService.cs`（🔴 **全仓库唯一联网的地方**，三地址降级：raw.githubusercontent → fastly.jsdelivr → cdn.jsdelivr；只负责"取"，不负责"转"） |
| 自动检查的节流判据 + "上次检查"记录文件格式 | `Services/HolidayCheckThrottle.cs`（🔴 **三档**：`Updated` 的安静期以"数据仍在可用位置"为前提 —— 记录说已获取而本地没有可用数据时**立即重来**；上游未公布 7 天；失败 1 小时。节流的对象是"重复的无效开销"，不是"用户想要的功能"；旧单行时间戳按"失败"、中间那代的 `ok` 按"已获取"兼容） |
| **节假日格式识别与归一化**（下载与导入共用） | `Serialization/HolidaySourceConverter.cs` —— 自动识别 `holiday-cn` 原始格式（`days`）与本地归一化格式（`workdays`/`restDays`）；🔴 **改这里会同时改变下载与导入的行为**，两处都是它的调用方 |
| 第三方数据源 schema | `Serialization/HolidaySource.cs` + `HolidaySourceJsonContext.cs` |
| 周期级编辑（新建 / 改名改星期 / 删除 / 把条目改到别的周期） | `Services/ConfigEditService.cs` 的 `AddCycle` / `UpdateCycle` / `DeleteCycle` / `SetItemCycle`（🔴 引用完整性校验只在这里一份） |
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

环节 | 去哪
|---|---|
| 周期目录（读 / 增改删 / 数引用 / 日历快照） | `Services/CycleCatalogService.cs`（🔴 写操作全部转交 `ConfigEditService`） |
| 周期展示文案与"今天跳不跳"（徽标 / tooltip / 包含哪些天） | `Services/CycleInfoProvider.cs`（🔴 判定复用 Core，界面绝不自算；`DaysText` 做「每天」简写、给列表用；`DaysListText` **不简写**、给弹窗"包含哪些天"用） |
| 启动后的节假日自动检查（只查当年；节流三档见 `HolidayCheckThrottle`） | `Services/HolidayAutoCheckService.cs`（`RunIfDueAsync` 启动用、`RunNowAsync` 开关打开时用；`Describe()` 是设置页副标题的唯一文案来源，三档结局各说各的） |
| 节假日更新的共享可见状态（进度 / 上次结果，单例 + INPC + 切回 UI 线程） | `Services/HolidayUpdateStatus.cs`（🔴 设置页与自动检查**共用同一条进度线**；`x:Bind` 直接绑它，不再往 ViewModel 里抄一份） |
| 七宫格 / 周期表单（两个宿主共用同一控件） | `Controls/WeekdayGrid.cs`（⚠️ **只在** `CycleEditorForm` 的可编辑形态里用 —— 拿它的只读形态做"纯展示"会变成假交互，见 `pitfalls.md` 十四）· `Controls/CycleEditorForm.cs` |
| 打开 / 另存为对话框（提权进程里 WinRT 选择器打不开） | `Interop/Win32FilePicker.cs`（`PickFile` / `PickSaveFile`） |
| 列表徽标 + 「今⊘」跳过标记 | `Views/DelayPage.xaml` 程序名行 + `ViewModels/DelayRow.cs` 的 `CycleText` / `IsSkippedToday` / `SkipToolTip` |
| 延时 / 周期 / 节假日设置 | `Views/SettingsPage.xaml` 的「延时」「周期」「节假日数据」三节 + `ViewModels/SettingsViewModel.cs`（`PresetRow` / `CycleRow` / `HolidayYearRow`）。🔴 「延时」「周期」两节是**可展开列表**（节头一张边框、展开后子内容左右各内收 16，默认收起）：**节头整行是一个透明 `Button`（`SectionHeaderButtonStyle`），右端三角只是 `FontIcon`** —— 装饰性图标不许用 `ToggleButton`（看起来就是颗按钮），整行可点也就不需要"回写按钮选中态"那层补丁。展开后**第一条固定是「添加」行**（左描述 + 右按钮），第二条起才是数据行。延时行 = `DisplayText.LogonDelayOf`（「登录后 2 分 30 秒（150 秒）」）+ 「设为默认」/绿色「当前默认」/「删除」；周期行里**内置 5 档与自定义周期共用 `CycleRow`**（`IsBuiltin` / `CanEdit` / `CanDelete` 是唯一判据，XAML 不重算），「包含哪些天」走 `CycleInfoProvider.DaysListText`（**不简写「每天」**；列表徽标那边仍用简写，同一份数据两种宽度） |

> 🔴 **「新建 / 编辑周期」面板有两副宿主**：设置页用真 `ContentDialog`，延时编辑弹窗里用同层 Overlay（`DelayEditorDialog.xaml` 的 `CycleEditOverlay`）—— **ContentDialog 之上不能叠第二个 ContentDialog**。两处的面板内容是同一个类（`CycleEditorForm`），改一处必须同时验证两处。

> 🔴 **设置页底部 `InfoBar` 的 `IsOpen` 绑的是 `SettingsViewModel.HasError`（`StatusText` 的派生属性）**：动 `StatusText` 时必须确认 `OnStatusTextChanged` 里的通知还在 —— 少了它，整页所有错误提示静默（2026-09-23 真机踩过，见 `pitfalls.md` 十四）。

> 🔴 **`stc:SettingsCard` 的 `Content` 区取不到 `DataContext`**：该区域内的行内按钮一律 `Tag="{x:Bind}"` 把行对象带上，不要直接读 `sender.DataContext`（`Views/SettingsPage.xaml.cs` 的 `RowOf` 是既有写法）。

**`tests/DelayStart.Core.Tests`** —— 45 个测试类 + `Fakes/` 假实现 + `RealCalendar2026`（夹具）。🔴 **单元测试禁止触碰真实注册表 / 文件系统 / 进程**，全部注入假实现。⚠️ 跑测试用 `dotnet run --project tests/DelayStart.Core.Tests -c Release`，**`dotnet test` 在此工程下报"零个测试"**（MTP 自执行形态）。

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
- 🔴 **exe 在哪**（2026-09-23 澄清，防"脚本编译不出 exe"式误判）：开发期双击的就是 `src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\DelayStart.exe`（`publish.ps1` 结尾与产物核对清单现在都会打出它）。**`publish.ps1` 不产出 App 的 publish 目录** —— 可分发目录与安装包归 `installer\build-installer.ps1`（`artifacts\publish\{rid}\{full|slim}\` + `dist\DelayStart-Setup-*.exe`）。两套脚本职责不要混。
- 🔴 排查 XAML 编译问题必须 `dotnet clean` + `--no-incremental` —— obj 里的 `.g.cs` 增量缓存会让"改了没生效"和"真的没生效"看起来一样。
- 🔴 构建前先关掉正在运行的 `DelayStart.exe`，否则报 `MSB3021/3027`。
- **验收口径**：Release 0 警告 0 错误 + 643 个用例全绿。

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
| `D-x` | 决策点（D1–D97） | `docs/decisions.md` | `D17` |
| `坑 x` | 技术陷阱（编号 1–10 沿用） | `docs/pitfalls.md` | `坑 1` 键名三级回退 |

规则：实现某 `FR` 时在代码注释里引用它；修某 `E` 场景时在提交信息里引用它；**新踩的坑必须追加进 `pitfalls.md`**。

---

## 六、文档地图与更新义务

| 文档 | 回答什么问题 | 什么时候必须改 |
|---|---|---|
| `docs/design.md` | 当前方案单一来源：需求（FR/NFR/E）、架构与关键机制、调度端交互、编码规范、开发流程 | 需求 / 机制 / 交互行为发生变化 |
| `docs/decisions.md` | 每个决策的结论与取舍（D1–D97）+ R1–R13 风险去向 | 出现新的取舍（追加编号），或推翻旧决策（并入取代它的条目，**编号保留**） |
| `docs/pitfalls.md` | 技术陷阱：Win32 / 注册表 / 计划任务 / UWP / 降权 / AOT / WinUI 3 / 安装器 | 踩到新坑，或旧坑被修掉 / 定性变化 |
| `docs/development.md` | 面向人：环境、构建测试、调试、发布打包、真机验收 | 环境要求 / 命令 / 流程变化 |
| `README.md` | 面向用户：项目介绍、安装、快速上手、FAQ | 用户可见行为或安装方式变化 |

> 🔴 `docs/schedule-rule-plan.md`（FR-15 方案稿）与 `docs/ui/schedule-cycle-ui.html`（交互原型）是**评审期留档，不入库**（`.gitignore` 已挡）—— 内容已并入 `design.md` FR-15 与 `decisions.md` D87–D97 / 附录 A。**代码注释不要再引用这两个文件**。

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
- ❌ 对非 INPC 属性写 `Mode=OneWay`，或在 `x:Bind` 里写三元表达式。
  - ⚠️ **`bool → Visibility` 是可行的**（`x:Bind` 编译期自动转换）：`RunsPage` 的 `IsLoading`、`SettingsPage` 两节的展开状态都在这么用。此前这条一并禁掉了它，2026-09-23 复核为**过期表述**，已收窄 —— 前提仍是属性本身有通知（`[ObservableProperty]` 或手工 `OnPropertyChanged`）。
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
- ❌ 把 shell 的 COM coclass 强转成"看着像"的派生接口（`FileSaveDialog` → `IFileOpenDialog`）—— 两者是**平行**接口（都只继承 `IFileDialog`），QueryInterface 直接回 `E_NOINTERFACE`，CLR 抛的 `InvalidCastException` 在事件处理器里没人接就**整个进程静默消失**（2026-09-23"导出点击闪退"的根因）。要 cast 就 cast 到两个 coclass 的共同基接口（对话框 = `IFileDialog`），见 `pitfalls.md` 十五。
- ❌ 在"多地址 / 多策略重试"的循环里把**超时**当终局 `return`（`HttpRequestException` 与空内容都 `continue` 了，偏偏最常见的那种没降级）—— 国内访问 `raw.githubusercontent.com` 稳定 15 秒超时，而两个 jsdelivr 镜像 1.1/1.5 秒返回 200，一个 `return` 就让整条降级链失效（2026-09-23"开关开着却什么都没有"的根因之一，`pitfalls.md` 十七）。
- ❌ 让"上次尝试时间"单独当节流键，或让后台任务不往共享状态上报 —— 前者把一次网络抖动放大成静默 7 天（三档见 `HolidayCheckThrottle`），后者让用户问不出"它到底跑没跑"（`Agents.md` 硬约束 8 配套，`pitfalls.md` 十七）。
- ❌ 把"已获取"与"上游未公布"合并成一个"成功"结局 —— 那样节流就表达不了**"记录说数据已写好、而它现在不在"**（文件被改名 / 删除 / 损坏），7 天安静期会退化成永久静默：用户看到"开关是开的却没反应"，年份行还说"未下载"（2026-09-23 第二次真机，`pitfalls.md` 十七）。
- ❌ 把"有条件地什么都不做"做成**日志空白**（周期不匹配 = 不上报、不统计、不通知）—— 功能正确但行为不可解释：用户周六登录看到一片安静，运行日志里连一行都没有。现在跳过的条目写 `Skipped` + 「今天不在启动周期内，未启动」，**全部跳过时也写一份归档 + `current-run.json`**（FR-15.26 / D89 / `pitfalls.md` 十八）。⚠️ 同时：`_items`（运行侧）与 `_record.Items`（日志侧）别再靠下标对齐，先建局部 `planned` 列表两边各自引用。
- ❌ 用 `DayOfWeek` 的数值直接做星期位运算 —— `WeekdaySet` 是**周一 = bit0**，而 .NET 的 `DayOfWeek.Sunday == 0`，两者**不一致**；"位错位一天"会在每个星期上都成立且**不会报错**，只能靠单测锁（`WeekdaySets` 是唯一换算处）。
- ❌ 让**引用失效**的周期兜底成"不启动"，或让**该年无节假日数据**兜底成"不启动" —— 兜底方向**只能更宽松，绝不更严格**：静默停摆是最坏的一类失败（"为什么今天没启动"是所有 bug 里最难查的），宁可近似 + 把近似说破（D87 / D90）。
- ❌ 拿 `WeekdayGrid` 的**只读形态**当"纯展示"控件 —— 它看起来能点、点下去什么都不变（假交互比没有交互更糟）；延时弹窗里"包含哪些天"必须是一行**纯文字**（FR-15.23 / `pitfalls.md` 十四）。
- ❌ 在收尾判定（`CompletionPolicy`）里读 `NotifyMode`，或让收尾自动弹面板 —— D83 起"面板是面板，通知是通知"：通知归 `NotifyDecision`，面板只跟随用户的手（N4）。
- ❌ 给 `NotifyBroker` 开 AOT / 让它引用 `Management` / 让它自行注册 AUMID —— 它是"读作业 JSON → 发通知 → 退出"的哑进程（N1/D83）。
- ❌ 给状态文字加 `✓` `✗` `◌` `✅` 这类前缀去"凑等宽" —— 它们**不在同一套度量里**（`✓`/`✗` 约 1 em，`◌` 更窄，emoji 是彩色、更宽、还会被 fallback 到 Segoe UI Emoji），逐字符试探字宽是个填不满的坑。状态列一律**纯中文**、差别用**颜色**表达（D98 / `pitfalls.md` 十九）。
- ❌ 靠"给字符塞空格 / 换个更宽的符号"去让两种形态看起来一样宽 —— 对齐靠**布局给位置**，不是靠字数。同一行里"按钮 ↔ 文字"会互换的那一格，用**固定列宽**先把位置占住（`设为默认` 按钮 / `当前默认` 文字共用中间一个 88px 列），最右那列才永远对得齐（2026-09-23 批复 24 / D102）。
- ❌ 用 `ToggleButton` 去当展开/折叠的**装饰性三角** —— 它自带背景、边框和悬停态，看起来就是一颗按钮，用户点之前得先猜"这颗按钮干什么"，而它真正的作用只是指示符。装饰性图标用 `FontIcon`，把展开/收起交给包住**整行**的 `Button`（`SettingsPage` 的 `SectionHeaderButtonStyle`，卡面由按钮自己画，见下一条）：点哪儿都在同一件事上，也就不需要"回写按钮选中态"那层补丁（2026-09-23 批复 24 / D101）。
- ❌ 给"整行可点"的元素**外面再套一层带 `Padding` 的容器**（`Border` 里塞 `Button`）—— 按钮的悬停高亮只会铺在容器的内边距以内（四周各让出 16 / 14、圆角还比卡片小一圈），用户看到的是"**文字那一小块变色了**"、整张卡片毫无反应，而他要的正相反：鼠标在哪、哪个面就亮。要整行可点就让**按钮自己画那个面** —— 卡面 / 描边 / 圆角 / 内边距全部写在按钮的 `Style` 上，`BasedOn="{StaticResource DefaultButtonStyle}"` 保住悬停 / 按下 / 禁用三态（2026-09-23 批复 25 / D104；`DelayPage.RowButtonStyle` 是同一手法）。
- ❌ 手算列头 `Padding` 时把**卡片 `Border`** 和**行容器 `ListViewItem`** 的内缩也算进去 —— 列头与行之间只隔着一层容器：卡片那圈是两者**共同**吃掉的，容器那圈列头根本不在里面。多算一层列头就比行多缩 8，同一列上下两个起点（2026-09-23 批复 26 / D105）。规则：行容器 `Padding` 钉死 **0**，列头 `Padding` 的左右 = **行 `Grid` 的左右**（延时启动页是例外：列头在分组卡片**外**，= 内层卡片 8 + 行 20 = 28）。
- ❌ 新加 `ListView` 时**忘挂 `ItemContainerStyle`** —— 它会静默落到 WinUI 默认 `ListViewItem`（`Padding 12,0`、`MinHeight 40`），行比列头多缩 12、每行还高出 40，**没有任何编译期提示**（总览小表的实测形态，2026-09-23 批复 26 / D105）。列表页的固定顺序：先挂行样式，再按「行 `Grid` 的 `Padding`」写列头。
