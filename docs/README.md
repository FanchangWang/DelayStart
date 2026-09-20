# DelayStart 文档索引

> 本目录是项目的**唯一事实来源**。代码与文档冲突时，以文档为准，并立即修正代码或更新文档——不允许两边长期不一致。

---

## 文档清单

| 文档 | 回答什么问题 | 受众 |
|---|---|---|
| **`requirements.md`** | **做什么、为什么做、做到什么程度算完成** | 所有人 |
| **`architecture.md`** | 代码怎么搭：项目结构、分层、数据模型、关键机制 | 开发者 |
| **`design-spec.md`** | 界面长什么样、每处文案是什么 | 开发者、评审 |
| **`scheduler-design.md`** | 调度端（登录后那个轻量进程）的交互与技术设计 | 开发者、评审 |
| **`coding-standards.md`** | 代码怎么写：命名、风格、错误处理、AOT 约束、提交规范 | 开发者 |
| **`build-and-test.md`** | 怎么编译、怎么跑、怎么测、本机有哪些坑 | 开发者 |
| **`api-analysis.md`** | Win32 / 注册表 / 计划任务 / UWP 的具体调用方式与 9 个坑 | 开发者 |
| **`changelog.md`** | 设计阶段每轮改了什么、决策落在哪个章节 | 所有人 |

---

## 按目的找文档

| 你要做的事 | 先读 |
|---|---|
| 了解这个软件是干什么的 | `requirements.md` 第一、二节 |
| 开始写代码 | `architecture.md` → `coding-standards.md` → `build-and-test.md` |
| 改界面 / 改文案 | `design-spec.md`（**文案改动必须同步此文档**） |
| 动调度端 | `scheduler-design.md`（先读它的技术约束章节，否则会写出发布不了的代码） |
| 写注册表 / 计划任务相关代码 | `api-analysis.md` 的坑清单**必须逐条看完** |
| 查"某条决策是在哪落地的" | `changelog.md` 的落点索引 |
| 准备提交 | `build-and-test.md` 第十二节 |
| 准备发版 | `requirements.md` 第九节验收清单 |

---

## 需求编号体系

代码注释、提交信息、测试用例都用这套编号互相引用，保证可追溯。

| 前缀 | 含义 | 举例 |
|---|---|---|
| `FR-x.y` | 功能性需求 | `FR-2.3` 禁用标记写入正确位置 |
| `NFR-x.y` | 非功能性需求 | `NFR-1.2` 调度端冷启动 < 0.3 秒 |
| `E-x` | 异常场景 | `E13` 连续 3 次登录失败 |
| `R-x` | 技术风险与验证点 | `R1` NativeAOT 下的无边框面板 |
| `D-x` | 决策点 | `D17` 启动失败的长期策略 |
| `坑 x` | `api-analysis.md` 里的具体技术陷阱 | `坑 1` 键名三级回退 |

**写代码时的规则**：实现某个 `FR` 时，在代码注释里引用它；修某个 `E` 场景时，在提交信息里引用它。这样任何人从任一方向都能追到上下文。

---

## 决策点状态

**D1–D25 已全部批复完毕并冻结；D27 / D28 / D29 于 Phase 1 期间批复；D30–D34 于 Phase 2 开工前批复；D35–D37 于 Phase 3 开工前批复（其中 D37 由用户改判，属需求变更）。仅 D26（测试命令走向）待决策，不阻塞编码。**

**Phase 0 / Phase 1 / Phase 2 出口条件均已达成**（Phase 2 含 D34 真机验证闭环）；**D34 及之后的批复记在 `requirements.md` 的决策表**（最新为 D64），本表只索引 D1–D33。全解决方案 Release 构建 **0 警告 0 错误**、**263 个单元测试全绿**。

| 编号 | 内容 | 结论 |
|---|---|---|
| D1 | 调度端技术 | A — 独立轻量进程 + AOT。**UI 层被 D24 改写为纯 Win32**（原 WinForms 部分作废） |
| D2 | 主窗口导航 | A — 左侧 NavigationView + 来源子项 |
| D3 | 界面语言 | A — 仅简体中文 |
| D4 | UWP 延时语义 | B — 做，文案写明会绕过系统启动管理。**激活方式被 D28 改写为 `shell:AppsFolder`** |
| D5 | 「系统启动项」页范围 | B — 只做服务 `delayed-auto` 切换 |
| D6 | 程序名 | A — 延时启动管理器 / DelayStart。🔴 **被 D61 改判**：应用名只用英文 `DelayStart`，中文名退回文档 |
| D7 | 「立即模拟调度」 | A — 做 |
| D8 | 分发方式 | ~~A → C — 第一版免安装 zip，发布阶段补 Inno Setup~~ → **被 D22 取代** |
| D9 | `.lnk` 解析 | A — 做 |
| D10 | 批量操作 | B — 第一版不做 |
| D11 | 预设延时值可配置 | B — 设置页可编辑 |
| D12 | 延时上限语义 | B — 仅输入校验，不影响调度 |
| D13 | 长延时调度方式 | A — 统一由本程序调度器驻留 |
| D14 | 调度端形态 | A — 无主窗口，仅托盘 + 点击弹出面板 |
| D15 | 通知策略 | A — 仅失败时通知 |
| D16 | 面板 `[退出调度]` | A — 不做 |
| D17 | 失败长期策略 | **D — 不自动处理，保持接管 + 持续提醒，由用户手动处理** |
| D18 | 点击通知 | A — 打开管理端日志页并定位本次运行 |
| D19 | 管理端显示调度进行中 | A — 读 `current-run.json` |
| D20 | 管理端权限模型 | **A — 全程提权**（原「按需提权」建议被否，见 `design-spec.md` 6.2） |
| D21 | 单元测试框架 | **A — xUnit v3**（主选 v3 模板 / 降级 SDK 内置模板；NUnit 明确排除，见 `design-spec.md` 6.3） |
| D22 | 分发形态（MSIX 是否可行） | **A — Inno Setup 安装器 + unpackaged**。MSIX 三条否决理由见 `design-spec.md` 6.4；**取代 D8** |
| D23 | 安装与数据布局 | **per-user、不提权**：程序 `%LOCALAPPDATA%\Programs\DelayStart`（只读）；配置 `%APPDATA%\DelayStart\config.json`（Roaming）；日志/状态/归档 `%LOCALAPPDATA%\DelayStart\`（Local）。见 `architecture.md` 1.5 |
| **D24** | **调度端 UI 技术选型（重开 D1）** | **✅ B — 纯 Win32 + AOT**：`Shell_NotifyIcon` 托盘 + 自绘无边框弹窗，全 P/Invoke，不引用任何 UI 框架。否决 A（WinForms+AOT 官方不支持，靠内部属性逃逸）／C（WinUI 3 官方支持 AOT 但冷启动 0.5–0.7s 超 NFR-1.2，**且 WinUI 3 无托盘 API**）／D（放弃 AOT，体积与冷启动不可接受）。见 `design-spec.md` 6.6、`architecture.md` R11 |
| **D25** | **Phase 0 模板残留页处理** | **✅ A — 现在就删**：`Pages/` 的 Home / About / Settings 全删，`MainWindow` 导航项清空、**不预置死链**，Phase 3 按 `architecture.md` 1.4 从零建 6 个页面 |
| **D26** | **测试命令（`dotnet test` 跑不了）** | ⏳ **待决策**：`xunit.v3.mtp-v2` 4.0.1 + SDK 10.0.401 下 `dotnet test` 报"运行了零个测试"（**测试发现本身正常**）。**AI 建议 A**：规范命令改 `dotnet run --project tests/DelayStart.Core.Tests -c Release`（已生效，零改动）；B 补 `xunit.runner.visualstudio` 退回 VSTest（VS 测试资源管理器可用）／C 换包。**不阻塞编码**。见 `design-spec.md` 6.8 |
| **D27** | **Phase 1 是否连 P/Invoke 启动器一起写** | **✅ A — 本期只做接口 + 纯逻辑**：`ProcessLauncher` / `TokenHelper` / `Core/Interop/*` 推到 **Phase 4** 与调度引擎一起落地。理由：P/Invoke 的正确性只能在**真实登录会话**里验证，Phase 1 写了只能靠编译通过自证（假绿灯）。见 `design-spec.md` 6.9 |
| **D28** | **UWP 项怎么激活（AOT 约束下）** | **✅ A — `explorer.exe shell:AppsFolder\<AUMID>`**，纯 `Process.Start`、零 COM。B（demo 的 `IApplicationActivationManager` + `Marshal.GetObjectForIUnknown`）在 NativeAOT 下**运行时必抛 `PlatformNotSupportedException`**，与 D24=B 的 AOT 前提直接冲突。**代价：拿不到 PID → 机制 7 不适用，`null` 快照判成功。新增 R12**。见 `design-spec.md` 6.10 |
| **D29** | **Phase 1 期间的文档修订（F1–F4）** | **✅ 全部执行**：F1 追踪矩阵实现层 6 行路径、F2 `architecture.md` 2.1/2.2 补 `PathService` 等、F3 删 Core 的 `Interop/Shell32.cs`（误植）、F4 登记 R12。均为一句话级修正，不改变行为约定 |
| **D30** | **Phase 2 是否连图标 / 服务查询 / 系统启动项检查一起做** | **✅ A — 本期不做**，推到 Phase 3（`IconProvider`）与 Phase 5（`ServiceQueryService` / `SystemStartupInspector`）。三者本期无消费者，且按 14.1 进不了单元测试 → 只能靠编译自证（假绿灯，同 D27 逻辑）。FR-7 在追踪矩阵已标注延期 |
| **D31** | **`FailureStreakService` 归属（🔴 文档错误修正）** | **✅ A — 搬进 Core**。原文档三处互相矛盾（调度端要角标 / 调度端只引用 Core / 却写"由管理端聚合"），该链路原本**实现不了** —— 登录那一刻管理端没运行。连续失败计算是纯函数、AOT 安全，两端共用；调度端仍无状态（现算不存计数）。见 `design-spec.md` 6.11.2 |
| **D32** | **Phase 2 无 UI 时怎么执行安全验收** | **✅ A — 给管理端加 headless CLI**：`--restore-all` / `--reinstall-task`（D22 本来就要求）+ `--takeover` / `--release`。B（等 Phase 3）会让整个 Phase 2 的写入链路一次都没跑过就进入下一阶段。见 `design-spec.md` 6.11.3 |
| **D33** | **Management 层单元测试范围** | **✅ A — 本期做**：假 `IStartupSource` 测 FR-1.4「单条失败不影响整次扫描」、假 `IAppConfigStore` 测 FR-3.1「任一步失败逆序回滚」（含手工测不出的失败路径）。真机写入链路由 D32 的 CLI + 注册表导出对比覆盖，两层互补 |
| **D34** | **`DelayStartScheduler` 本期真机注册还是推 Phase 4** | **✅ A — 本期做并验证**：用 `schtasks /query /v /fo LIST` 只读核对三要素（登录触发 / 延迟 3s / `Highest`）**与身份是否为当前交互用户**（🔴 必须不是 SYSTEM —— SYSTEM 下 `%APPDATA%` 解析到 `systemprofile`，配置读不到且**不报任何错**）。验证不需要调度端能跑 |
| **D35** | **管理端 DI 容器选型** | **✅ A — `Microsoft.Extensions.DependencyInjection`**。约 20 个服务要装配，生命周期（`ConfigStore` 单例 / Scanner 复用 / Dialog 瞬态）必须显式；手写组合根会让 ViewModel 只能静态取服务（依赖藏进方法体，漏注册是运行时空引用） |
| **D36** | **导出/导入与预设延时值是否提前到 Phase 3** | **✅ A — 都不做**。两者归宿都是 Phase 5 设置页（FR-9.10「数据分组」本就与 FR-12.2 是同一套组件）；本期延时编辑器只用 `AppConfig` 已有默认预设值 `10/30/60/120`，**不影响任何交互** |
| **D37** | **时间轴视图是否保留**（延时启动页双视图 + 总览页时间轴卡片） | **✅ B（用户改判，两处都砍）—— ⚠️ 这是需求变更**。同步改了 `design-spec.md` 页面 1/3 与 D7 措辞、`requirements.md` FR-10.4、`architecture.md` 5.1（删 `TimelineView.xaml`）。砍掉的是「双视图 + 搜索态同步过滤 + 上下调序与过滤态互斥」那套最易长 bug 的分支；「依赖关系用更大延时」语义改由常驻提示条承担 |

> 详细选项与论证：`design-spec.md` 第六节（6.1 D17 / 6.2 D20 / 6.3 D21 / 6.4 D22 / 6.5 D23 / **6.6 D24** / **6.7 D25** / **6.8 D26** / **6.9 D27** / **6.10 D28** / **6.11 D30–D34** / **6.12 D35–D37**）。工程落地：`architecture.md` 1.3 / 1.4 / 1.5 / 第九节 R1–**R13**、`build-and-test.md` 第一、二、四、七、九节。
>
> ✅ **Phase 0（2026-09-19）出口条件全部达成**：骨架生成完毕、Release 构建 **0 警告 0 错误**、冒烟测试 2/2 通过、
> manifest 三个标记确认嵌入 exe、**R3 / R8 / R9 三项人工验证由用户在 VS 之外实测通过**、模板残留清理完毕（D25）。
> 执行记录见 `architecture.md` 10.1 与 `build-and-test.md` 9.0。
>
> ✅ **Phase 1（2026-09-19）已完成**：`Core` 层 **38 个源文件**落地（15 模型 / 7 抽象 / 10 服务 / 2 启动结果 / 3 序列化 / 1 日志），
> **`IsAotCompatible=true` 全程守门且构建 0 警告 0 错误** —— 证明 Core 无 IL2026 / IL3050 违规，调度端的 AOT 边界成立。
> **单元测试 128 个全绿**。执行记录见 `architecture.md` 10.2。
>
> ✅ **Phase 2（2026-09-19）已完成并提交（8 commit / 52 文件）**：`Management` 层 20 个源文件（4 个 Source 的 7 个实例位置 + `StartupApprovedStore` +
> `ScanService` / `TakeoverService` / `TaskRegistrationService`）+ `Core` 的 `FailureStreakService` + 管理端 headless CLI。
> **出口条件（9.3 安全验收）已按 D34 真机闭环**：白名单四项走完 `--takeover` ×4 → `--restore-all`，
> 三个 `Run` 键全量快照**逐行 IDENTICAL**；`DelayStartScheduler` 注册为 `OnLogon PT3S` / `Highest` / **`Interactive`**。
> **单元测试 208 个全绿**，全解决方案 0 警告 0 错误。执行记录见 `architecture.md` 10.3。
> ⚠️ 期间跑出并修复两条缺陷（`InvariantGlobalization` 导致注册必崩 → **R13**；`OriginalState.WasEnabled` 实现漏读导致恢复动作抹掉用户原有禁用状态）。
>
> ▶️ **下一步：Phase 3（管理端 UI 骨架：导航 + 自启动项页 + 延时启动页，跑通单链路）**，含 R10（提权窗口拖放）与 R7（高 DPI）两项实测。
>
> ⚠️ **一个已知非阻塞项**：D26（测试命令，用 `dotnet run`；`dotnet test` 在本项目跑不了）。


---

## 项目硬约束速查

写代码前先记住这几条，它们决定了哪些做法是**禁止**的。

1. **绝不删除用户数据**。禁用一律走软禁用标记（`StartupApproved` / `Enabled=false` / `State=0`），原值分毫不动。
2. **软件不替用户改系统状态**。启动失败不自动恢复自启动（D17）。
3. **`DelayStart.Core` 必须保持 NativeAOT 兼容**。任何 COM 互操作、反射、`Reflection.Emit` 都只能放在 `DelayStart.Management`。
4. **`DelayStart.Scheduler` 绝不引用 `DelayStart.Management`**。
5. **`StartupApproved` 键名匹配必须三级回退**（原名 → 原名+`.exe` → 去扩展名）。
6. **hive 判断必须用 `StartupScope` 显式枚举**，禁止用字符串 `StartsWith` 猜。
7. **延时是相对登录时刻的绝对时间点**，不是累加。计时用 `Stopwatch`，不用 `DateTime.Now`。
8. **`LibraryImport` 的 `bool` 返回值必须标 `[return: MarshalAs(UnmanagedType.Bool)]`**。
9. **Win32 调用失败必须把 `GetLastWin32Error()` 写进异常消息**。
10. **管理端与调度端全程 elevated**（D20）。UAC 被拒时**退出，不降级运行**——半残运行比不让用更糟。
11. **测试项目绝不引用 `DelayStart.App`**。一旦引用，就得背上 `WindowsAppSDKSelfContained` + Windows App Runtime 预装 + 多 RID，测试从秒级变分钟级（见 `architecture.md` 1.3）。
12. **单元测试以普通权限运行**，禁止要求管理员权限，禁止触碰真实注册表/文件/进程。
13. **AI 只生成 commit message，不自动执行 `git commit`**（用户明确要求时除外）。
14. **交付形态是 Inno Setup 安装器，不是 zip**（D22）。**安装路径必须固定且不含版本号** —— 计划任务 action 指向它。
15. 🔴 **卸载必须先还原全部接管项再删文件**（NFR-6.4）。还原失败则中止卸载。违反这条会让用户卸载后所有程序永久不自启。
16. **安装目录只读，运行时数据与安装目录分离**（D23 / NFR-6.7）。程序放 `%LOCALAPPDATA%\Programs\DelayStart`，配置放 `%APPDATA%\DelayStart`（Roaming），日志与状态放 `%LOCALAPPDATA%\DelayStart`（Local）。**全部路径经 `PathService` 解析，禁止硬编码**。
17. 🔴 **计划任务身份必须是交互用户**（`LogonType=Interactive` + `RunLevel=Highest`），**禁止 SYSTEM / 服务账户**（NFR-6.8）。SYSTEM 下 `%APPDATA%` 会解析到 `systemprofile`，配置读不到、日志写错位置，**且不报错**。
18. 🔴 **调度端是纯 Win32 + NativeAOT，不引用任何 UI 框架**（D24 = B）。因此调度端里**禁止** `UseWindowsForms` / `UseWPF` / WinUI 3，也**禁止** `.resx` 反射式资源加载（图标一律 `Assembly.GetManifestResourceStream`）、`DataGridView`、`RichTextBox`、动态 COM 互操作、`System.Reflection`。**IL2xxx AOT 警告必须逐个消掉，不能用开关兜着。**
19. **Release 产物里 `DelayStart.exe`（管理端）与 `DelayStart.Scheduler.exe`（调度端）名字固定**，安装器与计划任务 action 都按这两个名字写死（D22 / D23）。

---

## 文档维护规则

- **需求变更** → 先改 `requirements.md`，再改代码。不允许"代码先改，文档后补"。
- **文案变更** → 必须同步 `design-spec.md`。界面上的每一个字都以此文档为准。
- **新增决策点** → 在 `requirements.md` 第十一节与本文「决策点状态」两处登记，并在 `changelog.md` 当轮记录落点。
- **设计阶段的每轮改动** → 记入 `changelog.md`。**编码开始后停止逐条维护该文件**（R1–R7 覆盖到 Phase 1 为止），此后只在**新增决策点**时追加一条「落点索引」，实现层面的变更改由 `git log` 承载。
- **发现新的技术坑** → 补进 `api-analysis.md`，并在 `coding-standards.md` 里加对应的【必须】条目。
- **新增 NativeAOT 不兼容的 API 用法** → 登记为新风险（`architecture.md` 第九节）。判据：凡是 `[ComImport]` / `Marshal.GetObjectForIUnknown` / 动态 COM / `System.Reflection` / `Reflection.Emit`，**默认视为不能进 `Core`**（R11 与 R12 同源）。
- **文档间不重复**。同一个内容只在一个文档里详述，其他文档用链接引用。发现重复就删掉一份。
