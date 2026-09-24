# DelayStart — 开发文档

> 面向**要改代码、构建、调试、打包**的人。
> AI 编码代理请先读 [`../Agents.md`](../Agents.md)（硬约束 + 代码地图）；架构方案与需求见 [`design.md`](design.md)；踩过的坑见 [`pitfalls.md`](pitfalls.md)。
> 只想安装使用？看 [`../README.md`](../README.md)。

---

## 一、环境要求

| 项目 | 版本 | 说明 |
|---|---|---|
| Windows | 10 21H2+ / 11 | 开发与运行都在此版本以上 |
| .NET SDK | **10.0.401** | 由 `global.json` 钉住，装错版本 `dotnet build` 会直接拒绝 |
| Visual Studio | 2026（18.x） | WinUI 3 开发用；命令行构建不依赖它 |
| Inno Setup | 6 | **只在打安装包时需要**，日常开发不用装 |

> NuGet 未配国内镜像（只有 nuget.org + VS Offline Packages）。下载慢或失败时按 `pitfalls.md` 十处理：先项目级镜像，再代理，**连续失败两次就停下来查因，不要重试循环**。

---

## 二、项目结构与依赖方向

```
DelayStart.slnx
├─ src/DelayStart.Core           # 模型 / 配置读写 / 时序 / 启动 / 日志 —— ★ 必须 NativeAOT 兼容
├─ src/DelayStart.Management     # 各来源扫描器 / 软禁用 / 接管 / 计划任务 / COM 互操作
├─ src/DelayStart.App            # WinUI 3 管理端（unpackaged，产出 DelayStart.exe）
├─ src/DelayStart.Scheduler      # 纯 Win32 + NativeAOT 调度端（产出 DelayStart.Scheduler.exe）
├─ src/DelayStart.LaunchBroker   # NativeAOT 降权中转器（产出 DelayStart.LaunchBroker.exe，见 D70）
├─ src/DelayStart.Guard          # 自启动项守卫（产出 DelayStart.Guard.exe，非 AOT，见 D74/D75）
├─ src/DelayStart.NotifyBroker   # 通知中转器（产出 DelayStart.NotifyBroker.exe，非 AOT，见 N1/D83）
├─ tests/DelayStart.Core.Tests   # xUnit v3 单元测试（走 Microsoft.Testing.Platform）
├─ scripts/                      # 开发期脚本：build / test / publish / all
├─ installer/                    # Inno Setup 安装器与构建矩阵（见 D22 / D60）
├─ tools/                        # 图标等资源生成脚本
├─ assets/                       # 图标源图
└─ docs/                         # design.md（方案）· decisions.md（决策）· pitfalls.md（踩坑）· development.md（本文）
```

**依赖方向是单向的**：`App → Management → Core`、`Scheduler → Core`、`LaunchBroker → Core`、`Guard → Core + Management`、`NotifyBroker → Core`、`Tests → Core + Management`。

- 🔴 `Scheduler` / `LaunchBroker` **绝不引用 `Management`** —— NativeAOT 发布直接失败。
- 🔴 `Guard` **可以**引用 `Management`（它要 COM 与 `TaskScheduler`），代价是**它不能 AOT**（D75）—— 所以它的发布形态跟随管理端，而不是跟随调度端。
- 🔴 `NotifyBroker` **只引用 `Core`**（N1）：AUMID 由作业 JSON 携带（`NotifyToastJob.DefaultAumid`），它不读配置、不注册任何系统资源；发通知要 WinRT 投影，故不能 AOT，发布形态跟随管理端。
- 🔴 `Tests` **绝不引用 `App`** —— 一旦引用就要背上 WindowsAppSDK 自包含 + 运行时预装的包袱。

**为什么按"AOT 兼容性"而不是按领域拆两个共享库**：调度端用 NativeAOT，它引用的一切代码都必须 AOT 兼容；而扫描所需的计划任务库（`TaskScheduler` 包）、COM 互操作（`.lnk` 解析、图标提取）恰恰都不兼容。按兼容性切分后，调度端只引用 `Core`，拿到一个**零 COM、零反射**的最小集合。往 Core 里放一个 COM 依赖，`IsAotCompatible=true` 会在构建期拦住你。

**TFM 与关键开关**：

| 工程 | TFM | 关键设置 |
|---|---|---|
| Core / Management | `net10.0-windows` | Core 另开 `IsAotCompatible=true`（架构护栏） |
| App | `net10.0-windows10.0.26100.0` | `TargetPlatformMinVersion=10.0.19041.0`、`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true` |
| Scheduler / LaunchBroker | `net10.0-windows` | `PublishAot=true` |
| Guard | `net10.0-windows10.0.26100.0` | **`PublishAot=false`**（要 COM，D75）；发 WinRT 系统通知需要平台版本（D79），故与 App 同 TFM —— ⚠️ 产物路径比 Scheduler **多一段平台号**；发布形态跟随管理端（full 自包含 / slim 框架依赖） |
| NotifyBroker | `net10.0-windows10.0.26100.0` | **`PublishAot=false`**（发通知走 WinRT 投影，N1/D83）；与 Guard 同 TFM、同"产物路径多一段平台号"、同"发布形态跟随管理端"；只引用 Core，四件套同步（`App.csproj` CopyNotifyBrokerBuildOutput） |

---

## 三、常用命令

```powershell
.\scripts\build.ps1        # 编译全解决方案（Release，0 警告验收）
.\scripts\test.ps1         # 单元测试
.\scripts\publish.ps1      # 发布四个 exe（调度端 / 中转器 AOT publish，守卫 / 管理端 build）+ 同步调度端产物进管理端 bin
.\scripts\all.ps1          # 一条龙：build → test → publish
.\installer\build-all.ps1  # 安装包矩阵（自包含 / 精简 × x64 / arm64）
```

手动等价：

```powershell
dotnet build DelayStart.slnx -c Release
dotnet run --project tests/DelayStart.Core.Tests -c Release     # 规范测试命令
```

**验收口径**：Release **0 警告 0 错误** + 全部单元测试绿（当前 668 个）。

> 🔴 **构建要求零警告**（`TreatWarningsAsErrors=true`）—— 出现警告即编译失败，这是故意的。
>
> ⚠️ **`dotnet test` 不可用**（D26）：在 xunit.v3.mtp-v2 + SDK 10 下报"零个测试"并退出码 5；用上面的 `dotnet run` 形式。
>
> ⚠️ **改 XAML 后排查问题务必 `dotnet clean` + `--no-incremental`**：obj 里的 `.g.cs` 增量缓存会污染对照实验，让"改了没生效"和"真的没生效"看起来一样。
>
> ⚠️ **构建前先关掉正在运行的 `DelayStart.exe`**：它会锁住 `bin` 里的 DLL，报 `MSB3021/3027`（症状是一堆 `MSB3061 无法删除文件` 警告）。

---

## 四、本地运行与调试

**管理端**：manifest 带 `requireAdministrator`，`dotnet run` 会弹 UAC。
🔴 **提权是否真的生效，必须在 VS 之外双击 exe 验证** —— 在 VS 里跑会继承 VS 的权限，结果不可信。

**调试用环境变量**（仅开发期）：

| 变量 | 作用 |
|---|---|
| `DELAYSTART_LOCAL_DIR` | 覆盖日志 / 状态 / 归档目录 |
| `DELAYSTART_CONFIG_DIR` | 覆盖配置文件目录 |
| `DELAYSTART_FAKE_MISSING` | 伪装"运行库缺失"，回归安装器检测分支 |
| `DELAYSTART_RUNTIME_CHECK` | 运行库自检出口，可自动化验证 |

**调度端**：唯一合法入口是 `RunLevel=Highest` 的计划任务。手动双击会命中提权门槛 —— 静默退出并记日志（不是崩溃，别去修）。

**守卫端**：同样只有计划任务一个合法入口。手动双击 `DelayStart.Guard.exe` 会走入口自检 —— 写 `guard.log` 一行"未以管理员身份运行（疑似手动双击启动）"后**静默退出**（无窗口、无弹窗，退出码 0）。要手动跑一次巡检，必须**以管理员身份**启动（管理员终端里直接跑 exe，或从"任务计划程序"里手动运行 `\DelayStartGuard`）。日志落在 `logs\guard.log`；每次运行都有一行巡检汇总（扫描 / 纠正 / 新增 / 失效计数）——**没有这一行就等于它根本没跑**，这是区分"跑了但没变化"和"没跑"的唯一依据。`DELAYSTART_LOCAL_DIR` / `DELAYSTART_CONFIG_DIR` 同样生效（守卫走的是同一套 `PathService`）。

> 通报载体是**系统通知**（D79）：有变化时右下角弹一条、并停留在通知中心，点击经 `delaystart://` 协议拉起管理端。**通知不显示不等于巡检失败** —— 它可能被系统通知设置 / 专注助手 / 组策略挡掉；判断"守卫生效了没有"永远看 `guard.log`。要复现"有变化"的分支，可以把 `config.json` 的 `guardNotifyMode` 设为 `never` 对照跑一次（日志里会多一行"已跳过通报"）。
>
> ⚠️ **通知弹不出来时的排查顺序**：① 开始菜单里有没有 `DelayStart.lnk` 且它的目标指向当前 exe（AUMID 靠它存在，重装/换目录后旧快捷方式会指向失效路径）；② `HKCU\Software\Classes\delaystart` 在不在；③ `guard.log` 里有没有 `已发送系统通知` 一行 —— 有就说明**我们这边发成功了**，问题在系统侧（通知被关 / 专注助手），不在代码里。

> 🔴 **开发期要跑的那个 exe 在 App bin 里**：`src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\<rid>\DelayStart.Guard.exe` —— 因为 `PathService.GuardExecutablePath` 按 `AppContext.BaseDirectory` 就近解析，计划任务指向的正是它（守卫自己 bin 里的同名产物也能跑，但计划任务不会用它）。这道同步由 `publish.ps1` 保证，**必须先跑 `.\scripts\publish.ps1`**。
>
> ⚠️ **双击后连 `guard.log` 都没有、退出码 1** ⇒ 不是自检逻辑的问题，是 exe 根本没起来。看 Application 日志的 `SideBySide` 事件（Id=59）。**注意：守卫的 `app.manifest` 已随原生提示框在 D79 一并删除**，所以"清单里有 emoji / 非 BMP 字符导致激活上下文生成失败"这一类原因**不再适用于守卫**（那份清单没了；它现在不调用任何原生 UI API）。剩下的可能是缺运行时（把守卫四件单独拷出来跑就会复现）或 exe 被拦。这条路仍适用于**任何原样嵌入清单**的工程，管理端那份走 WinAppSDK 规范化、不受影响（`pitfalls.md` 十一）。

**调试 AOT 闪退**：把涉及的 P/Invoke 类原样链接进一个 CoreCLR 控制台程序直接调用即可 —— CoreCLR 能把 `EntryPointNotFoundException` 原样抛出来，AOT 只剩下 fail-fast（`0xC0000409`），看不到任何异常信息。详见 D69。

---

## 五、发布与打包

**`scripts/publish.ps1`** —— 开发期链路：发布调度端 / 中转器（NativeAOT）+ 构建管理端、守卫与通知中转器（build，非 AOT），并把**全部同级 exe 四件套同步进管理端 `bin`**。🔴 产物缺件时**返回非零退出码**（D61 教训：曾经缺件只打黄字然后照样报成功，让人以为可以直接拿 `bin` 跑）。核对清单为 **14 条**（各自 bin 里的 4 个 + 同步进 App bin 的 10 个文件）。

> 🔴 **三个同级 exe 的同步机制不同，顺序不能调**：调度端 / 中转器由 App 的 `CopySchedulerPublishOutput` 从 **AOT publish 目录**拉单文件；守卫由 `CopyGuardBuildOutput` 从**守卫自己的 build 目录**拉**四个文件**（`exe` + `dll` + `runtimeconfig.json` + `deps.json`）—— 守卫非 AOT，只搬 exe 会得到一个"找不到运行时"的空壳。两个钩子都是 `AfterTargets="Build"` 的**拉**式钩子，所以 `publish.ps1` 里 `dotnet build Guard` 必须排在 `build App` 之前（2026-09-22 修：原先排在后面，导致 App bin 里始终没有 `DelayStart.Guard.exe`，守卫计划任务是死链）。

**AOT 发布验收**：产物约 **3.4–3.6 MB**（上限 6 MB）；`scheduler.log` 首行记录启动耗时（**< 0.3 s**）。

**打包分发**：

```powershell
.\installer\build-all.ps1                          # 2 形态 × 2 架构 = 4 包
.\installer\build-installer.ps1 -Rid win-x64 [-Slim]   # 单包
```

- 自包含版 ~60 MB / 精简版 ~10 MB；精简版要的是 .NET **Runtime** 而非 Desktop Runtime。
- 版本号唯一来源是 `Directory.Build.props`。
- 🔴 守卫随管理端形态发布到 `{app}` 根目录。**slim 形态下 publish 目录里不得出现 `hostfxr.dll`**（`build-installer.ps1` 已内置该 guard-rail，出现即中止）—— 否则框架依赖的管理端会误判"运行时根 = 程序目录"并报"必须安装 .NET"（D64-1 / D75）。
- 安装器的 iss 参数、硬约束与验证配方见 [`../installer/README.md`](../installer/README.md) 与 [`pitfalls.md`](pitfalls.md) 九。

**CI**：`.github/workflows/release.yml`，tag `v*` 与手动双触发，矩阵 `fail-fast: false`；arm64 前置检测工具集。Release 正文由 `installer/make-release-notes.ps1` 生成：从根目录 `CHANGELOG.md` 提取对应 `## vX.Y.Z` 节（**缺节则发版失败**，发版前先补日志）+ `dist\*.exe` 附件表（大小 / SHA256 / 用途）。

---

## 六、真机验收清单（要点）

自动化测试覆盖不到的地方，必须真机跑：

- [ ] 软禁用前后**注册表导出对比零变化**（`StartupApproved` 标记写入属设计内正常写入）
- [ ] 接管 → 重启 → 各条目按预期时间启动
- [ ] 四条收尾路径（N2–N5，2026-09-22 批复后）：完成·面板已显示 → 切完成态+倒计时+**发通知**；完成·面板未显示 → **发通知后同拍退出**（不再自动弹面板）；菜单跳过 → 发通知后立即退；面板跳过 → 完成态+倒计时+发通知、不退出
- [ ] 卸载可逆：还原失败必须**中止卸载**，不能照删文件（D84 后含静默分支：还原失败 + `/VERYSILENT` → 无人拍板 → 中止、不弹窗）
- [ ] **卸载交互（D85）**：GUI 卸载弹**原生任务对话框**（三按钮：保留配置并卸载〔默认焦点，排第一〕/ 删除配置并卸载〔带盾牌〕/ 取消；文案 = 「卸载 DelayStart」+ 实际展开的配置/日志目录两行；取消 = 终止卸载且接管项不动、对话框在还原之前、跟随系统深浅色）；选「删除配置并卸载」后 `%APPDATA%\DelayStart` 与 `%LOCALAPPDATA%\DelayStart` 被删；静默 `/VERYSILENT` 保留、`/VERYSILENT /DELETEDATA` 删除；桌面图标默认勾选（静默也创建，`/MERGETASKS="!desktopicon"` 排除）
- [ ] DPI 三档（100 / 150 / 200%）与明暗主题
- [ ] 安装 / 升级 / 卸载全链路，含 slim 覆盖 full 的混装回归（往安装目录丢 `hostfxr.dll` → 装 → 断言被清）
- [ ] **守卫入口自检**：不带提权双击 App bin 里的 `DelayStart.Guard.exe` → `guard.log` 出现"未以管理员身份运行"一行，进程静默退出、无窗口（⚠️ 若连日志都没有，先按四节末的排查段）
- [ ] **守卫写回纠正**：接管一个应用 → 用脚本重写其 `Run` 值并删掉 `StartupApproved` 标记 → 运行守卫 → 断言再次被禁用，`guard.log` 有纠正记录
- [ ] **守卫系统通知**：手工新增一个启动项 → 运行守卫 → **右下角弹系统通知**（标题显示 `DelayStart`），且**通知中心里能再找到它**；点通知 → 管理端打开并定位到对应来源页。⚠️ 同时验 `guardNotifyMode=never` 时**不弹**、但 `guard.log` 仍写完整汇总 + 一行"已跳过通报"
- [ ] **守卫静默退出**：无变化时运行 → 无通知，`guard.log` 只有一行巡检汇总
- [ ] **守卫失效检测与清理**：接管后卸载该应用（或删其 `Run` 值）→ 通知"失效" → 点通知落到「延时启动」页 → 该行「启用」列显示"已失效"、「操作」列有「删除」（目标程序还在时应另有「转为手动」，点了之后该行变成普通手动条目、主键已换）
- [ ] **调度日志改名 + 守卫日志页（按次分组）+ 总览两卡（D115 / D116）**：① 左侧导航「调度日志」下方是「守卫日志」，点开**按次分组**列出每次巡检（组标题 = 时间 + 汇总，组内明细 = 纠正 / 新增 / 失效 / 来源失败，最新一组默认展开，纯扫描轮显示"本轮没有纠正、新增与失效"），「全部巡检 / 仅含变化与异常」筛选与「打开日志文件」生效；② 总览「上次开机调度」卡左两行（结果口径 + 调度时间，含失败标红）、右侧直达调度日志，无归档时显示空态；「上次守卫巡检」卡显示最近一次巡检汇总 + 时间、右侧直达守卫日志；③ 菜单 / 页标题 / 托盘菜单 / 进度面板按钮 / 通知点击落点全部是「调度日志」新文案
- [ ] **目录迁移与守卫归档（D116）**：① 手工把 `current-run.json` 放回旧 `state\`、旧归档放回旧 `runs\` → 打开管理端 → 文件出现在 `scheduler\` 与 `scheduler\archive\`（文件名不变）、旧目录被删、调度日志页历史记录完整；② 旧 `state\` 文件还在而 `scheduler\current-run.json` 已有新文件 → 旧文件被**丢弃**且新状态不被覆盖；③ 运行守卫一次 → `guard\inspections\` 出现一份 `<时间>.json`，守卫日志页出现对应分组（纠正 / 新增 / 失效明细可见），总览守卫卡跟着更新；④ 管理端一直不开 → 调度日志页暂时少旧记录（已知窗口），打开后补齐
- [ ] **防双启动**：先手动启动目标应用 → 触发调度 → 该条目 `Skipped`、原因 = 进程已存在；关闭目标应用后再触发 → 正常启动。另验 `.ps1` 条目照常启动（跳过检查）
- [ ] **提权模型（D82，本轮改动重点）**：① 双击快捷方式 / exe → **仍弹一次 UAC**（次数与改动前一致，只是时机从"启动瞬间"挪到"入口判定之后"），且 exe / 快捷方式**不再显示 UAC 盾牌**；② 管理端**已开着**时点系统通知 → **不弹 UAC**、窗口前置、切到对应页且**数据已刷新**（不需要手动刷新）；③ `manager.log` 里能看到完整唤起链（写入请求 → "已有管理端实例：…本进程不申请提权（零 UAC）"）。⚠️ 若日志里出现"实例存活探测未得出结论（AccessDenied）"，说明"中完整性进程按只读权限打开高完整性事件"这个前提不成立（见 `pitfalls.md` 十二）—— 表现**不是**坏掉，而是"仍多弹一次 UAC、落点正常"。
- [ ] **CLI 提权（D82）**：从**非提权** PowerShell 跑 `DelayStart.exe --restore-all --result-file <临时文件>` → 弹一次 UAC、退出码写进结果文件、**命令输出落在新开的控制台窗口**（已知代价，见 `pitfalls.md` 十二）；`--scan` 同理。另验带上 `--elevation-attempted` 时不会无限弹窗（UAC 被拒只退出一次）。
- [ ] **面板「钉」交互（N8–N10，2026-09-22 批复）**：默认失焦自动关闭（启动中仅收起可再开、完成态收起后进程退出/托盘消失）；点「钉」高亮 + 置顶于其他窗口之上 + 失焦不关；再点取消恢复；钉住时倒计时照走、归零退出；右上角无 ✕。
- [ ] **调度完成系统通知（N1–N12，2026-09-22 批复）**：① 三档策略 —— `Never` 全程无通知（`scheduler.log` 有"跳过完成通知"）；默认 `FailuresOnly` 全成功无通知、制造失败后有通知；`Always` 每次完成都有通知；② 通知内容「启动完成：N 项成功 · M 项失败…」、失败名前 3 条；③ 点通知 → 管理端**调度日志**页（已开零 UAC / 未开一次 UAC，D82）；④ 通知中心连续多轮只保留最新一条 `schedule-done`，且不与守卫 `guard-change` 互相替换；⑤ `notifybroker.log` 有"已交给系统"，中转器缺失 / 拉起失败时 `scheduler.log` 有 Warn 且调度照常退出（N12）。
- [ ] **调度周期（FR-15）**：① 把条目设成「法定工作日」，在一个**调休补班日**（2026-09-20 周日·中秋前补班）登录 → 照常启动；同一天设成「法定节假日」→ 不启动；② 把当年的 `holidays\2026.json` 改名 → 重启 → 列表徽标带 `≈`、悬停写明缺哪年、`scheduler.log` 有降级 warn，且条目**照常按星期规律启动**（不许停摆）；③ 新建自定义周期（如「上一休一」）→ 引用它的条目按星期生效；改它的星期 → 引用条目同步变化；④ 用同一个名字再新建 → 红字「已有同名周期，换一个名字。」，与内置「每天」同名同样被拒；⑤ 七宫格一天不选 → 点「保存」不关面板、不落盘、红字报错；⑥ 被引用的周期「删除」置灰、右侧写「N 个条目在用，禁止删除」、悬停有操作指引；把条目改成别的周期后可以删；⑦ 今天不在周期内的条目：列表有「今天不启动」徽章（**整行不变淡**）、悬停三行 tooltip；调度日志出现「◌ 跳过」+「今天不在启动周期内，未启动」，延时照填、发起时刻留空；当**所有**启用条目都被跳过时，日志里仍有一条记录（标题写「今天没有条目在启动周期内（跳过 N）」）且总览页「最近一次运行」跟着更新
- [ ] **节假日数据（FR-15 / D88）**：① 设置 → 节假日数据 → 「自动检查更新」打开 → **立刻**看到进度 `InfoBar`（不用等下次启动、也不用重新进页面）；② 断开网络后点「重新下载」→ 顶部 `InfoBar` 报错（不是静默）、本地旧数据**不被覆盖**、`manager.log` 有降级记录；③ 「导出」选年份 → 落盘成功且**不再闪退**（2026-09-23 修的是 COM 强转到平行接口，见 `pitfalls.md` 十五）；④ 用上游 `NateScarlet/holiday-cn` 的原始 `2026.json`（顶层是 `days`）直接「从文件导入」→ 成功，且年份取自**文件内容**而非文件名；⑤ 尚无数据的年份「导出」置灰；⑥ 11 月 1 日之后、次年无数据、且有条目用法定两档 → 延时页与设置页各有一条提示（不弹窗）；⑦ 启动管理端后等一轮自动检查跑完 → 应用内出现「节假日数据已自动就绪」通知，设置页该年份行副标题显示"上次自动检查时间 + 结果"

---

## 七、文档维护约定

四份文档各管一摊，改动代码时按下面的对应关系同步，**不允许代码与文档长期不一致**：

| 文档 | 什么时候必须改 |
|---|---|
| [`design.md`](design.md) | 需求 / 机制 / 交互行为发生变化 —— 它是当前方案的**单一事实来源** |
| [`decisions.md`](decisions.md) | 出现需要留痕的新取舍（追加 `D编号`）；或推翻旧决策（并入取代它的条目，编号保留） |
| [`pitfalls.md`](pitfalls.md) | 踩到新坑（追加），或旧坑被修掉 / 定性变化 |
| [`development.md`](development.md) | 环境要求、命令、构建发布流程、验收清单变化 |

代码注释、提交信息、测试用例统一按 `FR-x.y` / `NFR-x.y` / `E-x` / `D-x` / `坑 x` 编号互相引用，编号定义处见 [`../Agents.md`](../Agents.md)。

**提交前必查**：Release 构建 0 警告 0 错误 + 测试全绿；`git status` 无构建产物；新踩的坑已追加进 `pitfalls.md`。
