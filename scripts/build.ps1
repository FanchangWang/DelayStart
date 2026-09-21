# DelayStart —— 编译全解决方案（Release，0 警告验收）
# 用法: .\scripts\build.ps1
#Requires -Version 7
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location "$PSScriptRoot\.."

dotnet build DelayStart.slnx -c Release
if ($LASTEXITCODE -ne 0) { Write-Host "`n[X] build 失败 (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }

Write-Host "`n[OK] build 完成 (exit 0)" -ForegroundColor Green
