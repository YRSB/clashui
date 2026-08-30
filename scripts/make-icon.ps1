# 从 clash-cat.png（经典 Clash 猫头，取自 clash-verge-rev 仓库）生成 4 个托盘状态图标：
#   app.ico       原色（核心运行中，无代理/TUN）
#   app-off.ico   灰化（核心未运行）
#   app-proxy.ico 右下绿点（系统代理开）
#   app-tun.ico   右下橙点（TUN 开；与系统代理同开时优先显示 TUN）
# ICO 为 PNG 压缩条目，16/24/32/48 四档（24 覆盖 150% DPI 的托盘）。
param([string]$BasePng)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not $BasePng) { $BasePng = Join-Path $PSScriptRoot "clash-cat.png" }
if (-not (Test-Path $BasePng)) { throw "base png not found: $BasePng" }
$outDir = Join-Path $PSScriptRoot "..\src\Clashui.App\Assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$base = New-Object System.Drawing.Bitmap($BasePng)
$sizes = 16, 24, 32, 48

$gray = New-Object System.Drawing.Imaging.ColorMatrix
# ColorMatrix 是行向量右乘：结果第 i 个分量取矩阵第 i 列，灰度权重须按列排
$gray.Matrix00 = [single]0.299; $gray.Matrix10 = [single]0.587; $gray.Matrix20 = [single]0.114
$gray.Matrix01 = [single]0.299; $gray.Matrix11 = [single]0.587; $gray.Matrix21 = [single]0.114
$gray.Matrix02 = [single]0.299; $gray.Matrix12 = [single]0.587; $gray.Matrix22 = [single]0.114
$gray.Matrix33 = [single]1
$grayAttrs = New-Object System.Drawing.Imaging.ImageAttributes
$grayAttrs.SetColorMatrix($gray)

function New-Frame([int]$s, [string]$mode) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s)
  $bmp.SetResolution(96, 96)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.SmoothingMode = 'AntiAlias'
  $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
  if ($mode -eq 'off') {
    $g.DrawImage($base, $rect, 0, 0, $base.Width, $base.Height, [System.Drawing.GraphicsUnit]::Pixel, $grayAttrs)
  } else {
    $g.DrawImage($base, $rect)
  }
  if ($mode -eq 'proxy' -or $mode -eq 'tun') {
    $color = if ($mode -eq 'proxy') { [System.Drawing.Color]::FromArgb(255, 46, 204, 64) }
             else { [System.Drawing.Color]::FromArgb(255, 255, 149, 0) }
    $d = [Math]::Max(4, [int][Math]::Round($s * 0.42))
    $cx = [Math]::Round($s * 0.76); $cy = [Math]::Round($s * 0.76)
    $ringW = [Math]::Max(1, [int][Math]::Round($s * 0.07))
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]$ringW)
    $g.DrawEllipse($pen, ($cx - $d / 2 - $ringW), ($cy - $d / 2 - $ringW), ($d + 2 * $ringW), ($d + 2 * $ringW))
    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.FillEllipse($brush, ($cx - $d / 2), ($cy - $d / 2), $d, $d)
    $pen.Dispose(); $brush.Dispose()
  }
  $g.Dispose()
  return $bmp
}

$variants = [ordered]@{
  'app'       = 'normal'
  'app-off'   = 'off'
  'app-proxy' = 'proxy'
  'app-tun'   = 'tun'
}

foreach ($entry in $variants.GetEnumerator()) {
  $name = $entry.Key; $mode = $entry.Value
  $pngs = @()
  foreach ($s in $sizes) {
    $bmp = New-Frame $s $mode
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += , $ms.ToArray()
    $ms.Dispose()
  }
  $out = Join-Path $outDir "$name.ico"
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
}

# 预览图：灰底上排 48px、下排 16px 放大 3 倍，供人工检查
$cell = 48
$pv = [System.Drawing.Bitmap]::new([int]($cell * $variants.Count), [int]($cell * 2))
$pg = [System.Drawing.Graphics]::FromImage($pv)
$pg.InterpolationMode = 'NearestNeighbor'
$pg.PixelOffsetMode = 'Half'
$pg.Clear([System.Drawing.Color]::FromArgb(255, 128, 128, 128))
$x = 0
foreach ($entry in $variants.GetEnumerator()) {
  $big = New-Frame $cell $entry.Value
  $pg.DrawImage($big, $x, 0, $cell, $cell)
  $big.Dispose()
  $small = New-Frame 16 $entry.Value
  $pg.InterpolationMode = 'NearestNeighbor'
  $pg.DrawImage($small, ($x + 16), ($cell + 8), 48, 48)
  $small.Dispose()
  $x += $cell
}
$pg.Dispose()
$preview = Join-Path $env:TEMP "clashui-icons\tray-preview.png"
New-Item -ItemType Directory -Force -Path (Split-Path $preview) | Out-Null
$pv.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
$pv.Dispose()
Write-Host "preview: $preview"
