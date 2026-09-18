# DelayStart — 设计阶段变更记录

> **作用域：仅设计阶段**（文档建立 → 决策冻结 → 编码开始前）。
> 编码开始后本文件不再逐条维护，日常变更由 `git log` 承载。
>
> 它保留的是 **决策 ID → 文档章节的反查索引** —— 这部分 `git log` 给不了，用于从需求编号（`FR` / `NFR` / `R` / `D`）反查落地位置。
> 决策的完整论证在 `design-spec.md` 第六节；当前状态在 `README.md`「决策点状态」。

---

## R1 — 文档体系建立与原型评审（2026-09-19）

从 `demo/` 提取 API 与注册表知识，建立需求 / 架构 / 设计 / 规范 / 流程五类文档（提交 `842285c`）。

- `docs/` 确立为**唯一事实来源**；建立 `FR` / `NFR` / `E` / `R` / `D` / `坑` 六套编号体系
- **架构按 AOT 兼容性切分 `Core` 与 `Management`，不按领域** —— 调度端是 NativeAOT，而扫描所需的 `Microsoft.Win32.TaskScheduler`、`IShellLinkW`、`IShellItemImageFactory` 全都不兼容，混在一起调度端直接发布不了
- 可交互 HTML 原型 `ui-mockup.html` 交付评审，并配上可复跑的渲染冒烟测试
- `demo/` 与 `.workbuddy/` 加入 git 忽略

---

## R2 — 决策冻结（2026-09-19）

**主题**：D4–D13 批量批复、D20 改判、D21 定框架、新增两条 Phase 0 必须实测的风险、脚手架方式确定。

### D20 从「按需提权」改判为「全程提权」

原建议被否。评估口径错了 —— 不是按"需要提权的功能占比"算，而是**"有没有任何一个功能必须提权"**。有，就省不掉那次 UAC：

| 必须提权的操作 | 不提权的后果 |
|---|---|
| 注册调度器计划任务（还要求 `RunLevel=Highest`） | **软件根本不能工作** |
| HKLM Run / Run32 / 系统启动文件夹软禁用 | 拒绝访问 |
| 系统级 / 他人计划任务禁用 | 拒绝访问 |
| 服务切 `delayed-auto`（D5） | 拒绝访问 |

真正省下来的只有"纯查看"这一种用法。而按需提权还有个更隐蔽的代价：**用户连点 10 个禁用，HKCU 的 6 个成功、HKLM 的 4 个弹 UAC —— 系统进入一个用户无法理解的中间状态**，比"一开始就不让用"糟得多。

- 管理端改为 `app.manifest` `requireAdministrator`，与调度端权限一致
- UAC 被拒必须**退出、不降级运行**（`E17` / `NFR-3.6`）
- 明确不支持「以其他用户身份运行」（`E18` / `NFR-3.8`）
- 提权窗口的拖放主路径改 `[浏览…]`，拖放降为增强（`R10`）

### D21 定为 xUnit v3

**前提澄清：微软对 WinUI 3 测试没有单一推荐框架**，官方文档把 MSTest / NUnit / xUnit 三者并列。因此选型依据是技术性的：

| 框架 | 结论 | 原因 |
|---|---|---|
| NUnit | **排除** | .NET 10 SDK 下 `NUnit3TestAdapter` 与 SDK 注入的 `Microsoft.Testing.Platform` 版本冲突，报 `CS1705` 且包依赖不可诊断 |
| MSTest | 排除 | 独有价值 `[UITestMethod]` 属 XAML 线程测试，第一版不做 UI 自动化 |
| xUnit v3 | **选定** | 单一 NuGet 包、原生 MTP、`Assert.Skip`；主路径 `dotnet new install xunit.v3.templates`，降级路径用 SDK 内置 `xunit` 模板 |

**额外收益**：测试项目 TFM 只需 `net10.0-windows`，不引用 App、不需自包含、不需预装 Windows App Runtime —— 这是按 AOT 兼容性分层的白捡红利（`architecture.md` 1.3）。

### 新增两条 Phase 0 必须实测的风险

| 风险 | 内容 | 影响 |
|---|---|---|
| **R9** 🔴 | `WindowsAppSDKSelfContained=true` 时 `app.manifest` 的 `requestedExecutionLevel` 可能被忽略 | 管理端不弹 UAC 也不提权 → **直接推翻 D20**。附三条降级路径：去自包含 / 运行时 `runas` 自重启 / 外部 AOT 启动器 |
| **R10** 🟡 | 提权进程受 UIPI 限制收不到 OLE 拖放，而 WinUI 的 `AllowDrop` 走的正是 OLE，`ChangeWindowMessageFilterEx` 救不回来 | 「手动添加」主路径改为 `[浏览…]` 按钮，拖放降为增强，不允许留"拖进去没反应"的控件 |

> **R9 不通过就不进入 Phase 1。** 这是唯一会推翻已批复决策的风险。

### 权限语义修正

管理端全程提权后，`UnauthorizedAccessException` **不再等于"需要提权"**，而是"受 ACL / 组策略保护"。失败原因枚举由 `NeedElevation` 改为 `AccessDenied`，且要求异常消息**带具体条目标识** —— 否则用户会被提示"请以管理员身份运行"，而他已经在管理员身份下。

### 脚手架方式确定

- 项目创建统一走 `dotnet new`，**不手写 csproj、不从 `demo/` 复制**
- 实测确认：WinUI 3 模板包 `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` 由微软官方发布，但**版本为 `0.0.6-alpha`**
- **所有 WinUI 3 模板只生成 MSIX 打包工程，没有 unpackaged 开关** —— 生成后必须手工改造才能满足 D8 的免安装 zip（改造清单见 `build-and-test.md` 2.1）
- 模板默认 `Microsoft.WindowsAppSDK` 版本 `1.8.260317003`，写入 CPM 固定

### 落点索引

| 内容 | 章节 |
|---|---|
| D20 完整论证 | `design-spec.md` 6.2 |
| D21 完整论证 | `design-spec.md` 6.3 |
| 权限模型 | `architecture.md` 1.4 |
| R9 / R10 完整分析与降级路径 | `architecture.md` 第九节 |
| Phase 0 验证清单（R9） | `build-and-test.md` 9.0 |
| 脚手架命令与模板实测事实 | `build-and-test.md` 2.1 |
| 提权约束 / xUnit v3 约定 | `coding-standards.md` 12.1 / 14.3 |
| `E17` / `E18` / `NFR-3.6`–`3.8` | `requirements.md` 第三、五、七节 |

### 关联编号

`D4`–`D13`、`D20`、`D21`、`R9`、`R10`、`E17`、`E18`、`NFR-3.1` / `NFR-3.6` / `NFR-3.7` / `NFR-3.8`

---

## 下一步

**Phase 0**：用 `dotnet new` 生成骨架 → 转 unpackaged → **实测 R9**。出口条件是 `dotnet build` 全绿且 R9 有明确结论。
