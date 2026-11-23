# Prototype: taskbar child widget painted with UpdateLayeredWindow (alpha), no color key.
# Run in background; writes hwnd to .tools\ulw-hwnd.txt
param([int]$Seconds = 90)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class ULW {
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
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", SetLastError=true)] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
  [StructLayout(LayoutKind.Sequential, Pack=1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
  [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER {
    public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
  }
  [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

  public static IntPtr FindChild(IntPtr parent, string cls) { return FindWindowEx(parent, IntPtr.Zero, cls, null); }

  public static bool Paint(IntPtr hwnd, int w, int h, string text, Color fg, float fontPt, int mode) {
    IntPtr screenDc = GetDC(IntPtr.Zero);
    IntPtr memDc = CreateCompatibleDC(screenDc);
    BITMAPINFO bmi = new BITMAPINFO();
    bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
    bmi.bmiHeader.biWidth = w;
    bmi.bmiHeader.biHeight = -h;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    bmi.bmiHeader.biCompression = 0;
    IntPtr bits;
    IntPtr hDib = CreateDIBSection(screenDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
    if (hDib == IntPtr.Zero) { DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc); return false; }
    IntPtr old = SelectObject(memDc, hDib);
    using (Bitmap bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppArgb, bits))
    using (Graphics g = Graphics.FromImage(bmp)) {
      g.Clear(Color.Transparent);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.TextRenderingHint = mode == 0 ? TextRenderingHint.AntiAlias : TextRenderingHint.ClearTypeGridFit;
      using (Font f = new Font("Segoe UI", fontPt, FontStyle.Bold, GraphicsUnit.Point))
      using (SolidBrush b = new SolidBrush(fg)) {
        StringFormat fmt = new StringFormat();
        fmt.Alignment = StringAlignment.Center;
        fmt.LineAlignment = StringAlignment.Center;
        fmt.FormatFlags = StringFormatFlags.NoWrap;
        g.DrawString(text, f, b, new RectangleF(0, 0, w, h), fmt);
        fmt.Dispose();
      }
    }
    RECT wr; GetWindowRect(hwnd, out wr);
    RECT pr; pr.Left = 0; pr.Top = 0; pr.Right = 0; pr.Bottom = 0;
    IntPtr par = GetParent(hwnd);
    if (par != IntPtr.Zero) GetWindowRect(par, out pr);
    POINT dst; dst.X = wr.Left - pr.Left; dst.Y = wr.Top - pr.Top;
    SIZE size; size.cx = w; size.cy = h;
    POINT src; src.X = 0; src.Y = 0;
    BLENDFUNCTION blend; blend.BlendOp = 0; blend.BlendFlags = 0; blend.SourceConstantAlpha = 255; blend.AlphaFormat = 1;
    bool ok = UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
    bool ok2 = true;
    SelectObject(memDc, old);
    DeleteObject(hDib); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
    return ok && ok2;
  }
}

public class Mini3 : Form {
  public Mini3() {
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    AutoScaleMode = AutoScaleMode.None;
  }
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000; return cp; }
  }
  protected override void OnPaint(PaintEventArgs e) { }
}
'@ -ReferencedAssemblies System.Windows.Forms, System.Drawing

[void][ULW]::SetProcessDPIAware()
$tray = [ULW]::FindWindow("Shell_TrayWnd", $null)
if ($tray -eq [IntPtr]::Zero) { Write-Host "no tray"; exit 1 }
$tr = New-Object ULW+RECT
[void][ULW]::GetWindowRect($tray, [ref]$tr)
$tbH = $tr.Bottom - $tr.Top
$dpi = [ULW]::GetDpiForWindow($tray); if ($dpi -le 0) { $dpi = 96 }
$scale = $dpi / 96.0
$startW = [ULW]::FindChild($tray, "Start")
$sr = New-Object ULW+RECT
if ($startW -ne [IntPtr]::Zero) { [void][ULW]::GetWindowRect($startW, [ref]$sr) }
$w = [int](84 * $scale)
$gap = [int](8 * $scale)
$x = $gap
if ($startW -ne [IntPtr]::Zero -and $sr.Left -ge $tr.Left) {
  $leftOfStart = ($sr.Left - $tr.Left) - $gap - $w
  if ($leftOfStart -ge $gap) { $x = $leftOfStart } else { $x = ($sr.Right - $tr.Left) + $gap }
}
$tbW = $tr.Right - $tr.Left
if ($x -gt ($tbW - $w)) { $x = $tbW - $w }

$f = New-Object Mini3
$f.Width = $w; $f.Height = $tbH
$h = $f.Handle
$st = [ULW]::GetWindowLong($h, -16)
[void][ULW]::SetWindowLong($h, -16, (($st -band 0x3FFFFFFF) -bor 0x40000000))
[void][ULW]::SetParent($h, $tray)
[void][ULW]::ShowWindow($h, 4)
[void][ULW]::MoveWindow($h, $x, 0, $w, $tbH, $true)
[void][ULW]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0010)
Start-Sleep -Milliseconds 200
$green = [System.Drawing.Color]::FromArgb(52, 211, 153)
$painted = [ULW]::Paint($h, $w, $tbH, "6.0W", $green, 11.0, 0)
Write-Host ("tray=0x{0:X} tbH={1} scale={2} w={3} x={4} parent=0x{5:X} painted={6}" -f $tray.ToInt64(), $tbH, $scale, $w, $x, ([ULW]::GetParent($h)).ToInt64(), $painted)
Set-Content -Path "D:\dsh projects\.tools\ulw-hwnd.txt" -Value ("0x{0:X}|{1}|{2}" -f $h.ToInt64(), $w, $tbH) -Encoding ASCII
$end = (Get-Date).AddSeconds($Seconds)
$n = 0
while ((Get-Date) -lt $end) {
  [System.Windows.Forms.Application]::DoEvents()
  $n++
  if ($n % 10 -eq 0) { [void][ULW]::Paint($h, $w, $tbH, "6.0W", $green, 11.0, 0) }
  Start-Sleep -Milliseconds 100
  if (-not $f.IsHandleCreated) { break }
}
Write-Host "ulw demo finished"