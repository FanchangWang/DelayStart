# DelayStart — 决策索引（D1–D73）

> 每条只记**最终批复**（含被后续推翻的指向）。完整论证随原稿废弃；现状以 `design.md` 为准。
> 标注 ✅ = 已批复；⚠️ = 被后续决策修订或推翻。

| 编号 | 议题 | 最终批复 |
|---|---|---|
| D1 | 调度端技术 | ✅ 独立轻量进程 ⚠️ 被 D24 重开 |
| D2 | `.lnk`/UWP 目标处理 | ✅ 解析真实目标；`.lnk`/UWP 启动经外壳委托（D40 修订） |
| D3 | 总览"开机调度任务"开关 | ✅ 开 = 注册 / 关 = 删除 ⚠️ **D68 废止**：开关删除，改状态卡 + 每次启动自检自愈 |
| D4 | UWP 延时启动 | ✅ B：做，文案写明绕过系统启动管理 |
| D5 | 系统启动项页范围 | ✅ B：只做服务 `delayed-auto` 切换，驱动/Winlogon 纯只读 |
| D6 | 程序名 | ✅ A：延时启动管理器 / DelayStart ⚠️ **D61 改判**：应用名只用英文 `DelayStart` |
| D7 | 立即模拟调度 | ✅ A：做（进度视图；时间轴被 D37 砍除） |
| D8 | 分发方式（zip 过渡） | ✅ 被 D22 取代 |
| D9 | `.lnk` 目标与图标解析 | ✅ A：做 |
| D10 | 批量操作 | ✅ B：第一版不做 |
| D11 | 预设延时可配置 | ✅ B：设置页可编辑 |
| D12 | 延时上限语义 | ✅ B：仅输入校验 |
| D13 | 长延时调度方式 | ✅ A：统一由调度器驻留 |
| D14 | 调度端形态 | ✅ A：无主窗口，托盘 + 点击弹面板 |
| D15 | 完成通知策略 | ✅ A：仅失败时通知 |
| D16 | 面板 `[退出调度]` | ✅ A：第一版不做（若将来做，退出必须恢复剩余项自启动） |
| D17 | 启动失败长期策略 | ✅ D：不自动处理，保持接管 + 提醒升级，出口只有用户手动移出 |
| D18 | 点击通知行为 | ✅ A：打开管理端运行日志并定位本次运行 |
| D19 | 实时调度状态展示 | ✅ A：读 `current-run.json` |
| D20 | 权限模型 | ✅ A：**全程提权**（"按需提权"建议被否——核心链路 100% 需管理员，省不掉那次 UAC） |
| D21 | 测试框架 | ✅ A：xUnit v3（NUnit 排除） |
| D22 | 分发形态 | ✅ A：Inno Setup 安装器 + unpackaged（MSIX 被否：Win10 提权/注册表虚拟化/路径含版本号） |
| D23 | 安装与数据布局 | ✅ per-user 不提权安装；程序 `%LOCALAPPDATA%\Programs`（只读）、配置 Roaming、日志/状态 Local |
| D24 | 调度端 UI 选型（重开 D1） | ✅ **B：纯 Win32 + NativeAOT**（WinForms+AOT 官方不支持 NETSDK1175；WinUI 3 无托盘 API 且冷启动超 NFR） |
| D25 | 模板残留页 | ✅ A：现在就删，导航壳留空 |
| D26 | 测试命令 | ⏳ 仍待决策（`dotnet test` 在 xunit.v3.mtp-v2 下跑零个用例；规范命令 = `dotnet run --project tests/...`，不阻塞） |
| D27 | Phase 1 是否含 P/Invoke | ✅ A：只做接口与纯逻辑，P/Invoke 推 Phase 4（正确性只能在真实会话验证） |
| D28 | UWP 激活方式 | ✅ A：`explorer.exe shell:AppsFolder\<AUMID>` 零 COM（AOT 无 built-in COM）；null 快照判成功 |
| D29 | Phase 1 文档修订 F1–F4 | ✅ 全部执行 |
| D30 | Phase 2 范围 | ✅ A：图标/服务查询/系统启动项推后（无消费者且进不了单测） |
| D31 | FailureStreakService 归属 | ✅ A：搬进 Core（调度端要用而调度端只引 Core；纯函数两端共用） |
| D32 | headless CLI | ✅ A：`--restore-all` / `--reinstall-task` / `--takeover` / `--release` / `--scan`（卸载可逆的必需品） |
| D33 | Management 单测 | ✅ A：假实现测 FR-1.4 / FR-3.1 回滚 |
| D34 | 计划任务真机注册 | ✅ A：本期做并验证（发现 R13） |
| D35 | DI 容器 | ✅ A：Microsoft.Extensions.DependencyInjection（组合根 CLI/GUI 共用） |
| D36 | 导出/导入、预设编辑是否提前 | ✅ A：都不做，归宿 Phase 5 设置页 |
| D37 | 时间轴视图 | ✅ **B：两处都砍**（需求变更；模拟改进度视图） |
| D38 | 降权机制（六连败后） | ✅ A：双进程代理 ⚠️ **被 D40 整体取代** |
| D39 | D38 修订（单任务 + 令牌握手） | ⚠️ 随 D38 作废 |
| D40 | 降权机制再评估（七方案对照） | ✅ **A：调度端亲自降权**（外壳令牌 → DuplicateTokenEx → CreateProcessWithTokenW）；`.lnk`/UWP 经外壳委托；删除代理。COM ShellExecute "降权"技巧实测不成立，禁用 |
| D41 | UWP 启动失败修复 | ✅ A：解析名（`shell:AppsFolder\…`）为主判据 + `UwpAppIdResolver`（TaskId ≠ AppId）+ 委托路也降权 |
| D42 | 跨页数据不同步 + 遗留清理 | ✅ A：来源级扫描缓存过期标记；删 `\DelayStart` 文件夹清理代码 |
| D43 | 总览结果列空白 + 管理员 UWP 失败 | ✅ A：拆两份互斥 TextBlock（画刷 null 切断继承链）；UWP 判定提前 ⚠️ ②的定性被 D44/D45 更正 |
| D44 | UWP 管理员身份（推翻 D43②） | ✅ A：真因是裸 AUMID 非文件路径 ⚠️ 机制假设又被 D45 推翻 |
| D45 | UWP 启动身份（二度更正） | ✅ **A：UWP 进程恒为普通用户身份，不提供身份选择**（教训：涉令牌/权限的结论必须测到进程完整性级别） |
| D46 | 手动添加 UWP 入口 | ✅ A/A/A/A：入口 + `GetAppListEntriesAsync` 列表 + 复用 IconProvider + 参数框禁用态；存解析名非裸 AUMID |
| D47 | 目标类型白名单 | ✅ A：移除 `.msi`（193 BAD_EXE_FORMAT）、新增 `.ps1`（pwsh 优先宿主）；`LaunchTargetTypes` 唯一事实来源 |
| D48 | 编辑器目标区 | ✅ A/A：「更换」按目标形态分流；**UWP 没有工作目录概念**，置灰 |
| D49 | 默认窗口尺寸 | ✅ A：1400×900 DIP（DPI 换算 + 工作区钳制 + 居中），最小 960×600（`WM_GETMINMAXINFO`） |
| D50 | 预设值与分区顺序 | ✅ A/A：预设 `0/5/10/15/20/30/60`；设置页「外观」提前 |
| D51 | 延时页分组 | ✅ A：按延时值分组（已排序 Rows 顺序切片） |
| D52 | 分组版式 | ✅ A：边框按组（子标题在框外，每组一张卡片） |
| D53 | 编辑器结构 | ✅ A：目标块改 tab、两页签各存各的目标、删底部活摘要 |
| D54 | UWP 只读条目字段 | ✅ A：只显示 UWP 应用信息（名称/命令行/工作目录全隐藏） |
| D55 | 弹窗宽度 | ✅ A：回归框架默认（实测 548，旧注"540"有误）；滚动上限 560 |
| D56 | 编辑器版式 | ✅ A：子标题 + 卡片；页签换分段 ToggleButton（须挡"再点取消"）；去序号 |
| D57 | 原 ④ 接管对象块 | ✅ A：整块删除 |
| D58 | 工作目录自动填充 | ✅ A：保持留空（"留空 = 程序所在目录"有明确语义） |
| D59 | 参数/工作目录可见性缺陷 | ✅ A：移到页签外按"形态+页签"统一算（教训：可见性判据别写在会被折叠的子树里） |
| D60 | 安装包矩阵 | ✅ 2 形态 × 2 架构 = 4 包；**砍 x86**（ServiceQueryService x64 布局硬编码未修）；arm64 交 CI；slim 要的是 .NET Runtime 非 Desktop Runtime；版本号唯一来源 Directory.Build.props |
| D61 | 首轮真机安装缺陷 | ✅ publish 缺 XBF/PRI/AppIcon → `CopyWinUIResourcesToPublishDir` + 构建期守门（0xc000027b 秒崩根因；**bin 能跑 ≠ publish 能跑**）；`[Run]`/卸载 740 → `shellexec` / `ShellExec('runas')` + `--result-file` 轮询；补桌面快捷方式与 `<ApplicationIcon>`；应用名统一英文 |
| D62 | 二轮真机改判 | ✅ 缺运行时不自动启动（`Check: RuntimeReadyForApp` + `DELAYSTART_FAKE_MISSING` 自检）；桌面图标改可选任务（静默装需 `/MERGETASKS`）；卸载询问删数据（静默=否）；图标换用户图，`tools/make-icon.py` 手写混合 ICO 容器（Pillow 全 PNG 条目在小尺寸外壳路径会空白） |
| D63 | 三轮真机反馈 | ✅ 图标三份重生成（PNG 阈值 256→96，单份 −54%）；调度端补 `<ApplicationIcon>` 且 `IconResources` 按尺寸挑条目；默认延时 10s；「设置→应用」显示裸名（取 `AppVerName` 非 `AppName`）；首启自动注册计划任务 ⚠️ 注册语义被 D68 改为每次启动自检 |
| D64 | slim 报缺运行库 | ✅ 定性 = **两形态混装**（apphost 见 `hostfxr.dll` 即把运行时根当程序目录）；装前 `PrepareToInstall` 清空 `{app}`（仅精简版 `hostfxr.dll` 删不掉时中止）；下载入口按缺哪项开哪项。Restart Manager 不重启被关进程 = 已知缺口，**用户定性维持现状，禁止"管理端启动调度端"** |
| D65 | CI 首跑 ISCC exit 2 | ✅ 中文 .isl 不随官方 Inno 分发 → 入库 + 相对路径按 .iss 目录解析 + 垫 `Default.isl` |
| D66 | 过滤误伤"名字带 Microsoft" | ✅ 判据 `@"\Microsoft"` 少尾分隔符 → `IsProtectedFolderPath` 钉死只认根级 `\Microsoft\` 文件夹；16 例单测 + 红/绿对照 |
| D67 | 接管连带禁用其他触发器 | ✅ 禁用粒度四层规则（任务级关则不动 / 单触发器切任务级 / 多触发器只切自启动触发器且全切 / 启用先开任务级）；`Trigger.Id` 记账方案实测不可行（id 普遍为空） |
| D68 | UI v3 批复同步（R6–R8） | ✅ 调度任务改状态卡 + `SchedulerTaskBootstrap` 每次启动自检自愈（D63"仅首启一次"废止）；总览删最大延时/横幅；运行日志最新组默认展开；设置页 Toast（`ToastService`）与删除确认；编辑器钉宽/描述精简/确认 overlay 弹窗化；列头居中 |
| D69 | 托盘左键闪退（0xC0000409） | ✅ `FillRect` 误声明在 gdi32（实为 user32 导出）→ WM_PAINT 抛 `EntryPointNotFoundException`，`UnmanagedCallersOnly` 帧无法展开 → AOT fail-fast；`WndProcThunk` 加兜底 catch + 日志钩子。定位法：把 PanelWindow 原样编进 CoreCLR 复现工程，异常当场抛出 |
| D70 | uiAccess 目标降权（Quicker 报 740） | ✅ 预检 RT_MANIFEST 命中 uiAccess → 走 A(High) → CPWT → `DelayStart.LaunchBroker`(Medium) → `ShellExecuteEx` → 目标 C 链，broker 回传**目标**真实状态；`SHELLEXECUTEINFOW` 必须补 union（x64 sizeof=112）；COM STA 初始化 |
| D71 | 调度端 UI v2（面板 + 菜单） | ✅ 完成通知从气泡改为**自动弹面板**（已开则激活）；启动中面板不自动关闭（去掉 `WM_ACTIVATE` 失焦即关）；完成后倒计时自动关闭（成功 10s / 失败 60s，鼠标移入暂停不重置）；完成态关闭面板（倒计时归零或手动 ✕）= 退出调度器 + 托盘消失；右键菜单两套（启动中 / 启动完毕，完成态含「退出」）；管理端通知策略文案改为「弹出面板」 |

| D73 | 已显示的面板被通知策略关掉 | ✅ 判据漏了「面板此刻已显示」这一条：`FailuresOnly` + 全成功 → `showPanel=false` → 下一拍 `Quit()` → 面板连同托盘一起消失，用户手动打开的面板自己没了。语义澄清：**`NotifyMode` 只管"要不要主动弹"，管不到已弹出的面板**；判据加 `|| IsPanelVisible`，已显示就切完成态起倒计时（成功 10s / 失败 60s，hover 暂停），关闭才退。`Never` 同理 —— 面板已在屏幕上时照样给倒计时 |
| D72 | 收尾动作按入口分流（菜单 vs 面板） | ✅ 同一个「立即启动剩余 / 跳过剩余」在菜单与面板上语义不同，拆成两套入口：菜单「立即启动剩余任务」遵循通知策略；菜单「跳过剩余任务并退出」不弹面板直接退（**菜单文案不动**），且**不等**复查窗口：Launching 条目一并标记 `Skipped`（原因文案同未执行项），归档后立刻 `Quit()`；面板按钮「跳过剩余任务」按批复去掉"并退出"且不退出 —— 面板上两者都**必定**留在面板上给结果（切完成态 + 起倒计时），用户已手动打开面板，点了之后面板反而消失是倒退。引擎用 `_panelRequestedCompletion` / `_suppressCompletionPanel` 两标记，都压过 `NotifyMode` |

> **现状**：D1–D73 仅 D26 待决策（只影响测试命令，不阻塞编码）。

---

## 附表：R1–R13 技术风险去向（已全部闭环）

| # | 风险 | 结局 |
|---|---|---|
| R1 | 无边框窗口在 AOT 下能否工作 | ✅ Phase 4 实测通过（PanelWindow 失焦即关） |
| R2 | TaskScheduler 与 System.Threading.Tasks.Task 重名 | ✅ `using TaskSchedulerTask = …` alias |
| R3 | unpackaged 用 app.manifest | ✅ 确认 |
| R4 | HBITMAP → ImageSource 生命周期 | ✅ GetDIBits 负高度读像素 + finally 释放 |
| R5 | Core 引入 AOT 不兼容 API | ✅ IsAotCompatible 构建期拦截，持续生效 |
| R6 | 任务操作被 ACL/GPO 拒绝 | ✅ AccessDenied 语义 + 具体条目消息 |
| R7 | 高 DPI 错位 | ✅ 100/150/200% 实测通过 |
| R8 | .slnx 兼容性 | ✅ VS 18 正常打开 |
| R9 | 自包含时 manifest 提权被忽略 | ✅ 实测 UAC 正常弹出（WinAppSDK 1.8 未复现社区问题） |
| R10 | UIPI 拦拖放 | ✅ UipiMessageFilter 放行 WM_DROPFILES；主路径仍是浏览按钮 |
| R11 | WinForms + NativeAOT 官方不支持 | ✅ D24=B 根源消解；约束条款（禁 .resx/反射绑定）继续生效 |
| R12 | NativeAOT 无 built-in COM | ✅ D28=A 消解（shell:AppsFolder 零 COM） |
| R13 | InvariantGlobalization 使计划任务注册必崩 | ✅ 改回 false（Windows 用系统 icu.dll，省体积论据本就不成立） |
