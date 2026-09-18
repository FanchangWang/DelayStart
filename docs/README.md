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

### 全部批复完毕 —— 决策冻结，可进入编码

| 编号 | 内容 | 结论 |
|---|---|---|
| D1 | 调度端技术 | A — 独立轻量进程 WinForms+AOT |
| D2 | 主窗口导航 | A — 左侧 NavigationView + 来源子项 |
| D3 | 界面语言 | A — 仅简体中文 |
| D4 | UWP 延时语义 | B — 做，文案写明会绕过系统启动管理 |
| D5 | 「系统启动项」页范围 | B — 只做服务 `delayed-auto` 切换 |
| D6 | 程序名 | A — 延时启动管理器 / DelayStart |
| D7 | 「立即模拟调度」 | A — 做 |
| D8 | 分发方式 | A → C — 第一版免安装 zip，发布阶段补 Inno Setup |
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

> 详细选项与论证：`design-spec.md` 第六节。D20 / D21 的工程落地：`architecture.md` 1.3 / 1.4 / R9 / R10、`build-and-test.md` 第四节。
>
> **编码前的第一件事是 Phase 0 验证 R9**（WinUI 3 自包含 + 提权 manifest 是否生效）。不通过则需按降级路径调整 D20 的实现方式，见 `build-and-test.md` 9.0。

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

---

## 文档维护规则

- **需求变更** → 先改 `requirements.md`，再改代码。不允许"代码先改，文档后补"。
- **文案变更** → 必须同步 `design-spec.md`。界面上的每一个字都以此文档为准。
- **新增决策点** → 在 `requirements.md` 第十一节与本文「决策点状态」两处登记，并在 `changelog.md` 当轮记录落点。
- **设计阶段的每轮改动** → 记入 `changelog.md`。**编码开始后停止维护该文件**，改由 `git log` 承载。
- **发现新的技术坑** → 补进 `api-analysis.md`，并在 `coding-standards.md` 里加对应的【必须】条目。
- **文档间不重复**。同一个内容只在一个文档里详述，其他文档用链接引用。发现重复就删掉一份。
