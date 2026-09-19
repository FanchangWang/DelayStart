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

### 1.2 工具链前置依赖（Phase 0 前必须齐）

**实测状态（2026-09-19）**：

| 依赖 | 状态 | 说明 |
|---|---|---|
| .NET SDK 10.0.401 | ✅ 已装 | — |
| VS 18.10.1 Community | ✅ 已装 | — |
| Windows SDK 10.0.26100.0 | ✅ 已装 | — |
| **VS 组件「.NET WinUI 应用开发工具」** | ✅ **已装**（2026-09-19 复核实测） | 装不上的话不阻塞编译（见注），但 VS 里 XAML 会退化成纯文本编辑 |
| **Inno Setup 6** | ✅ **已装 6.7.3** | `winget list -q innosetup` 实测。已是最新，**无需重跑安装** |
| **WinUI 命令行模板包** | ✅ **已装**（2026-09-19 Phase 0 装回，`0.0.6-alpha`） | `dotnet new list winui` 实测可见 `winui` / `winui-navview` / `winui-mvvm` / `winui-lib` / `winui-unittest` 等。**NuGet 上最新仍是 `0.0.6-alpha`，无正式版**——但生成物已验证可用（Phase 0 构建 0 警告 0 错误），模板只在"生成一次"这一步起作用 |
| **xUnit v3 模板包** | ✅ **已装**（`xunit.v3.templates` **4.0.1**） | 短名 `xunit3`。⚠️ 其 `-f` 默认值是 `net8.0`，创建时必须显式写 `-f net10.0` |

#### VS 组件的正确 ID（别再用错的）

```
Microsoft.VisualStudio.ComponentGroup.WindowsAppDevelopment.Prerequisites   ← 「.NET WinUI 应用开发先决条件」，装这个
Microsoft.VisualStudio.Component.WindowsAppSdkSupport.CSharp                ← 「.NET WinUI 应用开发工具」，上者的唯一依赖
```

> 🔴 **`Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs` 这个 ID 不存在。** 用它会失败。
> 上述两个 ID 来自本机 VS 18 实例的 `C:\ProgramData\Microsoft\VisualStudio\Packages\_Instances\<实例ID>\catalog.json`（20575 个包全量检索，实测确认）。找不到时用同样方式核对，**不要凭记忆写 ID**。

#### 【必须】`--passive` / `--quiet` 必须从提权终端启动

非提权终端下实测输出：

```
[7c50:0001][...] Commands with --quiet or --passive should be run elevated from the beginning.
[7c50:0001][...] Exit Code: 5007
```

注意日志里 `vs.willow.isadmin : True` 但 `vs.willow.iselevated : False` —— **在 Administrators 组里不等于令牌已提升**，必须开「管理员 PowerShell」。

```powershell
# 在「管理员」PowerShell 中执行
& "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" modify `
  --installPath "C:\Program Files\Microsoft Visual Studio\18\Community" `
  --add Microsoft.VisualStudio.ComponentGroup.WindowsAppDevelopment.Prerequisites `
  --passive --norestart
```

`--passive` = 显示进度但不需要人工点击。判定成功看**退出码不是 5007**（0 / 3010 都算成功，3010 表示需要重启）。

> **这个组件是体验项还是硬依赖？** 它提供 VS 内的 WinUI XAML 智能感知与设计器支持。理论上命令行构建不依赖它（XAML 编译目标随 `Microsoft.WindowsAppSDK` NuGet 分发），但**这一条属于 Phase 0 要实测的项**（见 9.0），不要当成既成事实。
> 附带说明：它是个**依赖树不小的组件**（会拉入 `Microsoft.WindowsAppSDK.Cs.Dev17`、MSIX 单项目打包工具、UWP 公共包组等），安装体积和耗时都比名字看起来大。
>
> **VS 侧的 WinUI 模板同样没有 unpackaged**。实测本机 `...\Common7\IDE\Extensions\<随机名>.vpr\ProjectTemplates\CSharp\1033\` 下只有 `WinUI.Desktop.Cs.PackagedApp` / `SingleProjectPackagedApp` / `ClassLibrary` / `UnitTestApp` 四个 —— **没有 UnpackagedApp**。所以"生成后手工转 unpackaged"这一步，走 CLI 还是走 VS 都省不掉。

#### 复核"到底装上没有"（不要靠记忆）

VS 的已安装集合是权威记录，直接读它：

```
C:\ProgramData\Microsoft\VisualStudio\Packages\_Instances\<实例ID>\state.json           # 已安装
C:\ProgramData\Microsoft\VisualStudio\Packages\_Instances\<实例ID>\state.packages.json  # 已选集合
C:\ProgramData\Microsoft\VisualStudio\Packages\_Instances\<实例ID>\catalog.json         # 全部可用包（约 20 MB，含 BOM）
```

在任一个里检索 `WindowsApp` 字符串即可。**判定标准**：`state.json` 里出现 `Component.WindowsAppSdkSupport.CSharp` 才算真装上；只出现在 `state.packages.json` 里说明只是"被选"，不一定落地。

---

## 二、解决方案结构

```
DelayStart.slnx                      # XML 格式解决方案
├─ src/DelayStart.Core               # ★ AOT 安全，被调度端引用
├─ src/DelayStart.Management         # 扫描 / 接管 / COM 互操作
├─ src/DelayStart.App                # WinUI 3 管理端
├─ src/DelayStart.Scheduler          # WinForms + NativeAOT 调度端
├─ tests/DelayStart.Core.Tests       # xUnit v3 单元测试
└─ installer/DelayStart.iss          # Inno Setup 安装器脚本（D22，Phase 6 产出）
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

# ④ 加入解决方案（bash 用 "\" 续行；cmd 用 "^"。这里给成一行的形式，两种 shell 都能跑）
dotnet sln DelayStart.slnx add src/DelayStart.Core/DelayStart.Core.csproj src/DelayStart.Management/DelayStart.Management.csproj src/DelayStart.App/DelayStart.App.csproj src/DelayStart.Scheduler/DelayStart.Scheduler.csproj tests/DelayStart.Core.Tests/DelayStart.Core.Tests.csproj
```

> **`winui-navview` 的 `-tpmv`（target-platform-min-version）实测不生效**：Phase 0 传了 `-tpmv 10.0.19041.0`，
> 产出的仍是模板默认 `<TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>`，
> TFM 也是 `<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>`。
> **这两个值一律手动改，别指望模板参数**（改法见下方改造清单）。

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

**⚠️ 关键限制：所有 WinUI 3 模板生成的都是 MSIX 打包工程，没有 unpackaged 开关。** 本项目要 unpackaged（D22 的安装器分发），因此生成后必须手工改造。

**改造清单（Phase 0 已按实际生成物固化，2026-09-19）**：

| 改造项 | 内容 |
|---|---|
| csproj 加 | `<WindowsPackageType>None</WindowsPackageType>`、`<WindowsAppSDKSelfContained>`（true / false **由 R9 结果定**，见 7.1）、`<RootNamespace>DelayStart.App</RootNamespace>`、`<AssemblyName>DelayStart</AssemblyName>`、`<TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>` |
| csproj 删 | `<EnableMsixTooling>true</EnableMsixTooling>`，以及它连带的两个 `ItemGroup` / `PropertyGroup`（`ProjectCapability=Msix`、`HasPackageAndPublishMenu`） |
| csproj 删 | `<PublishProfile Condition="...">win-$(Platform).pubxml</PublishProfile>` 那一行 |
| csproj 删 | `<PackageReference Include="Microsoft.Windows.SDK.BuildTools.WinApp" />`（它是为"**打包应用**的 `dotnet run`"服务的，unpackaged 用不上） |
| csproj 删 | 模板自带的 `Publish Properties` 块（`PublishReadyToRun` / **`PublishTrimmed=true`**）。**WinUI 3 不支持裁剪**，留着是颗雷 |
| csproj 保留 | `<ApplicationManifest>app.manifest</ApplicationManifest>` 与 `<Manifest Include="$(ApplicationManifest)" />` —— ⚠️ **模板已经生成了这两个，不用自己加**（Phase 0 实测） |
| 文件保留 | `app.manifest` —— **模板已生成**，只缺 `trustInfo/requestedPrivileges`，补上即可（Phase 0 已完成，另加了 `longPathAware`） |
| 文件删 | `Package.appxmanifest`；`Assets/` 里除 `AppIcon.ico` 之外的 8 个 MSIX 徽标 png；`Properties/PublishProfiles/*.pubxml` |
| 命名空间 | 模板产出 `DelayStart_App`，已统一改为 `DelayStart.App`（xaml 的 `x:Class` / `using:` 与 .cs 同步改，否则 XAML 编译报找不到类型） |

**🔴 `dotnet new` 的执行顺序有个坑（Phase 0 踩到）**：

`dotnet new packagesprops` 生成的 `Directory.Packages.props` 会立刻把 CPM 打开。此时再跑 WinUI 模板，模板的收尾步骤 `dotnet add package` 会失败，并在生成的 csproj 里留下**内联 `Version`**：

```
error: 使用中央包版本管理的项目不应定义 PackageReference 项上的版本…
error NU1008: 以下 PackageReference 项无法定义版本的值…
```

**对策**：按 2.1 的顺序生成没问题（本次就是这样，冲突发生在模板**收尾**而不是生成），生成后必须做两件事——
① 把三个包引用改写成不写 `Version` 的形式；② 把版本补进 `Directory.Packages.props`。
**不要**为了绕开它去关 CPM，那是把简单问题换成一个长期问题。

**模板包是 alpha 版 —— 这是事实，且只影响"生成一次"这一步**：产物是普通文本文件，之后与模板无耦合。Phase 0 实测生成物可用（构建 0 警告 0 错误），**无需手写 csproj**。

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

> **创建后必须手改三处**：
> ① `<TargetFramework>` 从 `net10.0` 改为 **`net10.0-windows`**（与 `Core` / `Management` 对齐）；
> ② 🔴 **务必保留 `<OutputType>Exe</OutputType>`** —— 这是 xUnit v3 的硬要求，漏了会直接报
> `xUnit.net v3 test projects must be executable`（Phase 0 实测踩到）；
> ③ 删掉模板生成的 `UnitTest1.cs`。
>
> ⚠️ `dotnet new xunit3` 的 `-f` 默认值是 **`net8.0`**（不是当前 SDK 版本），所以 `-f net10.0` 必须显式写。
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

> 🔴 **Phase 0 实测（2026-09-19）：`dotnet test` 在本项目不可用，规范命令是 `dotnet run`。**
>
> | 命令 | 结果 |
> |---|---|
> | `dotnet run --project tests/DelayStart.Core.Tests -c Release` | ✅ **正确执行**：`Total: 2, Errors: 0, Failed: 0`，退出码 0 |
> | `dotnet test tests/DelayStart.Core.Tests` | ❌ **"运行了零个测试"，退出码 5**（MTP 的 `ZeroTests`）|
>
> **不是配置漏了属性**，已逐一排除：加了 `IsTestProject=true` 无效；`--list-tests` 传不进去；
> 包版本也无冲突 —— 解析结果是 `Microsoft.Testing.Platform` **2.4.0** + `xunit.v3.mtp-v2` **4.0.1**（两侧都是 MTP v2）。
> 而**发现本身是好的**：同一个产物用 xUnit 自带运行器枚举，两个用例都在（`-list tests` 有输出）。
> 结论：**`xunit.v3.mtp-v2` 4.0.1 与 .NET SDK 10.0.401 的 `dotnet test` 集成这一段有问题，不是我们的用法有问题。**
> 深挖它是版本轮盘，对产品零价值 —— 记为 **D26**，Phase 1 期间不阻塞（测试照写，只是用 `dotnet run` 跑）。

```bash
# ✅ 规范命令：全部测试（xUnit v3 原生 in-proc 运行器）
dotnet run --project tests/DelayStart.Core.Tests -c Release

# 只看发现了哪些用例（排查"测试没被发现"时用）
dotnet run --project tests/DelayStart.Core.Tests -c Release --no-build -- -list tests

# 只跑匹配的用例
dotnet run --project tests/DelayStart.Core.Tests -c Release -- -method "*DelayCalculator*"
```

> **为什么 `dotnet run` 反而更好**：xUnit v3 的测试项目本身就是可执行程序（`OutputType=Exe`），
> 独立运行模式下 stdout 直接可读、**退出码语义明确**（0 = 全过），不需要解析中间层的输出格式。
> AI 与 CI 场景一律用它。

**【必须】** 提交前 `dotnet run --project tests/DelayStart.Core.Tests -c Release` 必须全绿。

> ⚠️ **`dotnet test` 失败 ≠ 测试失败。** 看到"运行了零个测试"+退出码 5 时，
> 先确认是不是用错命令了 —— 这是 D26 的已知现象，不是新 bug。
>
> ⚠️ **对 VS 的影响**：VS 的测试资源管理器（Test Explorer）走 VSTest 适配器，
> 本项目未引用 `xunit.runner.visualstudio`，加上 MTP 路径又不通 ——
> 所以**在 VS 里看不到、也点不动这些测试**。目前用命令行跑（D26 待决策是否补适配器）。

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

命令行参数：

| 参数 | 用途 | 谁调用 |
|---|---|---|
| `--goto-log --run=<runId>` | 打开管理端并定位到指定运行记录 | 调度端通知（D18） |
| `--reinstall-task` | 按 `config.json` 重新注册 `DelayStartScheduler`（**幂等**） | 开发期手动；**安装器 `[Run]` 段** |
| `--restore-all` | 🔴 **还原全部被接管条目与软禁用标记，然后退出**。返回 0 = 全部还原成功；非 0 = 有项失败并打印原因 | **安装器 `[Code] InitializeUninstall()`**（D22 / D23 / NFR-6.4，见 7.2） |

```bash
# 示例：验证通知跳转链路
DelayStart.App.exe --goto-log --run=20260919-084112

# 示例：卸载前的还原（安装器自动调用，手动跑仅用于验证）
DelayStart.App.exe --restore-all
```

> 🔴 **`--restore-all` 必须能独立运行、不依赖主窗口**，且**逐项返回结果**（不能"整体成功/失败"）。它是卸载流程的最后一道防线，见 `build-and-test.md` 7.2 与 `requirements.md` NFR-6.4。

**【必须】** 开发期调试管理端的正确做法：**以管理员身份运行 Visual Studio**，然后 F5 启动。否则每次 F5 都会弹一次 UAC。

> **提权状态下 VS 的"附加到进程"仍可用**，但**热重载（Hot Reload）不可用**——这是提权调试的固有代价，接受它。

> WinUI 3 unpackaged 运行需要 Windows App Runtime。本项目**倾向于**设 `WindowsAppSDKSelfContained=true`，因而不依赖系统预装的运行时；代价是发布体积增大（见 7.1）。
>
> **但自包含 + 提权有冲突风险（R9）**，Phase 0 必须先验证。若不通过，按 `architecture.md` R9 的降级路径①改为框架依赖 —— **有 D22 的安装器兜底，这条路的用户代价已经消失**。

### 5.2 调度端

```bash
# 开发期直接跑（非 AOT，方便调试）
dotnet run --project src/DelayStart.Scheduler

# 跑 AOT 产物（真实性能表现）
src/DelayStart.Scheduler/bin/Release/net10.0-windows/win-x64/publish/DelayStart.Scheduler.exe
```

**【必须】** 调度端调试时注意：它会**真的按延时启动程序**。调试前先把 `config.json` 里的条目延时调小（如 3 / 6 / 9 秒）或用测试专用数据目录，避免等待。用环境变量 `DELAYSTART_LOCAL_DIR`（覆盖 `%LOCALAPPDATA%\DelayStart`）与 `DELAYSTART_CONFIG_DIR`（覆盖 `%APPDATA%\DelayStart`）隔离测试环境 —— 这两个开关**仅用于开发调试**，实现时必须加上，正式代码不得依赖（D23 · `architecture.md` 1.5）。

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

  <!-- 🔴 没有这一行，构建立刻失败：NETSDK1175。见 architecture.md R11 -->
  <_SuppressWinFormsTrimError>true</_SuppressWinFormsTrimError>
</PropertyGroup>
```

> `<OutputType>WinExe</OutputType>` 是**硬要求** —— 用 `Exe` 会在登录瞬间闪一个控制台黑框。

> 🔴 **`_SuppressWinFormsTrimError` 是内部属性，不是官方支持路径。** 官方文档把 WinForms 与 WPF 明确列为 trimming/AOT 不支持（Windows 的 Native AOT 没有 built-in COM，而 WinForms 对 built-in COM marshalling 依赖很重），SDK 在 `Microsoft.NET.RuntimeIdentifierInference.targets` 里主动拦截。写这一行等于签字承认风险自担。
>
> 它**不会**修复任何 trim 问题，只是把拦截降级。因此 6.2 那条"零 AOT 警告"的规矩在这里更重要：**IL2xxx 警告必须逐个处理，不许靠它兜着**。
>
> 完整背景、已知高危路径清单与 **D24 决定（B：纯 Win32 + AOT）** 见 `architecture.md` R11。

---

## 七、打包与分发（D22）

### 7.1 交付形态：Inno Setup 安装器

**"免安装 zip"不再是交付形态**（D8 原方案，已被 D22 取代）。MSIX 被否决的三条理由见 `design-spec.md` 6.4。

**固定安装路径是硬要求**，因为它决定计划任务是否长期有效：

| 项 | 值 | 为什么 |
|---|---|---|
| 安装目录 | `{localappdata}\Programs\DelayStart` → `%LOCALAPPDATA%\Programs\DelayStart` | per-user 安装（**D23**），**不放 Program Files**、**不含版本号**。计划任务的 action 指向这个路径，升级只换文件不换路径 → 调度链路永不断 |
| 调度端 exe | 同目录 `DelayStart.Scheduler.exe` | 同上 |
| 安装目录**只读** | 程序运行时不向安装目录写任何东西 | 保证覆盖升级与卸载不留残留（NFR-6.7） |
| 计划任务注册 | **管理端首次启动时自动注册**（`--reinstall-task`，幂等），**不由安装器负责** | 注册必须提权，而 per-user 安装刻意不弹 UAC。把提权点收敛到"用户第一次打开程序"这唯一一次（NFR-6.5） |

**发布与目录组装**：

```bash
dotnet publish src/DelayStart.App       -c Release -r win-x64 -o dist/app
dotnet publish src/DelayStart.Scheduler -c Release -r win-x64 -o dist/scheduler

dist/DelayStart/
├─ DelayStart.exe              # 管理端（unpackaged，manifest 已嵌进 exe）
├─ DelayStart.Scheduler.exe    # 调度端（NativeAOT）
├─ LICENSE
└─ README.md
```

**自包含 vs 框架依赖 —— 由 Phase 0 的 R9 结果决定**（`architecture.md` 第九节 R9）：

| R9 结果 | 属性 | 体积 | 运行时依赖 |
|---|---|---|---|
| 通过 | `WindowsAppSDKSelfContained=true` | 约 100–150 MB | 零 |
| 不通过（走 R9 降级路径①） | `WindowsAppSDKSelfContained=false` | 约 10–15 MB | 由**安装器**部署 Windows App Runtime |

> 有安装器之后，框架依赖这条路的唯一代价（"让用户自己装运行时"）就消失了 —— 安装器随包带 `WindowsAppRuntimeInstall.exe` 并在 `[Run]` 段静默执行。**所以 R9 不通过并不是坏结局。**

### 7.2 Inno Setup 脚本要点

脚本入库 `installer/DelayStart.iss`。必含项：

| 项 | 内容 |
|---|---|
| 提权 | 🔴 **`PrivilegesRequired=lowest`** —— per-user 安装，**安装全程零 UAC**。⚠️ **卸载会弹一次 UAC**：还原 HKLM 接管项与删除计划任务必须有管理员，这一次躲不掉（D23）。`ArchitecturesInstallIn64BitMode=x64compatible` |
| 目录 | `DefaultDirName={localappdata}\Programs\DelayStart`、`DisableDirPage=auto`（用户可改，实际路径必须写进 `config.json`） |
| 计划任务 | **安装器不注册**（NFR-6.5）。安装完成后提供可选勾选「立即运行 延时启动管理器」（`[Run]` `Flags: postinstall nowait skipifsilent`）—— 程序自提权并幂等注册，UAC 只弹这一次 |
| 快捷方式 | `[Icons]` 开始菜单；桌面快捷方式做成可选任务（`Tasks: desktopicon`） |
| 卸载入口 | per-user 模式自动写 `HKCU\...\Uninstall`，**在"应用和功能"里可卸载且不要求管理员** |
| 🔴 卸载顺序<br>**必须用 `[Code]`，不能用 `[UninstallRun]`** | `[UninstallRun]` 段**读不到子进程退出码**，无法实现"还原失败即中止"。正确做法是在 `[Code]` 的 `InitializeUninstall()` 里执行：<br>`ShellExec('runas', ExpandConstant('{app}\DelayStart.exe'), '--restore-all', '', SW_SHOW, ewWaitUntilTerminated, R)`<br>要点：① `runas` 动词 → 触发 UAC；② 🔴 必须 `ewWaitUntilTerminated`，否则文件先被删、还原还没跑完；③ `R <> 0` 时 `Result := False` —— **返回 False 会中止卸载** |
| 🔴 卸载失败处理 | `InitializeUninstall` 返回 `False` → 卸载中止、安装目录文件保留、弹窗列出失败项（`--restore-all` 的标准输出需落盘供展示） |
| 卸载清理 | 删除 `DelayStartScheduler` 计划任务由 `--restore-all` 内部完成；删除失败必须提示用户手动删，不能静默忽略 |
| 数据保留 | 默认**只删程序目录**。`%APPDATA%\DelayStart` 与 `%LOCALAPPDATA%\DelayStart` 在 `InitializeUninstall` 里**询问后**才删（重装可保留配置） |
| 覆盖升级 | 识别已装版本 → 升级模式；**保留 `%APPDATA%\DelayStart\config.json`** |
| 实际安装路径 | 用户可能改目录，**必须把实际路径写进 `config.json`**，供计划任务重新注册使用 |

🔴 **卸载可逆性是本节最高优先级要求，高于任何视觉与体积优化。** 用户卸载后所有程序必须恢复自启动 —— 做不到就是本项目最严重的缺陷（`requirements.md` NFR-6.4 / 9.4 验收清单）。

### 7.3 本地自测用的 zip（**不是**交付形态）

开发时不想每轮跑安装器，可以用目录组装直接验证运行：

```bash
Compress-Archive -Path dist/DelayStart -DestinationPath dist/DelayStart-dev-<版本>.zip -Force
```

⚠️ **不要用它验收计划任务链路**：zip 解压路径随用户选择而变，计划任务会指向一个可能被随手删掉的路径 —— 这正是 D22 要解决的问题。计划任务相关验收一律走安装器装出来的固定路径。

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

### 9.0 Phase 0 出口验证（R9：自包含 + 提权 manifest）

> 这是**编码前的第一件事**。详细分析与三条降级路径见 `architecture.md` 第九节 R9。**R9 不通过就不进入 Phase 1。**
>
> **进度（2026-09-19）**：自动化部分已全部完成（`dotnet build` 0 警告 0 错误、测试跑通、manifest 已嵌入 exe）。
> **三项人工验证已由用户在 VS 之外实测，全部通过 —— Phase 0 出口条件达成。**

- [x] 按 2.1 用 `dotnet new` 生成 5 个项目骨架 → `DelayStart.slnx` + `src/`×4 + `tests/`×1
- [x] 管理端按 2.1 的改造清单转成 unpackaged：`WindowsPackageType=None` + `ApplicationManifest=app.manifest` + `WindowsAppSDKSelfContained=true`
- [x] `app.manifest` 写入 `requestedExecutionLevel level="requireAdministrator" uiAccess="false"` + `PerMonitorV2` DPI 声明（另加 `longPathAware`）
- [x] 自动校验：Release 产物 `DelayStart.exe` 的字节流中检出了 `requireAdministrator` / `PerMonitorV2` / `longPathAware` → **声明确实嵌进去了**
- [x] ✅ **在 VS 之外直接双击 exe**（2026-09-19 用户实测）
      ```
      src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\DelayStart.exe
      ```
- [x] ✅ **判定 R9：通过** —— 双击后 **UAC 提权对话框正常弹出**。
      （`IsInRole(Administrator)` 的代码级确认留到 Phase 1 有代码后补测）
- [x] ✅ **R9 结论已记录：`WindowsAppSDKSelfContained=true` 保持不动** —— 即 R9 的三条降级路径**都不需要**。
      已回写 `DelayStart.App.csproj` 的注释。⚠️ 该风险来自社区在 WinAppSDK 1.0–1.2 时期的报告，
      本机 **WinAppSDK 1.8 + .NET 10** 未复现。升版后若回归，降级路径首选①（去自包含 + Inno 部署运行时）。
- [x] ✅ 顺带确认 **R3：通过** —— 100% / 150% / 200% 三档 DPI 下界面不糊、不越界
- [x] ✅ 顺带确认 **R8：通过** —— `DelayStart.slnx` 可被 VS 18 正常打开，**无需回退 `.sln`**
- [x] ✅ 顺带确认：**VS 组件「.NET WinUI 应用开发工具」是体验项还是硬依赖** → **体验项**。
      依据：`XamlCompiler.exe` 与全部 `Microsoft.UI.Xaml.Markup.Compiler.*.targets` 都由 NuGet 包
      `Microsoft.WindowsAppSDK.WinUI/1.8.260224000` 的 `buildTransitive/` 提供（本机包缓存实测），
      命令行构建不经过 VS 组件。**残留混淆因子**：该组件当前已装，无法做隔离对照，结论的置信度是"证据充分"而非"实验证明"。

> **Phase 0 的出口条件是：`dotnet build` 全绿 + R2 / R3 / R8 已确认 + R9 有明确结论。**
> **→ 已于 2026-09-19 全部达成**：构建 0 警告 0 错误、R3 / R8 确认通过、**R9 通过**。
>
> **⚠️ 保留这个区分，它就是 R9 的全部意义**：「manifest 嵌进了 exe」**不等于**「Windows 采纳了 manifest」。
> 前者静态可查（已查，三个标记都命中），后者只有双击那一下能回答（已做，弹 UAC）。
> 两个都过了，**D20 的实现方式才算确定，Phase 1 才能开工**。

> **Phase 0 踩到的两个坑（已处置，记录备查）**
>
> **坑① 零测试是"失败"，不是"跳过"。** 删掉模板的 `UnitTest1.cs` 后测试项目一度零个用例，
> 测试宿主把这种情况判定为**失败**（MTP 的 `ZeroTests`，退出码 5，见
> <https://aka.ms/testingplatform/exitcodes>），**不是**静默跳过。
> 后果：CI 一片红，或者更糟 —— 有人把非零退出码当噪音忽略，真到测试全挂那天也没人发现。
> 处置：新增 `tests/DelayStart.Core.Tests/ScaffoldSmokeTests.cs`，断言两件**运行期**的事
> （`DelayStart.Core` / `DelayStart.Management` 能按程序集名解析、宿主确实跑在 .NET 10 上）——
> 编译期通过挡不住这两条，属于真实的 Phase 0 出口项，不是凑数。
> **✅ Phase 1 已删**：128 个业务用例到位后，`ScaffoldSmokeTests.cs` 按本条约定删除（2026-09-19）。
> 它挡的两件事已由真实用例间接覆盖 —— 测试项目能跑起来就说明程序集解析与宿主版本没问题。
>
> **坑③（Phase 1）`CA1707` 与测试命名规范冲突。** `TreatWarningsAsErrors=true` + `AnalysisLevel=latest-recommended`
> 下，`CA1707`（成员名禁下划线）会把**每一个**测试方法名报成编译错误，而 14.2 **强制**要求
> `被测方法_场景_期望结果` 格式。**规范是权威**：在 `tests/DelayStart.Core.Tests/.editorconfig` 就近
> `dotnet_diagnostic.CA1707.severity = none`，**不要**改测试方法名，也**不要**全局关闭
> （生产代码仍应受该规则约束）。新增测试项目时记得拷一份这个 `.editorconfig`。
>
> **坑② `dotnet test` 在本项目跑不了（已登记 D26）。** 退出码 5 还有第二个来源：
> `xunit.v3.mtp-v2` 4.0.1 与 .NET SDK 10.0.401 的 `dotnet test` 集成有缺陷 ——
> **即使项目里有测试，它照样报"运行了零个测试"**。测试发现本身没问题（xUnit 自带运行器能枚举到）。
> **规范测试命令见 4.2：`dotnet run --project tests/DelayStart.Core.Tests -c Release`。**

---

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

### 9.8 安装 / 升级 / 卸载（Phase 6 出口，D22）

**验收标准见 `requirements.md` 9.4**，这里是具体做法。

```bash
# 安装（提权，Inno 会自己弹 UAC）
installer/output/DelayStart-<版本>-setup.exe

# 确认固定路径 + 计划任务 action 指向它
schtasks /query /tn DelayStartScheduler /v /fo LIST

# 记录安装后的注册表基线，供卸载后对比
reg export "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" after-install.reg
```

- [ ] 安装过程**无 UAC 提示**（`PrivilegesRequired=lowest`），装到 `%LOCALAPPDATA%\Programs\DelayStart`
- [ ] 装完**不手动运行任何东西**时**不存在** `DelayStartScheduler` 任务（符合 D23 设计）；启动一次管理端后任务存在，`RunLevel=Highest`、身份为**当前交互用户**（🔴 不是 SYSTEM）
- [ ] `action` 路径为 `%LOCALAPPDATA%\Programs\DelayStart\DelayStart.Scheduler.exe`，**不含版本号**
- [ ] 用安装器装一个更高版本覆盖升级 → 计划任务仍有效、`config.json` 未被清空
- [ ] 🔴 **接管 3 个条目 → 卸载 → 重启 → 三个程序全部恢复自启动**，`Run` 键与 `before-*.reg` 完全一致
- [ ] 🔴 人为制造还原失败（如把某条目目标文件设为不可访问）→ **卸载中止并给出原因**，安装目录文件仍在
- [ ] 卸载后 `DelayStartScheduler` 已删除；若删除被策略阻止，界面有明确提示

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
| VS Installer 报 `Exit Code: 5007` | `--passive` / `--quiet` 未从提权终端启动 | 用**管理员** PowerShell 重跑（1.2）。日志里 `isadmin=True` 但 `iselevated=False` 一样被拒 |
| VS Installer 报「找不到组件」/ 参数无效 | 组件 ID 写错 | 用 1.2 节的两个实测 ID；`...ComponentGroup.WindowsAppSDK.Cs` **不存在** |
| 装完/升级后计划任务立刻失效 | 安装路径带版本号 | 见 7.1：路径必须固定为 `{localappdata}\Programs\DelayStart`，**不含版本号** |
| 调度端启动了但"什么都没干"（无日志、无动作） | 计划任务身份被设成了 SYSTEM，`%APPDATA%` / `%LOCALAPPDATA%` 指向 `systemprofile`，配置读不到且**不报错** | 见 NFR-6.8：`LogonType=Interactive` + `RunLevel=Highest`，身份必须是交互用户 |
| 🔴 卸载后所有程序不再自启 | 卸载未先还原接管项 | 见 7.2：必须在 `InitializeUninstall()` 里先跑 `--restore-all` 并 `ewWaitUntilTerminated` |
| MSIX 装上后 HKCU 软禁用"成功"但任务管理器不认 | 打包应用的注册表虚拟化 | 本项目不用 MSIX（`design-spec.md` 6.4） |
| 单项失败导致整批中止 | 缺少逐条 try/catch | 见 FR-1.4 / FR-5.5 |

---

## 十二、提交前检查清单

每次提交前逐项过一遍。

### 必跑命令

```bash
dotnet build DelayStart.slnx -c Release                                    # 零警告
dotnet run --project tests/DelayStart.Core.Tests -c Release                # 全绿（D26：不用 dotnet test）
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
