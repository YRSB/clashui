Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\src\Clashui.App\Assets\app.ico"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

$sizes = 16, 32, 48
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.TextRenderingHint = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)

  $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 46, 110, 245))
  $r = [Math]::Max(1, [int]($s * 0.06))
  $g.FillEllipse($brush, $r, $r, $s - 2 * $r, $s - 2 * $r)

  $font = New-Object System.Drawing.Font('Segoe UI', ($s * 0.58), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = 'Center'
  $fmt.LineAlignment = 'Center'
  $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
  $g.DrawString('C', $font, [System.Drawing.Brushes]::White, $rect, $fmt)
  $g.Dispose()
  $bmp.SetResolution(96, 96)

  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  $pngs += , $ms.ToArray()
  $ms.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose(); $fs.Dispose()
Write-Host "icon written: $out"
