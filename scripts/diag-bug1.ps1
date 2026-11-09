# bug1 feedback loop: show window -> shot -> verdict
# Red signal = window content area is solid white (panel failed to render).
# Prereq for RED state: dwm restart (needs UAC):
#   pwsh -NoProfile -Command "Start-Process taskkill -ArgumentList '/F','/IM','dwm.exe' -Verb RunAs -Wait"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinFinder {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinShot {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
"@
function Shot-Window([string]$title, [string]$path) {
  $found = @{ H = [IntPtr]::Zero }
  $want = $title
  $cb = { param([IntPtr]$h, [IntPtr]$l) $sb = New-Object System.Text.StringBuilder(256); [WinFinder]::GetWindowText($h, $sb, 256) | Out-Null; if ($sb.ToString() -eq $want -and [WinFinder]::IsWindowVisible($h)) { $found.H = $h; return $false }; return $true }.GetNewClosure()
  [WinFinder]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
  $h = $found.H
  if ($h -eq [IntPtr]::Zero) { return -1.0 }
  [WinShot]::ShowWindow($h, 9) | Out-Null
  [WinShot]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 800
  $r = New-Object WinShot+RECT
  [WinShot]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
  if ($w -le 0 -or $hh -le 0) { return -1.0 }
  $bmp = New-Object System.Drawing.Bitmap($w, $hh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $hh)))
  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $stepX = [Math]::Max(1, [int]($w / 60)); $stepY = [Math]::Max(1, [int]($hh / 40))
  $white = 0; $total = 0
  for ($y = 100; $y -lt $hh; $y += $stepY) {
    for ($x = 0; $x -lt $w; $x += $stepX) {
      $c = $bmp.GetPixel($x, $y); $total++
      if ($c.R -gt 240 -and $c.G -gt 240 -and $c.B -gt 240) { $white++ }
    }
  }
  $bmp.Dispose()
  return [double]$white / [double]$total
}
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo 'Publish/ClashUI.App.exe'
& $exe
Start-Sleep -Seconds 3
$ratio = Shot-Window 'ClashUI' (Join-Path $PSScriptRoot 'shot-bug1.png')
if ($ratio -lt 0) { Write-Output 'VERDICT=UNKNOWN window-missing'; exit 2 }
Write-Output ("white-ratio={0:N3}" -f $ratio)
if ($ratio -gt 0.90) { Write-Output 'VERDICT=RED white-panel'; exit 1 }
Write-Output 'VERDICT=GREEN panel-rendered'; exit 0
