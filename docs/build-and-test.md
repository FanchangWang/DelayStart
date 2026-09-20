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
  <!-- 🔴 刻意**不设** InvariantGlobalization（继承仓库级的 false）。
       设成 true 会让 TaskScheduler 注册计划任务时抛 CultureNotFoundException —— 见 architecture.md R13 -->
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

**发布与目录组装**（D60 起由脚本驱动，不再手敲 `dotnet publish`）：

```powershell
.\installer\build-all.ps1                          # 本机 win-x64：自包含 + 精简版
.\installer\build-all.ps1 -Rids win-x64,win-arm64  # 加 arm64（需 ARM64 工具集，见 installer\README.md）
```

脚本内部：`dotnet publish` 管理端 → `dotnet publish` 调度端（NativeAOT）→ 调 `ISCC` 编译安装包。
产物落 `dist\DelayStart-Setup-<版本>-<Rid>[-slim].exe`。安装目录内容：

```
{app}\
├─ DelayStart.exe              # 管理端（unpackaged，manifest 已嵌进 exe）
├─ DelayStart.Scheduler.exe    # 调度端（NativeAOT 单文件）
└─ （其余为 .NET 与 Windows App SDK 的运行时文件 —— 精简版不含它们）
```

**两种形态都交付（D60）**：

| 形态 | 交付属性 | 实测体积（win-x64） | 运行时依赖 | 安装器行为 |
|---|---|---|---|---|
| 自包含 | `--self-contained` + `WindowsAppSDKSelfContained=true` | 安装包 **60.4 MB**（publish 217.0 MB / 536 文件） | 零 | — |
| 精简版 | `--no-self-contained` + `-p:WindowsAppSDKSelfContained=false` | 安装包 **9.8 MB**（publish 41.7 MB / 72 文件） | .NET 10 **Runtime** + Windows App Runtime 1.8 | 安装前检测并提示，**不阻断安装** |

> ⚠️ 精简版要的是 **.NET Runtime**（`Microsoft.NETCore.App`），**不是** Desktop Runtime ——
> 它的 `runtimeconfig.json` 里 framework 只有 `Microsoft.NETCore.App 10.0.0`（2026-09-20 实测）。
> 安装器按 `Microsoft.NETCore.App` 探测，装了 Desktop Runtime 的机器同样能识别（其内含 NETCore Runtime）。
>
> 调度端**两种形态都保持 AOT** —— 它不依赖 .NET 运行时，否则精简版用户得补三个运行时。
>
> 自包含与提权 manifest 的兼容性（R9）见 `architecture.md` 第九节；本机已通过，故自包含为默认交付形态。

> 🔴 **publish 目录不自动包含 XAML 产物与资源包（D61 实测，2026-09-21）**
>
> `dotnet publish` 的输出比 `bin` 少 **11 个** App 自己的文件：`App.xbf` / `MainWindow.xbf` /
> `Views\*.xbf`（6 个）/ `Dialogs\DelayEditorDialog.xbf` / `DelayStart.pri` / `Assets\AppIcon.ico`。
> 原因：unpackaged 转换按官方清单删掉了 `EnableMsixTooling`（见 2.1），而这些文件原本是靠
> MSIX 工具链进 publish 清单的。**症状**：装出来的程序一启动就在 `Microsoft.UI.Xaml.dll` 里
> `0xc000027b`（内部 `E_FAIL`）秒崩，窗口完全不出现 —— 而**同一份产物在 `bin` 里双击一切正常**
> （R9 与历次真机验收都是在 `bin` 目录做的，所以这个缺口一直没暴露）。
>
> 两道防线：`DelayStart.App.csproj` 的 `CopyWinUIResourcesToPublishDir`（`AfterTargets="Publish"`）
> 补齐文件；`build-installer.ps1` 在 publish 之后断言这些文件存在，缺一个就中止构建。
>
> ⚠️ 资源包名是 **`DelayStart.pri`（= 主模块名）**，不是 `resources.pri` —— MRT Core 对非打包
> 应用按主模块名查找，`bin` 里能跑通靠的就是这个名字；改名会让程序起不来。

### 7.2 Inno Setup 脚本要点

脚本入库 `installer/DelayStart.iss`。必含项：

| 项 | 内容 |
|---|---|
| 提权 | 🔴 **`PrivilegesRequired=lowest`** —— per-user 安装，**安装全程零 UAC**。⚠️ **卸载会弹一次 UAC**：还原 HKLM 接管项与删除计划任务必须有管理员，这一次躲不掉（D23） |
| 架构防呆（D60） | `ArchitecturesAllowed`：x64 包用 `x64compatible`（arm64 可模拟运行 x64），arm64 包用 `arm64` —— 装错架构直接被拒 |
| 目录 | `DefaultDirName={localappdata}\Programs\DelayStart`、**`DisableDirPage=yes`** —— 固定路径、用户不可改（计划任务按此路径注册，改目录会让升级后的任务指向旧位置） |
| 计划任务 | **安装器不注册**（NFR-6.5）—— 它只有 `lowest` 权限，注册任务需要管理员。落点是**管理端第一次启动**：`FirstRunBootstrap`（D63）自动注册并落一个"已初始化"标记，之后再不插手（详见 7.7）。安装完成后提供可选勾选「立即运行 DelayStart」（`[Run]` `Flags: postinstall nowait skipifsilent shellexec`），UAC 只弹这一次 |
| 🔴 首启勾选运行（D61） | **必须带 `shellexec`**。`DelayStart.exe` 的 manifest 是 `requireAdministrator`，而安装器是 `PrivilegesRequired=lowest`；不带 `shellexec` 的默认 CreateProcess 路径会以 **740（`ERROR_ELEVATION_REQUIRED`）** 失败 —— 表现为"勾选启动、点确定后弹报错框，程序根本没起来"。`shellexec` 让外壳按 manifest 弹 UAC |
| 快捷方式 | `[Icons]` 开始菜单项 + **桌面项**（D61 补：原脚本只建了开始菜单项）。两者都用 `{autoprograms}` / `{autodesktop}` —— `lowest` 模式下都落在**当前用户**目录，与"程序装进 `%LOCALAPPDATA%`"的定位一致，不碰 all users |
| 🔴 桌面项改为可选任务（D62） | `[Tasks] Name: "desktopicon"`，桌面 `[Icons]` 项挂 `Tasks: desktopicon`（开始菜单项不受影响）。不写 `Flags: unchecked` → **默认勾选**。⚠️ **静默安装不自动勾选任何任务**：`/VERYSILENT` 要出桌面图标必须显式加 `/MERGETASKS="desktopicon"` |
| 图标来源（D61 / D62） | 快捷方式与「应用和功能」的图标都取自 **exe 内嵌资源**（`UninstallDisplayIcon` 也指向 exe）→ `App.csproj` 的 `<ApplicationIcon>Assets\AppIcon.ico</ApplicationIcon>` 是必需品，只声明 `<Content>` 不够。安装程序**自身**的图标由 iss 的 `SetupIconFile={#AppIconFile}` 指定，同一个 ico 文件。生成方式见 7.6 |
| 🔴 publish 资源守门（D61） | 安装包用的是 **publish** 目录，而 XBF / PRI / Content 资源**不会自动进 publish**（详见 7.1 的两形态表下方⚠️）。守门有两道：① `DelayStart.App.csproj` 的 `CopyWinUIResourcesToPublishDir`（`AfterTargets="Publish"`）补齐；② `build-installer.ps1` 在 publish 之后断言 `App.xbf` / `MainWindow.xbf` / `DelayStart.pri` / `Assets\AppIcon.ico` 存在且 `*.xbf` ≥ 2 个，缺一个就 `throw` —— **绝不把注定崩溃的包发出去** |
| 卸载入口 | per-user 模式自动写 `HKCU\...\Uninstall`，**在"应用和功能"里可卸载且不要求管理员** |
| 🔴 卸载顺序<br>**必须用 `[Code]`，不能用 `[UninstallRun]`** | `[UninstallRun]` 段**读不到子进程退出码**，无法实现"还原失败即中止"。实现是在 `[Code]` 的 `InitializeUninstall()` 里拉起 `--restore-all` 并检查退出码：① 退出码 `<> 0` → `Result := False`（**返回 False 会中止卸载**），并弹二次确认允许用户强行跳过；② 程序文件已不在（用户手工删过）时直接放行 |
| 🔴 卸载怎么拉起（D61） | **不能用 `Exec`** —— 它与首启运行同因（740），还原动作根本不会发生而卸载照常进行，直接违反 D22。改用 **`ShellExec('runas', ...)`** 提权拉起（弹一次 UAC），代价是 **ShellExec 拿不到退出码**；因此约定 `--result-file <路径>` 由程序把退出码回写进文件，卸载器**轮询**该文件（`Sleep(250)` × 240 ≈ 60 s，覆盖用户看 UAC 的时间）。⚠️ 不用 `ewWaitUntilTerminated`：提权启动能否拿到进程句柄并不确定，"文件出现了没有"才是确定性判据 |
| 🔴 卸载失败处理 | `InitializeUninstall` 返回 `False` → 卸载中止、安装目录文件保留，提示用户先打开程序把条目逐一移出。**三种失败都要走到这条路上**：ShellExec 起不来、轮询超时（用户取消 UAC / 程序损坏）、退出码非 0 —— 后两种按"还原未完成"处理并给知情的放行/中止选择 |
| 卸载清理 | 删除 `DelayStartScheduler` 计划任务由 `--restore-all` 内部完成；删除失败必须提示用户手动删，不能静默忽略 |
| 数据保留（D62 修订） | 默认**只删程序目录**；`%APPDATA%\DelayStart` 与 `%LOCALAPPDATA%\DelayStart` **默认保留**，卸载时用 `SuppressibleMsgBox(..., MB_YESNO, IDNO)` 问一次「是否一并删除配置与日志」，**选「是」才 `DelTree`**。🔴 **静默卸载一律按「否」**（`SuppressibleMsgBox` 的 Default 参数）—— 删用户数据不能在用户没看见提示时发生。询问时机 = `CurUninstallStepChanged(usUninstall)`，即**确认卸载之后**（用户在确认页反悔时数据还在；此时 `--restore-all` 已跑完，顺序天然正确）。选「否」或删不干净（文件被占用）时，完成页把目录位置与手动清除方式说清楚 |
| 覆盖升级 | 同一 `AppId` → 覆盖安装，**保留 `%APPDATA%\DelayStart\config.json`**。⚠️ 自包含与精简版**共用同一 `AppId` 与安装目录**，装后者会覆盖前者 |
| 🔴 装前清空安装目录（**D64-1**） | `[Code] PrepareToInstall` 递归清空 `{app}` 里的程序文件（**不动用户数据**：配置在 `%APPDATA%`、日志在 `%LOCALAPPDATA%`、计划任务在目录外）。<br>**为什么必须清**：两形态同目录 + 覆盖安装只覆盖同名文件 → "先装 full 再装 slim" 会留下 full 的 `hostfxr.dll` / `coreclr.dll`，而 **.NET 的 apphost 只要在自己目录里看到 `hostfxr.dll`，就把"运行时根"当作程序目录本身**，转而去 `<程序目录>\shared\Microsoft.NETCore.App\10.x` 找共享框架（自包含布局是平铺的、那里没有）→ 精简版弹 `You must install or update .NET`，**哪怕机器上装着 10.0.12**（报错框还会列出一份 x86 的 10.0.12 来自证"确实装了"）。2026-09-21 本机 1:1 复现：把 `hostfxr.dll` + `hostpolicy.dll` 放进目录即可复现，卸载清空目录后重装即消失。<br>**为什么放在 `PrepareToInstall`**：Inno 文档保证它早于 Setup 的"文件占用检查"（Restart Manager）执行；而安装器是 `lowest` 权限，删不掉**提权进程**（管理端 / 托盘里的调度端）占用的文件，占用者只能交给 Restart Manager（脚本显式设 `CloseApplications=yes` / `RestartApplications=yes`，两者都是 Inno 默认值）。顺序不会互相抢：Windows 上没带 `FILE_SHARE_DELETE` 打开的文件**删都删不掉**，所以凡是被运行中程序占着的文件必然还在原地，一定被随后那次"文件占用检查"发现 —— 我们提前清掉的只是本来就没人在用的文件。⚠️ 但 RM **不会把关掉的应用自动重启回来**（`RestartApplications` 只对调用过 `RegisterApplicationRestart` 的程序生效），详见 7.8 末尾。<br>**唯一中止安装的情况**：精简版发现 `hostfxr.dll` 删不掉（＝自包含形态的管理端还在运行）—— 中止优于装出一个起不来的程序；探测放在清理**之前**，所以中止时一个文件都还没删、旧安装保持完整。`unins*`（旧卸载器）刻意保留，安装失败时用户仍能卸干净 |
| 缺运行时的下载入口（**D64-2**） | `MissingRuntimeLinks()` + `OpenRuntimeDownloads()` 由安装前提示与安装后提示共用：**只列、只打开实际缺的那几项**（两项都缺则 .NET 在前）。🔴 原实现无论缺什么都把两个地址全列、点「是」却只开 .NET 下载页 —— 而现实中最常见的恰恰是"装着 .NET、只缺 Windows App Runtime"（本机实测即如此），用户被引去装一个已经装好的东西，装完回来还是缺 |
| 中文语言文件（**D65**） | iss 写 `MessagesFile: "compiler:Default.isl,languages\ChineseSimplified.isl"` —— 中文 .isl **随仓库分发**（`installer\languages\ChineseSimplified.isl`）。🔴 原因：官方 Inno Setup **不带**简体中文 .isl（属用户贡献翻译），所以 `compiler:Languages\ChineseSimplified.isl` 只在"本机手工放过这个文件"的机器上成立 —— 2026-09-21 CI 首跑即因此 ISCC exit 2。相对路径按 **.iss 所在目录**解析（实测与 cwd 无关）。垫 `compiler:Default.isl` 是版本错位兜底：缺 message 静默回落英文。详见 7.9 |
| 应用名（D61） | 安装器里**只此一处**名字 = `AppName` = **`DelayStart`**（英文，不带中文）。快捷方式名同为 `DelayStart`，窗口标题同步（`MainWindow.xaml` 的 `Window.Title` / `TitleBar.Title`） |
| 🔴 「设置 → 应用」的显示名取 `AppVerName`（**D63**） | 不是 `AppName`。Inno 的缺省值是 `AppName + " version " + AppVersion` 一类的拼接，D60 起又按形态拼了后缀 → 真机注册表实测 `DisplayName = DelayStart 0.1.0`（slim 档为 `DelayStart 0.1.0-slim`）。D63 起固定 `AppVerName={#AppName}` → 只显示裸 `DelayStart`。版本号仍由 `DisplayVersion` 承载（系统自己会显示），**形态后缀只留在产物文件名里** |
| 精简版运行时检测（D60 / **D61 修正**） | `[Code] InitializeSetup` 两项检测，**每项都是多判据"任一命中即算装了"**：<br>· **.NET**：① `{pf64}` / `{pf32}` 下 `dotnet\shared\Microsoft.NETCore.App\10.*` 目录（主判据，用 `FindFirst` 通配，不必预知版本号）；② **32 位注册表视图**（`HKLM` 常量）`...\InstalledVersions\{x64,arm64,x86}\sharedfx\Microsoft.NETCore.App`；③ **64 位视图**（`HKLM64`）同三条路径。<br>· **Windows App Runtime 1.8**：`HKCU` → `HKLM` → `HKLM64` 三处的包仓库里找 `Microsoft.WindowsAppRuntime.1.8` **且架构段匹配**（`_x64__` / `_arm64__`，由 `/DWinAppRuntimeArch` 注入）。<br>缺失时 `MsgBox(MB_YESNOCANCEL)`，**不阻断安装**。<br>🔴 **教训**：最初两项各只查一个位置（都在 `HKLM64`），在**已装 .NET 10.0.12 + WindowsAppRuntime 1.8** 的机器上双双误报缺失。`.NET` 的记录落在 **32 位视图**（`HKLM\SOFTWARE\WOW6432Node\dotnet\...`，.NET 安装器是 32 位进程），Windows App Runtime 框架包则**按用户注册在 `HKCU`**。**误报比不检测更糟** —— 用户明明装了却被劝去下载 |
| 检测自检出口（D61） | 设环境变量 `DELAYSTART_RUNTIME_CHECK=<结果文件路径>` 后运行精简版安装包：两项检测结果写进该文件后**直接退出、不安装**（`InitializeSetup` 返回 `False`）。用途是让"检测会不会误报"可**自动化回归** —— 原先结果只出现在一个要人点确定的 MsgBox 里，只能人肉装一遍。见 7.5 |
| 🔴 缺运行时就不再自动启动（D62） | `[Run]` 的 postinstall 项挂 `Check: RuntimeReadyForApp`：精简版要求两个运行时都齐（自包含恒真），缺任一项时完成页的「启动 DelayStart」**整项消失**，并由 `CurStepChanged(ssPostInstall)` 给出一次性说明（缺什么 + 两条下载链接 + 说明为什么没自动启动 + 可直接打开下载页）。🔴 原行为：用户在检测提示里选「先装程序、稍后补装」之后照样被自动拉起 → 得到 apphost 的 `You must install or update .NET to run this application` 报错框（指不到安装器、也没有解法）—— **提示与处置必须配套** |
| 检测自检出口扩展（D62） | 自检文件新增 `ready=`（= `[Run]` 那个 `Check` 的**实际取值**）与 `forced=` 两行；配合 `DELAYSTART_FAKE_MISSING=1` 可在**装了运行时的开发机**上强制走完"缺运行时"整条分支（实测：不设 `ready=1 forced=0`；设了 `ready=0 forced=1`）。详见 7.5 |
| 版本号（D60） | 唯一来源 `Directory.Build.props` 的 `<Version>`，由 `build-installer.ps1` 注入 `/DAppVersion`，产物名同源 |
| 🔴 三条硬约束（D60 / D61 实测） | ① **任何一行不得以 `[` 开头**（含 `[Code]` 段内、含缩进后的 `[`）—— ISCC 报 `Invalid section tag`，哪怕那是合法的 Pascal 数组字面量；② `TaskDialogMsgBox` 的 `Shields` 参数是集合类型 `TMsgBoxShields`，传整数 `0` 报 `Type mismatch`；③ **`FILE_ATTRIBUTE_DIRECTORY` 是 Inno Pascal 自带的**，重复声明报 `Duplicate identifier` |

🔴 **卸载可逆性是本节最高优先级要求，高于任何视觉与体积优化。** 用户卸载后所有程序必须恢复自启动 —— 做不到就是本项目最严重的缺陷（`requirements.md` NFR-6.4 / 9.4 验收清单）。

### 7.3 本地自测用的 zip（**不是**交付形态）

开发时不想每轮跑安装器，可以用目录组装直接验证运行：

```bash
Compress-Archive -Path dist/DelayStart -DestinationPath dist/DelayStart-dev-<版本>.zip -Force
```

⚠️ **不要用它验收计划任务链路**：zip 解压路径随用户选择而变，计划任务会指向一个可能被随手删掉的路径 —— 这正是 D22 要解决的问题。计划任务相关验收一律走安装器装出来的固定路径。

### 7.4 CI 出包：GitHub Release（D60）

`.github\workflows\release.yml`，两种触发：

| 触发 | 行为 |
|---|---|
| 推 `v*` 标签 | 出 4 个包（2 架构 × 2 形态）并**创建 Release**，附上全部安装包 |
| 手动 `workflow_dispatch` | 只出包、留 artifact（可选填版本号），**不建 Release** —— 避免每次试构建都多出一个版本 |

要点：

- 矩阵 `fail-fast: false` —— 某个架构失败不拖累其余三个；
- arm64 两组先跑一步 **ARM64 工具集检测**（`vswhere -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64`）：runner 缺组件时立刻给出可读结论，而不是等 NativeAOT 链接阶段抛一个看不懂的 LNK 错；
- 版本号：标签触发以**标签**为准（`v0.2.0` → `0.2.0`）；手动触发可填 `version` 输入；都为空则读 `Directory.Build.props`；
- NuGet 缓存以 `Directory.Packages.props` 为键（`actions/setup-dotnet` 的 `cache: true`）；
- Release 用 `gh release create --generate-notes --verify-tag`（`gh` 在 runner 上预装）。

> 🔴 本机与 CI 的差异只有一处：**arm64 的 AOT 链接器**。本机未装 ARM64 工具集，所以
> `build-all.ps1` 在本机只跑 win-x64；arm64 只能在 CI 出 —— **也因此 arm64 包没有本地验收**，
> 首次发布前需要在 arm64 设备上过一遍 9.8 的安装/卸载清单。

### 7.5 精简版运行时检测的自检（D61）

检测结果原先只出现在一个**要人点确定**的 `MsgBox` 里，没法自动化验证 —— 而这项检测在首次真机
安装时**两项各误报一次**（本机已装 .NET 10.0.12 与 WindowsAppRuntime 1.8，却双双报"缺失"）。
为此留了一个环境变量出口：**设了就只写结果文件、直接退出、不安装任何东西**。

```powershell
$env:DELAYSTART_RUNTIME_CHECK = "$env:TEMP\rt.txt"
.\dist\DelayStart-Setup-0.1.0-win-x64-slim.exe /VERYSILENT /SUPPRESSMSGBOXES
Get-Content "$env:TEMP\rt.txt"
# dotnet=1               ← 1 = 判定为已安装（本机确实装了 .NET 10.0.12）
# winappruntime=1
# ready=1                ← D62：= [Run] 那个 Check 的实际取值（1 = 完成页会出现「启动 DelayStart」）
# forced=0               ← D62：是否被 DELAYSTART_FAKE_MISSING 强制
# pf64=C:\Program Files
# needdll=C:\Program Files\dotnet\shared\Microsoft.NETCore.App\-10.x
```

`dotnet` / `winappruntime` 为 `1` 表示**判定为已安装**（`IntToStr(Ord(布尔))`）。装运行时前后各跑一次，
两项都应从 `0` 变 `1`；若机器上明明装了运行时却仍是 `0`，说明判据位置又选错了（D61 的教训：
`.NET` 的记录在 **32 位注册表视图** `HKLM\SOFTWARE\WOW6432Node\dotnet\...`，
Windows App Runtime 框架包**按用户注册在 `HKCU`**，两者都不在 `HKLM64`）。

#### 缺运行时那条分支怎么在"装了运行时的机器"上验（D62）

D62 修的正是这条分支（缺运行时时不再自动启动程序），但开发机装了运行时 → `MissingRuntimeList()`
恒为空，这条路**永远走不到**。为此加了第二个诊断开关：

```powershell
$env:DELAYSTART_RUNTIME_CHECK = "$env:TEMP\rt2.txt"
$env:DELAYSTART_FAKE_MISSING  = '1'      # 强制两项都判为"缺失"
.\dist\DelayStart-Setup-0.1.0-win-x64-slim.exe /VERYSILENT /SUPPRESSMSGBOXES
Get-Content "$env:TEMP\rt2.txt"
# dotnet=1              ← 真实检测结果不变
# winappruntime=1
# ready=0               ← 闸门已关闭：完成页不会出现「启动 DelayStart」
# forced=1
```

去掉 `DELAYSTART_FAKE_MISSING` 立刻恢复 `ready=1 / forced=0`（实测值）。正常用户永远不会碰到这个开关
（不设环境变量即完全不生效），它的唯一用途就是让这条分支可**自动化回归**，而不必找一台干净机器人肉装一遍。

👉 想看带提示的真机效果（不自动化）：设 `DELAYSTART_FAKE_MISSING=1` 后**正常双击运行**该安装包 ——
安装前的缺运行时对话框、装完的说明框、完成页上消失的启动勾选框会一起出现，退出安装即可，不会留下任何文件。

### 7.6 图标（D61 / D62 / D63）

图标是「**一张源图 → 三份多尺寸 ico**」，全程脚本化：

```powershell
# 源图：assets\icon\delay.png（正方形、带 alpha，推荐 256x256）
uv run tools\make-icon.py                 # 三份一起生成（默认 --roles app,tray）
uv run tools\make-icon.py --roles app     # 只生成管理端
uv run tools\make-icon.py --roles tray    # 只生成调度端两枚
uv run tools\make-icon.py --info          # 只看源图信息，不写文件
```

| 产物 | 档位 | 谁在用 |
|---|---|---|
| `src\DelayStart.App\Assets\AppIcon.ico` | 10 | ① exe 内嵌资源（`<ApplicationIcon>` → 快捷方式 / 任务栏 /「设置 → 应用」）② 窗口图标（`AppWindow.SetIcon("Assets/AppIcon.ico")`，靠 `<Content>` 随程序发布）③ 安装程序自身与卸载入口（iss 的 `SetupIconFile`） |
| `src\DelayStart.Scheduler\Assets\Scheduler.ico` | 10 | ① 调度端 exe 内嵌图标（`<ApplicationIcon>`）② 托盘（正常态，`EmbeddedResource`） |
| `src\DelayStart.Scheduler\Assets\SchedulerWarning.ico` | 10 | 托盘「完成但有失败」的红色告警角标态（D31），只有 `EmbeddedResource` |

🔴 **脚本自己拼 ICO 容器**，不用 `PIL.Image.save(format="ICO", sizes=[...])` —— 后者对**所有**尺寸都写 PNG
条目，小尺寸在部分外壳路径（缩略图、某些文件对话框）会取不到图标而显示空白。本脚本的阈值是
**≥ 96 用 PNG、其余用 BMP/DIB（32bpp BGRA + AND 掩码）**：16~48 才是有兼容风险的那一档，96/128
只服务"大图标 / 超大图标"这些现代外壳路径，用 PNG 能把单份文件从 154 KB 压到 71 KB。

🔴 **托盘不是"取第一个条目"**：`IconResources` 按目标尺寸（32）挑条目并把 cx/cy 显式传给
`CreateIconFromResourceEx`。原先取首个条目 + `LR_DEFAULTSIZE` 的写法，在多尺寸容器上会拿到 16×16、
被系统放大到 32、再被外壳缩回 16 —— 两次重采样。**改了 `DEFAULT_SIZES` 的顺序或档位时留意这一点**
（挑条目是按尺寸而不是按顺序，所以顺序无关；但档位里必须存在 ≤ 32 的档）。

⚠️ 图标是**编译期**（exe 内嵌）或**构建期**（嵌入资源）进去的：换了源图必须重跑脚本**并重新
publish/构建**，否则开始菜单图标不会变（`bin` 里旧 exe 与安装目录里已装的旧 exe 都不会自动更新）。

---

### 7.7 调度计划任务启动期保障与默认延时（D63 → 2026-09-21 改判）

**计划任务**：管理端**每次启动**都检测 `\DelayStartScheduler`，**缺失即自动补建**
（`SchedulerTaskBootstrap`，在 `App.OnLaunched` 解析主窗口之前执行；总览页进入时也会经
状态卡再检测一轮，失败态给「重试创建」按钮）。

```powershell
# 验证「任务确实存在」
schtasks /query /tn DelayStartScheduler            # 应能查到
Select-String -Path "$env:LOCALAPPDATA\DelayStart\manager.log" -Pattern '调度计划任务'
```

🔴 **语义已改判（2026-09-21 批复，原型 v3）**：原 D63 是"仅首启注册一次、之后绝不插手"，
判据是 `config.json` 里的 `schedulerTaskInitialized` —— 那是为了保护总览页开关（D3）表达的
用户意图。本轮该开关被**删除**，任务改为**强制存在**：本软件强依赖调度器，缺失即视为故障
自动修复，"自动补建推翻用户关掉开关的意愿"的反对理由不复存在。`schedulerTaskInitialized`
标记随之废止（模型字段已删；老配置里残留的该键被 JSON 反序列化忽略，无需清理）。

失败/边界语义：

| 情形 | 行为 |
|---|---|
| 任务缺失 | `RegisterOrUpdate()` 自动补建，日志记一条 `Info` |
| 注册失败（组策略、临时故障） | 不抛异常（启动路径），日志记带完整栈的 `Error`；总览页状态卡显示失败态 + 「重试创建」 |
| 已存在任务 | `IsRegistered()` 命中 → 直接返回，不重复注册 |

**默认延时**：`Settings.DefaultPreset` = **10 秒**（D63 前是 30）。⚠️ 与 D50 的预设列表同理 ——
**已落盘的 `config.json` 原样保留**，默认值只影响新装 / 未改过该项的配置。想验证默认值需要
先删掉 `%APPDATA%\DelayStart\config.json`（或在卸载时选「一并删除配置与日志」）。

---

### 7.8 两形态混装 / 装前清空安装目录（D64）

**缺陷定性**：不是"缺运行时"，是**两种形态装进了同一个目录**。装完 full 再装 slim，Inno 只覆盖
同名文件、不清理对方形态的残留 → 程序目录里留着自包含形态的 `hostfxr.dll` → 精简版的 apphost
按"程序目录就是运行时根"去找共享框架，找不到就报缺 .NET。

**根因复现**（不必真装两遍，扔两个文件进去即可）：

```powershell
$d = "$env:TEMP\apphost-probe"; New-Item -ItemType Directory $d -Force | Out-Null
Copy-Item "$env:LOCALAPPDATA\Programs\DelayStart\*" $d -Recurse -Force
Copy-Item artifacts\publish\win-x64\full\hostfxr.dll    $d -Force   # 自包含形态的两个关键文件
Copy-Item artifacts\publish\win-x64\full\hostpolicy.dll $d -Force
& "$d\DelayStart.exe"     # 未加之前：正常；加上之后：apphost 报「必须安装 .NET」
```

报错里 `Required Microsoft.NETCore.App version 10.0.0 x64` + `.NET location: <程序目录>` +
`No frameworks were found.` 是这套机制的指纹 —— **`.NET location` 指向程序目录本身**而不是
`C:\Program Files\dotnet`。

**回归怎么测**（安装目录里放的全是程序文件，随便造）：

```powershell
$app = "$env:LOCALAPPDATA\Programs\DelayStart"
New-Item -ItemType File "$app\hostfxr.dll" -Force            # 伪造上一次形态的残留
"X" | Set-Content "$app\OldFlavorOnly.dll"

.\dist\DelayStart-Setup-0.1.0-win-x64-slim.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG="$env:TEMP\ds.log"

Test-Path "$app\hostfxr.dll"        # 期望 False（被清掉）
Test-Path "$app\OldFlavorOnly.dll"  # 期望 False
Test-Path "$app\DelayStart.exe"     # 期望 True（程序文件都装回来了）
Select-String -Path "$env:TEMP\ds.log" -Pattern '安装目录已清理|残留文件删不掉'
```

`/LOG` 里 `安装目录已清理：删除 N 个文件` 一行给的是实际删除数；`残留文件删不掉（被占用）` 一行
给的是交给 Restart Manager 的文件（正常只有正在运行的调度端 exe）。

想验**中止**分支：另开一个进程把 `hostfxr.dll` 占住（`[IO.File]::Open($p,'Open','ReadWrite','None')`
且不放句柄），再跑同一条命令 —— 安装会在"准备安装"页停下并显示中文说明，且 `{app}` 里其他文件
**一个都没被删**（探测先于清理）。

**验收标准（真实场景）**：`装 full → 装 slim → 运行 DelayStart.exe` 不再弹缺 .NET。

**✅ 2026-09-21 用户真机复测通过**：① 装 full → 启动正常；② **装 slim 覆盖 full → 启动正常**
（＝ 装前清空生效，残留 `hostfxr.dll` 已被清掉）；③ 在 slim 目录里手工丢回一个 full 的
`hostfxr.dll` → 立即复现"必须安装 .NET"（＝ 反向确认病根就是这一个文件，也说明修复效果来自
清理而非环境巧合）。

| 诊断开关 | 作用 |
|---|---|
| `DELAYSTART_SKIP_PURGE=1` | 跳过"清空安装目录 + hostfxr 残留探测"，用于排查"清目录是否与某个安装场景冲突" |
| `DELAYSTART_RUNTIME_CHECK` / `DELAYSTART_FAKE_MISSING` | 见 7.5（运行时检测自检） |

> ⚠️ **托盘程序会不会挡住安装？** 调度端 exe 在安装时通常正在运行、文件被锁 —— 这**不是** D64
> 引入的问题（任何一次升级都要替换它）。交给 Windows Restart Manager：它列出正在占用待替换文件的
> 应用、征得同意后关掉（`CloseApplications=yes` / `RestartApplications=yes`，Inno 的默认值）。
> 若用户拒绝关闭，Inno 会走到它自己的"重试 / 忽略 / 放弃"提示，最坏结果是保留旧调度端 exe
> （程序仍可正常使用，只是调度端没升级）。
>
> 🔴 **但 Restart Manager 不会把被关掉的程序自动重启回来。** Inno 文档原文：`RestartApplications`
> 只对调用过 Windows `RegisterApplicationRestart` API 的程序生效，而 DelayStart 与调度端都没有调用。
> 实际后果：安装时调度端被关掉后，**托盘图标要到下次登录才回来**（它的计划任务只在登录时触发），
> 本次登录尚未到点的延时条目也随之下次登录一并补上 —— 与"关机早于延时到点"是同一套语义，不丢数据。
> 🔴 **这是已知缺口，2026-09-21 用户已明确定性：什么都不做，维持现状。** 并且**明确禁止**
> "管理端启动调度端"这条修法 —— 调度端进程的生死只由计划任务决定，管理端不代管。

---

### 7.9 CI 编译失败：语言文件不随官方 Inno 分发（D65）

**症状**：GitHub Actions 上 `ISCC 编译失败（exit 2）`，ISCC 日志末尾是

```
Determining language code pages
Parsing [Languages] section, line 160
Reading file: C:\Program Files (x86)\Inno Setup 6\Languages\ChineseSimplified.isl
```

**定性**：不是代码问题，是 **iss 依赖了一份既不在仓库、也不在官方 Inno 安装包里的文件**。官方
Inno Setup 6 的 `Languages\` 只含官方翻译（实测本机 6.7.3 共 32 个，Arabic … Ukrainian），**没有**
`ChineseSimplified.isl` —— 简体与繁体中文都属"用户贡献翻译"，要从
<https://jrsoftware.org/files/istrans/> 单独下载。本机之所以一直能编译，是因为有人手工把它放进了
Inno 安装目录：本机那份的时间戳 `2026-06-27 3:05:33` 与官方载荷时间 `2026/5/22 8:00:00` 明显不同。
CI 用 `choco install innosetup`（当前 6.7.1）装到 `C:\Program Files (x86)\Inno Setup 6`，那份目录里
没有中文文件 → ISCC 打不开 → exit 2。

**修法**：语言文件入库 `installer\languages\ChineseSimplified.isl`（21436 B），iss 改为

```pascal
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,languages\ChineseSimplified.isl"
```

**ISCC 路径语义与版本错位的实测结论**（Inno 6.7.3，探针脚本留在 `.workbuddy/tmp/langprobe/`）：

| 探针 | 结果 |
|---|---|
| 相对路径只存在于 **.iss 所在目录** | ✅ 编译成功，日志打印 `Reading file: <脚本目录>\reltest.isl` |
| 相对路径只存在于**当前工作目录** | ❌ exit 2 `Couldn't open include file "<脚本目录>\.workbuddy\...cwdtest.isl"` —— **证明相对路径按脚本目录解析、与 cwd 无关**，且错误形态与 CI 日志 1:1 吻合 |
| 语言文件多一个该编译器不认识的 message 名 | ⚠️ 仅警告 `Message name "..." is not recognized by this version of Inno Setup. Ignoring.`，**exit 0** |
| 语言文件缺一个 message | ⚠️ 仅警告 `... has not been defined for the "probe" language. Will use the English message from Default.isl.`，**exit 0** |
| 写成 `compiler:Default.isl,<文件>` 且文件缺 message | ✅ **零警告**通过 —— 这就是 iss 里垫 `Default.isl` 的理由 |

**结论**：CI 的 Inno 版本（choco 6.7.1）与随包语言文件（6.7.3 时代）错位**只会产生警告、不会失败**，
所以不必把 CI 的 Inno 版本钉死；垫上 `compiler:Default.isl` 后"缺 message"那类警告也消失。

**怎么确认修好了**：`build-installer.ps1` 编译前打印 `语言文件: <路径>`；ISCC 输出里出现
`Reading file: ...\installer\languages\ChineseSimplified.isl` 就说明读的是仓库那份。文件缺失时脚本
会**提前抛人话错误**，不再让人面对难读的 `Couldn't open include file`。

🔴 **别再写回 `compiler:Languages\ChineseSimplified.isl`** —— 那要求每台机器（含 CI runner）都手工往
Inno 安装目录放一份文件，是不可复现的隐性前置条件。

---

### 7.10 计划任务的禁用粒度：任务级 vs 触发器级（D67）

**症状**（用户报告）：`\MicrosoftEdgeUpdateTaskMachineCore` 这类任务有**多个触发器** ——
一个"登录时"（要接管）、一个"每日定时"（不该动）。DelayStart 接管后**每日定时那一个也失效了**。

**根因**：`ScheduledTaskSource.Disable` 一律写 `task.Enabled = false`。那是**任务级**开关，
会把任务里所有触发器一起关掉 —— "禁用"的粒度错了。

**规则（D67）**：

| 情况 | 落点 |
|---|---|
| 禁用时任务级开关**已经是关的** | **什么都不做** —— 它本来就不会自启动（"全部可逆"：避免留下"我们动过、释放时无从判断原始值"的痕迹） |
| 触发器**总数 ≤ 1** | 切**任务级**开关（此时两者等价、语义更明确） |
| 触发器**多于 1 个** | 只切**登录 / 启动触发器**的 `Enabled`（`RegisterChanges()` 落盘），其余触发器原样保留 |
| 启用时任务级开关是关的 | **先把任务打开**，再开触发器（任务级关着的时候，改触发器等于点了没反应） |

🔴 **多于 1 个「登录 / 启动触发器」时是全部切掉，不是只切第一个** —— 只切一个的话程序照旧在登录时自启，
再叠加延时启动 = 启动两次，比不修更糟。

**顺带改掉的一项**：`StartupEntry.IsEnabled` 从 `task.Enabled` 改为 `ComputeIsEnabled`
（任务级开关 **且** 至少一个自启动触发器启用）。不改的话，多触发器任务被接管后列表会一直显示「已启用」、
「禁用」按钮永远可点、点完也不变 —— 界面上看就是坏的。

**否掉的方案**：用 `Trigger.Id` 记账"动过哪几个触发器"、释放时精确还原。
❌ 实测不可行：`Trigger.Id` 在第三方任务里**普遍是空串**（本机 25 个多触发器任务里只有 5 个带 id，且全是 `\Microsoft\*` 系统任务），
而且厂商更新任务时会重写整份定义、把我们的改动一起抹掉。⇒ 释放路径**按规则重新推导**，不做逐触发器记账。

**怎么验（单测，不动系统）**：

```bash
# 只跑 D67 新增的规则用例
dotnet run --project tests/DelayStart.Core.Tests -c Release -- -method "*ShouldToggleWholeTask*"
```

`ScheduledTaskTriggerGranularityTests` 共 15 例：触发器分类 3 例 + 粒度规则 9 例 + 位置描述文案 3 例。
**红/绿对照**（2026-09-21 实测）：把 `ShouldToggleWholeTask` 临时改成恒 `true` → 多触发器那 **5 例立刻变红**，
还原后 **294 例全绿**。

**怎么验（真机，会改系统状态）**：见 9.6 的 D67 条目。

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

> **执行状态（2026-09-19 · D34 已真机执行完毕 → Phase 2 出口达成）**
>
> 白名单约束：本次只操作**用户指定的 4 个项目**，系统其余自启动项一个字节未动（有前后快照为证）。
>
> | 目标 | 主键 | 接管前状态 |
> |---|---|---|
> | HKCU `Run\Gemini` | `registry:hkcu:gemini` | 启用（标记 `02…`） |
> | HKLM `Run\小米电脑管家` | `registry:hklm:小米电脑管家` | 启用（标记 `02…`） |
> | HKLM WOW6432Node `Run\SunJavaUpdateSched` | `registry:hklmwow:sunjavaupdatesched` | 启用（标记 `02…`） |
> | 计划任务 `XiaomiPCHostTask` | `scheduledtask:none:\xiaomipchosttask` | `Enabled=True` |
>
> **实测结果**
>
> | 步骤 | 结果 |
> |---|---|
> | `--scan` | ✅ 37 项 / 4 来源全出货；15 个 `03…` 标记判定**零误判**；`Microsoft.Windows.DevHome` 的两个同名 UWP 被 `ItemKey` 正确区分（**机制 1 的真机反例**，用 `(Name, Source)` 二元组会误判）；`SunJavaUpdateSched` 的标记确实读自 `Run32`（5 值）而非 `Run`（3 值）—— **机制 4 现场确认** |
> | `--reinstall-task` | ✅ 任务 `Ready` / `LogonTrigger Delay=PT3S` / `RunLevel=Highest` / **`LogonType=Interactive`** / `UserId=guyue`，action 指向 `DelayStart.Scheduler.exe` |
> | `--takeover` ×4 | ✅ 四项退出码全 0；三个 `Run` 键**值数量与内容零变化**；四个目标全部落到"已禁用"（标记 `03…` / 任务 `Enabled=False`） |
> | `--restore-all` | ✅ 「还原完成：成功 4 项，失败 0 项。调度计划任务已删除。」 |
> | 前后全量快照 diff | ✅ 三个 `Run` 键**逐行 IDENTICAL**；`XiaomiPCHostTask` 完整还原（`Enabled=True / State=Running`）；其余 30+ 个未接管条目全部未出现于 diff |
> | 回归 `--scan` | ✅ 四项全部回到「启用」，总数仍 37、已接管 0 |
>
> **对 9.3 安全验收的逐条结论**
>
> | 9.3 条目 | 结论 |
> |---|---|
> | 全流程操作后 `Run` 下原值数量与内容**零变化** | ✅ **通过** —— 三个键逐行 IDENTICAL |
> | 启动文件夹文件数量与内容零变化 | ✅ 未触碰（本轮白名单不含启动文件夹项） |
> | 未接管的计划任务 `Enabled` 状态零变化 | ✅ 通过 |
> | 卸载/重置后系统回到接管前状态 | ✅ 通过（功能状态一致） |
>
> ⚠️ **唯一非字节级还原点（如实记录，判定为"符合需求"，不是缺陷）**：三个目标接管前在 `StartupApproved` 里带有
> **显式启用标记** `02000000…`，释放时按 **FR-2.2「启用 = 删除标记值」** 被删除
> （`HKCU SA\Run` 36→35、`HKLM SA\Run` 4→3、`HKLM SA\Run32` 5→4）。
> 这是需求定义的恢复动作，且 `StartupApproved` **不在 9.3 的承诺范围内** —— 9.3 第 1 条限定的是 **`Run` 键**。
> 功能上"无标记"与 `02…` 对 Windows 完全等价（任务管理器一律显示"已启用"）。
> 若要连标记形态也逐字节还原，就得改 FR-2.2 —— **不建议**（收益为零，代价是给"启用"引入第二种语义）。
>
> **执行期发现并修复的两个缺陷**（详见 `architecture.md` 10.3 的 D34 缺陷小节）
>
> | # | 缺陷 | 根因 | 处置 |
> |---|---|---|---|
> | **1** | 计划任务注册 **100% 崩溃**（连带接管第 4 步全部回滚 → FR-3.1 不可用） | `Directory.Build.props` 的 `<InvariantGlobalization>true</InvariantGlobalization>`（**Phase 0 自行引入，无对应决策**，理由写的是"省体积"）→ `Microsoft.Win32.TaskScheduler.Trigger` 静态构造器 `CreateSpecificCulture("en")` 抛 `CultureNotFoundException` | 改 `false`。**Windows 上 .NET 用系统 `icu.dll`，产物 140.8 MB 不变 —— 体积代价实测 ≈ 0**，原理由本就不成立 |
> | **2** | 「移出延时启动」无法还原"**接管前已被禁用**"的项（会把它变成启用） | `Release` / `Rollback` 无条件调 `Enable`（删标记），而 `OriginalState.WasEnabled` **全仓库只有写入点、零读取点** —— 实现漏了 `DelayedItem.OriginalState` 注释里明写的"移除接管时据此精确还原（FR-2.7）" | 恢复动作改为按 `OriginalState.WasEnabled` 分支（`true` → `Enable`，`false` → `Disable`）；并修正该字段默认值 `false` → `true`（取"原本会自启动"这一安全侧，否则老配置条目释放后会永久不启动且无从解释） |
>
> **证据文件**（`.workbuddy/tmp/`，**均不入库**）：`d34-reg-d35-before.txt` / `-mid.txt` / `-after.txt`（三份全量快照）、
> `d35-final-diff.txt`（37 行差异全文）、`d35-takeover.txt` / `d35-restore.txt`、`manager.log`（含缺陷 1 的完整三层异常链）。
>
> ⚠️ **`reg.exe` 在本机被安全策略列入 Program Blacklist**（策略明确禁止换 shell / 脚本绕过），因此上面的"导出对比"
> 改用**等价的 `.NET Registry` 值级快照**：逐值记录类型与内容，字节数组额外记 hex。
> 覆盖面（值数量 + 值内容）与 `.reg` 导出一致，仅少了原始 `.reg` 文本这份**形式证据** —— 如需归档，请手动补跑三条 `reg export`。
>
> ⏳ **仍未验证的一项**：任务管理器 / MSCONFIG 的 **UI 显示**未人工核对。标记字节与任务 `Enabled` 已确认落到正确取值，
> 但"界面显示为已禁用"需要人眼。建议留到 Phase 6 真机全面验收时一次性补 —— 在那里重新截图留档更自然。
>
> **单测侧的锚点**（真机之外的进程内保证，仍然有效）：`StartupApprovedStoreTests.cs` 钉住三级回退 / `Run32` 仅对 WOW6432Node /
> 标记恰 12 字节；`TakeoverServiceTests.cs` 钉住事务顺序与逆序回滚（含回滚自身失败）；`ScanServiceTests.cs` 钉住"坏来源不拖垮整页"。
>
> **重跑命令**（会写真实注册表与计划任务 —— 务必先确认操作白名单）：
>
> ```bash
> # 1. 基线
> reg export "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" before-hkcu.reg
> reg export "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" before-hklm.reg
> reg export "HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" before-wow.reg
>
> # 2. 注册调度任务（幂等；不碰任何 Run 值）
> DelayStart.exe --reinstall-task
> schtasks /query /tn DelayStartScheduler /v /fo LIST   # 核对：登录触发 / 延迟 3s / RunLevel=Highest / 身份=当前交互用户
>
> # 3. 挑 1 个 HKCU 项 + 1 个 WOW6432Node 项做接管→移出往返
> DelayStart.exe --scan
> DelayStart.exe --takeover <ItemKey> 15
> DelayStart.exe --release  <ItemKey>
>
> # 4. 收尾：全量还原 + 对比
> DelayStart.exe --restore-all
> reg export "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" after-hkcu.reg
> # 三个键逐一 diff：值数量与内容必须零变化
> ```
>
> 清理：`--restore-all` 之后 `DelayStartScheduler` 任务应被删除（`schtasks /query /tn DelayStartScheduler` 报"找不到"即正确）。
> 上面这些 `*.reg` 是**验收证据**，执行完请保留到 Phase 2 出口签字为止，**不要提交进仓库**。
> ⚠️ 本机因 `reg.exe` 被策略拦截，D34 实际使用的是等价的 `.NET Registry` 值级快照（见上"证据文件"）。

### 9.2 接管与调度（Phase 4 出口）

- [ ] 把 3 个程序分别设为 10 / 20 / 30 秒延时，接管
- [ ] `schtasks /query /tn DelayStartScheduler /v /fo LIST` 确认触发方式为"登录时"、延迟 3 秒、最高权限
  - ℹ️ 这一条**注册侧的验证已提前到 Phase 2**（D34），命令与核对点见 9.1 末尾的执行状态块。
    此处要验的是**反过来的一半**：任务在登录时真的把调度端拉起来了，而不只是"任务本身长得对"。
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
- [ ] 计划任务子页的**列表内容**（D66）：能看到根级名叫 `\MicrosoftEdgeUpdateTaskMachineCore` / `\MicrosoftEdgeUpdateTaskMachineUA` 的**第三方**任务；同时**看不到** `\Microsoft\Windows\...` 下的系统任务
- [ ] 计划任务的**禁用粒度**（D67）：挑一个"登录触发 + 每日定时"两个触发器的任务（本机可用 `\QuarkCloudDriveUpdaterUser\…`）→ 点「禁用」→ 打开任务计划程序核对：**任务本身仍是"已启用"、每日定时触发器仍是"已启用"，只有登录触发器变成"已禁用"**；再点「启用」→ 登录触发器恢复。位置列应显示「计划任务（登录时；另有 1 个触发器）」。⚠️ 若手上没有现成的多触发器任务，用第五节的两行命令自建一个（`schtasks /create` 加两个 `/sc logon` 与 `/sc daily`），验完删掉

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
| 计划任务子页里看不到某些任务（尤其名字带 Microsoft 的） | 受保护文件夹判据把**名字**当成了**文件夹** —— `task.Path` 是"文件夹 + 任务名"，前缀常量少了尾部分隔符 | 见 D66 / `architecture.md` 10.18：`ScheduledTaskSource.IsProtectedFolderPath` 只认根级 `\Microsoft\` 文件夹 |
| 接管计划任务后，同一任务里的**其他**触发器（每日定时等）也不跑了 | 禁用落在了**任务级**开关 `task.Enabled` 上，把整个任务关掉了 | 见 D67 / `architecture.md` 10.19：多触发器任务只切登录 / 启动触发器。⚠️ **已被旧版本整体关掉的任务需要手工重新启用**（在任务计划程序里对该任务点「启用」）—— 旧版本没记录"是谁关的"，程序不会去猜 |
| 接管多触发器任务后列表仍显示「已启用」「禁用」按钮也点得动 | `IsEnabled` 只看 `task.Enabled`，而我们的禁用落在**触发器**上 | 见 D67：`ComputeIsEnabled` = 任务级开关 **且** 至少一个自启动触发器启用 |

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
