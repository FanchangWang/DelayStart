# DelayStart —— AOT 发布双 exe + 同步进管理端 bin
# 用法: .\publish.ps1
# 说明: Agent/Scheduler 为 NativeAOT 单文件发布；最后 build App 触发 csproj 同步钩子拷入。
#Requires -Version 7
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location $PSScriptRoot

dotnet publish src/DelayStart.Agent -c Release
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] publish Agent 失败" -ForegroundColor Red; exit $LASTEXITCODE }

dotnet publish src/DelayStart.Scheduler -c Release
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] publish Scheduler 失败" -ForegroundColor Red; exit $LASTEXITCODE }

dotnet build src/DelayStart.App/DelayStart.App.csproj -c Release
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] build App（同步钩子）失败" -ForegroundColor Red; exit $LASTEXITCODE }

# 核对四个落点的产物时间戳
Write-Host "`n--- 产物核对 ---" -ForegroundColor Cyan
foreach ($f in @(
    "src\DelayStart.Agent\bin\Release\net10.0-windows\win-x64\publish\DelayStart.Agent.exe",
    "src\DelayStart.Scheduler\bin\Release\net10.0-windows\win-x64\publish\DelayStart.Scheduler.exe",
    "src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\DelayStart.Agent.exe",
    "src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\DelayStart.Scheduler.exe"
)) {
    $i = Get-Item $f -ErrorAction SilentlyContinue
    if ($i) { Write-Host ("{0}  {1:N2} MB  {2}" -f $i.Name, ($i.Length/1MB), $i.LastWriteTime) }
    else    { Write-Host "MISSING: $f" -ForegroundColor Yellow }
}

Write-Host "`n[OK] publish 完成 (exit 0)" -ForegroundColor Green
