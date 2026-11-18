param([int]$ForPid)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinList {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$found = New-Object System.Collections.ArrayList
$cb = [WinList+EnumProc]{ param($h, $l)
  $wp = 0
  [WinList]::GetWindowThreadProcessId($h, [ref]$wp) | Out-Null
  if ($wp -eq $ForPid -and [WinList]::IsWindowVisible($h)) {
    $r = New-Object WinList+RECT
    [WinList]::GetWindowRect($h, [ref]$r) | Out-Null
    $t = New-Object System.Text.StringBuilder 256
    [WinList]::GetWindowText($h, $t, 256) | Out-Null
    $c = New-Object System.Text.StringBuilder 256
    [WinList]::GetClassName($h, $c, 256) | Out-Null
    $null = $found.Add([pscustomobject]@{ H = $h; L = $r.Left; T = $r.Top; R = $r.Right; B = $r.Bottom; Title = $t.ToString(); Class = $c.ToString() })
  }
  return $true
}
[WinList]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
foreach ($w in $found) {
  Write-Output ("hwnd={0} rect={1},{2},{3},{4} size={5}x{6} class={7} title={8}" -f $w.H, $w.L, $w.T, $w.R, $w.B, ($w.R - $w.L), ($w.B - $w.T), $w.Class, $w.Title)
}
