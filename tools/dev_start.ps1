param([switch]$Restore, [switch]$Rebuild, [switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $workspace
try {
    Write-Host '[MeowLauncher] 停止正在运行的实例...'
    Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
    $project = 'MeowLauncher.WinUI/MeowLauncher.WinUI.csproj'
    $projects = @($project)
    if (Test-Path '_export/meow-room-module/MeowRoom.Module.csproj') { $projects += '_export/meow-room-module/MeowRoom.Module.csproj' }
    $inputs = @($projects) + @('version.props','MeowLauncher.WinUI/online.props','global.json','NuGet.Config','nuget.config','Directory.Build.props','Directory.Build.targets','Directory.Packages.props')
    foreach ($dir in @('MeowLauncher.WinUI','_export','_export/meow-room-module')) {
        foreach ($file in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config')) { $inputs += Join-Path $dir $file }
    }
    $inputs += Join-Path $env:APPDATA 'NuGet/NuGet.Config'
    $fingerprint = ((& dotnet --version) + "`n" + (($inputs | Sort-Object -Unique | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
        $_ + ':' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    }) -join "`n"))
    $stamp = 'build/dev-restore-inputs.txt'
    $missing = @($projects | Where-Object { -not (Test-Path (Join-Path (Split-Path $_) 'obj/project.assets.json')) }).Count -gt 0
    $saved = if (Test-Path $stamp) { Get-Content -LiteralPath $stamp -Raw } else { '' }
    if ($Restore -or $missing -or $saved.TrimEnd() -ne $fingerprint.TrimEnd()) {
        Write-Host '[MeowLauncher] 依赖首次还原或配置发生变化，正在还原...'
        # 只对本地快速启动关闭联网漏洞审计；常规 build/publish 保持原来的审计设置。
        & dotnet restore $project -p:Platform=x64 -p:NuGetAudit=false --force -v:m
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        New-Item -ItemType Directory -Path 'build' -Force | Out-Null
        $fingerprint | Set-Content -LiteralPath $stamp -Encoding UTF8
    }
    Write-Host '[MeowLauncher] 编译（复用已还原依赖）...'
    $buildArgs = @('build',$project,'-c','Debug','-p:Platform=x64','--no-restore','-p:NuGetAudit=false','-v:m')
    if ($Rebuild) { $buildArgs += '-t:Rebuild' }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not $BuildOnly) {
        Write-Host '[MeowLauncher] 启动...'
        $exe = Join-Path $workspace 'MeowLauncher.WinUI/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/MeowLauncher.WinUI.exe'
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Normal
    }
} catch { Write-Error $_; exit 1 } finally { Pop-Location }
