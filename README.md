# DelayStart · 延时启动管理器

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
| 管理端 | .NET 10 + WinUI 3 | 现代 Windows 原生观感 |
| 调度端 | WinForms + **NativeAOT**（约 6 MB，冷启动 < 0.3 秒） | 登录瞬间执行，体积和冷启动是硬指标；WinUI 3 的 AOT 仍是 preview |
| 共享层 | `DelayStart.Core`（AOT 安全）+ `DelayStart.Management` | 见下方说明 |

**为什么拆两个共享库**：调度端用 NativeAOT，它引用的一切代码都必须 AOT 兼容。而扫描所需的计划任务库、COM 互操作（`.lnk` 解析、图标提取）恰恰都不兼容。所以按 **AOT 兼容性** 切分而不是按领域切分——调度端只引用 `Core`，拿到一个零 COM、零反射的最小集合。

---

## 环境要求

- Windows 10 21H2+ / Windows 11
- .NET SDK **10.0.401**（实测版本）
- Visual Studio 2026（18.x）— 用于 WinUI 3 开发

---

## 安装位置与数据（D23）

**per-user 安装，安装过程不弹 UAC**（`PrivilegesRequired=lowest`）。程序与数据严格分离：

| 类别 | 路径 |
|---|---|
| 程序（只读） | `%LOCALAPPDATA%\Programs\DelayStart\` |
| 配置（Roaming） | `%APPDATA%\DelayStart\config.json` |
| 日志 / 实时状态 / 运行归档（Local） | `%LOCALAPPDATA%\DelayStart\logs\` · `state\` · `runs\` |

程序运行时**不向安装目录写任何东西**，所以覆盖升级与卸载不会留残留。计划任务在**管理端首次启动时**幂等注册（注册必须提权，全流程只弹这一次 UAC）。

---

## 快速开始

```bash
dotnet restore DelayStart.slnx
dotnet build   DelayStart.slnx -c Release
dotnet test    DelayStart.slnx
dotnet run --project src/DelayStart.App
```

完整流程（含 NativeAOT 发布、真机验证清单、本机环境已知坑）见 **[`docs/build-and-test.md`](docs/build-and-test.md)**。

---

## 文档

**入口：[`docs/README.md`](docs/README.md)** — 文档索引、需求编号体系、决策点状态、硬约束速查。

| 文档 | 内容 |
|---|---|
| [`docs/requirements.md`](docs/requirements.md) | 完整项目需求、异常场景矩阵、验收标准 |
| [`docs/architecture.md`](docs/architecture.md) | 项目结构、分层设计、数据模型、关键机制 |
| [`docs/design-spec.md`](docs/design-spec.md) | 界面设计与全量文案 |
| [`docs/scheduler-design.md`](docs/scheduler-design.md) | 调度端交互与技术约束 |
| [`docs/coding-standards.md`](docs/coding-standards.md) | 编码规范 |
| [`docs/build-and-test.md`](docs/build-and-test.md) | 编译 / 运行 / 测试流程 |
| [`docs/api-analysis.md`](docs/api-analysis.md) | Win32 / 注册表 / 计划任务调用方式与 9 个坑 |

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

## License

见 [LICENSE](LICENSE)。
