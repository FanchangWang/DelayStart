#Requires -Version 7
<#
.SYNOPSIS
    一次构建多套 DelayStart 安装包（D60）。
.DESCRIPTION
    对每个架构分别构建「自包含」与「精简版」两套安装包，逐个调用 build-installer.ps1。
    默认只跑本机可用的 win-x64；arm64 需 ARM64 工具集，通常在 CI 上跑：

        .\build-all.ps1 -Rids win-x64,win-arm64
.PARAMETER Rids
    要构建的架构列表，默认 win-x64。
.PARAMETER FullOnly
    只出自包含（带运行时）包。
.PARAMETER SlimOnly
    只出精简版（框架依赖）包。
.PARAMETER Version
    覆盖版本号，透传给 build-installer.ps1。
.EXAMPLE
    .\build-all.ps1
    .\build-all.ps1 -Rids win-x64,win-arm64
#>
[CmdletBinding()]
param(
    [string[]]$Rids = @('win-x64'),

    [switch]$FullOnly,

    [switch]$SlimOnly,

    [string]$Version
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$single = Join-Path $PSScriptRoot 'build-installer.ps1'
$built = [System.Collections.Generic.List[string]]::new()
$failed = [System.Collections.Generic.List[string]]::new()

foreach ($rid in $Rids) {
    foreach ($slim in @($false, $true)) {
        if ($slim -and $FullOnly) { continue }
        if (-not $slim -and $SlimOnly) { continue }

        $label = "$rid · $(if ($slim) { 'slim' } else { 'full' })"
        try {
            $params = @{ Rid = $rid }
            if ($slim) { $params.Slim = $true }
            if ($Version) { $params.Version = $Version }
            & $single @params
            if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "exit $LASTEXITCODE" }
            $built.Add($label)
        }
        catch {
            Write-Host "[X] $label 构建失败：$_" -ForegroundColor Red
            $failed.Add($label)
        }
    }
}

Write-Host ''
Write-Host '================ 汇总 ================' -ForegroundColor Cyan
foreach ($b in $built) { Write-Host "  [OK] $b" -ForegroundColor Green }
foreach ($f in $failed) { Write-Host "  [X]  $f" -ForegroundColor Red }
Write-Host ('  产物目录：{0}' -f (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))

# 失败即非零退出 —— CI 靠它判定这一步是否成功
if ($failed.Count -gt 0) { exit 1 }
