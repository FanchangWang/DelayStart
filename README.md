# DelayStart

扫描 Windows 全部自启动位置 → **软禁用**（不删除任何数据）→ 由独立的轻量调度进程按延时启动。

解决的痛点：Windows 的自启动是全有或全无的。要么所有程序在登录瞬间一起抢资源，让桌面卡一分钟；要么彻底禁用，从此忘了它的存在。DelayStart 给第三种选择 —— **晚 10 秒、晚 30 秒、晚 2 分钟再启动**。

---

## 核心原则

| 原则 | 含义 |
|---|---|
| **全部可逆** | 禁用 = 写软禁用标记（`StartupApproved` / `Enabled=false` / `State=0`），**绝不删除注册表值、绝不移动用户文件**。一键还原到接管前的状态 |
| **判定可靠** | 延时是「相对登录时刻的绝对时间点」。软件能准确告诉你第几秒启动，而不是"大概" |
| **不替用户做决定** | 启动失败时只提醒、不自动改系统状态。所有改变系统行为的动作，出口都在用户手上 |

---

## 技术栈

| 组件 | 技术 | 理由 |
|---|---|---|
| 管理端 | .NET 10 + WinUI 3（unpackaged） | 现代 Windows 原生观感 |
| 调度端 | **纯 Win32 + NativeAOT**（D24 批复 B，目标约 6 MB，冷启动 < 0.3 秒） | 登录瞬间执行，体积和冷启动是硬指标；托盘与面板全部 P/Invoke，零 UI 框架依赖 |
| 共享层 | `DelayStart.Core`（AOT 安全）+ `DelayStart.Management` | 见下方说明 |

**为什么拆两个共享库**：调度端用 NativeAOT，它引用的一切代码都必须 AOT 兼容。而扫描所需的计划任务库、COM 互操作（`.lnk` 解析、图标提取）恰恰都不兼容。所以按 **AOT 兼容性** 切分而不是按领域切分——调度端只引用 `Core`，拿到一个零 COM、零反射的最小集合。

---

## 项目结构

```
DelayStart.slnx
├─ src/DelayStart.Core           # 模型 / 配置读写 / 时序 / 启动 / 日志 —— ★ 必须 NativeAOT 兼容
├─ src/DelayStart.Management     # 各来源扫描器 / 软禁用 / 接管 / 计划任务 / COM 互操作
├─ src/DelayStart.App            # WinUI 3 管理端（unpackaged，产出 DelayStart.exe）
├─ src/DelayStart.Scheduler      # 纯 Win32 + NativeAOT 调度端（产出 DelayStart.Scheduler.exe）
├─ tests/DelayStart.Core.Tests   # xUnit v3 单元测试（走 Microsoft.Testing.Platform）
├─ scripts/                      # 开发期脚本：build / test / publish / all
├─ installer/                    # Inno Setup 安装器与构建矩阵（D22 / D60）
├─ tools/                        # 图标等资源生成脚本
├─ assets/                       # 图标源图
└─ docs/                         # design.md（方案）· decisions.md（D1–D68）· pitfalls.md（踩坑）
```

**依赖方向是单向的**：`App → Management → Core`，`Scheduler → Core`，`Tests → Core + Management`。
🔴 **`Scheduler` 绝不引用 `Management`** —— 那会让 NativeAOT 发布直接失败。
🔴 **`Tests` 绝不引用 `App`** —— 一旦引用就要背上 WindowsAppSDK 自包含 + 运行时预装的包袱（见 `docs/design.md` 七）。

---

## 环境要求

- Windows 10 21H2+ / Windows 11
- .NET SDK **10.0.401**（实测版本）
- Visual Studio 2026（18.x）— 用于 WinUI 3 开发

---

## 快速开始

```powershell
.\scripts\build.ps1     # 编译（Release，0 警告验收）
.\scripts\test.ps1      # 单元测试
.\scripts\publish.ps1   # AOT 发布双 exe + 同步进管理端 bin
.\scripts\all.ps1       # 一条龙
```

> **构建要求零警告**（`TreatWarningsAsErrors=true`）—— 出现警告即编译失败，这是故意的。
>
> 管理端带 `requireAdministrator` manifest，`dotnet run` 会弹 UAC。**要在 VS 之外双击 exe 才能测出提权是否真的生效**（在 VS 里跑会继承 VS 的权限，结果不可信）。
>
> 安装包（自包含 / 精简 × x64 / arm64）走 `installer\build-all.ps1`，见 [`installer/README.md`](installer/README.md)。

完整流程（含 NativeAOT 发布、本机环境已知坑）见 **[`docs/design.md`](docs/design.md)** 十。

---

## 安装位置与数据（D23）

**per-user 安装，安装过程不弹 UAC**（`PrivilegesRequired=lowest`）。程序与数据严格分离：

| 类别 | 路径 |
|---|---|
| 程序（只读） | `%LOCALAPPDATA%\Programs\DelayStart\` |
| 配置（Roaming） | `%APPDATA%\DelayStart\config.json` |
| 日志 / 实时状态 / 运行归档（Local） | `%LOCALAPPDATA%\DelayStart\logs\` · `state\` · `runs\` |

程序运行时**不向安装目录写任何东西**，所以覆盖升级与卸载不会留残留。计划任务在**管理端首次启动时**幂等检测创建，失败时管理端提供重试入口（D68）。

---

## 扫描范围

| 来源 | 位置 | 可延时启动 |
|---|---|---|
| 注册表 | `HKCU` / `HKLM` / `HKLM\WOW6432Node` 的 `CurrentVersion\Run` | ✅ |
| 启动文件夹 | 用户 / 系统 `Startup` 目录（`.lnk` / `.url`） | ✅ |
| 计划任务 | 带 `LogonTrigger` / `BootTrigger` 的任务 | ✅ |
| UWP 应用 | `AppModel\SystemAppData` | ⚠️ 受限（系统不允许第三方延后，改为"禁系统自启 + 到点激活"） |
| 手动添加 | 任意 exe / lnk | ✅ |
| 系统服务 | — | ❌ 用 Windows 原生「延迟自动启动」，不由本程序接管 |
| 驱动 / Winlogon / 登录脚本 | — | ❌ 只读展示，不可操作 |

---

## 文档

AI 编码代理与新人请先读 **[`Agents.md`](Agents.md)** —— 硬约束速查、需求编号体系、常用命令。

| 文档 | 内容 |
|---|---|
| [`docs/design.md`](docs/design.md) | **当前方案单一来源**：需求（FR/NFR/E）、架构与关键机制、调度端交互、编码规范、开发与交付流程 |
| [`docs/decisions.md`](docs/decisions.md) | D1–D68 决策索引 + R1–R13 风险去向 |
| [`docs/pitfalls.md`](docs/pitfalls.md) | 踩坑大全：Win32 / 注册表 / 计划任务 / UWP / 降权 / AOT / WinUI 3 / 安装器 / 本机环境 |

---

## License

见 [LICENSE](LICENSE)。
