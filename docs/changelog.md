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

## R3 — 分发形态改判：D22（2026-09-19）

**主题**：MSIX 被否决，交付形态定为 **Inno Setup 安装器 + unpackaged**，取代 D8 的「第一版免安装 zip」。

### 起因

用户提出：想要**安装包**，zip 解压路径会影响计划任务；若 WinUI 3 只能出 MSIX 那也不强求 unpackaged。这一问直接指向了一个此前的设计漏洞 —— 我们从未认真对待「计划任务的 action 指向哪个路径」。

### MSIX 被否决的三条理由（按力度）

| # | 理由 | 依据 | 后果 |
|---|---|---|---|
| 1 | 🔴 打包应用**提权只在 Windows 11+ 可用** | WindowsAppSDK issue #896 官方 feature status：支持打包应用提权需要 OS 层改动 | 与 `NFR-5.1`（Win10 21H2+）**直接冲突** → D20 在 Win10 上做不到 |
| 2 | 🔴 **注册表虚拟化**吞掉 HKCU 运行时写入 | MS Learn *Flexible virtualization*：写入进入 per-app / per-user 私有结构，只有本应用可见 | HKCU 侧软禁用**静默失效**（任务管理器、调度端都看不到标记）—— 本项目明令禁止的失败模式 |
| 3 | 🔴 **安装路径含版本号** | `...\WindowsApps\<Name>_<Version>_<Arch>__<PublisherId>` | 每次更新路径变 → 计划任务 action 失效 → 这正是要解决的问题 |

**理由 2 的补救路径也堵死**：关虚拟化需要 `unvirtualizedResources` + `allowElevation` 两个**受限能力**，而带受限能力的包 **App Installer / 双击 .msix 装不上**，只能提权 `Add-AppxPackage` 或自写 bootstrapper —— 分发体验比原生安装器更差。

### D22 落地内容

- 应用形态：`WindowsPackageType=None`（unpackaged），真实注册表、无虚拟化
- 安装到**固定路径** `{autopf}\DelayStart`，路径不含版本号 → 计划任务 action 长期有效
  > ⚠️ **该路径已被 R4 / D23 取代**：改为 `%LOCALAPPDATA%\Programs\DelayStart`（per-user）。本节保留为当时的决策记录。
- 安装器：Inno Setup（本机已装 6.7.3），脚本入库 `installer/DelayStart.iss`，自身提权并负责注册计划任务
- 自包含 / 框架依赖**由 R9 结果决定**；框架依赖分支下由安装器代装 Windows App Runtime（R9 降级路径①的用户代价因此消失）

### 新增硬约束（🔴）

- **卸载必须先还原全部接管项再删文件**（`NFR-6.4`）。还原失败则**中止卸载**。
  违反这条的后果是：用户卸载后所有程序永久不自启，而他不知道原因 —— 这会是本项目最严重的缺陷。
- 计划任务清理失败必须提示，不得静默忽略
- 实际安装路径必须写进 `config.json`

### 同轮修正的文档缺陷与命令错误

| 项 | 问题 | 处理 |
|---|---|---|
| VS 组件 ID | 我上一轮给的 `Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs` **在 VS 18 catalog 中不存在** | 改为实测存在的 `Microsoft.VisualStudio.ComponentGroup.WindowsAppDevelopment.Prerequisites`（及其依赖 `...Component.WindowsAppSdkSupport.CSharp`）。核对方式写进 `build-and-test.md` 1.2 |
| VS Installer 报错 | 非提权终端跑 `--passive` 直接 `Exit Code: 5007` | 已在 1.2 写明必须从**管理员**终端启动（`isadmin=True` ≠ `iselevated=True`） |
| 悬空引用 | `changelog` / `README` 引用 `build-and-test.md` **9.0**，但该节不存在 | 补齐 9.0「Phase 0 出口验证（R9）」 |
| 行尾策略未落地 | `.gitattributes` 只有 `* text=auto`，未定工作区行尾 | 改为 `* text=auto eol=lf` + `*.sln text eol=crlf`（例外）+ 二进制资源显式声明 |
| CLI 契约缺失 | `--restore-all` 是卸载链路的必需入口，却没有任何文档定义 | 在 `build-and-test.md` 5.1 补齐**命令行参数表**（3 个参数 + 调用方） |

### 落点索引

| 内容 | 章节 |
|---|---|
| D22 完整论证（含 MSIX 三条否决理由） | `design-spec.md` 6.4 |
| NFR-6 分发指标 / 9.4 分发验收 | `requirements.md` 第六、九节 |
| 工具链前置依赖（VS 组件正确 ID、5007 坑） | `build-and-test.md` 1.2 |
| 打包与安装器要点 | `build-and-test.md` 第七节 |
| 安装 / 升级 / 卸载验证步骤 | `build-and-test.md` 9.8 |
| 命令行参数表 | `build-and-test.md` 5.1 |
| 硬约束速查 14 / 15 | `docs/README.md` |

### 关联编号

`D22`、`D8`（被取代）、`NFR-6.3`–`6.6`、`NFR-5.1`、`R9`、`D20`

---

## R4（2026-09-19）：安装与数据布局（D23）+ 工具链核对

用户给出两项明确要求：① 核对工具链安装结果；② 安装目录不放 Program Files，改 per-user，并拆分配置与日志。

### 环境核对结果（实测，非推测）

| 项 | 结论 |
|---|---|
| VS 组件 | ✅ **已装**。`state.json`（已安装集合）实测含 `Component.WindowsAppSdkSupport.CSharp` + `ComponentGroup.WindowsAppDevelopment.Prerequisites` + `Microsoft.WindowsAppSDK.Cs.Dev17`；`MSBuild\Microsoft\WindowsXaml` 已落盘。**证明上一轮修正的 ID 正确**，当时那次 `Exit Code 5007` 是「ID 不存在 + 终端未提权」两个原因叠加 |
| Inno Setup | ✅ 6.7.3 已装 |
| WinUI dotnet 模板包 | ❌ **未装回**。`dotnet new list winui` → 无匹配；`dotnet new details winui-navview` → 找不到。Phase 0 第①步必须先跑 `dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`（NuGet 最新仍为 `0.0.6-alpha`，无正式版） |

顺带确认：VS 侧 WinUI 项目模板只有 `PackagedApp` / `SingleProjectPackagedApp` / `ClassLibrary` / `UnitTestApp` **四种，没有 unpackaged 模板** —— 无论走 CLI 还是 VS，**转 unpackaged 这一步都省不掉**。

### D23 落地内容

| 项 | 结论 |
|---|---|
| 安装目录 | `%LOCALAPPDATA%\Programs\DelayStart`（per-user，**不放 Program Files**），**只读** |
| 配置 | `%APPDATA%\DelayStart\config.json`（**Roaming**） |
| 日志 / 实时状态 / 运行归档 | `%LOCALAPPDATA%\DelayStart\`（**Local**） |
| 安装器权限 | `PrivilegesRequired=lowest` —— **安装零 UAC**；卸载弹一次 UAC（还原 HKLM 接管项 + 删计划任务必须有管理员） |
| 计划任务注册 | 从安装器**移到管理端首次启动**（`--reinstall-task` 幂等）；卸载仍由 `--restore-all` 提权完成 |

### 新增需求与硬约束

- `NFR-6.7` 安装目录只读 · `NFR-6.8` 计划任务身份必须是交互用户（🔴 禁止 SYSTEM）
- `architecture.md` **新增 1.5 路径与数据目录**：全部路径经 `PathService` 解析 + 6 条实现规则（含调试用 `DELAYSTART_LOCAL_DIR` / `DELAYSTART_CONFIG_DIR`）
- 新增 `E19`：程序目录被移动 / profile 变更 → 启动时对比实际路径与 `config.json` 记录，不一致则提示重建计划任务
- 🔴 本轮新识别的隐性坑：**SYSTEM 身份下 `%APPDATA%` 指向 `C:\Windows\System32\config\systemprofile`** —— 不报错、静默失效，危险等级与 MSIX 注册表虚拟化同级
- 🔴 Roaming 的代价：域环境漫游配置可能把机器相关条目带到另一台机器 → 加载时必须按 `ItemKey` 校验存在性，失效条目标记「已失效」不参与调度（复用 E3）

### 落点索引

| 内容 | 章节 |
|---|---|
| D23 路径规范与实现规则 | `architecture.md` 1.5 |
| 数据契约表 | `requirements.md` 第七节 |
| 安装器要点（含卸载提权链） | `build-and-test.md` 7.1 / 7.2 |
| 调度端路径与运行身份约束 | `scheduler-design.md` 第六节 |
| 安装 / 卸载验收 | `requirements.md` 9.4 · `build-and-test.md` 9.8 |

### 关联编号

`D23`、`NFR-6.5`–`6.8`、`E18`、`E19`、`D22`、`D19`、`架构 1.5`

---

## 下一步

**Phase 0**：先按 `build-and-test.md` 1.2 装齐工具链前置（VS 组件 + WinUI 模板包已卸载需装回）→ 用 `dotnet new` 生成骨架 → 转 unpackaged → **实测 R9**。
出口条件：`dotnet build` 全绿、R2 / R3 / R8 已确认、**R9 有明确结论**（结论决定自包含取值）。
