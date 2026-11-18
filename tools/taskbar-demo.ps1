# Taskbar widget visual demo: number only, background sampled from the taskbar itself.
# Run: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\taskbar-demo.ps1
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class TB2 {
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
  [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr c, IntPtr p);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern uint GetPixel(IntPtr dc, int x, int y);
  public struct RECT { public int Left, Top, Right, Bottom; }

  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
  public static IntPtr FindChild(IntPtr parent, string cls) { return FindWindowEx(parent, IntPtr.Zero, cls, null); }

  public static Color? Sample(IntPtr hwnd) {
    RECT r;
    if (!GetWindowRect(hwnd, out r)) return null;
    IntPtr dc = GetDC(IntPtr.Zero);
    if (dc == IntPtr.Zero) return null;
    try {
      int w = r.Right - r.Left, h = r.Bottom - r.Top;
      int[] dxs = new int[] { -4, -10, w + 4, w + 10 };
      int[] dys = new int[] { h / 4, h / 2, 3 * h / 4 };
      var reds = new System.Collections.Generic.List<int>();
      var greens = new System.Collections.Generic.List<int>();
      var blues = new System.Collections.Generic.List<int>();
      foreach (int dx in dxs) {
        foreach (int dy in dys) {
          uint px = GetPixel(dc, r.Left + dx, r.Top + dy);
          if (px == 0xFFFFFFFFu) continue;
          reds.Add((int)(px & 0xFF));
          greens.Add((int)((px >> 8) & 0xFF));
          blues.Add((int)((px >> 16) & 0xFF));
        }
      }
      if (reds.Count == 0) return null;
      reds.Sort(); greens.Sort(); blues.Sort();
      int mid = reds.Count / 2;
      return Color.FromArgb(reds[mid], greens[mid], blues[mid]);
    } finally { ReleaseDC(IntPtr.Zero, dc); }
  }
}

public class Mini2 : Form {
  public Color Bg = Color.FromArgb(32, 32, 32);
  public Color Key = Color.FromArgb(255, 0, 255);
  public string Label = "32.2W";
  public Color Fg = Color.FromArgb(52, 211, 153);
  public float FontPt = 10.5F;
  public Mini2() {
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    AutoScaleMode = AutoScaleMode.None;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
  }
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000; return cp; }
  }
  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Key);
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
    using (var f = new Font("Segoe UI", FontPt, FontStyle.Bold, GraphicsUnit.Point))
    using (var b = new SolidBrush(Fg)) {
      var fmt = new StringFormat();
      fmt.Alignment = StringAlignment.Center;
      fmt.LineAlignment = StringAlignment.Center;
      fmt.FormatFlags = StringFormatFlags.NoWrap;
      g.DrawString(Label, f, b, new RectangleF(0, 0, Width, Height), fmt);
      fmt.Dispose();
    }
  }
}
'@ -ReferencedAssemblies System.Windows.Forms, System.Drawing

[void][TB2]::SetProcessDPIAware()
$tray = [TB2]::FindWindow("Shell_TrayWnd", $null)
if ($tray -eq [IntPtr]::Zero) { Write-Host "no tray"; exit 1 }
$tr = New-Object TB2+RECT
[void][TB2]::GetWindowRect($tray, [ref]$tr)
$tbH = $tr.Bottom - $tr.Top
$dpi = [TB2]::GetDpiForWindow($tray)
if ($dpi -le 0) { $dpi = 96 }
$scale = $dpi / 96.0
$startW = [TB2]::FindChild($tray, "Start")
$sr = New-Object TB2+RECT
if ($startW -ne [IntPtr]::Zero) { [void][TB2]::GetWindowRect($startW, [ref]$sr) }

$w = [int](76 * $scale)
$gap = [int](8 * $scale)
$x = $gap
if ($startW -ne [IntPtr]::Zero -and $sr.Left -ge $tr.Left) {
  $leftOfStart = ($sr.Left - $tr.Left) - $gap - $w
  if ($leftOfStart -ge $gap) { $x = $leftOfStart } else { $x = ($sr.Right - $tr.Left) + $gap }
}
$tbW = $tr.Right - $tr.Left
if ($x -gt ($tbW - $w)) { $x = $tbW - $w }
Write-Host ("start=0x{0:X} startRect={1},{2}-{3},{4} gap={5} x={6}" -f $startW.ToInt64(), $sr.Left, $sr.Top, $sr.Right, $sr.Bottom, $gap, $x)

$f = New-Object Mini2
$f.Width = $w
$f.Height = $tbH
$h = $f.Handle
$st = [TB2]::GetWindowLong($h, -16)
[void][TB2]::SetWindowLong($h, -16, (($st -band 0x3FFFFFFF) -bor 0x40000000))
[void][TB2]::SetParent($h, $tray)
$err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
[void][TB2]::SetLayeredWindowAttributes($h, 0x00FF00FF, 0, 1)
[void][TB2]::ShowWindow($h, 4)
[void][TB2]::MoveWindow($h, $x, 0, $w, $tbH, $true)
# bring it to the top of the taskbar child z-order so neighbouring buttons cannot cover it
[void][TB2]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010)
Start-Sleep -Milliseconds 250
$sampled = [TB2]::Sample($h)
if ($sampled -ne $null) { $f.Bg = $sampled; $f.BackColor = $sampled; $f.Invalidate() }
Write-Host ("tray=0x{0:X} tbH={1} dpi={2} scale={3} w={4} x={5} err={6} parent=0x{7:X} sampled={8}" -f $tray.ToInt64(), $tbH, $dpi, $scale, $w, $x, $err, ([TB2]::GetParent($h)).ToInt64(), $sampled)
$hwndFile = "D:\dsh projects\.tools\widget-hwnd.txt"
Set-Content -Path $hwndFile -Value ("0x{0:X}|{1}|{2}" -f $h.ToInt64(), $w, $tbH) -Encoding ASCII
$end = (Get-Date).AddSeconds(75)
while ((Get-Date) -lt $end) {
  [System.Windows.Forms.Application]::DoEvents()
  [void][TB2]::MoveWindow($h, $x, 0, $w, $tbH, $true)
  [void][TB2]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010)
  Start-Sleep -Milliseconds 40
  if (-not $f.IsHandleCreated) { break }
}
Write-Host "demo finished"