# DelayStart —— 安装脚本语法体检（ISCC 真编译，但不打包、不碰 dotnet）
# 用法: .\scripts\lint-installer.ps1
#
# 为什么要它：v0.6.1 这一轮 DelayStart.iss 连踩三个编译器坑（`Exit` 不接受参数、
# 没有 `FileCreate`/`FileWrite`、`SaveStringToFile` 是三参不是两参），全都只能靠
# 事后反复真机编译才发现。ISCC 本身**没有** "只 parse 不打包" 的开关，但它有
# `/O-`（"Enable or disable output (overrides Output)"，2026-10-03 实测 `ISCC.exe /?`）：
# 关掉输出后仍然完整跑预处理 + 逐段解析 + [Code] 的 Pascal 编译，只在最后一步
# 打印 "Skipping preparing Setup program executable, output is disabled" 并跳过产物生成。
# 这正好覆盖上面三个坑（它们全是 [Code] 段的编译期错误），代价约 40ms/轮。
#
# 🔴 刻意**不**调 installer\build-installer.ps1：那个脚本会跑 5 次 dotnet publish
#    （管理端 / 调度端 / 中转器 / 守卫 / 通知中转器，含 AOT），还会覆盖 dist\ 与
#    artifacts\ —— 一条只想查 .iss 语法的检查不该拖上几十秒的重活，也不该动产物。
#
# 🔴 [Files] 的 Source 在编译期必须真实存在，否则 ISCC 直接报 "Source file ... does
#    not exist"（实测）。但这个脚本不该依赖「先跑过 publish」—— 所以 PublishDir /
#    SchedulerDir 都指向一个**临时沙箱**，里面只放四个占位文件（DelayStart.exe /
#    App.xbf / DelayStart.Scheduler.exe / DelayStart.LaunchBroker.exe）。
#    于是检查的是脚本本身（预处理 + 全部 section + [Code] Pascal 编译），
#    而「publish 产物齐不齐」由 build-installer.ps1 的守门段负责（D61 XBF 自检）。
#
# 跑**两轮**：full 与 slim。#ifdef Slim 在 [Code] 里有一整块运行时检测逻辑
# （D60-3 / D61 / D62），不显式传 /DSlim 就永远编译不到那一支。
#
# [Languages] 里的 MessagesFile 是相对 .iss 所在目录解析的，所以这里必须把
# **真实的 installer\DelayStart.iss** 交给 ISCC（不能拿临时副本），
# 否则会撞上 installer\languages\ChineseSimplified.isl 找不到。
#Requires -Version 7
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location "$PSScriptRoot\.."

$issPath = Join-Path $PSScriptRoot '..\installer\DelayStart.iss'
if (-not (Test-Path -LiteralPath $issPath)) {
    Write-Host "`n[X] 找不到安装脚本：$issPath" -ForegroundColor Red
    exit 1
}

# ---- Inno Setup 定位（候选列表照抄 installer\build-installer.ps1）-----------
# ⚠️ winget 装的 Inno Setup 6 是 per-user 布局，路径不在 Program Files。
# 🔴 找不到就**明确报错退出**，绝不静默跳过 —— 静默跳过等于这条检查形同虚设：
#    .iss 写错了没人知道，而它恰恰是靠真机编译才能暴露错误的那类文件。
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host @"

[X] 找不到 ISCC.exe，安装脚本无法体检。
请先安装 Inno Setup 6：winget install --id JRSoftware.InnoSetup
探测路径：
  $($isccCandidates -join "`n  ")
"@ -ForegroundColor Red
    exit 1
}
Write-Host "ISCC: $iscc"

# ---- 临时沙箱：只为让 [Files] 的 Source 解析得过，不含任何真实产物 ------------
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) "delaystart-inno-lint-$PID"
$appDir = Join-Path $sandbox 'app'
$schedulerDir = Join-Path $sandbox 'scheduler'
New-Item -ItemType Directory -Path $appDir, $schedulerDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $appDir 'DelayStart.exe') -Value 'lint placeholder'
Set-Content -LiteralPath (Join-Path $appDir 'App.xbf') -Value 'lint placeholder'
Set-Content -LiteralPath (Join-Path $schedulerDir 'DelayStart.Scheduler.exe') -Value 'lint placeholder'
Set-Content -LiteralPath (Join-Path $schedulerDir 'DelayStart.LaunchBroker.exe') -Value 'lint placeholder'

# ---- 两轮：full / slim -------------------------------------------------------
# /O- = 关闭输出（只编译不打包）；/Q = 只打印错误信息。
# AppVersion 不传：版本号是纯字符串，体检它没有意义，让 .iss 用自带缺省值即可。
$variants = @(
    @{ Name = 'full'; Defines = @() },
    @{ Name = 'slim'; Defines = @('/DSlim') }
)

$failed = @()
try {
    foreach ($variant in $variants) {
        # ⚠️ 别把这段叫 $args —— 那是 PowerShell 的自动变量（未绑定实参数组），占用它迟早出事。
        $isccArgs = @(
            '/O-', '/Q',
            $issPath,
            "/DRid=win-x64",
            "/DPublishDir=$appDir",
            "/DSchedulerDir=$schedulerDir",
            '/DTargetArch=x64compatible',
            '/DWinAppRuntimeUrl=https://aka.ms/windowsappsdk/1.8/latest/windowsaptruntimeinstall-x64.exe',
            '/DWinAppRuntimeArch=x64'
        ) + $variant.Defines

        Write-Host "`n=== ISCC $($variant.Name) ===" -ForegroundColor Cyan
        & $iscc @isccArgs
        if ($LASTEXITCODE -ne 0) {
            $failed += $variant.Name
            Write-Host "[X] $($variant.Name)：ISCC 退出码 $LASTEXITCODE" -ForegroundColor Red
        }
        else {
            Write-Host "[OK] $($variant.Name)：解析 + [Code] 编译通过（未产出安装包）" -ForegroundColor DarkGray
        }
    }
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed.Count -gt 0) {
    Write-Host "`n[X] 安装脚本语法体检失败：$($failed -join ' / ')" -ForegroundColor Red
    Write-Host '    上方 ISCC 报错里的行号直接对应 installer\DelayStart.iss。' -ForegroundColor Red
    exit 1
}

Write-Host "`n[OK] 安装脚本语法体检完成（full + slim，exit 0）" -ForegroundColor Green
