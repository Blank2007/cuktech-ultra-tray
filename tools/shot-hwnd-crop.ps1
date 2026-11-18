param(
  [Parameter(Mandatory=$true)][string]$Hwnd,
  [int]$CX = 0, [int]$CY = 0, [int]$CW = 0, [int]$CH = 0,
  [Parameter(Mandatory=$true)][string]$Out,
  [int]$Scale = 1
)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class PW3 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[void][PW3]::SetProcessDPIAware()
$h = [IntPtr]([Convert]::ToInt64($Hwnd, 16))
$r = New-Object PW3+RECT
[void][PW3]::GetWindowRect($h, [ref]$r)
$fullW = $r.Right - $r.Left
$fullH = $r.Bottom - $r.Top
if ($CW -le 0) { $CW = $fullW }
if ($CH -le 0) { $CH = $fullH }
$bmp = New-Object System.Drawing.Bitmap($fullW, $fullH)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [PW3]::PrintWindow($h, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$crop = New-Object System.Drawing.Bitmap($CW, $CH)
$cg = [System.Drawing.Graphics]::FromImage($crop)
$cg.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0, 0, $CW, $CH)), (New-Object System.Drawing.Rectangle($CX, $CY, $CW, $CH)), [System.Drawing.GraphicsUnit]::Pixel)
$cg.Dispose()
if ($Scale -ne 1) {
  $bw = $CW * $Scale
  $bh = $CH * $Scale
  $big = New-Object System.Drawing.Bitmap($bw, $bh)
  $g2 = [System.Drawing.Graphics]::FromImage($big)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g2.DrawImage($crop, 0, 0, $bw, $bh)
  $g2.Dispose()
  $big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  $big.Dispose()
} else {
  $crop.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
$crop.Dispose()
$bmp.Dispose()
Write-Host ("printwindow={0} full={1}x{2} crop={3},{4} {5}x{6} scale={7} out={8}" -f $ok, $fullW, $fullH, $CX, $CY, $CW, $CH, $Scale, $Out)
