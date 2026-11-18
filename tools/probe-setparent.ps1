
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$cs = @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class P {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetParent(IntPtr c, IntPtr p);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll", SetLastError=true)] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll", SetLastError=true)] public static extern int SetWindowLong(IntPtr h, int i, int v);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll", SetLastError=true)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("kernel32.dll")] public static extern void SetLastError(int e);
  [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(int acc, bool inherit, uint pid);
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr proc, int acc, out IntPtr tok);
  [DllImport("advapi32.dll", SetLastError=true)] public static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr buf, int len, out int need);
  [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr p);

  public static IntPtr Tray() { return FindWindow("Shell_TrayWnd", null); }

  public static uint PidOf(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }

  public static string IL(IntPtr proc) {
    IntPtr tok;
    if (!OpenProcessToken(proc, 0x0008, out tok)) return "openToken err=" + Marshal.GetLastWin32Error();
    int need;
    GetTokenInformation(tok, 25, IntPtr.Zero, 0, out need);
    IntPtr buf = Marshal.AllocHGlobal(need);
    try {
      if (!GetTokenInformation(tok, 25, buf, need, out need)) return "tokInfo err=" + Marshal.GetLastWin32Error();
      IntPtr sid = Marshal.ReadIntPtr(buf);       // SID_AND_ATTRIBUTES.Sid
      byte count = Marshal.ReadByte(sid, 1);      // SubAuthorityCount
      int last = Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
      string name = last >= 0x4000 ? "High" : last >= 0x3000 ? "Medium" : last >= 0x2000 ? "Low" : "other";
      return name + "(0x" + last.ToString("X") + ")";
    } finally { Marshal.FreeHGlobal(buf); }
  }

  public static string ILOfPid(uint pid) {
    IntPtr h = OpenProcess(0x0400, false, pid);
    if (h == IntPtr.Zero) return "openProcess err=" + Marshal.GetLastWin32Error();
    try { return IL(h); } finally { }
  }
}

public class M2 : Form {
  public M2(bool layered, bool child) {
    _layered = layered; _child = child;
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = false;
    StartPosition = FormStartPosition.Manual;
    AutoScaleMode = AutoScaleMode.None;
    Text = "probe" + (layered ? "L" : "") + (child ? "C" : "");
    Width = 90; Height = 40; Left = 700; Top = 1300;
  }
  private bool _layered, _child;
  protected override CreateParams CreateParams {
    get {
      var cp = base.CreateParams;
      cp.ExStyle |= 0x08000000 | 0x80;
      if (_layered) cp.ExStyle |= 0x00080000;
      return cp;
    }
  }
  public void MarkChild() {
    int s = P.GetWindowLong(Handle, -16);
    P.SetWindowLong(Handle, -16, (s & ~unchecked((int)0x80000000)) | 0x40000000);
  }
}
'@
Add-Type -TypeDefinition $cs -ReferencedAssemblies System.Windows.Forms, System.Drawing

"self IL: " + [P]::IL([P]::GetCurrentProcess())
$tray = [P]::Tray()
$trayPid = [P]::PidOf($tray)
"tray=0x{0:X} pid={1} IL={2}" -f [long]$tray, $trayPid, [P]::ILOfPid($trayPid)
$ex = Get-Process explorer -ErrorAction SilentlyContinue | Select-Object -First 1
"explorer pid=$($ex.Id) IL=" + [P]::ILOfPid([uint32]$ex.Id)

# A) 不 Show，先建句柄再改样式再 SetParent（模拟主程序构造里的时序）
"--- variant A: handle only, no Show ---"
$fA = New-Object M2($true, $true)
$hA = $fA.Handle
"createHandle=0x{0:X} visible={1}" -f [long]$hA, $fA.Visible
$fA.MarkChild()
[P]::SetLastError(0)
$retA = [P]::SetParent($hA, $tray)
$errA = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
"A ret=0x{0:X} err={1} parent=0x{2:X} matchTray={3}" -f [long]$retA, $errA, [long]([P]::GetParent($hA)), ([P]::GetParent($hA) -eq $tray)
if ([P]::GetParent($hA) -ne $tray) {
  $fA.Show()
  Start-Sleep -Milliseconds 150
  [P]::SetLastError(0)
  $retA2 = [P]::SetParent($hA, $tray)
  $errA2 = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
  "A-retry-after-show ret=0x{0:X} err={1} parent=0x{2:X}" -f [long]$retA2, $errA2, [long]([P]::GetParent($hA))
}

foreach ($cfg in @(@{n='plain';  layered=$false; child=$false},
                   @{n='layered'; layered=$true;  child=$false},
                   @{n='childstyle'; layered=$false; child=$true},
                   @{n='layered+child'; layered=$true; child=$true})) {
  $f = New-Object M2($cfg.layered, $cfg.child)
  $f.Show()
  $f.Refresh()
  Start-Sleep -Milliseconds 200
  if ($cfg.child) { $f.MarkChild() }
  [P]::SetLastError(0)
  $ret = [P]::SetParent($f.Handle, $tray)
  $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
  $par = [P]::GetParent($f.Handle)
  "cfg={0} ret=0x{1:X} err={2} parent=0x{3:X} matchTray={4}" -f $cfg.n, [long]$ret, $err, [long]$par, ($par -eq $tray)
  if ($par -ne $tray) { $f.Close() }
}
