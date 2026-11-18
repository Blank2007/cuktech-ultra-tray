Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public class Cap {
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
'@ -ReferencedAssemblies System.Drawing
$tray = [Cap]::FindWindow("Shell_TrayWnd", $null)
$r = New-Object Cap+RECT
[void][Cap]::GetWindowRect($tray, [ref]$r)
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
$out = "D:\dsh projects\cuktech-ultra-tray\docs\taskbar-prototype.png"

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Cap]::PrintWindow($tray, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host ("printwindow={0} rect={1},{2} {3}x{4}" -f $ok, $r.Left, $r.Top, $w, $h)

# 同时也用屏幕抓取做对照（裁左侧一段，看白不平衡）
$bmp2 = New-Object System.Drawing.Bitmap($w, $h)
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
$g2.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g2.Dispose()
$bmp2.Save("D:\dsh projects\cuktech-ultra-tray\docs\taskbar-screen.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp2.Dispose()
Write-Host "done"
