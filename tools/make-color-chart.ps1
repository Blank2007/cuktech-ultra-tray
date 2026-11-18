Add-Type -AssemblyName System.Drawing
$W = 1000; $H = 470
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(22,24,29))
$g.FillRectangle($bg, 0, 0, $W, $H)
$title = New-Object System.Drawing.Font "Microsoft YaHei UI", 20, ([System.Drawing.FontStyle]::Bold)
$near  = New-Object System.Drawing.Font "Microsoft YaHei UI", 14
$small = New-Object System.Drawing.Font "Microsoft YaHei UI", 11
$wb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230,234,240))
$gb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150,158,168))
$g.DrawString("总功率数字的颜色跟着功率走", $title, $wb, 40, 28)
$g.DrawString("这台充电器满载 120W。数字每帧按当前总功率重新取色。", $small, $gb, 42, 76)

$rows = @(
  @{ V = "0 W";        R = "空闲 / 没接设备";      C = @(154,160,166); N = "灰 #9AA0A6" },
  @{ V = "0.1 – 20 W"; R = "小电流（耳机、手表、待机）"; C = @(125,211,252); N = "淡蓝 #7DD3FC" },
  @{ V = "20 – 60 W";  R = "正常充电（手机、平板）";  C = @(52,211,153);  N = "绿 #34D399" },
  @{ V = "60 – 100 W"; R = "偏高（轻薄本满载）";      C = @(251,191,36);  N = "黄 #FBBF24" },
  @{ V = "100 W 以上"; R = "接近满载（游戏本）";      C = @(248,113,113); N = "红 #F87171" }
)
$y = 116
foreach ($r in $rows) {
  $col = [System.Drawing.Color]::FromArgb($r.C[0], $r.C[1], $r.C[2])
  $br = New-Object System.Drawing.SolidBrush $col
  $g.FillRectangle($br, 44, $y + 6, 26, 26)
  $g.DrawString($r.V, $near, $br, 86, $y)
  $g.DrawString($r.R, $small, $gb, 300, $y + 8)
  $g.DrawString($r.N, $small, $gb, 760, $y + 8)
  $y += 62
}
$out = Join-Path $PSScriptRoot "..\docs\watt-colors.png"
$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $out"
