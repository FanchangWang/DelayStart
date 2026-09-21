# Agents.md — AI 协作指南

> 供 AI 编码代理（与新人）快速建立约束意识的入口文件。
> `docs/design.md` 是当前方案的**单一事实来源**：代码与文档冲突时，以文档为准，并立即修正另一方——不允许两边长期不一致。

---

## 项目是什么

**DelayStart**：扫描 Windows 全部自启动位置 → **软禁用**（不删数据）→ 独立轻量调度进程按延时启动。
把「登录瞬间全有或全无」的自启动变成「晚 10 秒 / 30 秒 / 2 分钟错峰启动」。

三原则（违反任何一条即架构性错误）：

| 原则 | 含义 |
|---|---|
| **全部可逆** | 禁用 = 写软禁用标记（`StartupApproved` / `task.Enabled=false` / UWP `State=0`），绝不删注册表值、绝不移动用户文件 |
| **判定可靠** | 延时 = 相对登录时刻的绝对时间点，不是累加；必须能准确说出第几秒启动 |
| **不替用户做决定** | 启动失败只提醒、不自动改系统状态（D17）；改变系统行为的动作出口都在用户手上 |

---

## 硬约束（🔴 违反即构建失败或发布失败）

1. **依赖方向单向**：`App → Management → Core`，`Scheduler → Core`，`Tests → Core + Management`。
   - 🔴 `Scheduler` 绝不引用 `Management` —— NativeAOT 发布直接失败。
   - 🔴 `Tests` 绝不引用 `App` —— 会背上 WindowsAppSDK 自包含包袱。
2. **`DelayStart.Core` 必须 AOT 兼容**（`IsAotCompatible` 护栏）：零 COM、零反射。往 Core 放 `Microsoft.Win32.TaskScheduler`、`IShellLinkW` 之类的东西 = 打断调度端发布。
3. **调度端是纯 Win32**（D24 批复 B）：不引用任何 UI 框架，托盘 / 面板全走 P/Invoke。禁止引入 WinForms/WPF。
4. **0 警告构建**（`TreatWarningsAsErrors=true`）：出现警告即失败，这是故意的。
5. **调度端计划任务禁止 SYSTEM**（NFR-6.8）：`LogonType=Interactive` + `RunLevel=Highest`。
6. **应用名只用英文 `DelayStart`**（D61），中文名「延时启动管理器」仅文档使用。
7. **可逆优先**：任何改变系统状态的操作必须有配对的还原路径；失败必须可见，禁止静默吞异常。

---

## 常用命令

```powershell
.\scripts\build.ps1      # 编译全解决方案（Release，0 警告验收）
.\scripts\test.ps1       # 单元测试（D26 约定：dotnet run，不用 dotnet test）
.\scripts\publish.ps1    # AOT 发布双 exe + 同步进管理端 bin
.\scripts\all.ps1        # 一条龙：build → test → publish
.\installer\build-all.ps1  # 安装包矩阵（自包含版 / 精简版 × x64 / arm64）
```

- 手动等价：`dotnet build DelayStart.slnx -c Release` / `dotnet run --project tests/DelayStart.Core.Tests -c Release`
- 全解决方案 Debug 构建：`dotnet build DelayStart.slnx --no-incremental`（排查 XAML 编译问题时务必加 `--no-incremental`，obj 增量缓存会掩盖真相）
- 验收口径：Release 0 警告 0 错误 + 全部单元测试绿（当前 292 个）

---

## 编号体系（可追溯性）

代码注释、提交信息、测试用例都用这套编号互相引用：

| 前缀 | 含义 | 定义处 | 举例 |
|---|---|---|---|
| `FR-x.y` / `NFR-x.y` | 功能 / 非功能需求 | `docs/design.md` 三、四 | `FR-2.3`、`NFR-1.2` |
| `E-x` | 异常场景矩阵 | `docs/design.md` 五 | `E13` |
| `R-x` | 技术风险（已全部闭环） | `docs/decisions.md` 附表 | `R11` |
| `D-x` | 决策点（D1–D68） | `docs/decisions.md` | `D17` |
| `坑 x` | 技术陷阱（1–9 编号沿用） | `docs/pitfalls.md` | `坑 1` 键名三级回退 |

规则：实现某 `FR` 时在代码注释里引用它；修某 `E` 场景时在提交信息里引用它；踩到新坑必须追加进 `docs/pitfalls.md`。

---

## 文档地图

> 2026-09-21 重组：原 7 个文档 + 原型 HTML 合并为 3 个，过时内容已删除。

| 文档 | 回答什么问题 |
|---|---|
| `docs/design.md` | **当前方案单一来源**：需求（FR/NFR/E）、架构与关键机制、调度端交互、编码规范、开发与交付流程 |
| `docs/decisions.md` | D1–D68 决策索引（每条 = 议题 + 最终批复）+ R1–R13 风险去向 |
| `docs/pitfalls.md` | 踩坑大全：Win32/注册表/计划任务/UWP/降权/AOT/WinUI 3/安装器/本机环境，写相关代码前逐条看完 |

---

## 本机已知坑（摘自 docs/pitfalls.md，编码前先看）

- XAML 编译器 pass-1 可能静默崩溃（exit 1 且不写 output.json）：字符串字面量三元表达式、DataTemplate 内裸元素挂 Pointer/Tapped 事件都实锤触发过；排查用二分 + `dotnet clean` + `--no-incremental`，勿信增量构建结果。
- `x:Bind OneWay` 的目标必须有通知源：VM 的 get-only 派生属性挂 `[NotifyPropertyChangedFor]`；非 INPC 快照行类只能 `OneTime`。
- 管理端 `requireAdministrator`：`dotnet run` 弹 UAC；提权验证必须在 VS 之外双击 exe。
- NuGet 未配国内镜像，慢时先项目级镜像再代理；连续失败 2 次停手查因，禁止重试循环。

---

## 协作约定

- 提交遵循 Conventional Commits，一次一个逻辑变更；AI 产出 commit message，提交动作按用户指示执行。
- 行尾统一 LF（见 `.gitattributes`）；`.sln` 例外 CRLF。
- UI 改动以代码为当前事实；界面文案改动不需要维护独立文案副本（原 design-spec.md / 原型 HTML 已删除）。
