param([int]$ProcId, [string]$Out)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class WinUtil
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public struct RECT { public int L; public int T; public int R; public int B; }

    public static IntPtr FindPanel(uint pid, int minArea)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint wp;
            GetWindowThreadProcessId(h, out wp);
            if (wp != pid) return true;
            if (!IsWindowVisible(h)) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            int area = (r.R - r.L) * (r.B - r.T);
            if (area < minArea) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string RectOf(IntPtr h)
    {
        RECT r;
        GetWindowRect(h, out r);
        return string.Format("{0},{1},{2},{3}", r.L, r.T, r.R, r.B);
    }

    public static void BringFront(IntPtr h)
    {
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    }
}

'@
[void][WinUtil]::SetProcessDPIAware()
$h = [WinUtil]::FindPanel([uint32]$ProcId, 40000)
Write-Output ("hwnd={0} rect={1}" -f $h, [WinUtil]::RectOf($h))
if ($h -ne [IntPtr]::Zero) { [WinUtil]::BringFront($h) }
Start-Sleep -Milliseconds 900
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap ($bounds.Width), ($bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, (New-Object System.Drawing.Size($bounds.Width, $bounds.Height)))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out"
