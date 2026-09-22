#Requires -Version 7
<#
.SYNOPSIS
    构建单套 DelayStart 安装包（D60）。
.DESCRIPTION
    1) publish 管理端  —— 自包含（默认）或框架依赖（-Slim）
    2) publish 调度端  —— 两种形态都用 NativeAOT 单文件（它不依赖 .NET 运行时）
    3) 调 Inno Setup 编译安装包，产物落 dist\

    版本号取自 Directory.Build.props 的 <Version>，再通过 ISCC /DAppVersion 注入 iss，
    保证「程序集版本 = 安装程序版本」同源。
.PARAMETER Rid
    目标架构：win-x64（默认）或 win-arm64。
    ⚠️ arm64 的 AOT 需要主机装有 MSVC 的 ARM64 工具集，本机尚未安装（2026-09-20 实测），
       arm64 产物由 CI 产出（.github\workflows\release.yml）。
.PARAMETER Slim
    产出精简版（框架依赖，安装包只有几 MB，但用户需自备 .NET 10 Runtime
    与 Windows App Runtime 1.8 —— 安装器会检测并提示）。
    ⚠️ 是要 **.NET Runtime**，不是 Desktop Runtime：精简版的 runtimeconfig.json
       只声明 Microsoft.NETCore.App（2026-09-20 实测），WinUI 3 这层不要求 Desktop Runtime。
.PARAMETER Version
    覆盖版本号。不传则读 Directory.Build.props。
.PARAMETER Configuration
    构建配置，默认 Release。
.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Rid win-x64 -Slim
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',

    [switch]$Slim,

    [string]$Version,

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Write-Step([string]$Text) {
    Write-Host "`n=== $Text ===" -ForegroundColor Cyan
}

# ---- 版本号：单一来源 Directory.Build.props --------------------------------
if (-not $Version) {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    $propsText = Get-Content -LiteralPath $propsPath -Raw
    if ($propsText -match '<Version>\s*([^<]+?)\s*</Version>') {
        $Version = $Matches[1]
    }
    else {
        throw "无法从 $propsPath 解析 <Version>，请显式传 -Version"
    }
}

$flavor = if ($Slim) { 'slim' } else { 'full' }
Write-Step "构建 DelayStart $Version · $Rid · $flavor"

# ---- Inno Setup 定位 --------------------------------------------------------
# ⚠️ winget 安装的 Inno Setup 6 是 **per-user** 布局（2026-09-20 实测），
#    路径不在 Program Files —— 两个都探，避免文档与实际不一致时静默失败。
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "找不到 ISCC.exe。请先安装 Inno Setup 6：winget install --id JRSoftware.InnoSetup`n探测路径：`n  $($isccCandidates -join "`n  ")"
}
# ⚠️ 别指望读 ISCC.exe 的版本信息 —— 它的 FileVersion 是 `0.0.0.0`（2026-09-21 实测），
#    真正的编译器版本在 ISCC 自己的输出里：`Compiler engine version: Inno Setup x.y.z`
#    （D65 判读"语言文件与编译器版本是否错位"时看的就是这一行）。
Write-Host "ISCC: $iscc"

# ---- 语言文件（D65）：必须随仓库分发 ----------------------------------------
# 🔴 官方 Inno Setup 安装器**不带**简体中文 .isl（属用户贡献翻译），因此 DelayStart.iss 引用的是
#    仓库内的 installer\languages\ChineseSimplified.isl —— 相对路径按 .iss 所在目录解析。
#    文件缺失时 ISCC 只会抛一句难读的 "Couldn't open include file"，这里提前给出人话。
$langFile = Join-Path $PSScriptRoot 'languages\ChineseSimplified.isl'
if (-not (Test-Path -LiteralPath $langFile)) {
    throw @"
缺少安装器语言文件：$langFile
它随仓库分发（D65）—— 官方 Inno Setup 不带中文 .isl，别指望 compiler:Languages\ 里有一份。
任选一处取回后放回原路径：
  https://jrsoftware.org/files/istrans/
  https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation
"@
}
Write-Host "语言文件: $langFile"

# ---- 1) 管理端 publish ------------------------------------------------------
# 输出目录按 <rid>\<形态> 分开，full 与 slim 各占一个目录，**不会互相污染**，
# 因此这里刻意不做"先删干净再 publish"—— 覆盖同名文件交给 publish 自己处理。
# （曾经写过递归删除，结果：① 触发本机安全策略对批量删除的拦截；
#   ② 为了让目录"更干净"而递归删用户磁盘，收益与风险不成比例。）
$appPublishDir = Join-Path $repoRoot "artifacts\publish\$Rid\$flavor"
New-Item -ItemType Directory -Path $appPublishDir -Force | Out-Null

$appArgs = @(
    'publish', 'src\DelayStart.App\DelayStart.App.csproj',
    '-c', $Configuration,
    '-r', $Rid,
    '-o', $appPublishDir,
    '-v', 'minimal'
)
if ($Slim) {
    # 两个开关都要关：前者去掉 Windows App SDK 运行时，后者去掉 .NET 运行时
    $appArgs += '--no-self-contained'
    $appArgs += '-p:WindowsAppSDKSelfContained=false'
}
else {
    $appArgs += '--self-contained'
}

Write-Step "publish 管理端 ($flavor)"
Write-Host "dotnet $($appArgs -join ' ')"
& dotnet @appArgs
if ($LASTEXITCODE -ne 0) { throw "管理端 publish 失败（exit $LASTEXITCODE）" }

# ---- 1b) 守门：publish 必须带上 XAML 产物与资源包（D61）---------------------
# 🔴 2026-09-21 真机翻车：unpackaged 转换删掉 EnableMsixTooling 之后，XBF / PRI /
#    Content 资源不进 publish 输出，装出来的程序一启动就在 Microsoft.UI.Xaml.dll
#    里 0xc000027b 秒崩（而同一份产物在 bin 里双击完全正常，所以历次验收都没发现）。
#    App.csproj 的 CopyWinUIResourcesToPublishDir 负责补文件，这里负责**证明它生效了** ——
#    缺文件就在构建期炸掉，绝不把注定崩溃的安装包发给用户。
$requiredPublishFiles = @(
    'App.xbf',
    'MainWindow.xbf',
    'DelayStart.pri',
    (Join-Path 'Assets' 'AppIcon.ico')
)
$missingPublishFiles = @($requiredPublishFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $appPublishDir $_))
    })
# Views\ / Dialogs\ 下的 XBF 按实际页面数算，这里只要求"至少有一个"。
$xbfCount = @(Get-ChildItem -LiteralPath $appPublishDir -Recurse -File -Filter '*.xbf').Count
if ($missingPublishFiles.Count -gt 0 -or $xbfCount -lt 2) {
    throw @"
publish 输出缺少 XAML 产物 / 资源包（D61），装出来的程序会在 Microsoft.UI.Xaml.dll 里
0xc000027b 秒崩。缺失项：$($missingPublishFiles -join ', ')（*.xbf 共找到 $xbfCount 个）
排查方向：src\DelayStart.App\DelayStart.App.csproj 的 CopyWinUIResourcesToPublishDir target。
输出目录：$appPublishDir
"@
}
Write-Host "publish 资源自检通过：*.xbf $xbfCount 个 + DelayStart.pri + Assets\AppIcon.ico" -ForegroundColor DarkGray

# ---- 2) 调度端 publish（AOT，两种形态一致）--------------------------------
# 刻意不传 -o：产物落在默认 publish 目录，App 项目的 CopySchedulerPublishOutput
# 同步钩子（开发期用）依赖这个固定位置。
$schedulerPublishDir = Join-Path $repoRoot "src\DelayStart.Scheduler\bin\$Configuration\net10.0-windows\$Rid\publish"

Write-Step "publish 调度端 (NativeAOT $Rid)"
& dotnet publish src\DelayStart.Scheduler\DelayStart.Scheduler.csproj -c $Configuration -r $Rid -v minimal
if ($LASTEXITCODE -ne 0) { throw "调度端 publish 失败（exit $LASTEXITCODE）" }

$schedulerExe = Join-Path $schedulerPublishDir 'DelayStart.Scheduler.exe'
if (-not (Test-Path -LiteralPath $schedulerExe)) { throw "调度端产物缺失：$schedulerExe" }

# ---- 2b) UIAccess 中转器 publish（D70：uiAccess="true" 目标的降权链第二跳）----
# 与调度端同目录（{app}）部署；调度端按 AppContext.BaseDirectory 就近解析。
$brokerPublishDir = Join-Path $repoRoot "src\DelayStart.LaunchBroker\bin\$Configuration\net10.0-windows\$Rid\publish"

Write-Step "publish UIAccess 中转器 (NativeAOT $Rid)"
& dotnet publish src\DelayStart.LaunchBroker\DelayStart.LaunchBroker.csproj -c $Configuration -r $Rid -v minimal
if ($LASTEXITCODE -ne 0) { throw "UIAccess 中转器 publish 失败（exit $LASTEXITCODE）" }

$brokerExe = Join-Path $brokerPublishDir 'DelayStart.LaunchBroker.exe'
if (-not (Test-Path -LiteralPath $brokerExe)) { throw "UIAccess 中转器产物缺失：$brokerExe" }

# iss 只认 SchedulerDir 一个来源目录 —— 把中转器并进调度端 publish 目录一起打包。
Copy-Item -LiteralPath $brokerExe -Destination (Join-Path $schedulerPublishDir 'DelayStart.LaunchBroker.exe') -Force

# ---- 2c) 守卫 publish（D75：形态**跟随管理端**，输出并入管理端 publish 目录）------
# 🔴 守卫不能走 AOT：它经 Management 调 COM（IShellLinkW / TaskScheduler），
#    AOT 产物里激活会运行时抛 PlatformNotSupportedException（docs/pitfalls.md 五 R12）。
#    所以它的形态必须与管理端一致，而不是与 AOT 的调度端一致：
#      full → --self-contained（管理端 publish 里本来就有一整套平铺的运行时）
#      slim → --no-self-contained
# 输出直接 -o 进管理端 publish 目录：落点是 {app} 根目录，iss 的 PublishDir 通配符即可覆盖。
Write-Step "publish 守卫 ($flavor)"
$guardArgs = @(
    'publish', 'src\DelayStart.Guard\DelayStart.Guard.csproj',
    '-c', $Configuration,
    '-r', $Rid,
    '-o', $appPublishDir,
    '-v', 'minimal'
)
if ($Slim) { $guardArgs += '--no-self-contained' } else { $guardArgs += '--self-contained' }

& dotnet @guardArgs
if ($LASTEXITCODE -ne 0) { throw "守卫 publish 失败（exit $LASTEXITCODE）" }

$guardExe = Join-Path $appPublishDir 'DelayStart.Guard.exe'
if (-not (Test-Path -LiteralPath $guardExe)) { throw "守卫产物缺失：$guardExe" }

# ---- 2c-2) 通知中转器 publish（N1：调度完成通知的代发进程，形态跟随管理端）----
# 与守卫完全同款：非 AOT + WinRT 投影（TFM 带平台版本），full → --self-contained /
# slim → --no-self-contained，输出 -o 进管理端 publish 目录（iss 的 PublishDir 通配符覆盖）。
# 🔴 不能走 AOT：发通知走 WinRT 投影，AOT 下没有投影运行时（同守卫 D79 的理由）。
Write-Step "publish 通知中转器 ($flavor)"
$notifyBrokerArgs = @(
    'publish', 'src\DelayStart.NotifyBroker\DelayStart.NotifyBroker.csproj',
    '-c', $Configuration,
    '-r', $Rid,
    '-o', $appPublishDir,
    '-v', 'minimal'
)
if ($Slim) { $notifyBrokerArgs += '--no-self-contained' } else { $notifyBrokerArgs += '--self-contained' }

& dotnet @notifyBrokerArgs
if ($LASTEXITCODE -ne 0) { throw "通知中转器 publish 失败（exit $LASTEXITCODE）" }

$notifyBrokerExe = Join-Path $appPublishDir 'DelayStart.NotifyBroker.exe'
if (-not (Test-Path -LiteralPath $notifyBrokerExe)) { throw "通知中转器产物缺失：$notifyBrokerExe" }

# ---- 2d) 守门：slim 形态下 {app} 里绝不能出现主运行时（D64-1 / D75）---------
# 🔴 2026-09-20 已 1:1 复现过：.NET apphost 只要在自己目录里看到 hostfxr.dll，就把
#    "运行时根"当成程序目录，于是**框架依赖**的管理端会弹 "You must install or update .NET"
#    —— 哪怕机器上明明装着 10.0.x。守卫与管理端同目录，它选错形态就会把这条踩响，
#    所以在构建期直接炸掉，绝不把注定起不来的安装包发出去。
if ($Slim) {
    $hostFxrFiles = @(Get-ChildItem -LiteralPath $appPublishDir -Recurse -File -Filter 'hostfxr.dll')
    if ($hostFxrFiles.Count -gt 0) {
        throw @"
slim 形态下 publish 目录里出现了主运行时文件（$($hostFxrFiles.Count) 个 hostfxr.dll）。
D64-1 已复现过：框架依赖的管理端会因此无法启动。首个：$($hostFxrFiles[0].FullName)
排查方向：守卫 publish 是否漏了 --no-self-contained（见本脚本 2c 段）。
输出目录：$appPublishDir
"@
    }

    Write-Host "slim 自检通过：publish 目录内无 hostfxr.dll" -ForegroundColor DarkGray
}

# ---- 3) 编译安装包 ----------------------------------------------------------
$targetArch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64compatible' }
$winAppRuntimeArch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
$winAppRuntimeUrl = if ($Rid -eq 'win-arm64') {
    'https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-arm64.exe'
}
else {
    'https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe'
}

$isccArgs = @(
    'installer\DelayStart.iss',
    "/DAppVersion=$Version",
    "/DRid=$Rid",
    "/DPublishDir=$appPublishDir",
    "/DSchedulerDir=$schedulerPublishDir",
    "/DTargetArch=$targetArch",
    "/DWinAppRuntimeUrl=$winAppRuntimeUrl",
    # D61：Windows App Runtime 框架包名字里带架构段（_x64__ / _arm64__），
    # 精简版的检测要连架构一起匹配 —— arm64 机器上只有 x64 框架包时 arm64 程序照样起不来。
    "/DWinAppRuntimeArch=$winAppRuntimeArch"
)
if ($Slim) { $isccArgs += '/DSlim' }

Write-Step '编译安装包'
Write-Host "ISCC $($isccArgs -join ' ')"
& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（exit $LASTEXITCODE）" }

# ---- 结果 -------------------------------------------------------------------
$setupExe = Join-Path $repoRoot "dist\DelayStart-Setup-$Version-$Rid$(if ($Slim) { '-slim' } else { '' }).exe"
if (-not (Test-Path -LiteralPath $setupExe)) { throw "安装包未生成：$setupExe" }

$setup = Get-Item -LiteralPath $setupExe
$appFiles = Get-ChildItem -LiteralPath $appPublishDir -Recurse -File

Write-Host ''
Write-Host ('[OK] {0}' -f $setup.Name) -ForegroundColor Green
Write-Host ('     体积   {0:N1} MB' -f ($setup.Length / 1MB))
Write-Host ('     路径   {0}' -f $setup.FullName)
Write-Host ('     内含   管理端+守卫 {0} 个文件 / {1:N1} MB（{2}）+ 调度端 {3:N2} MB' -f `
        $appFiles.Count, (($appFiles | Measure-Object Length -Sum).Sum / 1MB), $flavor, `
    ((Get-Item -LiteralPath $schedulerExe).Length / 1MB))
