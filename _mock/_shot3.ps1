$ErrorActionPreference='Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@
Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -match 'dsh-browser' } | ForEach-Object { [W2]::ShowWindow($_.MainWindowHandle, 6) | Out-Null }
Start-Sleep -Milliseconds 800
$p = Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'no window'; exit 1 }
[W2]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
[W2]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
[W2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 1500
Add-Type -AssemblyName System.Windows.Forms
$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size)
$out='C:\Users\Syai_\Downloads\onb-real-3.png'
$bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
[W2]::SetWindowPos($p.MainWindowHandle, [IntPtr](-2), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
Write-Output ("saved " + $out)
