<#
  打联机组件包：联机模块 + RoomAgent（自包含）+ EasyTier 四个文件 + 许可证 → 一个 zip。
  启动器不再带这些；它们由云端签名清单指向的下载地址提供（见 MeowLauncher.WinUI/Services/OnlineComponent.cs）。

  用法（在 mclauncher/ 下）：
    powershell -File tools/pack_online_component.ps1 -Version 1.0.0
  输出：_export/online-component/meow-online-<版本>-api<级别>-win-x64.zip，并打印 sha256 / 大小 / 清单片段
  （把片段填进 config-worker 的 COMPONENTS，再部署配置服务）。

  包里的 component.json：{version, apiLevel, files:[{path, sha256}]}。启动器解压后逐个文件核对，
  有缺的、多的、哈希不对的一律拒绝，所以这里要把包里每个文件都列进去。
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [int]$ApiLevel = 1,
    [string]$EasyTierDir = "demo/easytier/easytier-windows-x86_64",
    # 启动器自己的目录（自包含，里面有整套 .NET 运行时）。包里不再重复放运行时：和它同名的文件只在 component.json 里记名字，
    # 安装时由启动器从自己的目录复制过去。默认用 Release 构建输出；正式打包请指向要发布的那份启动器目录。
    [string]$LauncherDir = "MeowLauncher.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 配置入口 / 验签公钥来自 online.props（公钥不是密钥）。模块和 RoomAgent 都要带上，RoomAgent 会自己去拉配置验签。
[xml]$props = Get-Content "MeowLauncher.WinUI/online.props" -Raw
$entry = $props.Project.PropertyGroup.MeowConfigEntry
$keys = $props.Project.PropertyGroup.MeowConfigKeys
if (-not $entry -or -not $keys) { throw "online.props 里没有 MeowConfigEntry / MeowConfigKeys" }

$work = Join-Path $root "obj/online-component"
$stage = Join-Path $work "stage"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Write-Host "== 编译 RoomAgent（自包含）"
$agent = "_export/meow-room-module/RoomAgent/RoomAgent.csproj"
dotnet publish $agent -c Release -p:Platform=x64 -r win-x64 --self-contained true "-p:MeowConfigEntry=$entry" "-p:MeowConfigKeys=$keys" -o $stage | Out-Host
if ($LASTEXITCODE -ne 0) { throw "RoomAgent 编译失败" }
foreach ($need in "RoomAgent.exe", "MeowRoom.Module.dll") {
    if (-not (Test-Path (Join-Path $stage $need))) { throw "编出来的包里缺 $need" }
}
# pdb 不需要带
Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force

# 运行时不放进包：和启动器目录里同名的文件改成「安装时从启动器复制」。我们自己的文件一律留在包里。
if (-not (Test-Path (Join-Path $LauncherDir "MeowLauncher.WinUI.exe"))) { throw "LauncherDir 不对（找不到 MeowLauncher.WinUI.exe）：$LauncherDir" }
$ours = @("RoomAgent.exe", "RoomAgent.dll", "RoomAgent.deps.json", "RoomAgent.runtimeconfig.json",
          "MeowRoom.Module.dll", "BouncyCastle.Cryptography.dll", "System.IO.Pipes.AccessControl.dll")
$runtimeFromLauncher = @()
foreach ($f in Get-ChildItem $stage -File) {
    if ($ours -contains $f.Name) { continue }
    if (Test-Path (Join-Path $LauncherDir $f.Name)) { $runtimeFromLauncher += $f.Name; Remove-Item $f.FullName -Force }
}
Write-Host ("运行时文件 {0} 个改为安装时从启动器复制，包里不放" -f $runtimeFromLauncher.Count)

Write-Host "== 复制 EasyTier"
$et = Join-Path $stage "easytier/easytier-windows-x86_64"
New-Item -ItemType Directory -Force $et | Out-Null
foreach ($f in "easytier-core.exe", "wintun.dll", "Packet.dll", "WinDivert64.sys") {
    $src = Join-Path $EasyTierDir $f
    if (-not (Test-Path $src)) { throw "缺 $src" }
    Copy-Item $src $et
}

Write-Host "== 复制许可证"
$lic = Join-Path $stage "Licenses"
New-Item -ItemType Directory -Force $lic | Out-Null
Copy-Item "tools/online-component/Licenses/*" $lic

Write-Host "== 写 component.json"
$files = Get-ChildItem $stage -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
    [pscustomobject]@{ path = $rel; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() }
}
$manifest = [ordered]@{ version = $Version; apiLevel = $ApiLevel; runtimeFromLauncher = @($runtimeFromLauncher); files = @($files) }
$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $stage "component.json"), $json, (New-Object System.Text.UTF8Encoding $false))

$outDir = Join-Path $root "_export/online-component"
New-Item -ItemType Directory -Force $outDir | Out-Null
$zip = Join-Path $outDir ("meow-online-{0}-api{1}-win-x64.zip" -f $Version, $ApiLevel)
if (Test-Path $zip) { Move-Item $zip ($zip + ".old-" + (Get-Date -Format yyyyMMddHHmmss)) }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal

$h = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
$size = (Get-Item $zip).Length
Write-Host ""
Write-Host "包：$zip"
Write-Host "大小：$size 字节（$([math]::Round($size / 1MB, 1)) MB）"
Write-Host "sha256：$h"
Write-Host ""
Write-Host "云端清单片段（urls 换成实际发布地址）："
@{ apiLevel = $ApiLevel; version = $Version; minLauncher = ""; maxLauncher = ""; required = $false; size = $size; sha256 = $h; urls = @("https://github.com/ETC233233/meow-room-module/releases/download/v$Version/" + (Split-Path $zip -Leaf)) } | ConvertTo-Json -Compress
