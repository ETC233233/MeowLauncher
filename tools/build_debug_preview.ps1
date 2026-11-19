param([string]$PythonDir = "")
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $workspace
try {
    Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
    dotnet build MeowLauncher.WinUI/MeowLauncher.WinUI.csproj -c Debug -p:Platform=x64 -t:Rebuild -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Debug rebuild failed.' }
    $buildId = (Get-Content -LiteralPath 'build/review-counter.txt' -Raw).Trim()
    $name = "MeowLauncher-Review-$buildId-Debug-win-x64"
    $releaseRoot = Join-Path $workspace '_export/debug-preview'
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    $outputDir = Join-Path $releaseRoot $name
    if (Test-Path -LiteralPath $outputDir) { $name += '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'); $outputDir = Join-Path $releaseRoot $name }
    dotnet publish MeowLauncher.WinUI/MeowLauncher.WinUI.csproj -c Debug -p:Platform=x64 -r win-x64 --self-contained false --no-build -o $outputDir -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Debug publish failed.' }
    if (-not $PythonDir) { $PythonDir = Join-Path $workspace 'publish-slim/Backend/python' }
    if (-not (Test-Path -LiteralPath (Join-Path $PythonDir 'python.exe'))) { throw 'Set -PythonDir to the embedded Python runtime folder.' }
    Copy-Item -LiteralPath $PythonDir -Destination (Join-Path $outputDir 'Backend/python') -Recurse
    foreach ($required in @('MeowLauncher.WinUI.exe','MeowLauncher.WinUI.pri','Backend/sidecar.py','Backend/ml2/session.py','Backend/ml2/health.py','Backend/ml2/pack_status.py','Assets/Onboarding/oobe.html','Assets/skinviewer/vendor/skinview3d.bundle.js','Licenses/THIRD-PARTY-NOTICES.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $outputDir $required))) { throw "Missing published file: $required" }
    }
    $sourceCommit = (git rev-parse HEAD).Trim()
    @{channel='Review';build=$buildId;configuration='Debug';platform='win-x64';sourceCommit=$sourceCommit;selfContained=$false;embeddedPython=$true} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDir 'BUILD-INFO.json') -Encoding utf8
    @'
MeowLauncher Debug preview

This is a Review development build, not a stable release.
Extract the complete folder before running MeowLauncher.WinUI.exe.
Keep Backend, Assets, Licenses and the .pri file beside the application.

Requirements: Windows 10 1809 or later (x64), .NET 8 Desktop Runtime,
Windows App Runtime 1.8 and Microsoft Edge WebView2 Runtime.
Python is bundled. Online multiplayer remains an optional developer feature.
Do not install this preview over an existing launcher directory.
'@ | Set-Content -LiteralPath (Join-Path $outputDir 'DEBUG-PREVIEW.txt') -Encoding utf8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $releaseRoot ($name + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($outputDir, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
    Write-Output "OUTPUT=$outputDir"
    Write-Output "ZIP=$zip"
    Write-Output "SHA256=$hash"
} finally { Pop-Location }
