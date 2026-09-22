# DelayStart —— 发布四个可执行文件 + 把同级 exe 同步进管理端 bin
# 用法: .\scripts\publish.ps1 [-Rid win-x64]
# 说明: 开发期发布入口。可执行文件的发布方式**并不相同**：
#         DelayStart.exe              WinUI 3 管理端，build（非 NativeAOT）
#         DelayStart.Scheduler.exe    NativeAOT 单文件发布
#         DelayStart.LaunchBroker.exe NativeAOT 单文件发布（uiAccess 目标的降权中转器，D70）
#         DelayStart.Guard.exe        守卫，build（非 NativeAOT，形态随管理端，D75）
#         DelayStart.NotifyBroker.exe 通知中转器，build（非 NativeAOT，形态随管理端，N1）
#       同步进 App bin 的同级 exe 各有来源（都在 App.csproj 里，是"拉"式钩子）：
#         调度端 / 中转器(LaunchBroker) → CopySchedulerPublishOutput（读 AOT publish 的单文件）
#         守卫           → CopyGuardBuildOutput（读 build 输出的四个文件：exe+dll+runtimeconfig+deps）
#         通知中转器     → CopyNotifyBrokerBuildOutput（同守卫的四件套）
#       🔴 因此**守卫与通知中转器必须先于 App 构建**，否则钩子只能打一条 high 提示 —— 顺序不要随意调整。
#       正式安装包走 installer\build-installer.ps1 —— 它分别 publish 各工程，
#       与本次开发期发布的职责不同，不要混用。
#Requires -Version 7
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64'
)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location "$PSScriptRoot\.."

$ErrorActionPreference = 'Continue'

dotnet publish src/DelayStart.Scheduler -c Release -r $Rid
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] publish Scheduler 失败" -ForegroundColor Red; exit $LASTEXITCODE }

dotnet publish src/DelayStart.LaunchBroker -c Release -r $Rid
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] publish LaunchBroker 失败" -ForegroundColor Red; exit $LASTEXITCODE }

# 守卫：与管理端同样是 build（非 AOT）。🔴 绝不能改成 publish -p:PublishAot=true ——
# 它经 Management 调 COM（IShellLinkW / TaskScheduler），AOT 下激活会运行时抛
# PlatformNotSupportedException（see docs/pitfalls.md 五 R12）。
# ⚠️ 必须排在下面 build App 之前：App.csproj 的 CopyGuardBuildOutput 钩子在 App 构建时
# 从守卫 bin 里"拉"走 exe+dll+runtimeconfig+deps，产物不在位就搬不过去。
dotnet build src/DelayStart.Guard/DelayStart.Guard.csproj -c Release -r $Rid
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] build Guard 失败" -ForegroundColor Red; exit $LASTEXITCODE }

# 通知中转器（N1）：与守卫同样 build（非 AOT，要 WinRT 投影）。⚠️ 同样必须排在 build App 之前 ——
# App.csproj 的 CopyNotifyBrokerBuildOutput 钩子要从它 bin 里拉走 exe+dll+runtimeconfig+deps 四件套。
dotnet build src/DelayStart.NotifyBroker/DelayStart.NotifyBroker.csproj -c Release -r $Rid
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] build NotifyBroker 失败" -ForegroundColor Red; exit $LASTEXITCODE }

dotnet build src/DelayStart.App/DelayStart.App.csproj -c Release -r $Rid
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] build App（同步钩子）失败" -ForegroundColor Red; exit $LASTEXITCODE }

# TFM 输出目录名。注意：这是各项目的 **TargetFramework 目录名**，
# 不是 Windows SDK 安装路径 —— 不要把它当环境问题去治。
# 🔴 守卫与通知中转器是**第三/四个** TFM：它们要发 WinRT 系统通知，必须带平台版本
#    （D79 / N1），与调度端 / LaunchBroker 的纯 net10.0-windows 不同。
#    照调度端的路径找它们必然 MISSING。
$SchedulerTfm = 'net10.0-windows'
$GuardTfm = 'net10.0-windows10.0.26100.0'
$AppTfm = 'net10.0-windows10.0.26100.0'

# 核对产物：缺件必须失败。否则 App bin 没同步上也会打印 [OK]，
# 让人误以为可以拿 App bin 直接跑（D61 教训：bin 能跑 ≠ publish 能跑）。
# 🔴 清单里**必须**包含 App bin 里的三个同级 exe —— 它们是 dev 布局真正要用的东西，
# 只核对自己 bin 里的产物等于没验证（2026-09-22 守卫就是这么漏掉的）。
Write-Host "`n--- 产物核对 ---" -ForegroundColor Cyan
$missing = @()
foreach ($f in @(
    "src\DelayStart.Scheduler\bin\Release\$SchedulerTfm\$Rid\publish\DelayStart.Scheduler.exe",
    "src\DelayStart.LaunchBroker\bin\Release\$SchedulerTfm\$Rid\publish\DelayStart.LaunchBroker.exe",
    "src\DelayStart.Guard\bin\Release\$GuardTfm\$Rid\DelayStart.Guard.exe",
    "src\DelayStart.NotifyBroker\bin\Release\$GuardTfm\$Rid\DelayStart.NotifyBroker.exe",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.Scheduler.exe",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.LaunchBroker.exe",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.Guard.exe",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.Guard.dll",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.Guard.runtimeconfig.json",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.Guard.deps.json",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.NotifyBroker.exe",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.NotifyBroker.dll",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.NotifyBroker.runtimeconfig.json",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.NotifyBroker.deps.json"
)) {
    $i = Get-Item $f -ErrorAction SilentlyContinue
    if ($i) { Write-Host ("{0}  {1:N2} MB  {2}" -f $i.Name, ($i.Length/1MB), $i.LastWriteTime) }
    else    { Write-Host "MISSING: $f" -ForegroundColor Yellow; $missing += $f }
}

if ($missing.Count -gt 0) {
    Write-Host "`n[X] 产物核对失败：$($missing.Count) 个文件缺失（清单见上）" -ForegroundColor Red
    exit 1
}

Write-Host "`n[OK] publish 完成 (exit 0)" -ForegroundColor Green
