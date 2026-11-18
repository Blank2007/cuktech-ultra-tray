Add-Type -AssemblyName System.Drawing
$W = 1600; $H = 380
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

$bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(24,24,26))
$g.FillRectangle($bg, 0, 0, $W, $H)

$yb = 40; $hb = 96
$tbBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(32,32,34))
$g.FillRectangle($tbBrush, 0, $yb, $W, $hb)

$font = New-Object System.Drawing.Font("Microsoft YaHei", 11)
$fontSmall = New-Object System.Drawing.Font("Microsoft YaHei", 9)
$fontBig = New-Object System.Drawing.Font("Segoe UI", 22, [System.Drawing.FontStyle]::Bold)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(232,234,237))
$gray = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150,150,155))

# start button
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0,120,212))), 20, $yb+24, 48, 48)
# app icons placeholders
for ($i = 0; $i -lt 7; $i++) {
  $x = 110 + $i * 62
  $g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(58,58,64))), $x, $yb+24, 48, 48)
}

# notification area
$notifyX = 1210
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(38,38,42))), $notifyX, $yb+12, 370, $hb-24)
$g.DrawString("20:56", $font, $white, $notifyX + 18, $yb + 32)
$g.DrawString("2026/11/17", $fontSmall, $gray, $notifyX + 15, $yb + 58)

# our widget
$wx = $notifyX - 16 - 190
$wbg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(32,32,32))
$g.FillRectangle($wbg, $wx, $yb, 190, $hb)
$green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(52,211,153))
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$g.DrawString("32.2W", $fontBig, $green, (New-Object System.Drawing.RectangleF($wx, $yb, 190, $hb)), $sf)

$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(125,211,252), 2)
$pen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
$g.DrawRectangle($pen, $wx, $yb, 190, $hb)
$g.DrawString("这里就是那一小块（76×48，实时功率，随功率变色，点一下打开面板）", $font, $white, 620, 8)

# color ramp row
$y2 = 200
$g.DrawString("功率数字的颜色分档（总功率）", $font, $white, 40, $y2)
$items = @(
  @{ w = "0 W";    t = "空载";       c = @(154,160,166) },
  @{ w = "0.1-20 W"; t = "小功率";   c = @(125,211,252) },
  @{ w = "20-60 W";  t = "中功率";   c = @(52,211,153) },
  @{ w = "60-100 W"; t = "高功率";   c = @(251,191,36) },
  @{ w = "100 W+";   t = "接近满载"; c = @(248,113,113) }
)
$x = 40
foreach ($it in $items) {
  $col = [System.Drawing.Color]::FromArgb($it.c[0], $it.c[1], $it.c[2])
  $br = New-Object System.Drawing.SolidBrush($col)
  $g.FillRectangle($br, $x, $y2 + 40, 190, 92)
  $cb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(20,20,20))
  $g.DrawString($it.w, (New-Object System.Drawing.Font("Segoe UI", 15, [System.Drawing.FontStyle]::Bold)), $cb, $x + 14, $y2 + 62)
  $g.DrawString($it.t, (New-Object System.Drawing.Font("Microsoft YaHei", 9)), $cb, $x + 14, $y2 + 100)
  $x += 210
}

$g.Dispose()
$out = "D:\dsh projects\cuktech-ultra-tray\docs\taskbar-look.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host ("saved " + $out)
