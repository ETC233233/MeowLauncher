param(
    [Parameter(Mandatory=$true)][ValidateSet('beta','release')][string]$Channel,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Build,
    [string]$PythonDir = 'publish-slim/Backend/python'
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Build -notmatch '^\d{5,}-\d+$') { throw 'Invalid version/build' }
$display = if ($Channel -eq 'beta') { "$Version-$Build" } else { $Version }
$out = "_export/releases/MeowLauncher-$display-update-ready"
if (Test-Path -LiteralPath $out) { throw 'Output exists; use a fresh immutable version' }
Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish MeowLauncher.WinUI/MeowLauncher.WinUI.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    "-p:MeowChannel=$Channel" "-p:MeowVersion=$Version" "-p:MeowBuildId=$Build" -p:NuGetAudit=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
if (-not (Test-Path -LiteralPath (Join-Path $PythonDir 'python.exe'))) { throw 'Embedded Python is required' }
Copy-Item -LiteralPath $PythonDir -Destination (Join-Path $out 'Backend/python') -Recurse
$site = & python -c 'import PIL,pathlib; print(pathlib.Path(PIL.__file__).parent.parent)'
if ($LASTEXITCODE -ne 0) { throw 'Pillow not found' }
$dest = Join-Path $out 'Backend/python/Lib/site-packages'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -LiteralPath (Join-Path $site 'PIL') -Destination $dest -Recurse -Force
Get-ChildItem -LiteralPath $site -Directory -Filter 'pillow-*.dist-info' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $dest -Recurse -Force }
& (Join-Path $out 'Backend/python/python.exe') -s -c 'import PIL'
if ($LASTEXITCODE -ne 0) { throw 'Embedded Pillow validation failed' }
$feed = if ($Channel -eq 'release') { 'stable' } else { 'beta' }
python tools/package_update.py $out --version $display --channel $feed
if ($LASTEXITCODE -ne 0) { throw 'Package generation failed' }
Write-Host "Publish the ZIP and release notes on GitHub under tag $display. Verify the public asset size and SHA-256."
$offer = "_export/releases/MeowLauncher-$display-win-x64-portable.zip.launcher.json"
Write-Host "Before publishing LAUNCHER, run: python tools/sync_release_notes.py $offer _export/releases/MeowLauncher-$display.synced-offer.json"
