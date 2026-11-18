param([int]$ProcId)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class WinProbe
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

    public struct RECT { public int L; public int T; public int R; public int B; }

    public static string Describe(uint pid)
    {
        string found = "none";
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint wp;
            GetWindowThreadProcessId(h, out wp);
            if (wp != pid || !IsWindowVisible(h)) return true;
            RECT r;
            GetWindowRect(h, out r);
            int area = (r.R - r.L) * (r.B - r.T);
            if (area < 20000) return true;
            int ex = GetWindowLong(h, -20);
            bool topmost = (ex & 0x8) != 0;
            found = string.Format("hwnd={0} rect={1},{2},{3},{4} topmost={5} exstyle=0x{6:X}", h, r.L, r.T, r.R, r.B, topmost, ex);
            return false;
        }, IntPtr.Zero);
        return found;
    }
}

'@
Write-Output ([WinProbe]::Describe([uint32]$ProcId))
