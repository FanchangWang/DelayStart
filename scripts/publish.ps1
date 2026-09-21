# DelayStart —— AOT 发布双 exe + 同步进管理端 bin
# 用法: .\scripts\publish.ps1 [-Rid win-x64]
# 说明: Scheduler 为 NativeAOT 单文件发布；最后 build App 触发 csproj 同步钩子拷入。
#       （D40：普通用户代理 DelayStart.Agent 已删除，普通条目由调度端亲自降权启动。）
#       （D60：架构参数化 —— 安装包构建走 installer\build-installer.ps1，本脚本只服务开发期。）
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

dotnet build src/DelayStart.App/DelayStart.App.csproj -c Release -r $Rid
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] build App（同步钩子）失败" -ForegroundColor Red; exit $LASTEXITCODE }

# 核对两个落点的产物时间戳
Write-Host "`n--- 产物核对 ---" -ForegroundColor Cyan
foreach ($f in @(
    "src\DelayStart.Scheduler\bin\Release\net10.0-windows\$Rid\publish\DelayStart.Scheduler.exe",
    "src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\$Rid\DelayStart.Scheduler.exe"
)) {
    $i = Get-Item $f -ErrorAction SilentlyContinue
    if ($i) { Write-Host ("{0}  {1:N2} MB  {2}" -f $i.Name, ($i.Length/1MB), $i.LastWriteTime) }
    else    { Write-Host "MISSING: $f" -ForegroundColor Yellow }
}

Write-Host "`n[OK] publish 完成 (exit 0)" -ForegroundColor Green
