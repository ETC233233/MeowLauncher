
$root='C:\Users\Syai_\Documents\deepseek\harness\default\workspace\mclauncher'
$iscc='C:\Users\Syai_\AppData\Local\Programs\Inno Setup 6\ISCC.exe'
Get-Process | Where-Object { $_.ProcessName -like 'MeowLauncher-Setup*' } | Stop-Process -Force -ErrorAction SilentlyContinue
& $iscc (Join-Path $root 'installer\_test_setup.iss') | Select-Object -Last 1
$tmp=Join-Path $root '_setuptmp3'; New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:TEMP=$tmp; $env:TMP=$tmp
Start-Process -FilePath (Join-Path $root '_export\_test\MeowLauncher-Setup-0.5.0.exe')
Start-Sleep -Seconds 4
Add-Type -AssemblyName System.Windows.Forms
1..3 | ForEach-Object { [System.Windows.Forms.SendKeys]::SendWait('%n'); Start-Sleep -Milliseconds 800 }
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('%i')
Start-Sleep -Seconds 13
Add-Type -AssemblyName System.Drawing
$bmp=New-Object System.Drawing.Bitmap 1040,780
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(700,150,0,0,(New-Object System.Drawing.Size 1040,780))
$out=Join-Path $root '_screenshots\installer-installing.png'
$bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
Write-Output $out
