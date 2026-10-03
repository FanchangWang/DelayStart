# 测试债台账

> 本文记录**已知但暂未补测**的部分，来源为 2026-10-03 的一次全仓审计。
>
> 只收录**常青**结论：零覆盖的位置、消费方、以及为什么暂时不做。
> 已经处理完的历史与 commit 散记不在此列。

## 为什么单独立这份

项目的核心判据是「**纯逻辑住有测试工程的层**」—— 判错时不报错、构建照样绿、
单测也照不到（见 `pitfalls.md` 二十五 / 二十六）。本文就是这条判据的反面清单：
哪些地方目前**没有**那层保护。

## 已补（2026-10-03 一轮）

用例数 **804 → 995**。以下五处当时为零覆盖，现已补齐：

| 目标 | 补的用例 | commit |
|---|---|---|
| `RunStateService`（归档读写）+ `RunRecord` 宽松 schema | 归档往返、最近 N 份排序、恰好保留 30、**坏归档跳过并记 Warn 而非整批失败** | `6d06afe` |
| `FileLogger` | 行格式四段式、2MB 轮转保留 2 份、写失败不抛 | `6d06afe` |
| `UiRequestChannel` + 7 个导航令牌 | 读即删、空文件/不可解析返回 null、覆盖写、令牌往返与字面量 | `6d06afe` |
| `LogSinkExtensions` / `AppActivation` | 五个扩展方法的级别与异常对象；常量拼写不得漂移 | `6d06afe` |
| `ElevationCheck` / uiAccess 判定 / broker 时序 | 抽成纯函数后补测（见下） | `6c40271` |

## 仍待处理

### 🔴 降权启动的**发起层**约 420 行（最大的一块）

`src/DelayStart.Core/Launch/DeElevatedProcessLauncher.cs` 的
`Launch` / `LaunchAuxiliary` / `LaunchElevatedAuxiliary` / `IsUwpItem` /
`IsUiAccessTarget` / `LaunchViaUiAccessBroker` / `PollBrokerResult` —— 全部零测试。

`BrokerResultPolicyTests` 与 `LaunchResultEvaluatorTests` 只覆盖「回执怎么解读」，
**怎么发起一行没测**。Fakes 里也没有 `IProcessLauncher` 替身。

- **已做**（`6c40271`）：把三处纯判定抽成可测方法并补测 —— uiAccess 判定链、
  `BrokerTimingPolicy` 的秒退窗口与超时、`ElevationCheck` 的 `IsElevatedFromTokenInfo`。
- **未做**：真正的进程创建分支。补它需要 `IProcessLauncher` Fake，而这条链落在
  D20 / D128 红线上，**须单独立项**，不要顺手带进别的改动。
- `LaunchManifest.TryReadEmbeddedManifest` 的真实 PE 读取路径已随 `6c40271` 覆盖
  （走 `LoadLibraryEx(AS_DATAFILE)`，只映射不执行、不创建进程）。

### 🔴 `RunStateService` 的裁剪与读取判据

上面的「已补」覆盖了主路径，但**「读不出的归档要跳过而不是让整批失败」**
（FR-1.4 在归档层的对应）之外，还有 TrimArchive 的边界组合没铺满。

### 🟡 `ui-request` 通道的读-删竞态

`UiRequestChannel.Consume` 先读后 `Discard`，两步之间无原子性、无
compare-and-delete：若另一个写入方恰在这个亚毫秒窗口内完成写入，新请求会被一并删除。
表现为「点通知偶尔不跳页」，属**可用性问题**、非权限问题。契约已有测试（D144），
竞态改造待立项。

### 🟡 `UiTargetNavigation.TagFor` 住错了层

令牌 → 导航标签的映射是纯函数，却住在 **App 层**，而 tests **绝不引用 App**
（硬约束，`design.md` 7.1）⇒ 这段映射**结构上不可测**。应下沉到 Management / Core。
成本低、收益明确，是本表里争议最小的一条。

### 🟡 降权方向正确但缺可见性的三处静默吞（**刻意不做**）

`RunningProcessProbe`（取不到 `MainModule` 就跳过）、`DuplicateLaunchPolicy`
（路径归一化失败返回 `null`）、`PowerShellHost`（找不到 pwsh 时 `continue`）。

三者都是有意 fail-safe —— 降级方向与 D76 同源（误判成「已在跑」会让正常程序永远
启动不了）。后果也有下游可见出口（`DeElevatedProcessLauncher` 会转成用户可见的
Failure 消息）。补日志是「更好」不是「该做」，且会淹没真正需要人看的告警。

## 附：经核查确认**无问题**的部分（防止下次重复排查）

- Core → Management **不存在任何形式的反向依赖**（编译期 ProjectReference /
  接口签名泄露 COM 类型 / 运行时回调注入，三形态全否）。
- COM 接口全部 `internal`（`IShellLinkW` / `IShellItemImageFactory` / `IPropertyStore`），
  C# 可见性从语言层面禁止 Core 引用 —— 比约定更强的结构性保证。
- Core 的 `LibraryImport` 只指向 `user32` / `kernel32` / `advapi32`。
- Scheduler 是纯 Win32：`NativeMethods.cs` 37 处 `LibraryImport`、**0 处 `DllImport`**。
- D136 已落实：`StartTimer` 在 if/else **之外**无条件执行，托盘图标注册失败
  不会导致 `TimerTick` 一次不跑（`SchedulerEngine.cs:169-170`）。
- D119 fail-closed 四条路径全部落实：调度端不调度、守卫 `ConfigUnavailable` 且
  **不归档**、扫描上报且界面清空列表、CLI 非 0 退出。
- `Interop/InstanceProbe` 存活探测只申请 `SYNCHRONIZE`、不申请写权限 ⇒ 不会被
  完整性级别折叠成 `false`（D82 踩过的坑，代码是对的）。

## 维护约定

补完一条就从本文删掉，并在 commit message 里引用本文件名，让 `git log` 能追到
「什么时候补的、为什么当时没补」。