# 安装器构建说明（D22）

## 前置：发布两个产物

```powershell
# 1) 管理端（自包含，~135 MB）
dotnet publish src/DelayStart.App/DelayStart.App.csproj -c Release -r win-x64 --self-contained

# 2) 调度端（NativeAOT 单文件，~3.4 MB）
dotnet publish src/DelayStart.Scheduler/DelayStart.Scheduler.csproj -c Release
```

## 编译安装器

需要 [Inno Setup 6](https://jrsoftware.org/isdl.php)（含 ChineseSimplified.isl 语言包）：

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\DelayStart.iss
# 产物：dist\DelayStart-Setup-<版本>.exe
```

## 设计要点（对应 docs/requirements.md D22）

| 项 | 做法 |
|---|---|
| 安装路径 | 固定 `%LOCALAPPDATA%\Programs\DelayStart`，`DisableDirPage=yes` + `PrivilegesRequired=lowest`，用户不可改 |
| 调度任务注册 | 不归安装器管；管理端首启幂等注册（`--reinstall-task` 同一逻辑） |
| 卸载可逆 | `[Code] InitializeUninstall` 同步跑 `--restore-all` + `ewWaitUntilTerminated`，退出码非 0 **中止卸载**（可强行跳过但需二次确认） |
| 为什么不用 `[UninstallRun]` | 那里读不到退出码，恢复失败也会照删文件 |
| 配置与日志 | 默认保留（无 `[UninstallDelete]`），卸载完成页提示目录位置 |
| 升级 | 覆盖安装；调度任务按固定路径注册，升级后路径不变 |
