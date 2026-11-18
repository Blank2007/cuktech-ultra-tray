param(
  [int]$X = 0, [int]$Y = 0, [int]$W = 800, [int]$H = 60,
  [string]$Out = "region.png", [int]$Scale = 2
)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class DPI2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[void][DPI2]::SetProcessDPIAware()
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)), [System.Drawing.CopyPixelOperation]::SourceCopy)
$g.Dispose()
if ($Scale -ne 1) {
  $bw = $W * $Scale
  $bh = $H * $Scale
  $big = New-Object System.Drawing.Bitmap($bw, $bh)
  $g2 = [System.Drawing.Graphics]::FromImage($big)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g2.DrawImage($bmp, 0, 0, $bw, $bh)
  $g2.Dispose()
  $big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  $big.Dispose()
} else {
  $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
$bmp.Dispose()
Write-Host ("saved {0} from {1},{2} {3}x{4} scale={5}" -f $Out, $X, $Y, $W, $H, $Scale)
