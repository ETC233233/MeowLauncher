
$root='C:\Users\Syai_\Documents\deepseek\harness\default\workspace\mclauncher'
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Get-Process | Where-Object { $_.ProcessName -like 'MeowLauncher-Setup*' } | Stop-Process -Force -ErrorAction SilentlyContinue
$tmp=Join-Path $root '_setuptmp3'; New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$env:TEMP=$tmp; $env:TMP=$tmp
Start-Process -FilePath (Join-Path $root '_export\_test\MeowLauncher-Setup-0.5.0.exe')
Start-Sleep -Seconds 4
[System.Windows.Forms.SendKeys]::SendWait('%n'); Start-Sleep -Milliseconds 900
[System.Windows.Forms.SendKeys]::SendWait('%n'); Start-Sleep -Milliseconds 900
[System.Windows.Forms.SendKeys]::SendWait('%n'); Start-Sleep -Milliseconds 900
[System.Windows.Forms.SendKeys]::SendWait('%i')
$out=Join-Path $root '_screenshots'
for ($i=1; $i -le 10; $i++) {
  Start-Sleep -Seconds 2
  $p = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and ($_.ProcessName -like 'MeowLauncher-Setup*' -or $_.MainWindowTitle -like '*MeowLauncher*') } | Select-Object -First 1
  if (-not $p) { break }
  $h = $p.MainWindowHandle
  [Win]::SetForegroundWindow($h) | Out-Null
  $r = New-Object Win+RECT
  [Win]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
  if ($w -lt 200) { continue }
  $bmp = New-Object System.Drawing.Bitmap $w,$hh
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.Left,$r.Top,0,0,(New-Object System.Drawing.Size $w,$hh))
  $bmp.Save((Join-Path $out ("inst-frame{0}.png" -f $i)), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
}
Write-Output 'done'
