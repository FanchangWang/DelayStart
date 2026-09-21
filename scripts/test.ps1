# DelayStart —— 运行单元测试（D26 约定: dotnet run，不用 dotnet test）
# 用法: .\scripts\test.ps1
#Requires -Version 7
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location "$PSScriptRoot\.."

dotnet run --project tests/DelayStart.Core.Tests -c Release
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] test 失败 (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }

Write-Host "`n[OK] test 完成 (exit 0)" -ForegroundColor Green
