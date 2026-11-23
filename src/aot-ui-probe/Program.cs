using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

internal static class Program
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public int left, top, right, bottom;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern IntPtr GetDCEx(IntPtr hWnd, IntPtr hrgnClip, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    private const uint WM_PAINT = 0x000F;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_CLOSE = 0x0010;

    private static WndProcDelegate? _wndProc;
    private static int _frame;

    private static void Paint(IntPtr hWnd)
    {
        RECT rc;
        GetClientRect(hWnd, out rc);
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bmp = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr old = SelectObject(memDc, bmp);
        try
        {
            using var g = Graphics.FromHdc(memDc);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(255, 17, 24, 39));

            using (var path = Rounded(new RectangleF(1, 1, w - 2, h - 2), 16))
            using (var brush = new LinearGradientBrush(new RectangleF(0, 0, w, h),
                Color.FromArgb(255, 30, 41, 59), Color.FromArgb(255, 15, 23, 42), 90f))
            {
                g.FillPath(brush, path);
                using var pen = new Pen(Color.FromArgb(60, 52, 211, 153), 1f);
                g.DrawPath(pen, path);
            }

            using (var font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.FromArgb(255, 52, 211, 153)))
            {
                g.DrawString("32.2W", font, brush, new PointF(20, 16));
            }
            using (var font = new Font("Microsoft YaHei UI", 9f))
            using (var brush = new SolidBrush(Color.FromArgb(255, 148, 163, 184)))
            {
                g.DrawString("AOT 手绘 UI 已验证 " + _frame, font, brush, new PointF(20, 50));
            }

            using (var bar = new SolidBrush(Color.FromArgb(255, 30, 41, 59)))
                g.FillRectangle(bar, 20, 78, w - 40, 8);
            using (var bar = new SolidBrush(Color.FromArgb(255, 52, 211, 153)))
                g.FillRectangle(bar, 20, 78, (int)((w - 40) * 0.72), 8);

            _frame++;
        }
        finally
        {
            SelectObject(memDc, old);
            BitBlt(screenDc, 0, 0, w, h, memDc, 0, 0, 0x00CC0020);
            DeleteObject(bmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void SelfRenderPng(int w, int h)
    {
        try
        {
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.FromArgb(255, 17, 24, 39));
                using (var path = Rounded(new RectangleF(1, 1, w - 2, h - 2), 16))
                using (var brush = new LinearGradientBrush(new RectangleF(0, 0, w, h),
                    Color.FromArgb(255, 30, 41, 59), Color.FromArgb(255, 15, 23, 42), 90f))
                {
                    g.FillPath(brush, path);
                    using var pen = new Pen(Color.FromArgb(60, 52, 211, 153), 1f);
                    g.DrawPath(pen, path);
                }
                using (var font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.FromArgb(255, 52, 211, 153)))
                    g.DrawString("32.2W", font, brush, new PointF(20, 16));
                using (var font = new Font("Microsoft YaHei UI", 8.5f))
                using (var brush = new SolidBrush(Color.FromArgb(255, 148, 163, 184)))
                    g.DrawString("NativeAOT + GDI+ 渲染验证", font, brush, new PointF(20, 52));
                using (var bar = new SolidBrush(Color.FromArgb(255, 51, 65, 85)))
                    g.FillRectangle(bar, 20, 82, w - 40, 6);
                using (var bar = new SolidBrush(Color.FromArgb(255, 52, 211, 153)))
                    g.FillRectangle(bar, 20, 82, (int)((w - 40) * 0.72), 6);
            }
            bmp.Save(@"D:\dsh projects\cuktech-ultra-tray\docs\tmp-aot-ui3.png",
                System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("png saved");
        }
        catch (Exception ex)
        {
            Console.WriteLine("png fail: " + ex.Message);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
            {
                IntPtr hdc = BeginPaint(hWnd, out var ps);
                EndPaint(hWnd, ref ps);
                Paint(hWnd);
                return IntPtr.Zero;
            }
            case WM_TIMER:
                Paint(hWnd);
                return IntPtr.Zero;
            case WM_CLOSE:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void Main()
    {
        _wndProc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            lpszClassName = "AotUiProbeWnd",
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            Console.WriteLine("RegisterClassEx failed " + Marshal.GetLastWin32Error());
            return;
        }
        IntPtr hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, "AotUiProbeWnd", "aot probe", WS_POPUP,
            80, 80, 320, 130, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("CreateWindowEx failed " + Marshal.GetLastWin32Error());
            return;
        }
        ShowWindow(hwnd, 5);
        Paint(hwnd);
        SelfRenderPng(320, 130);
        var closer = new Thread(() =>
        {
            Thread.Sleep(2200);
            PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        });
        closer.IsBackground = true;
        closer.Start();
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
        Console.WriteLine("aot ui probe ok");
    }
}
