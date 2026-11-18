using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>
/// 任务栏里那一小块功率文字。
///
/// 它是任务栏的子窗口（SetParent 到 Shell_TrayWnd），不是独立窗口、更不置顶：
/// 任务栏移到哪它就跟到哪，别的窗口盖住任务栏时它跟着被盖住，Alt+Tab 里也没有它。
///
/// 显示方式：窗口是 WS_EX_LAYERED，每帧把一张 32 位 ARGB 位图交给 UpdateLayeredWindow，
/// 背景 alpha 全 0 —— 任务栏上只剩一行抗锯齿的数字，没有底色方块，也不会有残影/重影
/// （用 GDI 直接往分层窗口上画、靠色键抠透明，会在 DWM 合成层留下上一帧的叠影，
///  第 12 轮就是踩了这个坑，见 docs/taskbar-component.md 第 12 节）。
///
/// 位置：默认摆在开始按钮左边；用鼠标按住数字就能拖到任务栏任意位置，位置记进 settings.json
/// （TaskbarLeftDip）。托盘菜单里有「重置任务栏数字位置」回到自动摆位。
/// 做法参考 AF-Media-Bar / TrafficMonitor（见 docs/taskbar-component.md）。
/// </summary>
internal sealed class TaskbarWidget : Form
{
    /// <summary>小窗宽度（dip）。够放 "120W" 再留点呼吸。</summary>
    private const int DesignWidthDip = 84;

    /// <summary>自动摆位时与开始按钮 / 任务栏边缘之间的间距（dip）。</summary>
    private const int GapDip = 8;

    /// <summary>字号（dip 意义上的 pt，会再乘以系统缩放）。</summary>
    private const double FontPt = 11.0;

    /// <summary>多久检查一次任务栏（自愈 + 跟随位置）。</summary>
    private const int TickMs = 500;

    /// <summary>拖动判定阈值（像素），小于它就算单击。</summary>
    private const int DragThreshold = 4;

    private readonly Action _onOpen;
    private readonly Settings _settings;
    private readonly System.Windows.Forms.Timer _timer;

    private Snapshot _snap = new();
    private IntPtr _parent = IntPtr.Zero;
    private int _lastDockError;
    private int _failStreak;
    private DateTime _nextRetry = DateTime.MinValue;

    private double _scale = 1.0;

    private bool _dragging;
    private bool _dragMoved;
    private int _dragCursorX;
    private int _dragOriginX;

    private Font? _font;

    internal TaskbarWidget(Settings settings, Action onOpen)
    {
        _settings = settings;
        _onOpen = onOpen;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;          // 不进任务栏、不进 Alt+Tab
        TopMost = false;                // 本程序任何地方都不许置顶
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Text = "CuktechWatts";          // 验证脚本按这个标题找它
        Cursor = Cursors.Hand;

        _ = Handle;                     // 先把句柄建出来，才能改样式、换父
        bool ok = DockToTaskbar();
        LogSink.Write("[taskbar] dock ok=" + ok + " parent=0x" + _parent.ToInt64().ToString("X") +
                      (ok ? "" : " err=" + _lastDockError));

        _timer = new System.Windows.Forms.Timer { Interval = TickMs };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>带 NOACTIVATE + TOOLWINDOW：点它不抢焦点，任务栏里也不会多出一个按钮。
    /// LAYERED 是 ULW 绘制的前提。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= TaskbarNative.WS_EX_NOACTIVATE | TaskbarNative.WS_EX_TOOLWINDOW
                          | TaskbarNative.WS_EX_LAYERED;
            return cp;
        }
    }

    /// <summary>喂一帧数据过来。只在显示内容真的变了才重画，省点 CPU。</summary>
    internal void Apply(Snapshot snap)
    {
        string before = Text2(_snap);
        _snap = snap;
        if (Text2(_snap) != before) Redraw();
    }

    /// <summary>回到自动摆位（开始按钮左边）。</summary>
    internal void ResetPosition()
    {
        _settings.TaskbarLeftDip = -1;
        _settings.Save();
        Reposition(true);
        LogSink.Write("[taskbar] 位置重置为自动");
    }

    /// <summary>拆掉：先解除父子关系并恢复成普通窗口，再释放。</summary>
    internal void Stop()
    {
        try
        {
            _timer.Stop();
            _timer.Dispose();
        }
        catch { }

        try
        {
            if (IsHandleCreated)
            {
                TaskbarNative.SetParent(Handle, IntPtr.Zero);
                int style = TaskbarNative.GetWindowLong(Handle, TaskbarNative.GWL_STYLE);
                TaskbarNative.SetWindowLong(Handle, TaskbarNative.GWL_STYLE,
                    (style & ~TaskbarNative.WS_CHILD) | TaskbarNative.WS_POPUP);
                TaskbarNative.ShowWindow(Handle, TaskbarNative.SW_HIDE);
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("[taskbar] undock: " + ex.Message);
        }

        _parent = IntPtr.Zero;
        try { Close(); } catch { }
    }

    /// <summary>ULW 窗口的内容由 Redraw 提交，WM_PAINT 不再负责绘制。</summary>
    protected override void OnPaint(PaintEventArgs e) { }

    // ---- 鼠标：拖到哪算哪，单击打开面板 -------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragMoved = false;
        _dragging = true;
        _dragCursorX = Cursor.Position.X;
        _dragOriginX = CurrentX();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.Button != MouseButtons.Left) return;
        int dx = Cursor.Position.X - _dragCursorX;
        if (!_dragMoved && Math.Abs(dx) < DragThreshold) return;
        _dragMoved = true;
        Cursor = Cursors.SizeWE;
        DragTo(_dragOriginX + dx);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Cursor = Cursors.Hand;
        if (!_dragging) return;
        _dragging = false;

        if (_dragMoved)
        {
            SaveManualPosition(CurrentX());
        }
        else if (e.Button == MouseButtons.Left)
        {
            _onOpen();
        }
    }

    /// <summary>窗口左边在任务栏客户区里的 x。</summary>
    private int CurrentX()
    {
        if (_parent == IntPtr.Zero || !TaskbarNative.IsWindow(_parent)) return 0;
        if (!TaskbarNative.GetWindowRect(_parent, out var tb)) return 0;
        if (!TaskbarNative.GetWindowRect(Handle, out var me)) return 0;
        return me.Left - tb.Left;
    }

    /// <summary>拖动中：把窗口挪到 x（自动夹在任务栏范围内）。</summary>
    private void DragTo(int x)
    {
        if (_parent == IntPtr.Zero || !TaskbarNative.IsWindow(_parent)) return;
        if (!TaskbarNative.GetWindowRect(_parent, out var tb)) return;
        if (!TaskbarNative.GetWindowRect(Handle, out var me)) return;

        int tbW = tb.Right - tb.Left;
        int w = me.Right - me.Left;
        int h = me.Bottom - me.Top;
        int clamped = Math.Max(0, Math.Min(x, tbW - w));
        if (me.Left - tb.Left == clamped) return;

        TaskbarNative.MoveWindow(Handle, clamped, 0, w, h, false);
        Redraw();
    }

    private void SaveManualPosition(int x)
    {
        double dip = _scale > 0 ? x / _scale : x;
        _settings.TaskbarLeftDip = Math.Round(dip, 2);
        _settings.Save();
        LogSink.Write("[taskbar] 手动位置 x=" + x + " dip=" + _settings.TaskbarLeftDip.ToString("F2"));
    }

    // ---- 绘制 ---------------------------------------------------------------------------

    private string Text2(Snapshot snap)
        => !snap.Connected ? "--" : WattStyle.Format(snap.TotalWatts) + "W";

    private Font Font2()
    {
        float pt = (float)(FontPt * _scale);
        if (_font is not null && Math.Abs(_font.SizeInPoints - pt) < 0.05f) return _font;
        _font?.Dispose();
        _font = new Font("Segoe UI", pt, FontStyle.Bold, GraphicsUnit.Point);
        return _font;
    }

    /// <summary>把当前内容整张重画给系统（不是增量绘制，所以不会留下上一帧的残影）。</summary>
    private void Redraw()
    {
        if (!IsHandleCreated || !TaskbarNative.IsWindow(Handle)) return;
        if (!TaskbarNative.GetWindowRect(Handle, out var me)) return;

        int w = me.Right - me.Left;
        int h = me.Bottom - me.Top;
        if (w <= 0 || h <= 0) return;

        int parentX = 0, parentY = 0;
        var parent = TaskbarNative.GetParent(Handle);
        if (parent != IntPtr.Zero && TaskbarNative.GetWindowRect(parent, out var pr))
        {
            parentX = pr.Left;
            parentY = pr.Top;
        }

        string text = Text2(_snap);
        Color color = _snap.Connected ? WattStyle.Total(_snap.TotalWatts) : WattStyle.Dim;
        var font = Font2();

        // 子窗口的 UpdateLayeredWindow 坐标是"父客户区坐标"，不是屏幕坐标
        TaskbarNative.PaintLayered(Handle, w, h, me.Left - parentX, me.Top - parentY, (g, cw, ch) =>
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(color);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            g.DrawString(text, font, brush, new RectangleF(0, 0, cw, ch), fmt);
        });
    }

    // ---- 贴任务栏 -----------------------------------------------------------------------

    /// <summary>把窗口挂成任务栏的子窗口。失败（被第三方美化工具挡住、或进程权限不够）就返回 false。</summary>
    private bool DockToTaskbar()
    {
        var tray = TaskbarNative.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        if (TaskbarNative.GetParent(Handle) != tray)
        {
            int style = TaskbarNative.GetWindowLong(Handle, TaskbarNative.GWL_STYLE);
            TaskbarNative.SetWindowLong(Handle, TaskbarNative.GWL_STYLE,
                (style & ~TaskbarNative.WS_POPUP) | TaskbarNative.WS_CHILD);

            IntPtr before = TaskbarNative.GetParent(Handle);
            IntPtr ret = TaskbarNative.SetParent(Handle, tray);
            int err = Marshal.GetLastWin32Error();
            TaskbarNative.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                TaskbarNative.SWP_NOMOVE | TaskbarNative.SWP_NOSIZE |
                TaskbarNative.SWP_NOZORDER | TaskbarNative.SWP_NOACTIVATE |
                TaskbarNative.SWP_FRAMECHANGED);
            _lastDockError = err;
            LogSink.Write("[taskbar] setparent tray=0x" + tray.ToInt64().ToString("X") +
                          " before=0x" + before.ToInt64().ToString("X") +
                          " ret=0x" + ret.ToInt64().ToString("X") + " err=" + err +
                          " after=0x" + TaskbarNative.GetParent(Handle).ToInt64().ToString("X"));
        }

        if (TaskbarNative.GetParent(Handle) != tray)
        {
            _parent = IntPtr.Zero;
            return false;
        }

        _parent = tray;
        TaskbarNative.ShowWindow(Handle, TaskbarNative.SW_SHOWNA);
        return Reposition(true);
    }

    /// <summary>
    /// 算位置并摆好。用户拖过就按记住的位置放（TaskbarLeftDip >= 0），
    /// 否则自动摆在开始按钮左边；任务栏宽度、缩放、开始按钮位置变了都靠这里纠正。
    /// </summary>
    private bool Reposition(bool force = false)
    {
        if (_parent == IntPtr.Zero || !TaskbarNative.IsWindow(_parent)) return false;
        if (!TaskbarNative.GetWindowRect(_parent, out var tb)) return false;

        int tbW = tb.Right - tb.Left;
        int tbH = tb.Bottom - tb.Top;
        if (tbW <= tbH) return false;   // 竖向任务栏：第一版不处理

        _scale = Math.Max(1.0, TaskbarNative.GetDpiForWindow(_parent) / 96.0);
        int w = (int)Math.Round(DesignWidthDip * _scale);
        int gap = (int)Math.Round(GapDip * _scale);

        int x = _settings.TaskbarLeftDip >= 0
            ? (int)Math.Round(_settings.TaskbarLeftDip * _scale)
            : AutoX(tb, w, gap);

        x = Math.Max(0, Math.Min(x, Math.Max(0, tbW - w)));

        if (!force
            && TaskbarNative.GetWindowRect(Handle, out var cur)
            && cur.Left == tb.Left + x && cur.Top == tb.Top
            && cur.Right - cur.Left == w && cur.Bottom - cur.Top == tbH)
        {
            return true;                // 位置尺寸都对，不用动
        }

        TaskbarNative.MoveWindow(Handle, x, 0, w, tbH, false);
        Redraw();
        return true;
    }

    /// <summary>自动位置：开始按钮左边；左边放不下就它右边；连开始按钮都找不到就贴任务栏最左。</summary>
    private int AutoX(TaskbarNative.RECT tb, int w, int gap)
    {
        int x = gap;
        var start = TaskbarNative.FindWindowEx(_parent, IntPtr.Zero, "Start", null);
        if (start != IntPtr.Zero
            && TaskbarNative.GetWindowRect(start, out var sr)
            && sr.Left >= tb.Left && sr.Left < tb.Right)
        {
            int leftOfStart = sr.Left - tb.Left - gap - w;
            int tbW = tb.Right - tb.Left;
            x = leftOfStart >= gap ? leftOfStart : Math.Min(tbW - w, sr.Right - tb.Left + gap);
        }
        return x;
    }

    /// <summary>每 500ms：任务栏还在不在、我们还算不算它的孩子、位置对不对。</summary>
    private void Tick()
    {
        try
        {
            if (_parent == IntPtr.Zero
                || !TaskbarNative.IsWindow(_parent)
                || TaskbarNative.GetParent(Handle) != _parent)
            {
                // Explorer 重启过、或被人摘下来了：重新挂。挂不上就退避重试，别每 500ms 刷日志
                _parent = IntPtr.Zero;
                if (DateTime.UtcNow < _nextRetry) return;

                if (DockToTaskbar())
                {
                    if (_failStreak > 0)
                        LogSink.Write("[taskbar] 重新挂上了（之前连续失败 " + _failStreak + " 次）");
                    _failStreak = 0;
                    _nextRetry = DateTime.MinValue;
                }
                else
                {
                    _failStreak++;
                    int delayMs = Math.Min(30_000, 500 * (1 << Math.Min(_failStreak, 6)));
                    _nextRetry = DateTime.UtcNow.AddMilliseconds(delayMs);
                    if (_failStreak == 1)
                        LogSink.Write(_lastDockError == 5
                            ? "[taskbar] 挂不上任务栏：系统拒绝了 SetParent（错误 5 / 拒绝访问）。多半是启动本程序的进程权限比 explorer 低，正常双击启动就好了。"
                            : "[taskbar] 挂不上任务栏（错误 " + _lastDockError + "），" + delayMs + "ms 后再试");
                }

                return;
            }

            if (!_dragging) Reposition();
        }
        catch (Exception ex)
        {
            LogSink.Write("[taskbar] tick: " + ex.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _timer.Stop(); } catch { }
            _font?.Dispose();
            _font = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>贴任务栏要用到的 Win32 调用，签名照 MSDN。</summary>
internal static class TaskbarNative
{
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    internal const int WS_CHILD = 0x40000000;
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_LAYERED = 0x00080000;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNA = 8;

    /// <summary>UpdateLayeredWindow 的 dwFlags：按 alpha 通道混合。</summary>
    internal const uint ULW_ALPHA = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
        out IntPtr bits, IntPtr hSection, uint offset);

    /// <summary>
    /// 画一张 <paramref name="w"/>×<paramref name="h"/> 的 32 位 ARGB 位图交给窗口，
    /// 背景 alpha=0 的像素完全透明。坐标 <paramref name="dstX"/>/<paramref name="dstY"/> 是
    /// 父窗口客户区坐标（子窗口用屏幕坐标是画不出来的）。
    /// </summary>
    internal static bool PaintLayered(IntPtr hwnd, int w, int h, int dstX, int dstY, Action<Graphics, int, int> draw)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        IntPtr memDc = CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h;          // 负数 = 自上而下
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;      // BI_RGB

        IntPtr hDib = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (hDib == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        IntPtr old = SelectObject(memDc, hDib);
        bool ok;
        try
        {
            // Bitmap 直接包住 DIB 的内存，GDI+ 画上去就带 alpha，不用再过一道 GetHbitmap
            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppArgb, bits))
            using (var g = Graphics.FromImage(bmp))
            {
                draw(g, w, h);

                var dst = new POINT { X = dstX, Y = dstY };
                var size = new SIZE { cx = w, cy = h };
                var src = new POINT { X = 0, Y = 0 };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = 0,                  // AC_SRC_OVER
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = 1,              // AC_SRC_ALPHA
                };
                ok = UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hDib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return ok;
    }
}
