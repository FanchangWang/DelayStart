# DelayStart — 编码规范

> **适用范围**：`src/` 与 `tests/` 下的全部 C# 代码。
> 本文的规则分两级：**【必须】** 违反会导致编译失败、运行时崩溃或架构腐化；**【建议】** 违反会降低可读性，评审时指出但不阻塞。
> 涉及具体坑点的规则会标注引用（如"见 `api-analysis.md` 坑 1"），**这些引用不是装饰，是血泪**。

---

## 一、总原则

1. **可逆优先**。任何会改变系统状态的操作，都必须有一条与之配对的还原路径。写代码时先问"怎么撤销"。
2. **显式优于隐式**。宁可用枚举和显式分支，也不用字符串推断（见坑 5）。
3. **失败要可见**。禁止静默失败。捕获异常必须记录或转换后上抛。
4. **不猜。** 不确定的 API 行为就写最小验证代码实测，再写进正式代码。
5. **注释写"为什么"，不写"是什么"。** 代码能表达的东西不要用注释重复。

---

## 二、语言与编译器设置

### 2.1 项目级设置（`Directory.Build.props`）

```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>   <!-- 内部成员不强制 XML 文档 -->
    <!-- 🔴 刻意**不设** InvariantGlobalization。理由见下方第 6 条与 architecture.md R13 -->
  </PropertyGroup>
</Project>
```

**【必须】** `<Nullable>enable</Nullable>`。可空引用类型警告视为错误，不用 `!` 运算符掩盖——真需要时用显式判空或改设计。

**【必须】** `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`。警告堆着不看的项目最后会烂掉。

**【必须】** `DelayStart.Core.csproj` 额外开启：

```xml
<IsAotCompatible>true</IsAotCompatible>
```

这是**架构护栏**——编译器会在构建期直接报出 AOT 不兼容的 API 使用，避免 `Core` 层被悄悄污染后才发现调度端发布不了（见 `architecture.md` 1.1）。

### 2.2 中央包版本管理

用 `Directory.Packages.props` 的 CPM（Central Package Management）。`.csproj` 里只写 `<PackageReference Include="X" />`，不写 `Version`。

**【必须】** 版本取当前**最新稳定版**作为声明下限，不照搬 demo 或任何参考工程的版本号。

---

## 三、文件与命名空间

### 3.1 文件组织

**【必须】一个文件一个公开类型**，文件名与类型名完全一致（`RegistryStartupSource.cs` ↔ `class RegistryStartupSource`）。私有嵌套类型可以放在同一文件。

**【必须】** 文件范围命名空间（file-scoped namespace）：

```csharp
namespace DelayStart.Core.Services;

public sealed class DelayCalculator { }
```

**【必须】** 每个文件恰好一个末尾换行，文件编码 UTF-8（无 BOM）。

**【必须】** using 指令放在命名空间声明**之前**（file-scoped namespace 下的唯一合法位置）。

### 3.2 命名空间划分

| 命名空间 | 内容 |
|---|---|
| `DelayStart.Core.Models` | 数据模型、枚举 |
| `DelayStart.Core.Abstractions` | 接口 |
| `DelayStart.Core.Services` | 无状态服务与纯逻辑 |
| `DelayStart.Core.Launch` | 进程启动相关 |
| `DelayStart.Core.Logging` | 日志 |
| `DelayStart.Core.Interop` | Core 层的 P/Invoke |
| `DelayStart.Core.Serialization` | JSON 源生成上下文 |
| `DelayStart.Management.Sources` | 各来源的扫描器 |
| `DelayStart.Management.Services` | 扫描汇总、接管、计划任务、`.lnk`、图标 |
| `DelayStart.Management.Interop` | Management 层的 P/Invoke 与 COM 接口 |
| `DelayStart.App.Views` / `.ViewModels` / `.Controls` / `.Dialogs` / `.Services` | 见 `architecture.md` 5.1 |
| `DelayStart.Scheduler.Runtime` / `.Ui` | 见 `architecture.md` 六 |

---

## 四、命名规范

| 元素 | 规则 | 示例 |
|---|---|---|
| 类型、方法、属性、事件、常量 | `PascalCase` | `StartupEntry`、`ScanAll()`、`DelaySeconds` |
| 接口 | `I` + `PascalCase` | `IStartupSource` |
| 公开/私有实例字段 | `_camelCase` | `_configService` |
| 静态私有字段 | `s_camelCase` | `s_instance` |
| 局部变量、参数 | `camelCase` | `elapsedSinceStart` |
| 泛型参数 | `T` 前缀 | `TItem`、`TResult` |
| 枚举类型与成员 | `PascalCase`（成员不加前缀） | `StartupScope.HklmWow` |
| 布尔成员 | `Is` / `Has` / `Can` / `Should` 前缀 | `IsEnabled`、`HasFailed` |
| 异步方法 | `Async` 后缀 | `ScanAsync()` |
| 事件处理器方法 | `On` 前缀 | `OnTrayClick` |
| 私有常量 | `PascalCase` | `MaxRetryCount` |

**【必须】** 不用匈牙利命名法，不用缩写（除 `Id` / `Api` / `Ui` / `Config` 等公认词）。

**【建议】** 名称用英文，注释用中文。不要在标识符里混拼音。

---

## 五、代码风格

**【必须】** 这些由 `.editorconfig` 强制，构建时检查：

| 项 | 规则 |
|---|---|
| 缩进 | 4 个空格，**禁止 Tab** |
| 大括号 | Allman 风格（换行） |
| **单行语句/分支必须带大括号** | 即使只有一行 —— 防止后续插入语句时出现逻辑错误 |
| 行宽 | 建议 ≤ 120 字符 |
| `using` 排序 | `System.*` 优先，其余按字母序 |
| 空行 | 方法之间 1 行；不连续空 2 行以上 |
| 尾随空格 | 禁止 |
| 结尾换行 | 必须有且只有一个 |

**【建议】** 用 `var` 的判据：右侧能一眼看出类型时用（`var config = new AppConfig();`），否则显式声明（`IReadOnlyList<StartupEntry> entries = source.Scan();`）。

---

## 六、现代 C# 用法

| 场景 | 【必须】用 | 【禁止】用 |
|---|---|---|
| 集合初始化 | 集合表达式 `int[] presets = [0, 10, 30];` | `new int[] { ... }` |
| 模式匹配 | `if (entry is { IsMissing: true })`、`is not null` | 链式 `!= null && ...` |
| 正则 | `[GeneratedRegex]` 源生成 | `new Regex(...)`（AOT 下不可靠） |
| 数据载体 | `record` / `record struct` 表达值语义 | 无 `Equals` 重写的可变类 |
| 不可变性 | `init` / `required` / `readonly` | 可变公开字段 |
| 字符串 | 插值 `$"{x}"`；多行用 raw string `"""` | `string.Format`、`+` 拼接多段 |
| 类型声明 | 默认加 `sealed` | 无意识地留可继承 |
| 参数校验 | `ArgumentNullException.ThrowIfNull(x)` | 手写 `if (x == null) throw ...` |
| 空合并/赋值 | `??`、`??=` | 手写三元 |

**【必须】** 类默认 `sealed`。要继承必须先有明确理由（本项目中只有 `IStartupSource` 的实现族需要，它们本身也应 `sealed`）。

**【必须】** 不写 `public` 字段，用属性。`record` 的位置参数除外。

**【建议】** `Core.Models` 下的模型用 `sealed class` + `{ get; init; }`，不用 `record`——它们需要被源生成 JSON 序列化并在配置中往返，且字段会随版本演进增删。

---

## 七、异步与并发

**【必须】** 禁止 `async void`（唯一例外：WinUI 的事件处理器，且内部必须自己 try/catch 兜住所有异常）。

**【必须】** 禁止 `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`——死锁与线程池饥饿的来源。需要同步等待就用 `async` 一路向上传染。

**【必须】** 库代码（`Core` / `Management`）中的 `await` 加 `.ConfigureAwait(false)`；UI 层（`App`）**不加**（需要回到 UI 上下文更新界面）。

**【必须】** 接受 `CancellationToken` 的长任务参数要真正传递并检查；调试时用 `ct.ThrowIfCancellationRequested()`。

**【必须】** UI 元素只能从 UI 线程访问。后台线程更新界面必须经 `DispatcherQueue.TryEnqueue`；调度端经 `Control.BeginInvoke`。

**【禁止】** 在 UI 线程做注册表遍历、文件 IO、`Process.WaitForExit` 等阻塞操作。

---

## 八、错误处理

**【必须】** 禁止空 `catch { }` 和只吞不记的 `catch (Exception) { }`。最低限度：

```csharp
catch (Exception ex)
{
    _logger.Warn(ex, "读取计划任务 '{Path}' 失败，跳过该项", task.Path);
}
```

**【必须】** 系统访问被拒类异常统一转换为语义异常。**注意语义已经变了**：管理端全程提权（D20），因此 `UnauthorizedAccessException` **不再等于"需要提权"**，而是"该项受 ACL / 组策略保护"：

```csharp
catch (UnauthorizedAccessException ex)
{
    throw new StartupOperationException(
        StartupFailureReason.AccessDenied, $"访问被拒绝：{entry.Id}", ex);
}
```

- 异常消息**必须带具体条目标识**。提权状态下还遇到拒绝访问属于异常情况，需要可诊断（对应 `E2`）
- UI 据此显示"该项受系统策略保护，已跳过"，而不是笼统的"操作失败"，也**不要**提示用户"以管理员身份运行"——他已经在管理员身份下了

**【必须】** `ScanService` 与调度引擎中的**单条目失败绝不向外抛**——记入结果对象后继续处理下一条（FR-1.4 / FR-5.5）。

**【必须】** 释放非托管资源用 `using` / `try-finally`。获取到的 Win32 句柄（`HKEY`、`HBITMAP`、`HANDLE`、`SafeHandle`）在成功路径和异常路径上都必须释放。

**【禁止】** 用异常控制正常流程（例如用 `KeyNotFoundException` 判断注册表值是否存在——用 `value is null` 判断）。

---

## 九、日志

**【必须】** 日志句柄通过构造函数注入（`ILogSink`），不在类内部 `new`。便于测试时传假实现。

**【必须】** 日志消息用中文，格式统一（时间 + 级别 + 来源 + 消息）：

```
2026-09-19 08:41:12.345 [INF] [Scheduler] 正在启动 微信（延时 30s，身份 普通用户）
```

**【必须】** 记录异常时把异常对象传进去（`Warn(ex, "...")`），不要只拼 `ex.Message`——栈信息才是排错依据。

**【建议】** 日志只记"发生了什么"，不记"我很抱歉"。避免 `"出错了！"` 这类无信息量的消息。

---

## 十、P/Invoke 与互操作

### 10.1 声明规则

**【必须】** 集中放在 `Interop/` 目录下，**按 DLL 分文件**（`Kernel32.cs`、`AdvApi32.cs`、`WtsApi32.cs`……），不散落在业务代码里。

**【必须】** 只用源生成的 `LibraryImport`，不用 `DllImport`：

```csharp
[LibraryImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]   // ← 【必须】不写返回值会错（坑 9）
private static partial bool CloseHandle(IntPtr hObject);
```

**【必须】** `LibraryImport` 的 `bool` 返回值必须显式标注 `[return: MarshalAs(UnmanagedType.Bool)]`。不写会被当作 4 字节 BOOL 处理，返回值判断出错——这个错误不报错、只出错值，是最难查的一类（见 `api-analysis.md` 坑 9）。

**【必须】** 项目开启 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`（`LibraryImport` 生成代码需要）。

**【必须】** 结构体标注 `[StructLayout(LayoutKind.Sequential)]`；含字符串的用 `CharSet.Unicode`，参数用 `[MarshalAs(UnmanagedType.LPWStr)]`。

**【必须】** 声明 `SetLastError = true` 的函数，失败时用 `Marshal.GetLastWin32Error()` 拿到错误码并写进异常消息。**不写错误码的 Win32 失败等于没报错。**

### 10.2 封装规则

**【必须】** P/Invoke 不直接暴露给业务层。包装成托管方法，内部处理错误码、句柄释放、字符串编码：

```csharp
// 业务层看到的是这个
public static bool TryGetActiveConsoleSessionId(out uint sessionId)

// 而不是这个
[LibraryImport("kernel32.dll")] private static partial uint WTSGetActiveConsoleSessionId();
```

**【必须】** 句柄优先用 `SafeHandle` 派生类；无法使用时用 `try/finally` 显式关闭。

### 10.3 COM 互操作（仅 `Management` 层）

**【必须】** COM 互操作**只允许出现在 `DelayStart.Management`**。`Core` 层引用了任何 COM 类型都会让调度端的 AOT 发布失败（见 `architecture.md` 1.1）。

**【必须】** COM 接口用 `[ComImport]` + `[Guid]` + `[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]` 完整声明，方法顺序必须与 IDL 完全一致。**注释里贴上官方的接口声明顺序来源**，否则后续维护者不敢动。

**【必须】** COM 对象释放后用 `Marshal.ReleaseComObject` 或 `Marshal.FinalReleaseComObject`，且放在 `finally` 中。

---

## 十一、NativeAOT 硬约束

适用于 `DelayStart.Scheduler`，以及 `Core` 层（因为它被调度端引用）。

| # | 【必须】 | 说明 |
|---|---|---|
| 1 | `OutputType` = `WinExe` | 用 `Exe` 会在登录瞬间闪控制台黑框 |
| 2 | 图标/资源用 `Assembly.GetManifestResourceStream` | **禁止 `.resx`**——WinForms 的 `.resx` 资源加载在 AOT 下失败 |
| 3 | JSON 用 `JsonSerializerContext` 源生成 | 反射序列化会在裁剪后失效 |
| 4 | 控件数据绑定手工赋值 | **禁止 `DataSource`** 类反射绑定 |
| 5 | P/Invoke 用 `LibraryImport` | `DllImport` 在 AOT 下需额外配置且不生成高效代码 |
| 6 | **不设** `InvariantGlobalization`（保持默认 `false`） | 🔴 设 `true` 的含义不是"关闭多语言"，而是**不存在任何 culture** —— 任何 `CultureInfo` 创建都抛异常。`TaskScheduler` 注册计划任务时必崩（D34 真机实测），详见 `architecture.md` R13 |
| 7 | 异常用 `throw new XxxException("...")` 而非 `throw ex` | 保留原始栈 |

**【禁止】在 `Core` 与 `Scheduler` 中使用：**

- `Reflection.Emit` / `System.Linq.Expressions.Compile()`（动态代码生成）
- `BinaryFormatter`（已被 .NET 移除，且不安全）
- `Marshal.GetTypedObjectForIUnknown` 等动态 COM RCW 包装（NativeAOT 官方限制："Windows: No built-in COM"）
- `Assembly.LoadFrom` / `Activator.CreateInstance(string)`
- 任何 `Type.GetType(string)` 驱动的插件式加载

**【必须】** `Scheduler` 中新增任何第三方包前，先确认它是否 AOT 兼容。判断方法：发布一次 `-p:PublishAot=true` 并检查警告（`IL2026` / `IL3050`）。**有疑问就不加**——调度端的价值在于小而快，加包前先想清楚值不值。

---

## 十二、WinUI 3 约定

**【必须】** 数据绑定统一用 `x:Bind`（编译期生成、类型安全），**不用** `{Binding}`（反射、运行时才发现错误）。

**【必须】** ViewModel 用 CommunityToolkit.Mvvm 的源生成器：

```csharp
public sealed partial class ItemsViewModel : ObservableObject
{
    [ObservableProperty] private string _searchText = string.Empty;

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct) { }
}
```

**【必须】** 颜色、字号、圆角、间距**只用** `App.xaml` 中定义的资源键，不在 XAML 里写死颜色值。视觉规范的唯一来源见 `design-spec.md` 第五节。

**【必须】** code-behind 只放视图相关逻辑（焦点、动画、控件事件转发）。业务逻辑一律在 ViewModel 或 Service。

**【必须】** 所有会修改系统状态的按钮，点击后必须有明确的成功/失败反馈（`InfoBar` 或对话框），不能"点了没反应"。

**【建议】** 资源字典按用途拆分（`Colors.xaml` / `Styles.xaml` / `Icons.xaml`），在 `App.xaml` 合并。

### 12.1 提权带来的额外约束（D20）

管理端**全程以管理员权限运行**，这会让一部分 WinUI 3 交互行为发生变化，必须按下面的方式写。

**【必须】** 启动时检查权限，不足则提示后**退出**，绝不降级运行（`NFR-3.6` / `E17`）：

```csharp
using var identity = WindowsIdentity.GetCurrent();
var isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
if (!isAdmin)
{
    // 显示「本程序需要管理员权限才能管理自启动项」→ 退出
}
```

> 这个检查是可靠的：**未提权的管理员账户**，其令牌里的 Administrators 组被标记为 `SE_GROUP_USE_FOR_DENY_ONLY`，`IsInRole` 会返回 `false`；提权后才返回 `true`。

**【必须】** 所有需要窗口句柄的 WinRT 交互显式传入 HWND——提权进程不会自动关联正确窗口：

```csharp
var hwnd = WindowNative.GetWindowHandle(this);
InitializeWithWindow.Initialize(picker, hwnd);      // FileOpenPicker / FolderPicker
```

**【禁止】** 把依赖 OLE 拖放的交互作为**唯一**入口。提权进程受 UIPI 限制，**收不到**从资源管理器拖来的内容（`R10`）。规则：

| 能力 | 主入口（必须有） | 增强（有则更好） |
|---|---|---|
| 选择要添加的程序 | `[浏览…]` 按钮（`FileOpenPicker` + `InitializeWithWindow`） | 拖放区 |

若坚持做拖放，实现方式是**旧式 `WM_DROPFILES` 路径**（`ChangeWindowMessageFilterEx` 放行 + `DragAcceptFiles` + 子类化窗口处理消息），**不是** WinUI 的 `AllowDrop` 事件——后者走 OLE 拖放，UIPI 下无解。**不通就老实降级为纯按钮，不要留一个"拖进去没反应"的控件。**

**【禁止】** 设计"从本程序拖出到其他应用"的交互（拖到任务栏固定、拖到别的窗口）。这个方向 UIPI 无解，做不了。

---

## 十三、注释与文档

**【必须】** 注释用中文。

**【必须】** `public` / `protected` 成员写中文 XML 文档注释，至少包含 `<summary>`。有副作用的成员必须说明后果：

```csharp
/// <summary>
/// 软禁用该自启动项：写入 StartupApproved 标记，不修改原始注册表值。
/// </summary>
/// <exception cref="StartupOperationException">当前进程权限不足（需要管理员）。</exception>
public void Disable(StartupEntry entry)
```

**【必须】** 涉及已知坑点的代码，必须有注释指向来源，让后续维护者知道这段"看起来多余"的代码不能删：

```csharp
// 坑 1：任务管理器写标记时用的名字不一定与原值名一致，必须三级回退。
// 不做回退会把已禁用的项误报为启用。见 docs/api-analysis.md 1.2。
foreach (var candidate in EnumerateNameCandidates(entry.Name)) { ... }
```

**【禁止】** 注释掉的大段死代码。要留就提交，不要就删——Git 有历史。

**【必须】** `TODO` 必须带责任人/语境：`// TODO(Phase 4): R1 验证通过后替换为无边框面板`。

---

## 十四、单元测试规范

### 14.1 范围

**框架：xUnit v3**（`D21`）。**只测 `Core` / `Management` 的纯逻辑**，测试项目不引用任何 UI 框架。

| 层 | 必测 | 说明 |
|---|---|---|
| `Core` | `DelayCalculator` | 时序计算（绝对时间点语义、`remaining <= 0`、排序） |
| `Core` | `ItemKeyBuilder` | 主键生成与规范化 |
| `Core` | `CommandLineService` | 注册表值解析（带引号路径、含空格路径、参数识别边界） |
| `Core` | `LaunchResultEvaluator` | 三种判定分支（存活 / 退出码 0 / 退出码非 0） |
| `Core` | `ConfigService` | 加载、损坏恢复、v1→v2 迁移、原子写 |
| `Core` | `StartupSortComparer` | 同延时内 `SortOrder` 稳定性 |
| `Management` | 连续失败计数 | 纯函数部分：`RunRecord` 列表 → 连续失败次数（含中断、乱序 runId、不同条目交错） |

**不测**（用真机手工验证）：注册表实际读写、计划任务实际创建、进程实际启动、UI 渲染、提权 manifest 是否生效。

**【必须】** 单元测试中**禁止**触碰真实注册表、真实文件系统路径、真实进程。所有系统交互必须通过接口注入假实现（`IClock`、`IProcessLauncher`、`IAppConfigStore`）。

**【必须】** 单元测试以**普通权限**运行即可，**不得**要求管理员权限。若某个测试非提权跑不了，说明它不该是单元测试，应移到 `build-and-test.md` 第九节的手工验证清单。

**【必须】** 测试项目 TFM 用 `net10.0-windows`，**不**加 `WindowsAppSDKSelfContained`，**不**引用 `DelayStart.App`（见 `architecture.md` 1.3）。

### 14.2 命名与结构

**【必须】** 方法名格式：`被测方法_场景_期望结果`

```csharp
[Fact]
public void Remaining_DelayAlreadyElapsed_ReturnsNonPositive()

[Theory]
[InlineData("C:\\Program Files\\App\\app.exe -arg", "C:\\Program Files\\App\\app.exe", "-arg")]
[InlineData("\"C:\\Program Files\\App\\app.exe\" -arg", "C:\\Program Files\\App\\app.exe", "-arg")]
[InlineData("C:\\Program Files\\App\\app.exe", "C:\\Program Files\\App\\app.exe", "")]
public void Parse_RegistryValue_SplitsPathAndArgs(string raw, string path, string args)
```

**【必须】** 三段式结构，用注释分隔：

```csharp
// Arrange
var clock = new FakeClock();

// Act
var result = DelayCalculator.Remaining(30, TimeSpan.FromSeconds(10));

// Assert
Assert.Equal(TimeSpan.FromSeconds(20), result);
```

**【必须】** 测试项目根目录**放一份 `.editorconfig`**，内容为 `dotnet_diagnostic.CA1707.severity = none`：

```ini
# tests/DelayStart.Core.Tests/.editorconfig
[*.cs]
dotnet_diagnostic.CA1707.severity = none
```

原因：本仓库 `TreatWarningsAsErrors=true` + `AnalysisLevel=latest-recommended`，`CA1707`（成员名禁下划线）
会把**每一个**测试方法名报成编译错误，而它与上面强制的 `被测方法_场景_期望结果` 格式**直接冲突**。
**规范优先，分析器让位**，但**只在测试目录内关闭** —— 生产代码仍受 `CA1707` 约束（`src/` 下不允许下划线成员名），
所以**不要**把这行加进根 `.editorconfig`。新增测试项目时记得同样放置一份。

**【建议】** 边界值必须覆盖：`0`、负数、`int.MaxValue`、空字符串、`null`、单元素集合。

### 14.3 xUnit v3 特性约定

**【必须】** 条件跳过用 `Assert.Skip` / `Assert.SkipWhen`（v3 新增能力），**不用** `[Fact(Skip = "...")]` 硬编码——后者是永久的，前者是运行期判定的：

```csharp
[Fact]
public void SomePathTest()
{
    Assert.SkipWhen(!OperatingSystem.IsWindows(), "仅 Windows 可用");
    // ...
}
```

**【建议】** 跨测试类共享的昂贵资源用 `IClassFixture<T>`；需要跨整个程序集共享时用 v3 的 `[assembly: AssemblyFixture(typeof(T))]`。

**【建议】** 需要捕获被测代码的 `Console` 输出时用 `[CaptureConsole]`，别自己重定向 `Console.Out`。

**【必须】** v3 主路径下**不要**引入 `Microsoft.NET.Test.Sdk`——那是 VSTest 时代的入口，v3 走 MTP，加进来只会增加版本冲突面。仅当按 `build-and-test.md` 4.1 走了降级路径（SDK 内置模板）时才需要它。

**【禁止】** 跨框架混用（如同时装 xUnit 与 NUnit 适配器）。三个框架的 MTP 版本约束不同，混装必然撞版本。

---

## 十五、Git 提交规范

**【必须】** Conventional Commits 格式，类型用英文，摘要用中文：

```
<type>(<scope>): <中文摘要>

<正文：说明「为什么」，不是「改了什么」>

<引用需求编号：FR-x / NFR-x / D-n>
```

| type | 用途 |
|---|---|
| `feat` | 新功能 |
| `fix` | 修 bug |
| `docs` | 文档 |
| `refactor` | 重构（行为不变） |
| `test` | 测试 |
| `perf` | 性能 |
| `build` | 构建配置 |
| `chore` | 杂项 |

示例：

```
fix(core): 修正命令行解析把含空格路径切坏的问题

原实现对 "C:\Program Files\App\app.exe" 按第一个空格切分，
导致路径被截成 "C:\Program"。改为仅当后半段以 / 或 - 开头时
才认定为参数。

FR-1.7 · docs/api-analysis.md 1.1
```

**【必须】** 提交前必须通过：

```bash
dotnet build -c Release
dotnet test
```

**【必须】** 一次提交只做一件事。功能改动与格式化改动**不得**混在同一提交里。

**【禁止】** 提交 `demo/`、`.workbuddy/`、构建产物、本地日志。这些已在 `.gitignore` 中，提交前用 `git status` 确认。

**【必须】** AI 助手只负责生成 commit message，**不自动执行 `git commit`**——由人工审阅后提交。例外：用户明确要求执行时。

---

## 十六、评审清单

提交前自查，评审时逐条核对：

### 架构

- [ ] 新增代码放在正确的层（`Core` 是否被引入了 COM / 反射？）
- [ ] `Core` 层的改动不破坏 AOT 兼容性（构建无 `IL2026` / `IL3050` 警告）
- [ ] `Scheduler` 没有引用 `Management`

### 正确性

- [ ] 所有改变系统状态的操作都有配对的还原路径
- [ ] 注册表/启动文件夹的禁用操作写到了正确的 scope（不是靠字符串猜）
- [ ] 键名匹配走了三级回退
- [ ] 时序计算用的是 `Stopwatch` 而不是 `DateTime.Now`
- [ ] P/Invoke 的 `bool` 返回值标了 `[return: MarshalAs(UnmanagedType.Bool)]`
- [ ] Win32 调用失败时错误码进了异常消息

### 健壮性

- [ ] 没有空 `catch`；异常要么记录要么转换上抛
- [ ] 单条目失败不会中断整批处理
- [ ] 非托管资源在异常路径上也会释放
- [ ] 配置文件走原子写

### 权限（D20）

- [ ] 管理端提权失败时**退出**，没有降级运行的路径
- [ ] 所有 `FileOpenPicker` / `FolderPicker` 都调用了 `InitializeWithWindow.Initialize(picker, hwnd)`
- [ ] 没有把拖放作为选择文件的**唯一**入口（`R10`）
- [ ] 没有设计"从本程序拖出到其他应用"的交互
- [ ] 没有依赖"以其他用户身份运行"或 OTS 提权场景（`%APPDATA%` / `%LOCALAPPDATA%` 会指向另一账户）
- [ ] **没有硬编码路径**：`%LOCALAPPDATA%` / `%APPDATA%` / 安装目录一律经 `PathService` 解析（`architecture.md` 1.5 · D23）
- [ ] 程序运行时**不向安装目录写任何文件**（NFR-6.7）

### 可维护性

- [ ] 公开成员有中文 XML 文档注释
- [ ] 涉及坑点的代码有指向文档来源的注释
- [ ] 没有注释掉的死代码
- [ ] 没有 `.Result` / `.Wait()` / `async void`

### 验证

- [ ] `dotnet build -c Release` 全绿
- [ ] `dotnet test` 全绿
- [ ] 新增的纯逻辑有对应单元测试
- [ ] 涉及真机的改动已在 `build-and-test.md` 的手工验证清单中标出并实测
