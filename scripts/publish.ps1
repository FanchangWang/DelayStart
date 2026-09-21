# DelayStart —— 发布三个可执行文件 + 同步调度端产物进管理端 bin
# 用法: .\scripts\publish.ps1 [-Rid win-x64]
# 说明: 开发期发布入口。三个可执行文件的发布方式**并不相同**：
#         DelayStart.exe              WinUI 3 管理端，build（非 NativeAOT）
#         DelayStart.Scheduler.exe    NativeAOT 单文件发布
#         DelayStart.LaunchBroker.exe NativeAOT 单文件发布（uiAccess 目标的降权中转器，D70）
#       最后 build App 触发 csproj 同步钩子（CopySchedulerPublishOutput）把调度端两个 exe 拷入 App bin。
#       正式安装包走 installer\build-all.ps1 —— 它由 build-installer.ps1 分别 publish 三个工程，
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

dotnet build src/DelayStart.App/DelayStart.App.csproj -c Release -r $Rid
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] build App（同步钩子）失败" -ForegroundColor Red; exit $LASTEXITCODE }

# TFM 输出目录名。注意：这是 App / 调度端的 **TargetFramework 目录名**，
# 不是 Windows SDK 安装路径 —— 不要把它当环境问题去治。
$SchedulerTfm = 'net10.0-windows'
$AppTfm = 'net10.0-windows10.0.26100.0'

# 核对产物：缺件必须失败。否则 App bin 没同步上也会打印 [OK]，
# 让人误以为可以拿 App bin 直接跑（D61 教训：bin 能跑 ≠ publish 能跑）。
Write-Host "`n--- 产物核对 ---" -ForegroundColor Cyan
$missing = @()
foreach ($f in @(
    "src\DelayStart.Scheduler\bin\Release\$SchedulerTfm\$Rid\publish\DelayStart.Scheduler.exe",
    "src\DelayStart.LaunchBroker\bin\Release\$SchedulerTfm\$Rid\publish\DelayStart.LaunchBroker.exe",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.Scheduler.exe",
    "src\DelayStart.App\bin\Release\$AppTfm\$Rid\DelayStart.LaunchBroker.exe"
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
