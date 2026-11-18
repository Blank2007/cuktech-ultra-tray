Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class E2 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static List<string> Dump(IntPtr top) {
    var list = new List<string>();
    EnumChildWindows(top, delegate(IntPtr h, IntPtr l) {
      var cn = new StringBuilder(256); GetClassName(h, cn, 256);
      var tt = new StringBuilder(256); GetWindowTextW(h, tt, 256);
      RECT r; GetWindowRect(h, out r);
      list.Add(string.Format("hwnd=0x{0:X} parent=0x{1:X} vis={2} class={3} title={4} rect={5},{6}-{7},{8}",
        h.ToInt64(), GetParent(h).ToInt64(), IsWindowVisible(h), cn, tt, r.Left, r.Top, r.Right, r.Bottom));
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@
$tray = [E2]::FindWindow("Shell_TrayWnd", $null)
Write-Host ("tray=0x{0:X}" -f $tray.ToInt64())
[E2]::Dump($tray) | ForEach-Object { Write-Host $_ }
Write-Host "--- direct children of tray ---"
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class E3 {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static List<string> Siblings(IntPtr p) {
    var list = new List<string>();
    IntPtr c = IntPtr.Zero;
    while (true) {
      c = FindWindowEx(p, c, null, null);
      if (c == IntPtr.Zero) break;
      var cn = new StringBuilder(256); GetClassName(c, cn, 256);
      RECT r; GetWindowRect(c, out r);
      list.Add(string.Format("hwnd=0x{0:X} vis={1} class={2} rect={3},{4}-{5},{6}", c.ToInt64(), IsWindowVisible(c), cn, r.Left, r.Top, r.Right, r.Bottom));
    }
    return list;
  }
}
"@
[E3]::Siblings($tray) | ForEach-Object { Write-Host $_ }
