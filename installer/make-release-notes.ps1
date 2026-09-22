# 从 CHANGELOG.md 提取指定 tag 的功能变更节，并为 dist\ 下的安装包生成
# 附件说明表（文件名 / 大小 / SHA256 / 用途），拼成 GitHub Release 正文。
#
# 用法（CI 内）：
#   ./installer/make-release-notes.ps1 -Tag "v0.2.0" -DistDir dist -OutFile release-notes.md
#
# 规则：
#   · CHANGELOG.md 里必须有 "## <Tag>" 节（如 "## v0.2.0"），找不到直接 throw ——
#     宁可发版失败也不发布一个没有功能变更说明的 Release。
#   · "## 未发布" 节的内容**不会**进 Release（那是还没发的东西）。
#   · 附件说明按文件名模式匹配（build-installer.ps1 的命名约定）：
#       DelayStart-Setup-<版本>-<rid>[-slim].exe
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Tag,      # 如 v0.2.0（与 CHANGELOG.md 节标题一致）
    [Parameter(Mandatory)] [string] $DistDir,  # 安装包所在目录（dist）
    [Parameter(Mandatory)] [string] $OutFile   # 输出的 Release 正文 markdown
)

$ErrorActionPreference = 'Stop'

# ── 1. 提取功能变更节 ─────────────────────────────────────────────────────────
$changelogPath = Join-Path $PSScriptRoot '..\CHANGELOG.md'
if (-not (Test-Path $changelogPath)) { throw "找不到 $changelogPath" }
$changelog = Get-Content -LiteralPath $changelogPath -Raw -Encoding utf8

# 从 "## <Tag>" 行取到下一个 "## " 标题或文末；(?s) 让 . 跨行，(?m) 让 ^ 匹配行首。
# 节标题允许带日期后缀（"## v0.2.0 - 2026-03-01"，Keep a Changelog 格式）；
# tag 后的 (?=[\s\r\n-]) 是边界 —— 防 "v0.1" 误配到 "## v0.1.0" 节。
$pattern = '(?ms)^##\s+' + [regex]::Escape($Tag) + '(?=[\s\r\n-])[^\r\n]*\r?\n(.*?)(?=^##\s|\z)'
$section = [regex]::Match($changelog, $pattern)
if (-not $section.Success) {
    throw "CHANGELOG.md 里没有 '$Tag' 节 —— 发版前先补功能变更日志（参照既有节格式）。"
}
$featureNotes = $section.Groups[1].Value.Trim()

# ── 2. 附件清单表（大小 + SHA256 + 用途说明）──────────────────────────────────
# 附件本身不支持 caption（GitHub API 限制），说明统一放正文表格 —— 开源惯例做法。
$files = Get-ChildItem -LiteralPath $DistDir -Filter *.exe | Sort-Object Name
if (-not $files) { throw "$DistDir 下没有安装包，无法生成附件说明表。" }

function Get-AssetNote([string] $Name) {
    # slim 先判（win-x64-slim.exe 不匹配 win-x64.exe$，但顺序写清楚免将来改名踩坑）
    if ($Name -like '*-win-x64-slim.exe') {
        return 'x64 · 框架依赖：体积小，但需先装 [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) 与 [Windows App Runtime](https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe) 才能启动'
    }
    if ($Name -like '*-win-arm64-slim.exe') {
        return 'ARM64 · 框架依赖：体积小，需先装 [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) 与 [Windows App Runtime ARM64](https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-arm64.exe) 才能启动'
    }
    if ($Name -like '*-win-x64.exe') {
        return '**大多数人的选择** · 自包含运行时，装完即用'
    }
    if ($Name -like '*-win-arm64.exe') {
        return 'ARM64 设备（骁龙 Surface 等）· 自包含运行时，装完即用'
    }
    return '（未识别的产物，请核对 build-installer.ps1 命名约定）'
}

function Format-Size([long] $Bytes) {
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    return '{0:N0} KB' -f ($Bytes / 1KB)
}

$rows = foreach ($f in $files) {
    $sha = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "| ``$($f.Name)`` | $(Format-Size $f.Length) | ``$sha`` | $(Get-AssetNote $f.Name) |"
}

$body = @()
$body += $featureNotes
$body += ''
$body += '## 下载哪一个？'
$body += ''
$body += '| 文件 | 大小 | SHA256 | 说明 |'
$body += '|---|---|---|---|'
$body += $rows
$body += ''
$body += '> SHA256 用于校验下载完整性：`Get-FileHash <文件> -Algorithm SHA256`。'
$body += ''
$body += '所有安装包均为官方 Inno Setup 中文向导；卸载时会**先还原所有接管的自启动项**再删程序文件。'

Set-Content -LiteralPath $OutFile -Value ($body -join "`r`n") -Encoding utf8
Write-Host "Release 正文已生成：$OutFile（附件 $($files.Count) 个）"
