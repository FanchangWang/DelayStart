# DelayStart —— 一条龙：编译 → 安装脚本体检 → 单测 → 发布同步
# 用法: .\scripts\all.ps1
#
# 顺序说明：lint-installer 排在 build 之后、test 之前。理由是它**最快、且不依赖
# 任何构建产物** —— ISCC 的 /O- 只解析不打包，publish 目录由脚本自带的临时沙箱
# 顶替，所以它其实排在最前面也行；放 build 之后是为了让「代码改坏了没编译过」
# 这种更基础的问题先暴露，而 test / publish 才是真正吃时间的部分，体检排在它们
# 前面意味着 .iss 写错时不用先等一轮全量编译 + 995 个用例 + 四次 AOT publish。
#
# 🔴 这里刻意**不**挂 installer\build-installer.ps1：那个脚本每次会跑 5 次
#    dotnet publish（含 AOT）并覆盖 dist\ 与 artifacts\，把一条秒级的语法检查
#    变成几十秒的重活，还会顺手覆盖产物。产出安装包是发布动作，不是验证动作。
#Requires -Version 7
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-Location "$PSScriptRoot\.."

& "$PSScriptRoot\build.ps1";            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\lint-installer.ps1";   if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\test.ps1";             if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$PSScriptRoot\publish.ps1";          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[OK] 全流程完成：build + 安装脚本体检 + test + publish 同步" -ForegroundColor Green
