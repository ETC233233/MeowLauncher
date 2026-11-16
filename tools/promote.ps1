<#
  晋升：把一个 Review 构建「标成」Beta 或 Release 出包（三档版本号：Review / Beta / Release）。
  不自增迭代次数，用你指定的构建号（-p:MeowBuildId），所以包里的功能版本就是那个 Review 构建。

  用法（在 mclauncher/ 下）：
    powershell -File tools/promote.ps1 -To beta    -Version 1.0.0 -Build 01113-7829
    powershell -File tools/promote.ps1 -To release -Version 1.0.0 -Build 01113-7829

  显示：Beta = 1.0.0-01113-7829（预发行提示）；Release = 1.0.0（功能版本 01113-7829）。
  输出目录：_export/releases/MeowLauncher-<显示名>/ （自包含发布；内嵌 Python 从 -PythonDir 复制）。
  注意：同一个版本号在 Release 之前统一；版本号规则：修订+1=小幅更新，次版本+1=大幅更新，主版本+1=颠覆性更新。
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet("beta", "release")][string]$To,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Build,
    [string]$PythonDir = "publish-slim/Backend/python"
)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "版本号必须是 主.次.修订，例如 1.0.0" }
if ($Build -notmatch '^\d+\d{4}-\d+$') { throw "构建号格式是 <年序><月日>-<迭代次数>，例如 01113-7829" }

$name = if ($To -eq "beta") { "$Version-$Build" } else { $Version }
$out = "_export/releases/MeowLauncher-$name" + $(if ($To -eq "beta") { "-beta" } else { "" })
if (Test-Path $out) { throw "输出目录已存在（不覆盖）：$out" }

Write-Host "== 出包 $To $name（构建号 $Build）"
dotnet publish MeowLauncher.WinUI/MeowLauncher.WinUI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    "-p:MeowChannel=$To" "-p:MeowVersion=$Version" "-p:MeowBuildId=$Build" -o $out | Out-Host
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

if (Test-Path $PythonDir) {
    Copy-Item $PythonDir (Join-Path $out "Backend/python") -Recurse
} else {
    Write-Warning "没找到 $PythonDir，包里没有内嵌 Python（用户机器需要自备）"
}
# 后端要用 Pillow（头像 / 截图缩略图）。内嵌 Python 默认没有它——开发机上能跑只是因为用到了用户目录里装的那份，
# 新设备上就没有了。从本机 Python 的 site-packages 拷一份进包里，并用 -s（禁用用户目录）验证。
if (Test-Path (Join-Path $out "Backend/python/python.exe")) {
    $sp = & python -c "import PIL,os; print(os.path.dirname(os.path.dirname(PIL.__file__)))"
    if (-not $sp -or -not (Test-Path (Join-Path $sp "PIL"))) { throw "本机 Python 没有 Pillow，没法打进包里" }
    $dst = Join-Path $out "Backend/python/Lib/site-packages"
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item (Join-Path $sp "PIL") $dst -Recurse -Force
    Get-ChildItem $sp -Directory -Filter "pillow-*.dist-info" | ForEach-Object { Copy-Item $_.FullName $dst -Recurse -Force }
    Get-ChildItem $dst -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
    & (Join-Path $out "Backend/python/python.exe") -s -c "import PIL"
    if ($LASTEXITCODE -ne 0) { throw "内嵌 Python 导入 PIL 失败（已禁用用户目录）" }
    Write-Host "内嵌 Python 已带 Pillow（-s 验证通过）"
}
# 预编译 .pyc：冷启动时少一轮「读源码 + 编译」，也不依赖安装目录可写（Program Files 下运行时写不了 __pycache__）
if (Test-Path (Join-Path $out "Backend/python/python.exe")) {
    & (Join-Path $out "Backend/python/python.exe") -s -m compileall -q (Join-Path $out "Backend/ml2") (Join-Path $out "Backend/sidecar.py")
    if ($LASTEXITCODE -ne 0) { Write-Warning "预编译 .pyc 失败（不影响运行，只是冷启动慢一点）" } else { Write-Host "已预编译后端 .pyc" }
}
foreach ($need in "MeowLauncher.WinUI.exe", "MeowLauncher.WinUI.pri") {
    if (-not (Test-Path (Join-Path $out $need))) { throw "发布目录缺 $need" }
}
Write-Host ""
Write-Host "完成：$out"
Write-Host "显示名：$name" $(if ($To -eq "release") { "（功能版本 $Build）" } else { "" })
Write-Host "联机组件按此启动器的运行时打包时，请用：tools/pack_online_component.ps1 -Version <组件版本> -LauncherDir $out"
