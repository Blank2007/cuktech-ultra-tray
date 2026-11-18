Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W3 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static List<string> Widgets(IntPtr top) {
    var list = new List<string>();
    EnumChildWindows(top, delegate(IntPtr h, IntPtr l) {
      var cn = new StringBuilder(256); GetClassName(h, cn, 256);
      if (cn.ToString().StartsWith("WindowsForms10")) {
        RECT r; GetWindowRect(h, out r);
        uint pid; GetWindowThreadProcessId(h, out pid);
        list.Add(string.Format("hwnd=0x{0:X} pid={1} class={2} rect={3},{4}-{5},{6}", h.ToInt64(), pid, cn, r.Left, r.Top, r.Right, r.Bottom));
      }
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@
$tray = [W3]::FindWindow("Shell_TrayWnd", $null)
[W3]::Widgets($tray) | ForEach-Object { Write-Host $_ }
