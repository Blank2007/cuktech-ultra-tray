param(
  [int]$X = 0, [int]$Y = 0, [int]$W = 800, [int]$H = 60,
  [Parameter(Mandatory=$true)][string]$Out, [int]$Scale = 1
)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class GDIS {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, uint rop);
  [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
}
"@
[void][GDIS]::SetProcessDPIAware()
Write-Host ("pixel@widget={0:X6} pixel@start={1:X6}" -f [GDIS]::GetPixel([GDIS]::GetDC([IntPtr]::Zero), 650, 1570), [GDIS]::GetPixel([GDIS]::GetDC([IntPtr]::Zero), 800, 1570))
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dst = $g.GetHdc()
$src = [GDIS]::GetDC([IntPtr]::Zero)
$ok = [GDIS]::BitBlt($dst, 0, 0, $W, $H, $src, $X, $Y, 0x00CC0020)
[void][GDIS]::ReleaseDC([IntPtr]::Zero, $src)
$g.ReleaseHdc($dst)
$g.Dispose()
if ($Scale -ne 1) {
  $bw = $W * $Scale; $bh = $H * $Scale
  $big = New-Object System.Drawing.Bitmap($bw, $bh)
  $g2 = [System.Drawing.Graphics]::FromImage($big)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g2.DrawImage($bmp, 0, 0, $bw, $bh)
  $g2.Dispose()
  $big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  $big.Dispose()
} else { $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png) }
$bmp.Dispose()
Write-Host ("bitblt={0} out={1}" -f $ok, $Out)
