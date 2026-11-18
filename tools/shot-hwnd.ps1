# Capture one window by HWND with PrintWindow (works for XAML taskbar and child windows).
# Usage: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\shot-hwnd.ps1 -Hwnd 0x1234 -Out out.png -Scale 3
param(
  [Parameter(Mandatory=$true)][string]$Hwnd,
  [Parameter(Mandatory=$true)][string]$Out,
  [int]$Scale = 1
)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class PW {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@ -ReferencedAssemblies System.Drawing

# 本进程先声明 per-monitor 感知，否则 GetWindowRect 拿到的是被 DPI 虚拟化过的尺寸：
# 125% 缩放下的 465x572 窗口会报成 372x458，PrintWindow 也就只画左上 80%，右侧被裁。
[void][PW]::SetProcessDpiAwarenessContext([IntPtr](-4))

$hv = [IntPtr]([Convert]::ToInt64(($Hwnd -replace '0x',''), 16))
$r = New-Object PW+RECT
if (-not [PW]::GetWindowRect($hv, [ref]$r)) { Write-Host "GetWindowRect failed"; exit 2 }
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { Write-Host "empty rect"; exit 3 }
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [PW]::PrintWindow($hv, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
if ($ok -eq $false) { Write-Host "PrintWindow returned false (bitmap may be blank)" }
if ($Scale -gt 1) {
  $big = New-Object System.Drawing.Bitmap ($w * $Scale), ($h * $Scale)
  $g2 = [System.Drawing.Graphics]::FromImage($big)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g2.DrawImage($bmp, 0, 0, $w * $Scale, $h * $Scale)
  $g2.Dispose()
  $big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  $big.Dispose()
  $bmp.Dispose()
} else {
  $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}
Write-Host ("saved {0} ({1}x{2} source, scale {3}, visible={4})" -f $Out, $w, $h, $Scale, [PW]::IsWindowVisible($hv))
exit 0