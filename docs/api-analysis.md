# DelayStart — Demo API / 注册表分析

> 来源：`demo/`（.NET 10 WinForms 版 AutoRunManager + AutoRunScheduler）
> 说明：`demo/` 是 API 与注册表操作方法的验证代码，**不作为新项目的代码范例**。本文只提取「怎么做才对」的知识。
> 新项目：`dotnet 10 + WinUI 3`，目录结构、架构、命名全部重新设计。

---

## 一、自启动来源全景（demo 已实测覆盖）

### 1.1 注册表 Run

| 位置 | 注册表路径 | 说明 |
|---|---|---|
| HKCU | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | 当前用户自启 |
| HKLM | `HKLM\Software\Microsoft\Windows\CurrentVersion\Run` | 全机自启 |
| HKLM 32 位 | `HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run` | 32 位程序写入的重定向位置 |

读取要点：
- 全部按 `RegistryView.Registry64` 打开。WOW6432Node 是通过**显式子键路径**访问的，不用 `RegistryView.Registry32`。
- 值的字符串需要解析出 `Path` 与 `Args`，解析规则：
  1. 以 `"` 开头 → 取到第二个 `"` 为止是路径，其余是参数；
  2. 否则按第一个空格切分，**仅当后半段以 `/` 或 `-` 开头时才认定为参数**，否则整串都是路径（防止 `C:\Program Files\...` 被切坏）。

### 1.2 StartupApproved —— 软禁用标记（核心机制）

不删除原值，只在并行的 `StartupApproved` 子键下写一个同名 `REG_BINARY` 标记禁用，与任务管理器 / MSCONFIG 行为一致。

| 来源 | 标记存放路径 |
|---|---|
| HKCU Run | `HKCU\...\CurrentVersion\Explorer\StartupApproved\Run` |
| HKLM Run | `HKLM\...\CurrentVersion\Explorer\StartupApproved\Run` |
| HKLM 32 位 Run | `HKLM\...\CurrentVersion\Explorer\StartupApproved\Run32` |
| 用户启动文件夹 | `HKCU\...\CurrentVersion\Explorer\StartupApproved\StartupFolder` |
| 系统启动文件夹 | `HKLM\...\CurrentVersion\Explorer\StartupApproved\StartupFolder` |

数据格式（12 字节）：

```
字节 0      : 0x03 = 禁用；0x02 = 启用
字节 1..3   : 保留 0
字节 4..11  : REG_BINARY 内的时间戳，FILETIME (little-endian, 8 字节)
```

生成方式：`BitConverter.GetBytes(DateTime.UtcNow.ToFileTime())` 拷到偏移 4；字节 0 置 `0x03`。

- **禁用** = 写该二进制值（`CreateSubKey(path, true)` 保证子键存在）
- **启用** = `DeleteValue(name, throwOnMissingValue: false)` 删除该值，原 `Run` 值从未被动过

> ⚠️ **坑 1（必须处理）**：键名匹配要做**三级回退**。
> 任务管理器写标记时用的名字不一定和 `Run` 下的值名一致。
> `原名` → `原名 + ".exe"` → `Path.GetFileNameWithoutExtension(原名)`，三次都查不到才算「未禁用」。
> 不做回退会把已禁用的项误报为启用。

> ⚠️ **坑 2**：`StartupApproved\Run32` 只对 WOW6432Node 生效；对 32 位项误写 `Run` 键会完全失效且无报错。

### 1.3 启动文件夹

| 位置 | 路径 |
|---|---|
| 用户 | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup` |
| 系统 | `%PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs\Startup` |

- 扫 `*.lnk` 与 `*.url` 两类文件。
- 显示名 = 文件名去扩展名。
- 状态同样由 `StartupApproved\StartupFolder` 决定，**不需要真的移动文件**。
- demo 额外保留了 `backup\{user|system}\` 目录做物理备份，属于防御性设计；新方案主路径仍走 StartupApproved。
- 新项目需要补：**解析 `.lnk` 的真实目标路径**（demo 没有做，列表里只能显示 .lnk 路径，体验差）。推荐 `IShellLinkW` + `IPersistFile` COM 接口，或 `ShellLink` 轻量封装。

### 1.4 计划任务

依赖 NuGet `Microsoft.Win32.TaskScheduler`（命名空间 `Microsoft.Win32.TaskScheduler`，注意 `TaskService` 与 `System.Threading.Tasks.Task` 同名，需要 alias）。

```csharp
using var ts = new TaskService();
var tasks = ts.AllTasks.Where(t =>
    t.Definition.Triggers.Any(tr => tr is LogonTrigger || tr is BootTrigger)
    && t.Folder?.Path?.StartsWith("\\Microsoft") != true   // 排除系统自带
    && !t.Name.Equals("DelayStartScheduler", StringComparison.OrdinalIgnoreCase));
```

| 需要的信息 | 取法 |
|---|---|
| 名称 | `task.Name` |
| 完整路径（唯一标识） | `task.Path`，如 `\MyTasks\SyncTool` |
| 所在文件夹 | `task.Folder?.Path` |
| 命令与参数 | `def.Actions[0] as ExecAction` → `.Path` / `.Arguments` |
| 触发时机 | `trigger is LogonTrigger ? "登录时" : "启动时"` |
| 任务自带延迟 | `LogonTrigger.Delay` / `BootTrigger.Delay`（`TimeSpan?`） |
| 启动身份 | `def.Principal?.RunLevel == TaskRunLevel.Highest` → 管理员 |
| 启用状态 | `task.Enabled` |
| 原始 XML | `task.Xml`（需读 `<Principals><Principal><RunLevel>` 时用） |

- **禁用** = `task.Enabled = false`；**启用** = `true`。
- `AllTasks` 会抛异常的任务要 try/catch 单独跳过，不能让一个坏任务打断整次枚举。

> ⚠️ **坑 3**：`Regenerate` / 被 GPO 下发的任务、以及 `\Microsoft\Windows\*` 下的任务改不动或改了会被还原 —— 必须过滤或标记只读。

### 1.5 UWP / Store 应用

自启动状态不在 Run 里，在 `AppModel` 体系下：

```
HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\
    CurrentVersion\AppModel\SystemAppData\<PackageFamilyName>\<TaskId>
        State = 2  → 启用
        State = 0  → 禁用
```

- `<TaskId>` 通常就是 `App` 或某个后台任务 id。
- 启动标识是 **AUMID**：`<PackageFamilyName>!<TaskId>`（demo 里存成 `Path` 字段）。
- 显示名解析链路（demo 实现得很完整，值得照搬思路）：
  1. 读 `...\<PackageFamilyName>\SplashScreen\<AUMID>\AppName`，得到 `ms-resource://...` 间接字符串；
  2. 用 `SHLoadIndirectString`（`shlwapi.dll`）解析；
  3. 直接解析失败时，去 `HKCU\Software\Classes\Local Settings\MrtCache` 下反查 `PackageFullName`（从 `%...%\WindowsApps\<FullName>\resources.pri` 解码），拼成 `@{PackageFullName?ms-resource://...}` 再解析；
  4. 全失败则回退成 `PackageFamilyName` 下划线前的部分。
- 启用/禁用 = 写 `State`（DWord）为 2 / 0。
- 启动 UWP 用 COM 激活，**不能 Process.Start**：
  `CLSID_ApplicationActivationManager` = `45BA127D-10A8-46A8-A3B7-9F08D39D9E1F`，
  IID = `2E941141-7F97-4756-BA1D-9DECDE894A3D`，
  调用 `ActivateApplication(aumid, args, 0, out pid)`。

> ⚠️ **坑 4**：UWP 支持 `ActivateApplication` 传参数（第二个参数），但**延迟激活本身会被系统拒绝**——UWP 应用的"自启动"由系统 AppModel 调度（启动时/登录时），无法由第三方延后。可行的语义只有：**禁止**（写 `State=0`）或**允许**（`State=2`）。所谓"延时启动 UWP"实际上是"禁用系统自启 + 到点由调度端 ActivateApplication"。这条要写进 UI 文案，避免用户误解。

---

## 二、接管 / 恢复 语义（必须成对实现）

「接管自启动」= 禁用原启动项 + 记入本程序配置 + 由调度端按延时拉起。

| 来源 | `source` | `source_key` 存什么 | 接管（禁用原项） | 恢复 |
|---|---|---|---|---|
| HKCU/HKLM Run | `registry` | 注册表值名 | 写 `StartupApproved\Run` | 删除该标记值 |
| WOW6432Node Run | `registry` | 注册表值名 | 写 `StartupApproved\Run32` | 删除该标记值 |
| 用户启动文件夹 | `startup_folder` | 文件名（含 `.lnk`） | 写 `StartupApproved\StartupFolder`(HKCU) | 删除该标记值 |
| 系统启动文件夹 | `startup_folder` | 文件名 | 写 `StartupApproved\StartupFolder`(HKLM) | 删除该标记值 |
| 计划任务 | `scheduled_task` | 任务完整路径 `\Folder\Name` | `task.Enabled = false` | `task.Enabled = true` |
| UWP | `uwp` | `<TaskId>`（配合 `source_detail` = PackageFamilyName） | `State = 0` | `State = 2` |
| 手动添加 | `manual` | 空 | 无 | 无 |

**关键设计原则**：全部是**可逆软禁用**，绝不删除用户的原始注册表值 / 功能不被破坏。这一点要在 UI 上明确告诉用户（"不会删除任何原始配置"）。

> ⚠️ **坑 5**：`source_detail` 必须精确保存到**具体 hive 与子键**，不能只存 `"registry"`。否则恢复时会写到错误的 hive（HKLM 项被写到 HKCU 的 StartupApproved，静默失效）。demo 用 `source_detail.StartsWith("HKLM")` / `Contains("WOW6432Node")` 来分流 —— 能用但脆弱，新方案建议**显式存枚举值**（如 `RegistryScope.Hkcu` / `Hklm` / `HklmWow`），不要靠字符串猜。

> ⚠️ **坑 6**：demo 用 `(Name, Source)` 二元组判断「是否已接管」（`ConfigService.IsItemDelayed`）。同名不同来源会误判，同名同来源但路径改了也会误判。新方案必须用**稳定主键**：`source + scope + source_key`。

---

## 三、调度端（登录后执行延时启动）

### 3.1 触发方式

计划任务：`schtasks /create /tn "DelayStartScheduler" /tr "<exe>" /sc onlogon /delay 0000:03 /rl HIGHEST /f`

- `/sc onlogon`：登录时触发
- `/delay 0000:03`：登录后 3 秒，留给桌面/资源管理器加载
- `/rl HIGHEST`：以最高权限运行，**避免调度端启动管理员程序时弹 UAC**
- 建议用 `TaskService` API 创建（而不是 `schtasks.exe`），可以读取返回、设置更细的参数、并支持中文任务名

### 3.2 时序语义（重要）

```
remaining = item.DelaySeconds - (now - 登录后的整体开始时刻)
```

即 **延时秒数是「相对登录时刻的绝对时间点」，不是累加**。
- 3 个条目延时都是 30s → 三个都在第 30 秒附近启动，先后由 `SortOrder` 决定；
- 条目 A=10s、B=30s、C=45s → 第 10/30/45 秒各启一个。

`remaining <= 0` 时立即启动（用于调度端启动晚了、或用户手动触发的情况）。
排序：`OrderBy(DelaySeconds).ThenBy(SortOrder)`。

> ⚠️ **坑 7**：上述语义意味着「把微信设为 30s、把依赖它的插件设为 60s」是**可靠**的；但「A=30s、B=30s，要求 A 必须先于 B 完成启动」是**不可靠**的 —— 只保证发起顺序，不保证 A 完成初始化。UI 文案要提示用户：有依赖关系时用**不同**延时值。

### 3.3 以正确身份启动目标程序

| 场景 | 实现 |
|---|---|
| 目标需要管理员 | 调度端自身以 `HIGHEST` 运行，直接 `Process.Start` 继承令牌，**不弹 UAC** |
| 目标应为普通用户 | 不能只靠继承（会变成管理员进程，拖拽/OLE/文件权限异常）。需降权：
`WTSQueryUserToken(sessionId, out token)` → `CreateEnvironmentBlock(out env, token, false)` → `CreateProcessAsUser(token, ..., env, @"winsta0\default", ref si, out pi)` → 收拾句柄 |
| 降权失败 | 回退 `Process.Start`，并记录日志（宁可启动成管理员，也不要启动失败） |
| UWP | COM 激活 `ActivateApplication(aumid)`，见 1.5 |

`CreateProcessAsUser` 的 `dwCreationFlags` 用 `CREATE_UNICODE_ENVIRONMENT(0x400) | CREATE_NEW_CONSOLE(0x10)`。
`STARTUPINFO.lpDesktop` 必须指向 `winsta0\default`，否则窗口开不出来。

> ⚠️ **坑 8**：`Process.GetCurrentProcess().SessionId` 在计划任务里通常是正确的交互会话，但多用户/远程场景下应改用 `WTSGetActiveConsoleSessionId()`。新方案两者都试。

> ⚠️ **坑 9**：`LibraryImport`（源生成）要求 `AllowUnsafeBlocks`，且 `LibraryImport` 不支持 `bool` 自动封送 —— 必须显式 `[return: MarshalAs(UnmanagedType.Bool)]`，否则返回值会错。demo 里这点处理正确，照抄。

### 3.4 单实例与容错

- 单实例：`new Mutex(true, "DelayStartScheduler")` + `WaitOne(TimeSpan.Zero, true)`。
- 逐条 try/catch，单条失败不影响后续条目。
- 失败发 Windows Toast 通知（需 `Microsoft.Windows.SDK.NET.Ref`；且 **Toast 生效要求 exe 在开始菜单有带 AppUserModelID 的快捷方式**，否则静默失败）。这条是 demo 记录的最大隐患，新方案要评估是否值得。
- 日志：`%LOCALAPPDATA%\DelayStart\scheduler.log`。

---

## 四、配置模型

`%LOCALAPPDATA%\DelayStart\config.json`

```jsonc
{
  "version": 2,
  "items": [
    {
      "id": "uuid-v4",                  // 稳定主键
      "name": "微信",
      "path": "C:\\Program Files\\Tencent\\Weixin\\Weixin.exe",
      "args": "-autorun",
      "delaySeconds": 30,               // 相对登录时刻的绝对秒数
      "sortOrder": 1,                   // 同延时内的发起顺序
      "runAsAdmin": false,              // true = 继承调度端管理员令牌（无 UAC）
      "enabled": true,                  // 条目级开关，不牵连原始启动项
      "source": "registry",             // registry | startup_folder | scheduled_task | uwp | manual
      "scope": "hkcu",                  // hkcu | hklm | hklmWow | userFolder | systemFolder | "" —— 显式枚举，不靠字符串猜
      "sourceKey": "Weixin",            // 注册表值名 / 文件名 / 任务完整路径 / TaskId
      "sourceDetail": "",               // 展示用：人可读的来源描述
      "originalState": { "wasEnabled": true }  // 记录接管前的原始状态，用于精确恢复
    }
  ]
}
```

相对 demo 的字段改进（全部来自上面的坑）：
1. 新增 `scope` 显式枚举 → 解决坑 5
2. 新增 `enabled` 条目级开关 → 可以临时停用某个延时项而不丢失配置
3. 新增 `originalState` → 恢复时能还原到「接管前的状态」，而不是无脑置为启用
4. `version` 提升到 2，提供从 demo 格式的迁移逻辑
5. 主键判断改为 `id` 优先、`(source, scope, sourceKey)` 兜底 → 解决坑 6

---

## 五、WinUI 3 重写带来的差异与风险

| 项 | demo (WinForms) | 新方案 (WinUI 3) | 影响 |
|---|---|---|---|
| 管理端 UI | WinForms + DataGridView | WinUI 3 + NavigationView + ListView | 列表虚拟化、行内按钮、卡片式行需要重做 |
| 管理端提权 | `app.manifest requireAdministrator` | **同样全程提权（D20 已批复）**。unpackaged 场景用 `app.manifest`（csproj 里 `<ApplicationManifest>app.manifest</ApplicationManifest>`）；`Package.appxmanifest` 只在打包场景需要 | ⚠️ **风险 R9**：`WindowsAppSDKSelfContained=true` 时 manifest 的 `requestedExecutionLevel` 可能被忽略（社区在 WinAppSDK 1.0–1.2 时期报告过）。Phase 0 必须实测，见 `architecture.md` R9 |
| 提权窗口的文件拖放 | N/A（demo 未做拖放） | UIPI 会拦掉 explorer → 提权进程的拖放（OLE 拖放基本无解） | ⚠️ **风险 R10**：主路径必须用 `[浏览…]` 按钮，拖放只作增强 |
| 调度端 | WinForms + `PublishAot`（约 6 MB） | **保持轻量独立进程** | ✅ 已决策：WinUI 3 的 `PublishAot` 仍属 preview，且自包含体积大、冷启动 1–2 s，登录瞬间执行不可接受 |
| 配置序列化 | `System.Text.Json` 反射模式 | 用 **source generator**（`JsonSerializerContext`） | AOT 友好；WinUI 3 端也需要，避免裁剪问题 |
| `Microsoft.Win32.TaskScheduler` | 已用 | 继续用（注意与 `System.Threading.Tasks.Task` 的命名冲突） | 该包为 netstandard，WinUI 3 可用 |
| Toast 通知 | `Microsoft.Windows.SDK.NET.Ref` | WinUI 3 项目已自带 CsWinRT，直接用 `AppNotification` | WinUI 3 下更顺，但仍需 AppUserModelID 注册 |
| `.lnk` 解析 | 未做 | 需要新增 | 影响「启动文件夹」页的可读性 |
| 主题 | 跟随系统（SystemColors） | Mica + 跟随系统明暗 | UI 设计需要两套配色验证 |

### 新增功能（demo 没有、但必须有）

1. **总览时间轴** —— 横向展示「登录后 0s / 10s / 30s / 60s 各启动什么」，比表格直观得多，是这个软件的核心卖点可视化。
2. **`.lnk` 目标解析** —— 启动文件夹项显示真实目标 exe，而不是 `.lnk` 路径。
3. **`.lnk` 图标提取** —— 列表带真实程序图标，可读性大幅提升。
4. **条目级开关** —— 不删除配置即可临时停用某条延时。
5. **立即模拟调度** —— 不等重启就能验证延时配置是否正确（开发/调试必备，也能给用户做预览）。
6. **搜索与筛选** —— 启动项多时（常见 20–40 项）必须有。
7. **系统启动项只读检查页** —— 服务 / 驱动 / Winlogon / 登录脚本，见设计文档。
8. **批量操作** —— 多选后批量禁用 / 批量加入延时。
