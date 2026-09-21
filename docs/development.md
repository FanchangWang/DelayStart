# DelayStart — 开发文档

> 面向**要改代码、构建、调试、打包**的人。
> AI 编码代理请先读 [`../Agents.md`](../Agents.md)（硬约束 + 代码地图）；架构方案与需求见 [`design.md`](design.md)；踩过的坑见 [`pitfalls.md`](pitfalls.md)。
> 只想安装使用？看 [`../README.md`](../README.md)。

---

## 一、环境要求

| 项目 | 版本 | 说明 |
|---|---|---|
| Windows | 10 21H2+ / 11 | 开发与运行都在此版本以上 |
| .NET SDK | **10.0.401** | 由 `global.json` 钉住，装错版本 `dotnet build` 会直接拒绝 |
| Visual Studio | 2026（18.x） | WinUI 3 开发用；命令行构建不依赖它 |
| Inno Setup | 6 | **只在打安装包时需要**，日常开发不用装 |

> NuGet 未配国内镜像（只有 nuget.org + VS Offline Packages）。下载慢或失败时按 `pitfalls.md` 十处理：先项目级镜像，再代理，**连续失败两次就停下来查因，不要重试循环**。

---

## 二、项目结构与依赖方向

```
DelayStart.slnx
├─ src/DelayStart.Core           # 模型 / 配置读写 / 时序 / 启动 / 日志 —— ★ 必须 NativeAOT 兼容
├─ src/DelayStart.Management     # 各来源扫描器 / 软禁用 / 接管 / 计划任务 / COM 互操作
├─ src/DelayStart.App            # WinUI 3 管理端（unpackaged，产出 DelayStart.exe）
├─ src/DelayStart.Scheduler      # 纯 Win32 + NativeAOT 调度端（产出 DelayStart.Scheduler.exe）
├─ src/DelayStart.LaunchBroker   # NativeAOT 降权中转器（产出 DelayStart.LaunchBroker.exe，见 D70）
├─ tests/DelayStart.Core.Tests   # xUnit v3 单元测试（走 Microsoft.Testing.Platform）
├─ scripts/                      # 开发期脚本：build / test / publish / all
├─ installer/                    # Inno Setup 安装器与构建矩阵（见 D22 / D60）
├─ tools/                        # 图标等资源生成脚本
├─ assets/                       # 图标源图
└─ docs/                         # design.md（方案）· decisions.md（决策）· pitfalls.md（踩坑）· development.md（本文）
```

**依赖方向是单向的**：`App → Management → Core`、`Scheduler → Core`、`LaunchBroker → Core`、`Tests → Core + Management`。

- 🔴 `Scheduler` / `LaunchBroker` **绝不引用 `Management`** —— NativeAOT 发布直接失败。
- 🔴 `Tests` **绝不引用 `App`** —— 一旦引用就要背上 WindowsAppSDK 自包含 + 运行时预装的包袱。

**为什么按"AOT 兼容性"而不是按领域拆两个共享库**：调度端用 NativeAOT，它引用的一切代码都必须 AOT 兼容；而扫描所需的计划任务库（`TaskScheduler` 包）、COM 互操作（`.lnk` 解析、图标提取）恰恰都不兼容。按兼容性切分后，调度端只引用 `Core`，拿到一个**零 COM、零反射**的最小集合。往 Core 里放一个 COM 依赖，`IsAotCompatible=true` 会在构建期拦住你。

**TFM 与关键开关**：

| 工程 | TFM | 关键设置 |
|---|---|---|
| Core / Management | `net10.0-windows` | Core 另开 `IsAotCompatible=true`（架构护栏） |
| App | `net10.0-windows10.0.26100.0` | `TargetPlatformMinVersion=10.0.19041.0`、`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true` |
| Scheduler / LaunchBroker | `net10.0-windows` | `PublishAot=true` |

---

## 三、常用命令

```powershell
.\scripts\build.ps1        # 编译全解决方案（Release，0 警告验收）
.\scripts\test.ps1         # 单元测试
.\scripts\publish.ps1      # 发布三个 exe + 同步调度端产物进管理端 bin
.\scripts\all.ps1          # 一条龙：build → test → publish
.\installer\build-all.ps1  # 安装包矩阵（自包含 / 精简 × x64 / arm64）
```

手动等价：

```powershell
dotnet build DelayStart.slnx -c Release
dotnet run --project tests/DelayStart.Core.Tests -c Release     # 规范测试命令
```

**验收口径**：Release **0 警告 0 错误** + 全部单元测试绿（当前 359 个）。

> 🔴 **构建要求零警告**（`TreatWarningsAsErrors=true`）—— 出现警告即编译失败，这是故意的。
>
> ⚠️ **`dotnet test` 不可用**（D26）：在 xunit.v3.mtp-v2 + SDK 10 下报"零个测试"并退出码 5；用上面的 `dotnet run` 形式。
>
> ⚠️ **改 XAML 后排查问题务必 `dotnet clean` + `--no-incremental`**：obj 里的 `.g.cs` 增量缓存会污染对照实验，让"改了没生效"和"真的没生效"看起来一样。
>
> ⚠️ **构建前先关掉正在运行的 `DelayStart.exe`**：它会锁住 `bin` 里的 DLL，报 `MSB3021/3027`（症状是一堆 `MSB3061 无法删除文件` 警告）。

---

## 四、本地运行与调试

**管理端**：manifest 带 `requireAdministrator`，`dotnet run` 会弹 UAC。
🔴 **提权是否真的生效，必须在 VS 之外双击 exe 验证** —— 在 VS 里跑会继承 VS 的权限，结果不可信。

**调试用环境变量**（仅开发期）：

| 变量 | 作用 |
|---|---|
| `DELAYSTART_LOCAL_DIR` | 覆盖日志 / 状态 / 归档目录 |
| `DELAYSTART_CONFIG_DIR` | 覆盖配置文件目录 |
| `DELAYSTART_FAKE_MISSING` | 伪装"运行库缺失"，回归安装器检测分支 |
| `DELAYSTART_RUNTIME_CHECK` | 运行库自检出口，可自动化验证 |

**调度端**：唯一合法入口是 `RunLevel=Highest` 的计划任务。手动双击会命中提权门槛 —— 静默退出并记日志（不是崩溃，别去修）。

**调试 AOT 闪退**：把涉及的 P/Invoke 类原样链接进一个 CoreCLR 控制台程序直接调用即可 —— CoreCLR 能把 `EntryPointNotFoundException` 原样抛出来，AOT 只剩下 fail-fast（`0xC0000409`），看不到任何异常信息。详见 D69。

---

## 五、发布与打包

**`scripts/publish.ps1`** —— 开发期链路：发布调度端 / 中转器（NativeAOT）+ 构建管理端（build，非 AOT），并把调度端产物同步进管理端 `bin`。🔴 产物缺件时**返回非零退出码**（D61 教训：曾经缺件只打黄字然后照样报成功，让人以为可以直接拿 `bin` 跑）。

**AOT 发布验收**：产物约 **3.4–3.6 MB**（上限 6 MB）；`scheduler.log` 首行记录启动耗时（**< 0.3 s**）。

**打包分发**：

```powershell
.\installer\build-all.ps1                          # 2 形态 × 2 架构 = 4 包
.\installer\build-installer.ps1 -Rid win-x64 [-Slim]   # 单包
```

- 自包含版 ~60 MB / 精简版 ~10 MB；精简版要的是 .NET **Runtime** 而非 Desktop Runtime。
- 版本号唯一来源是 `Directory.Build.props`。
- 安装器的 iss 参数、硬约束与验证配方见 [`../installer/README.md`](../installer/README.md) 与 [`pitfalls.md`](pitfalls.md) 九。

**CI**：`.github/workflows/release.yml`，tag `v*` 与手动双触发，矩阵 `fail-fast: false`；arm64 前置检测工具集。

---

## 六、真机验收清单（要点）

自动化测试覆盖不到的地方，必须真机跑：

- [ ] 软禁用前后**注册表导出对比零变化**（`StartupApproved` 标记写入属设计内正常写入）
- [ ] 接管 → 重启 → 各条目按预期时间启动
- [ ] 四条收尾路径：常规完成不弹面板 / 面板已显示必留 / 菜单跳过立即退 / 面板跳过不退出
- [ ] 卸载可逆：还原失败必须**中止卸载**，不能照删文件
- [ ] DPI 三档（100 / 150 / 200%）与明暗主题
- [ ] 安装 / 升级 / 卸载全链路，含 slim 覆盖 full 的混装回归（往安装目录丢 `hostfxr.dll` → 装 → 断言被清）

---

## 七、文档维护约定

四份文档各管一摊，改动代码时按下面的对应关系同步，**不允许代码与文档长期不一致**：

| 文档 | 什么时候必须改 |
|---|---|
| [`design.md`](design.md) | 需求 / 机制 / 交互行为发生变化 —— 它是当前方案的**单一事实来源** |
| [`decisions.md`](decisions.md) | 出现需要留痕的新取舍（追加 `D编号`）；或推翻旧决策（并入取代它的条目，编号保留） |
| [`pitfalls.md`](pitfalls.md) | 踩到新坑（追加），或旧坑被修掉 / 定性变化 |
| [`development.md`](development.md) | 环境要求、命令、构建发布流程、验收清单变化 |

代码注释、提交信息、测试用例统一按 `FR-x.y` / `NFR-x.y` / `E-x` / `D-x` / `坑 x` 编号互相引用，编号定义处见 [`../Agents.md`](../Agents.md)。

**提交前必查**：Release 构建 0 警告 0 错误 + 测试全绿；`git status` 无构建产物；新踩的坑已追加进 `pitfalls.md`。
