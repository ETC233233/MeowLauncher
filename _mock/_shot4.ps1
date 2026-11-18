$ErrorActionPreference='Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W3 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Start-Process -FilePath 'C:\Users\Syai_\Documents\deepseek\harness\default\workspace\mclauncher\MeowLauncher.WinUI\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MeowLauncher.WinUI.exe'
Start-Sleep -Seconds 22
Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -match 'dsh-browser' } | ForEach-Object { [W3]::ShowWindow($_.MainWindowHandle, 6) | Out-Null }
Start-Sleep -Milliseconds 600
$p = Get-Process MeowLauncher.WinUI -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'no window'; exit 1 }
[W3]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
[W3]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
[W3]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0,0,0,0, 0x0003) | Out-Null
Start-Sleep -Milliseconds 1500
$r = New-Object W3+RECT
[W3]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
$out='C:\Users\Syai_\Downloads\onb-real-4.png'
$bmp.Save($out,[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
[W3]::SetWindowPos($p.MainWindowHandle, [IntPtr](-2), 0,0,0,0, 0x0003) | Out-Null
Write-Output ("saved " + $out + " " + $w + "x" + $h)
