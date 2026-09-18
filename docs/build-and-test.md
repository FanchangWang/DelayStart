# DelayStart — 编码 / 编译 / 运行 / 测试流程

> 本文是**可执行的**流程说明。所有命令都在本机实测过路径。
> 规范见 `coding-standards.md`，架构见 `architecture.md`。

---

## 一、环境基线（本机实测）

| 项 | 实测值 | 位置 |
|---|---|---|
| 操作系统 | Windows 11 | — |
| .NET SDK | **10.0.401** | `C:\Program Files\dotnet\dotnet.exe` |
| Visual Studio | **18.10.1 (Community)** = VS 2026 | `C:\Program Files\Microsoft Visual Studio\18\Community` |
| Windows SDK | **10.0.26100.0** | `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0` |
| NuGet 全局包目录 | `C:\Users\guyue\.nuget\packages\` | — |
| NuGet 源 | `nuget.org` + VS Offline Packages | **未配置国内镜像** |

`dotnet --list-sdks` 输出：

```
10.0.401 [C:\Program Files\dotnet\sdk]
```

> **注意**：只在 SDK 列表里看到 10.0.401 一个版本。若后续需要多版本并存，用 `global.json` 在仓库根固定 SDK 版本，避免"在我机器上能编译"。

### 1.1 首次克隆后

```bash
# 1. 确认 SDK 版本
dotnet --version          # 期望 10.0.401

# 2. 还原依赖
dotnet restore DelayStart.slnx
```

**NuGet 慢时的处理**（本机未配镜像）：

- 优先尝试项目级镜像源配置（`NuGet.config`，随仓库提交），而不是改全局配置
- 若仍失败，按项目约定走代理 `http://192.168.31.50:7890`：
  ```bash
  export HTTPS_PROXY=http://192.168.31.50:7890
  export HTTP_PROXY=http://192.168.31.50:7890
  dotnet restore DelayStart.slnx
  ```
- **不要陷入重试循环**。连续失败 2 次就停下来查原因（网络 / 源 / 包版本），不要靠反复重试解决

---

## 二、解决方案结构

```
DelayStart.slnx                      # XML 格式解决方案
├─ src/DelayStart.Core               # ★ AOT 安全，被调度端引用
├─ src/DelayStart.Management         # 扫描 / 接管 / COM 互操作
├─ src/DelayStart.App                # WinUI 3 管理端
├─ src/DelayStart.Scheduler          # WinForms + NativeAOT 调度端
└─ tests/DelayStart.Core.Tests       # xUnit v3 单元测试
```

**解决方件格式说明**：`.slnx` 需要 .NET 9+ SDK 与 VS 17.14+。本机 SDK 10.0.401 / VS 18 满足。**若 VS 打开报错，回退为 `.sln`**（见 `architecture.md` R8）。

### 2.1 创建骨架：全部用 `dotnet new`

**不手写 csproj，也不从 `demo/` 复制**。`demo/` 只是 API 与注册表用法的参考（见 `api-analysis.md`），不是项目范例。

```bash
# ① 安装模板包（一次性，装到用户级模板缓存）
dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates
dotnet new install xunit.v3.templates

# ② 解决方案与仓库级配置文件
dotnet new sln -n DelayStart -f slnx
dotnet new globaljson --sdk-version 10.0.401 --roll-forward latestFeature
dotnet new editorconfig
dotnet new buildprops        # Directory.Build.props
dotnet new packagesprops     # Directory.Packages.props

# ③ 项目
dotnet new classlib -o src/DelayStart.Core       -f net10.0
dotnet new classlib -o src/DelayStart.Management -f net10.0
dotnet new winui-navview -o src/DelayStart.App   -n DelayStart.App -tfm net10.0
dotnet new winforms -o src/DelayStart.Scheduler  -n DelayStart.Scheduler
dotnet new xunit3 -o tests/DelayStart.Core.Tests -f net10.0

# ④ 加入解决方案
dotnet sln DelayStart.slnx add ^
  src/DelayStart.Core/DelayStart.Core.csproj ^
  src/DelayStart.Management/DelayStart.Management.csproj ^
  src/DelayStart.App/DelayStart.App.csproj ^
  src/DelayStart.Scheduler/DelayStart.Scheduler.csproj ^
  tests/DelayStart.Core.Tests/DelayStart.Core.Tests.csproj
```

> `.gitignore` 已存在且已定制，**不要再跑 `dotnet new gitignore`**（会覆盖，需 `--force` 才生效）。
> 生成后各项目的 TFM / 属性按 `architecture.md` 1.3 覆盖，公共属性集中到 `Directory.Build.props`。

**模板事实（本机实测，2026-09-19）**：

| 项 | 实测值 |
|---|---|
| WinUI 模板包 | `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`，版本 **`0.0.6-alpha`**（发布者 Microsoft） |
| 可用 WinUI 模板 | `winui`(Blank App) / `winui-navview` / `winui-mvvm` / `winui-lib` / `winui-unittest` 等 |
| 模板默认 `Microsoft.WindowsAppSDK` | **`1.8.260317003`** |
| 模板默认 WindowsAppSDK / 平台版本 | TFM `net10.0`，target platform min 默认 `10.0.26100.0` |
| xUnit 模板包 | `xunit.v3.templates`，短名称 **`xunit3`** |
| `dotnet new sln` 的 `--format` 默认值 | **`slnx`**（.NET 10 起） |

**为什么选 `winui-navview` 而不是 `winui`**：管理端就是左侧 NavigationView + 6 个顶层模块（见 `design-spec.md` 第三节），navview 模板直接给出可运行的导航骨架。`winui-mvvm` 会带入 CommunityToolkit.Mvvm 与示例代码，与 `architecture.md` 5.2 的 MVVM 约定不完全一致，不采用。

**⚠️ 关键限制：所有 WinUI 3 模板生成的都是 MSIX 打包工程，没有 unpackaged 开关。** 本项目要 unpackaged（D8 免安装 zip），因此生成后必须手工改造：

| 改造项 | 内容 |
|---|---|
| csproj 改 | 加 `<WindowsPackageType>None</WindowsPackageType>`、`<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`、`<ApplicationManifest>app.manifest</ApplicationManifest>` |
| csproj 删 | `<EnableMsixTooling>`、`<PublishProfile>`、`AppxPackage*` 系列属性 |
| csproj 删 | `Microsoft.Windows.SDK.BuildTools.WinApp` 包引用（它是为"打包应用的 `dotnet run`"服务的，unpackaged 用不上） |
| 文件删 | `Package.appxmanifest`；`Assets/` 只保留实际用作图标的文件 |
| 文件增 | `app.manifest`：`requestedExecutionLevel level="requireAdministrator"` + DPI 感知声明（见 `architecture.md` 1.4） |

> **具体删除清单在 Phase 0 以实际生成物核对后固化**，此处不预先拍脑袋。

**模板包是 alpha 版 —— 这是事实，且只影响"生成一次"这一步**：产物是普通文本文件，之后与模板无耦合。若 Phase 0 发现生成物不可用，降级为**手写 csproj**（WinUI 3 unpackaged 的 csproj 只有约 30 行属性，见 `architecture.md` 1.3）。

---

## 三、构建

### 3.1 日常构建

```bash
# 全解决方案 Debug 构建
dotnet build DelayStart.slnx -c Debug

# Release 构建（提交前必须跑）
dotnet build DelayStart.slnx -c Release

# 单个项目
dotnet build src/DelayStart.Core -c Release
```

**【必须】** 验收标准是**零警告**。`TreatWarningsAsErrors` 已开启，出现警告即编译失败——这是故意的，不要用 `#pragma warning disable` 绕过。

### 3.2 Core 层的 AOT 兼容性检查

`DelayStart.Core` 开了 `IsAotCompatible=true`，构建时会报出 AOT 不兼容的 API 使用：

```
warning IL2026: Using member '...' which has 'RequiresUnreferencedCodeAttribute'
warning IL3050: Using member '...' which requires dynamic code
```

**【必须】** 这两类警告**不得出现**在 `Core` 项目中。一旦出现，说明有 COM / 反射代码混进了 Core 层——按 `architecture.md` 1.1，它应该被移到 `Management`。

### 3.3 若 `dotnet build` 在 WinUI 3 项目上失败

WinUI 3 项目在某些配置下需要 VS 的 MSBuild（因为它依赖 VS 安装的 Windows SDK 目标文件）。标准路径：

```
C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
```

```bash
# 用 VS 的 MSBuild 构建解决方案
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    DelayStart.slnx /p:Configuration=Release /restore
```

> **Phase 0 必须确认这一项**（见 `architecture.md` R3 / R8）。若 CLI 能构建 WinUI 3，全流程走 `dotnet`；若不能，WinUI 项目单独走 MSBuild，其余项目仍走 `dotnet`。

---

## 四、测试

### 4.1 框架：xUnit v3（D21）

**选型结论**：`xunit.v3`（MTP 版包 `xunit.v3.mtp-v2`）。对应决策点 **D21**（✅ 已批复）。

```bash
# 主路径：安装 v3 模板（一次性），然后创建测试项目
dotnet new install xunit.v3.templates
dotnet new xunit3 -o tests/DelayStart.Core.Tests -f net10.0

# 降级路径：若模板安装失败（网络/源问题），用 SDK 内置模板
dotnet new xunit -o tests/DelayStart.Core.Tests -f net10.0
```

> **创建后必须手改两处**：`<TargetFramework>` 从 `net10.0` 改为 **`net10.0-windows`**（与 `Core` / `Management` 对齐）；删掉模板生成的 `UnitTest1.cs`。
>
> 走降级路径时，csproj 会多出 `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk` 走 VSTest 老路径。**测试代码本身零改动**——`[Fact]` / `[Theory]` / `Assert` 在两个版本间完全一致。

**为什么是 xUnit v3，而不是"微软推荐"**：微软对 WinUI 3 测试**没有单一推荐框架**，官方文档把 MSTest / NUnit / xUnit 三者并列。选择依据是技术性的：

| 框架 | 结论 | 理由 |
|---|---|---|
| **xUnit v3** | ✅ 选它 | 单一 NuGet 包、原生 MTP、`OutputType=Exe` 可独立运行、`Assert.Skip` / `Assert.SkipWhen` 支持条件跳过、无版本冲突 |
| xUnit v2 | 🟡 降级备选 | SDK 内置可用，但需额外的 VSTest 适配器包 |
| NUnit | ❌ 排除 | .NET 10 SDK 下已知缺陷：`NUnit3TestAdapter` 5.1.0 与 SDK 注入的 `Microsoft.Testing.Platform` 版本冲突，报 **CS1705** 且不给出可诊断的包依赖，必须手动 pin 两个平台包才能编译 |
| MSTest | ❌ 排除 | 独有价值是 `[UITestMethod]`（XAML 线程测试），**第一版不做 UI 自动化测试**，用不上 |

完整论证见 `design-spec.md` 6.3。

**测试项目的 TFM 只需 `net10.0-windows`**——**不需要** `net10.0-windows10.0.19041.0`，**不需要** `WindowsAppSDKSelfContained`，**不需要**预装 Windows App Runtime，**也不需要** `RuntimeIdentifiers`。

> 微软文档 *Test apps built with the Windows App SDK and WinUI 3* 里那套配置要求，前提是**测试项目直接引用 WinUI 3 项目**。本项目的测试只引用普通类库，所以不适用。**任何让测试项目引用 `DelayStart.App` 的改动都会把这些包袱带回来——不要这么做**（见 `architecture.md` 1.3）。

**【必须】** 测试项目以**普通权限**运行即可。不得要求管理员权限——单元测试不碰真实系统。若某个测试必须提权才能跑，说明它不该是单元测试，应移到手工验证清单。

### 4.2 运行

```bash
# 全部测试（走 Microsoft Testing Platform）
dotnet test DelayStart.slnx

# 带覆盖率
dotnet test DelayStart.slnx --collect:"XPlat Code Coverage"

# 单跑某个项目 / 某个测试
dotnet test tests/DelayStart.Core.Tests
dotnet test tests/DelayStart.Core.Tests --filter "FullyQualifiedName~DelayCalculator"

# xUnit v3 是 OutputType=Exe，也可直接跑（输出更干净，退出码可靠）
dotnet run --project tests/DelayStart.Core.Tests
```

> **AI 与 CI 场景优先用 `dotnet run`**：xUnit v3 的独立可执行模式下，stdout 直接可读、退出码语义明确，不需要解析 VSTest 的输出格式。

**【必须】** 提交前 `dotnet test`（或 `dotnet run --project tests/DelayStart.Core.Tests`）必须全绿。

### 4.3 测试范围

| 层 | 必测 |
|---|---|
| `Core` | `DelayCalculator` 时序计算（含 `remaining <= 0` 过期分支） |
| `Core` | `ItemKeyBuilder` 主键生成与稳定性 |
| `Core` | `CommandLineService` 路径/参数解析（引号、空格、环境变量） |
| `Core` | `LaunchResultEvaluator` 成功判定三分支 |
| `Core` | `ConfigService` 加载 / 迁移 / 损坏恢复 |
| `Core` | `StartupSortComparer` 排序稳定性 |
| `Management` | 连续失败计数（纯函数部分，给定 `RunRecord` 列表 → 连续失败次数） |

**不测（真机手工验证，见第九节）**：注册表实际读写、计划任务实际创建、进程实际启动与降权、UI 渲染与交互、托盘图标与通知、提权 manifest 是否生效。

**【必须】** 单元测试中**禁止**触碰真实注册表、真实文件、真实进程。所有系统交互通过 `IClock` / `IProcessLauncher` / `IAppConfigStore` 等接口注入假实现（见 `coding-standards.md` 十四）。

---

## 五、运行（开发期）

### 5.1 管理端

管理端是**全程提权**的应用（D20），这会影响调试方式。

```bash
# 直接运行构建产物（会弹 UAC，这是预期行为）
src/DelayStart.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/DelayStart.App.exe

# dotnet run 也可用，但因提权后进程脱离父控制台，stdout 会分离，不便于看日志
dotnet run --project src/DelayStart.App
```

命令行参数（用于验证通知跳转链路）：

```bash
src/DelayStart.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/DelayStart.App.exe --goto-log --run=20260919-084112
```

**【必须】** 开发期调试管理端的正确做法：**以管理员身份运行 Visual Studio**，然后 F5 启动。否则每次 F5 都会弹一次 UAC。

> **提权状态下 VS 的"附加到进程"仍可用**，但**热重载（Hot Reload）不可用**——这是提权调试的固有代价，接受它。

> WinUI 3 unpackaged 运行需要 Windows App Runtime。本项目设为 `WindowsAppSDKSelfContained=true`，因此**不依赖系统预装的运行时而直接可跑**。代价是发布体积增大（见第七节）。
>
> **但自包含 + 提权有冲突风险（R9）**，Phase 0 必须先验证。若不通过，按 `architecture.md` R9 的三条降级路径调整（首选去掉自包含改为框架依赖）。

### 5.2 调度端

```bash
# 开发期直接跑（非 AOT，方便调试）
dotnet run --project src/DelayStart.Scheduler

# 跑 AOT 产物（真实性能表现）
src/DelayStart.Scheduler/bin/Release/net10.0-windows/win-x64/publish/DelayStart.Scheduler.exe
```

**【必须】** 调度端调试时注意：它会**真的按延时启动程序**。调试前先把 `config.json` 里的条目延时调小（如 3 / 6 / 9 秒）或用测试专用配置目录，避免等待。用环境变量 `DELAYSTART_DATA_DIR` 覆盖数据目录（该开关需要在实现时加上，仅调试用）。

---

## 六、NativeAOT 发布（调度端）

### 6.1 命令

```bash
dotnet publish src/DelayStart.Scheduler -c Release -r win-x64
```

`PublishAot=true` 已在项目文件中固定，不需要命令行再指定。

### 6.2 验收检查

```bash
# 1. 产物大小（期望约 6 MB）
ls -la src/DelayStart.Scheduler/bin/Release/net10.0-windows/win-x64/publish/

# 2. 冷启动耗时（期望 < 0.3 秒）
#    调度端会在 scheduler.log 首行记录一次启动耗时（见 architecture.md 8.2）
```

**【必须】** 发布时必须**零 AOT 警告**：

```
warning IL2026 / IL3050 / IL3053
```

出现任何一个都要处理，不能放着——它们在开发机上可能不发作，在用户机器上一定发作。处理方式：

| 警告来源 | 处理 |
|---|---|
| 第三方包用了反射 | 换包，或把该功能移到 `Management` 层 |
| `System.Text.Json` 反射序列化 | 改用 `JsonSerializerContext` 源生成 |
| 资源加载 | 改用 `Assembly.GetManifestResourceStream`，不用 `.resx` |
| COM 互操作 | 该代码不该在调度端链路里 |

### 6.3 发布配置

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net10.0-windows</TargetFramework>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <PublishAot>true</PublishAot>
  <UseWindowsForms>true</UseWindowsForms>
  <InvariantGlobalization>true</InvariantGlobalization>
  <SelfContained>true</SelfContained>
  <StripSymbols>true</StripSymbols>
</PropertyGroup>
```

> `<OutputType>WinExe</OutputType>` 是**硬要求** —— 用 `Exe` 会在登录瞬间闪一个控制台黑框。

---

## 七、打包

### 7.1 免安装 zip（第一版，对应 D8 选项 A）

```bash
# 1. 发布两个目标
dotnet publish src/DelayStart.App       -c Release -r win-x64 -o dist/app
dotnet publish src/DelayStart.Scheduler -c Release -r win-x64 -o dist/scheduler

# 2. 组装目录
dist/DelayStart/
├─ DelayStart.exe              # 管理端
├─ DelayStart.Scheduler.exe    # 调度端
├─ LICENSE
└─ README.md

# 3. 压缩
Compress-Archive -Path dist/DelayStart -DestinationPath dist/DelayStart-<版本>.zip -Force
```

**体积预期**：管理端自包含约 100–150 MB（WinUI 3 的固有代价），调度端约 6 MB。zip 压缩后管理端约 40–60 MB。

> 若体积不可接受，替代方案是管理端改为**框架依赖**（需用户装 Windows App Runtime，体积降到 ~10 MB）。这属于 D8 范围内的取舍，**编码前需确认**。

### 7.2 安装器（发布阶段，D8 选项 C）

用 Inno Setup：安装时注册计划任务、写开始菜单快捷方式（Toast 通知需要带 AppUserModelID 的快捷方式）。第一版不做。

---

## 八、计划任务管理（测试用）

调度任务名固定为 `DelayStartScheduler`。

```bash
# 查询（确认 onlogon / delay 3s / RunLevel=Highest）
schtasks /query /tn DelayStartScheduler /v /fo LIST

# 手动触发一次（不等重启）
schtasks /run /tn DelayStartScheduler

# 从配置文件重新注册
dotnet run --project src/DelayStart.App -- --reinstall-task
```

**【必须】清理命令需人工确认后执行**（本项目规定：任何删除类操作都要经用户审批）：

```bash
schtasks /delete /tn DelayStartScheduler /f      # 删除测试残留的计划任务
```

---

## 九、手工验证清单（真机）

> **执行方式**：以下操作会**真实改变系统状态**（注册表、计划任务、实际启动程序、需要重启）。
> 按项目约定，**由用户手动执行或明确许可后再执行**，AI 不代为运行。

### 9.1 软禁用正确性（Phase 2 出口）

- [ ] **操作前**导出注册表留底：
  ```bash
  reg export "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" before-hkcu.reg
  reg export "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" before-hklm.reg
  reg export "HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" before-wow.reg
  ```
- [ ] 用本软件禁用某 HKCU 项 → 打开任务管理器「启动应用」，该项显示为**已禁用**
- [ ] 用本软件禁用某 HKLM 项 → 同上
- [ ] 用本软件禁用某 WOW6432Node 项 → 同上（**重点**：确认标记写进了 `Run32` 而不是 `Run`）
- [ ] 禁用启动文件夹中的 `.lnk` → 任务管理器显示已禁用，且**文件仍在原处**
- [ ] 禁用某计划任务 → `schtasks /query /tn <名>` 显示 `Status: Disabled`
- [ ] 全部禁用后，**重新导出对比**：三个 `Run` 键的值**数量与内容零变化**

### 9.2 接管与调度（Phase 4 出口）

- [ ] 把 3 个程序分别设为 10 / 20 / 30 秒延时，接管
- [ ] `schtasks /query /tn DelayStartScheduler /v /fo LIST` 确认触发方式为"登录时"、延迟 3 秒、最高权限
- [ ] **重启登录**，观察：
  - 各程序在预期时刻启动（秒表或录屏核对，偏差 < 1 秒）
  - `%LOCALAPPDATA%\DelayStart\runs\` 下生成当次运行记录
  - `current-run.json` 的 `finishedAt` 被写入
- [ ] 把某个条目的路径改成不存在的文件 → 重启 → 确认：气泡通知出现、托盘角标变红、管理端出现横幅
- [ ] 连续 3 次触发失败 → 确认托盘角标**不自动消失**、管理端横幅**不提供"忽略"**、**没有任何自动恢复行为**（该项仍是禁用状态）
- [ ] 点通知 → 管理端打开并定位到该次运行记录

### 9.3 恢复与可逆性（安全验收）

- [ ] 「移出延时启动」某系统条目 → 重启 → 确认系统按**原样**启动它
- [ ] 全部移出后，三个 `Run` 键与 9.1 的 `before-*.reg` **完全一致**
- [ ] 启动文件夹内容与操作前一致

### 9.4 权限与身份

**管理端（D20）**

- [ ] 双击管理端 exe → **弹出 UAC**（NFR-3.1）
- [ ] UAC 选「否」→ 提示「本程序需要管理员权限才能管理自启动项」后**退出**，不进入主界面（E17 / NFR-3.6）
- [ ] 手动添加 → `[浏览…]` 能正常打开文件对话框并选中 exe（提权下的文件选择，`InitializeWithWindow` 生效）
- [ ] 从资源管理器拖文件到「手动添加」拖放区 → **记录结果**（R10）。若不通，确认拖放区已降级为纯按钮且不影响完成添加
- [ ] 「以其他用户身份运行」→ 检测到配置目录不匹配时给出提示（E18 / NFR-3.8）

**被接管的条目**

- [ ] 「管理员」身份的条目启动时**不弹 UAC**
- [ ] 「普通用户」身份的条目启动后，在任务管理器中确认其**不是以管理员身份**运行
- [ ] 手动把降权开关关掉 → 该条目以管理员身份启动（验证开关生效）

### 9.5 调度端性能（NFR-1.2 / NFR-1.5）

- [ ] 发布产物大小约 6 MB
- [ ] `scheduler.log` 首行的启动耗时 < 300 ms
- [ ] 任务管理器观察：调度端驻留期间 CPU 接近 0，内存 < 30 MB

### 9.6 界面（Phase 3 / 5 出口）

- [ ] 100% / 150% / 200% DPI 下逐页检查，无错位、无文字截断
- [ ] 明暗主题切换，检查对比度（特别是状态色与禁用态）
- [ ] **不做**100% 缩放下的截图验收——必须在 150% 和 200% 下也看一遍

### 9.7 模拟调度（Phase 6）

- [ ] 配置 3 个条目（3 / 6 / 9 秒） → 点「立即模拟调度」→ 加速播放 → 程序按时间轴真实启动
- [ ] 中途点停止 → 不残留状态、不继续启动剩余项

---

## 十、本机环境已知坑（实测）

这几条会直接浪费你的时间，每次都遇到。

### 10.1 Bash 工具的 PATH 是坏的

**现象**：

```
dirname: command not found
bash.exe: line 1: ls: command not found
```

**原因**：本机 Bash 工具链的 shell runtime 初始化脚本失败，`ls` / `head` / `grep` / `dirname` 全部不可用。

**对策**：
- 不要用 Bash 工具做文件列举、文本搜索、管道过滤
- **改用**：`Glob`（找文件）、`Grep`（搜内容）、`Read`（读文件）、`PowerShell`（跑命令）
- `git` 命令走 PowerShell 可以正常执行

### 10.2 PowerShell 的命令输出不会回显到终端

**现象**：命令返回 `Command completed with exit code 0`，但看不到任何 stdout。

**对策**：把输出重定向到文件，再用 `Read` 读：

```powershell
$out = "D:\Works\DelayStart\.workbuddy\tmp\check.txt"
[System.IO.File]::WriteAllText($out, "")            # 先清空
& dotnet build 2>&1 | Out-File $out -Encoding utf8
Add-Content $out ("EXIT: " + $LASTEXITCODE)
"DONE" | Out-File $out -Append -Encoding utf8
```

然后 `Read` 这个文件。**必须带 `DONE` 之类的结束标记**，否则无法判断是输出读完了还是命令还没跑完。

### 10.3 外部命令的中文输出会乱码

**现象**：`dotnet workload list` / `dotnet nuget list source` 的中文输出变成 `宸ヤ綔璐熻浇` 这样。

**原因**：外部进程按 GBK 输出，PowerShell 按别的编码读入，再写成 UTF-8 时乱码被固化。

**对策**：执行前先设置编码：

```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
```

对必须读中文输出的命令，加这一段。

### 10.4 安全策略会拦截含可执行文件字面量的命令

**现象**：整条 PowerShell 命令被拒绝，返回 `Known LOLBin executable that can run arbitrary code outside PowerShell validation`。

**触发条件**：命令里出现完整的 `.exe` 路径并参与变量赋值/判断时。

**对策**：把探测逻辑拆开，或在文档里直接写明标准路径而不去运行时探测。不要试图绕过安全策略。

### 10.5 `tasklist` / `schtasks` 的中文列名

用 `/fo LIST` 而不是 `/fo TABLE` 输出，避免中文表格列宽错位导致解析失败。**不要用中文列名做字符串匹配来解析输出**，改用 `TaskService` API。

---

## 十一、常见故障排查

| 症状 | 原因 | 处理 |
|---|---|---|
| `dotnet build` 报 `IL2026` / `IL3050` | Core 层混入了反射/COM 代码 | 移到 `Management`（见 `architecture.md` 1.1） |
| WinUI 3 项目 CLI 构建报找不到 Windows SDK 目标 | SDK 未通过 VS 组件安装 | 改用 VS 的 MSBuild（3.3），或安装对应 VS 组件 |
| 发布后调度端启动闪黑框 | `OutputType` 是 `Exe` | 改成 `WinExe` |
| 调度端发布失败，报 AOT 不支持的 API | 引用了不兼容的包 | 换包或隔离到 `Management` |
| 托盘图标不显示 | 图标用 `.resx` 加载 | 改嵌入资源 + `GetManifestResourceStream` |
| 托盘图标显示但糊 | 用了 16×16 图标 | 提供 16/20/24/32 多尺寸 `.ico` |
| 列表里图标模糊（高 DPI） | 用了 `SHGetFileInfo` 的 32×32 | 改 `IShellItemImageFactory` 按 DPI 取（见 `architecture.md` 3.4） |
| 气泡通知不出现 | 通知被系统"专注助手"拦截，或托盘图标未创建 | 检查专注助手；确认通知前托盘图标已创建 |
| 计划任务创建失败 | ACL / 组策略拒绝 | 指出具体条目，提示该项受系统策略保护（见 `architecture.md` R6） |
| 接管后某程序仍是开机自启 | `StartupApproved` 键名不匹配 | 检查三级回退逻辑（坑 1） |
| WOW6432Node 项禁用无效且无报错 | 标记写到了 `Run` 而不是 `Run32` | 见 `api-analysis.md` 坑 2 |
| 恢复后程序还是不自启 | 恢复时 hive 判断错误（静默失效） | 检查是否用了显式 `scope` 枚举（坑 5） |
| 单项失败导致整批中止 | 缺少逐条 try/catch | 见 FR-1.4 / FR-5.5 |

---

## 十二、提交前检查清单

每次提交前逐项过一遍。

### 必跑命令

```bash
dotnet build DelayStart.slnx -c Release      # 零警告
dotnet test DelayStart.slnx                  # 全绿
```

改动涉及调度端时，额外：

```bash
dotnet publish src/DelayStart.Scheduler -c Release -r win-x64   # 零 AOT 警告，产物约 6 MB
```

### 必查内容

- [ ] `git status` 扫描改动文件，**逐个确认内容正确**（不只看文件名）
- [ ] `demo/` 与 `.workbuddy/` **未出现在**改动列表里
- [ ] 没有提交构建产物（`bin/` / `obj/` / `dist/`）
- [ ] 没有注释掉的死代码
- [ ] 新增的纯逻辑有对应单元测试
- [ ] 涉及坑点的代码有指向文档的注释
- [ ] 改动涉及真机行为时，已在第九节清单中标出并**由用户实测确认**

### 提交信息

按 `coding-standards.md` 第十五节的格式生成 commit message。

**【必须】** AI 只**生成** commit message，**不执行** `git commit` —— 由人工审阅后提交。例外：用户明确要求执行时。
