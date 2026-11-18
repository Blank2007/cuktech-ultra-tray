param([string]$Exe, [string]$Out, [int]$Pad = 140)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class WinUtil2
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    public struct RECT { public int L; public int T; public int R; public int B; }
    public static IntPtr FindPanel(uint pid, int minArea)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (wp != pid) return true;
            if (!IsWindowVisible(h)) return true;
            RECT r; if (!GetWindowRect(h, out r)) return true;
            if ((r.R - r.L) * (r.B - r.T) < minArea) return true;
            found = h; return false;
        }, IntPtr.Zero);
        return found;
    }
    public static RECT RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
    public static bool IsTopMost(IntPtr h) { return (GetWindowLong(h, -20) & 0x8) != 0; }
}
'@
[void][WinUtil2]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$proc = Start-Process -FilePath $Exe -ArgumentList "--pin" -PassThru
Start-Sleep -Seconds 7
$h = [WinUtil2]::FindPanel([uint32]$proc.Id, 40000)
if ($h -eq [IntPtr]::Zero) { Write-Output "panel not found"; exit 1 }
$r = [WinUtil2]::RectOf($h)
Write-Output ("panel hwnd={0} rect={1},{2},{3},{4} topmost={5}" -f $h, $r.L, $r.T, $r.R, $r.B, [WinUtil2]::IsTopMost($h))

$f = New-Object System.Windows.Forms.Form
$f.Text = "别的窗口（普通应用）"
$f.FormBorderStyle = "Sizable"
$f.StartPosition = "Manual"
$f.Bounds = New-Object System.Drawing.Rectangle ($r.L - 70), ($r.T - 70), 640, 470
$f.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 252)
$lbl = New-Object System.Windows.Forms.Label
$lbl.Text = "这是一个普通窗口，它盖住了常驻的充电器面板。" + [char]10 + "说明面板不再强制置顶。"
$lbl.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 12)
$lbl.AutoSize = $false
$lbl.Dock = "Fill"
$lbl.TextAlign = "MiddleCenter"
$f.Controls.Add($lbl)
$f.Show()
for ($i = 0; $i -lt 12; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
[void][WinUtil2]::SetWindowPos($f.Handle, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010)
for ($i = 0; $i -lt 12; $i++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }

$screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($screen.Left, $screen.Top, 0, 0, (New-Object System.Drawing.Size($screen.Width, $screen.Height)))
$crop = New-Object System.Drawing.Rectangle ($r.L - $Pad), ($r.T - $Pad), (($r.R - $r.L) + 2 * $Pad), (($r.B - $r.T) + 2 * $Pad)
$crop.Intersect((New-Object System.Drawing.Rectangle 0, 0, $screen.Width, $screen.Height))
$out2 = [System.Drawing.Bitmap]::new($bmp.Clone($crop, $bmp.PixelFormat))
$out2.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $out2.Dispose()
Write-Output ("saved {0} crop={1},{2},{3},{4}" -f $Out, $crop.X, $crop.Y, $crop.Width, $crop.Height)

$f.Close()
Start-Sleep -Milliseconds 400
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
