$ErrorActionPreference='Continue'
Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process -FilePath 'C:\Users\Syai_\Documents\deepseek\harness\default\workspace\mclauncher\MeowLauncher.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MeowLauncher.WinUI.exe'
Start-Sleep -Seconds 20
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size)
$out='C:\Users\Syai_\Downloads\onb-real-1.png'
$bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("saved " + $out + " " + $b.Width + "x" + $b.Height)
