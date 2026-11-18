Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class TB {
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
  [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr c, IntPtr p);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
  public struct RECT { public int Left, Top, Right, Bottom; }
}

public class Mini : Form {
  public Mini() {
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
  }
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080; return cp; }
  }
  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Color.FromArgb(28, 28, 30));
    using (var f = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point))
    using (var b = new SolidBrush(Color.FromArgb(52, 211, 153))) {
      var s = "32.2W";
      var fmt = new StringFormat();
      fmt.Alignment = StringAlignment.Center;
      fmt.LineAlignment = StringAlignment.Center;
      g.DrawString(s, f, b, new RectangleF(0, 0, Width, Height), fmt);
      fmt.Dispose();
    }
  }
}
'@ -ReferencedAssemblies System.Windows.Forms, System.Drawing

$tray = [TB]::FindWindow("Shell_TrayWnd", $null)
if ($tray -eq [IntPtr]::Zero) { Write-Host "no tray"; exit 1 }
$tr = New-Object TB+RECT
[void][TB]::GetWindowRect($tray, [ref]$tr)
$tbW = $tr.Right - $tr.Left
$tbH = $tr.Bottom - $tr.Top
$dpi = [TB]::GetDpiForWindow($tray)
$scale = $dpi / 96.0
$notify = [TB]::FindWindowEx($tray, [IntPtr]::Zero, "TrayNotifyWnd", $null)
$nr = New-Object TB+RECT
if ($notify -ne [IntPtr]::Zero) { [void][TB]::GetWindowRect($notify, [ref]$nr) } else { $nr.Left = $tr.Right - 200 }

$w = [int](76 * $scale)
$x = ($nr.Left - $tr.Left) - [int](8 * $scale) - $w

$f = New-Object Mini
$f.Width = $w
$f.Height = $tbH
$h = $f.Handle
$st = [TB]::GetWindowLong($h, -16)
[void][TB]::SetWindowLong($h, -16, (($st -band 0x3FFFFFFF) -bor 0x40000000))
$old = [TB]::SetParent($h, $tray)
$err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
[void][TB]::ShowWindow($h, 4)
[void][TB]::MoveWindow($h, $x, 0, $w, $tbH, $true)
Write-Host ("tray=0x{0:X} tbW={1} tbH={2} dpi={3} scale={4} notifyLeft={5} w={6} x={7} old=0x{8:X} err={9} parent=0x{10:X}" -f $tray.ToInt64(), $tbW, $tbH, $dpi, $scale, $nr.Left, $w, $x, $old.ToInt64(), $err, ([TB]::GetParent($h)).ToInt64())
$f.Invalidate()
$end = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt $end) {
  [System.Windows.Forms.Application]::DoEvents()
  Start-Sleep -Milliseconds 40
  if (-not $f.IsHandleCreated) { break }
}
