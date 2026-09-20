# DelayStart —— 一条龙：编译 → 单测 → 发布同步
# 用法: .\all.ps1
#Requires -Version 7
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location $PSScriptRoot

& "$PSScriptRoot\build.ps1";  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\test.ps1";   if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\publish.ps1"; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[OK] 全流程完成：build + test + publish 同步" -ForegroundColor Green
