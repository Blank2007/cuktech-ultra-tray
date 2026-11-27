using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>任务栏数字的摆位方式。0.5.4 起由菜单直接选，不再只能靠拖。</summary>
internal enum TaskbarPlace { Auto, Center, Right, Custom }

/// <summary>
/// 任务栏里那一小块功率文字（NativeAOT 版）。
///
/// 窗口是 WS_EX_LAYERED，每帧把一张 32 位 ARGB 位图交给 UpdateLayeredWindow，背景 alpha 全 0 ——
/// 任务栏上只剩一行抗锯齿的数字，没有底色方块，也不会留残影。窗口 IsParent 到 Shell_TrayWnd，
/// 任务栏移到哪它跟到哪。
///
/// 0.5.4（issue #7「无法调到右边显示」）：
///   1. 位置四档：auto（开始按钮左边）/ center（屏幕正中）/ right（靠托盘左边）/ custom（自定义左边距）；
///   2. 大小四档：小 / 中 / 大 / 自定义宽度，字号跟着宽度走；
///   3. 每次重排都把窗口提到兄弟窗口最前 —— 以前挂上去之后再没动过 z 序，
///      拖到托盘区会被 TrayNotifyWnd 整块盖住，看着就是「挪不过去」；
///   4. 捕获被抢走时（WM_CAPTURECHANGED）收尾，不再卡在「拖动中」.
/// </summary>
internal sealed class TaskbarWindow : WinBase
{
    internal const int BaseWidthDip = 84;
    internal const double BaseFontPt = 11.0;
    internal const int MinWidthDip = 56;
    internal const int MaxWidthDip = 160;
    private const int GapDip = 8;
    private const int TickMs = 500;
    private const int DragThreshold = 4;
    private static readonly UIntPtr TimerId = new(1);

    private readonly Settings _settings;
    private readonly Action _onOpen;

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

    internal TaskbarWindow(Settings settings, Action onOpen)
    {
        _settings = settings;
        _onOpen = onOpen;

        int w = (int)Math.Round(WidthDip * 1.25);
        CreateWin(Math.Max(60, w), 60,
            Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED,
            Native.WS_POPUP, IntPtr.Zero, "taskbar");
        if (!IsValid)
        {
            LogSink.Write("[taskbar] 建窗失败 err=" + Marshal.GetLastWin32Error());
            return;
        }

        bool ok = DockToTaskbar();
        LogSink.Write("[taskbar] dock ok=" + ok + " parent=0x" + _parent.ToInt64().ToString("X") +
                      (ok ? "" : " err=" + _lastDockError));

        Native.SetTimer(Hwnd, TimerId, TickMs, IntPtr.Zero);
    }

    // ---------------- 配置视图 ----------------

    internal TaskbarPlace Place => ParsePlace(_settings.TaskbarPlace);

    /// <summary>当前宽度（dip），始终落在 56..160 之间。</summary>
    internal int WidthDip => ClampWidth(_settings.TaskbarWidthDip);

    /// <summary>字号跟着宽度走：宽 84 时 11 号，宽 160 时约 21 号。</summary>
    internal double FontPt => BaseFontPt * WidthDip / BaseWidthDip;

    internal static TaskbarPlace ParsePlace(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "center" => TaskbarPlace.Center,
        "right" => TaskbarPlace.Right,
        "custom" => TaskbarPlace.Custom,
        _ => TaskbarPlace.Auto,
    };

    internal static string PlaceKey(TaskbarPlace p) => p switch
    {
        TaskbarPlace.Center => "center",
        TaskbarPlace.Right => "right",
        TaskbarPlace.Custom => "custom",
        _ => "auto",
    };

    internal static int ClampWidth(double dip)
    {
        if (double.IsNaN(dip) || dip <= 0) return BaseWidthDip;
        return (int)Math.Round(Math.Clamp(dip, MinWidthDip, MaxWidthDip));
    }

    /// <summary>
    /// 位置算式（纯函数，--probe-taskbar 直接调它自检）。
    /// 坐标都是「相对任务栏左沿」，-1 表示这个参照物没找到。
    /// </summary>
    internal static int PlaceX(TaskbarPlace place, int tbW, int w, int gap,
                               int startLeft, int startRight, int trayLeft,
                               double customDip, double scale)
    {
        int x = place switch
        {
            TaskbarPlace.Center => (tbW - w) / 2,
            TaskbarPlace.Right => trayLeft >= 0 ? trayLeft - w - gap : tbW - w - gap,
            TaskbarPlace.Custom => (int)Math.Round(Math.Max(0, customDip) * scale),
            _ => AutoX(tbW, w, gap, startLeft, startRight),
        };
        if (x < 0) x = 0;
        int hi = Math.Max(0, tbW - w);
        if (x > hi) x = hi;
        return x;
    }

    /// <summary>自动位置：开始按钮左边；左边放不下就它右边；连开始按钮都找不到就贴任务栏最左。</summary>
    private static int AutoX(int tbW, int w, int gap, int startLeft, int startRight)
    {
        int x = gap;
        if (startLeft >= 0)
        {
            int leftOfStart = startLeft - gap - w;
            x = leftOfStart >= gap ? leftOfStart : Math.Min(Math.Max(0, tbW - w), startRight + gap);
        }
        return x;
    }

    /// <summary>菜单里选了一个位置档。</summary>
    internal void SetPlace(TaskbarPlace place)
    {
        if (place == TaskbarPlace.Custom && _settings.TaskbarLeftDip < 0)
        {
            // 第一次切到「自定义」：用当前实际位置当起点，免得数字突然跳到最左
            int x = CurrentX();
            _settings.TaskbarLeftDip = Math.Round(_scale > 0 ? x / _scale : x, 2);
        }
        _settings.TaskbarPlace = PlaceKey(place);
        _settings.Save();
        Reposition(true);
        LogSink.Write("[taskbar] place=" + _settings.TaskbarPlace + " dip=" + _settings.TaskbarLeftDip.ToString("F2"));
    }

    /// <summary>用户输入了一个左边距（dip），直接进自定义档。</summary>
    internal void SetCustomLeft(double dip)
    {
        _settings.TaskbarLeftDip = Math.Round(Math.Max(0, dip), 2);
        _settings.TaskbarPlace = PlaceKey(TaskbarPlace.Custom);
        _settings.Save();
        Reposition(true);
        LogSink.Write("[taskbar] 自定义左边距 dip=" + _settings.TaskbarLeftDip.ToString("F2"));
    }

    /// <summary>用户输入了一个宽度（dip）。字号会自动跟着变。</summary>
    internal void SetWidthDip(double dip)
    {
        _settings.TaskbarWidthDip = ClampWidth(dip);
        _settings.Save();
        _font?.Dispose();
        _font = null;
        Reposition(true);
        LogSink.Write("[taskbar] 宽度 dip=" + _settings.TaskbarWidthDip + " font=" + FontPt.ToString("F1"));
    }

    /// <summary>菜单状态行用的一句话：现在贴哪、多宽、实际在第几像素。</summary>
    internal string Describe()
    {
        string place = Place switch
        {
            TaskbarPlace.Center => "屏幕正中",
            TaskbarPlace.Right => "靠托盘左边",
            TaskbarPlace.Custom => "自定义左边距",
            _ => "贴开始菜单左边",
        };
        string where = "";
        if (_parent != IntPtr.Zero && Native.IsWindow(_parent))
        {
            int x = CurrentX();
            double sc = _scale > 0 ? _scale : 1.0;
            where = " · x=" + x + "px（" + (int)Math.Round(x / sc) + " dip）";
        }
        return "现在：" + place + " · 宽 " + WidthDip + where;
    }

    /// <summary>
    /// 自定义左边距的可用范围（dip）：0 到「任务栏宽减数字宽」。
    /// 任务栏还没挂上时给一个宽裕的默认上限，别让输入窗口报错。
    /// </summary>
    internal (int Min, int Max) PlaceLimits()
    {
        if (_parent == IntPtr.Zero || !Native.IsWindow(_parent)) return (0, 1200);
        if (!Native.GetWindowRect(_parent, out var tb)) return (0, 1200);

        int tbW = tb.Right - tb.Left;
        double sc = _scale > 0 ? _scale : 1.0;
        int w = (int)Math.Round(WidthDip * sc);
        int maxDip = (int)Math.Max(0, Math.Round((tbW - w) / sc));
        return (0, maxDip);
    }

    /// <summary>回到出厂：自动摆位 + 默认宽度。</summary>
    internal void ResetPosition()
    {
        _settings.TaskbarLeftDip = -1;
        _settings.TaskbarPlace = PlaceKey(TaskbarPlace.Auto);
        _settings.TaskbarWidthDip = BaseWidthDip;
        _settings.Save();
        _font?.Dispose();
        _font = null;
        Reposition(true);
        LogSink.Write("[taskbar] 位置与大小都已重置（自动 + 宽 84）");
    }

    /// <summary>喂一帧数据过来。只在显示内容真的变了才重画，省点 CPU。</summary>
    internal void Apply(Snapshot snap)
    {
        string before = Label(_snap);
        _snap = snap;
        if (Label(_snap) != before) Redraw();
    }

    /// <summary>拆掉：先解除父子关系并恢复成普通窗口，再释放。</summary>
    internal void Stop()
    {
        try { if (IsValid) Native.KillTimer(Hwnd, TimerId); } catch { }
        try
        {
            if (IsValid)
            {
                Native.SetParent(Hwnd, IntPtr.Zero);
                int style = Native.GetWindowLongW(Hwnd, Native.GWL_STYLE);
                Native.SetWindowLongW(Hwnd, Native.GWL_STYLE, (style & ~Native.WS_CHILD) | Native.WS_POPUP);
                Native.ShowWindow(Hwnd, Native.SW_HIDE);
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("[taskbar] undock: " + ex.Message);
        }

        _parent = IntPtr.Zero;
        _font?.Dispose();
        _font = null;
        Dispose();
    }

    /// <summary>ULW 窗口的内容由 Redraw 提交，WM_PAINT 不负责绘制。</summary>
    protected override void Paint(Graphics g) { }

    // ---- 鼠标：拖到哪算哪（自动记成自定义档），单击打开面板 ----

    protected override bool WantsHandCursor(int x, int y) => true;

    protected override void OnLeftDown(int x, int y)
    {
        if (!Native.GetCursorPos(out var pt)) return;
        _dragMoved = false;
        _dragging = true;
        _dragCursorX = pt.X;
        _dragOriginX = CurrentX();
    }

    protected override void OnMouseMove(int x, int y, bool entering)
    {
        if (!_dragging) return;
        if (!Native.GetCursorPos(out var pt)) return;
        int dx = pt.X - _dragCursorX;
        if (!_dragMoved && Math.Abs(dx) < DragThreshold) return;
        _dragMoved = true;
        DragTo(_dragOriginX + dx);
    }

    protected override void OnLeftUp(int x, int y)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_dragMoved) SaveManualPosition(CurrentX());
        else _onOpen();
    }

    /// <summary>
    /// 拖动途中捕获被抢走：以前会卡在 _dragging = true，之后 tick 里的自动重排全被挡住、
    /// 松手也存不下位置 —— 表现就是「拖到一半不动了，下次开机又回原位」。
    /// </summary>
    protected override void OnCaptureLost()
    {
        if (!_dragging) return;
        _dragging = false;
        LogSink.Write("[taskbar] 拖动被系统中断，按当前位置收尾 moved=" + _dragMoved);
        if (_dragMoved) SaveManualPosition(CurrentX());
    }

    private int CurrentX()
    {
        if (_parent == IntPtr.Zero || !Native.IsWindow(_parent)) return 0;
        if (!Native.GetWindowRect(_parent, out var tb)) return 0;
        if (!Native.GetWindowRect(Hwnd, out var me)) return 0;
        return me.Left - tb.Left;
    }

    private void DragTo(int x)
    {
        if (_parent == IntPtr.Zero || !Native.IsWindow(_parent)) return;
        if (!Native.GetWindowRect(_parent, out var tb)) return;
        if (!Native.GetWindowRect(Hwnd, out var me)) return;

        int tbW = tb.Right - tb.Left;
        int w = me.Right - me.Left;
        int h = me.Bottom - me.Top;
        int clamped = Math.Max(0, Math.Min(x, tbW - w));
        if (me.Left - tb.Left == clamped) return;

        Native.MoveWindow(Hwnd, clamped, 0, w, h, false);
        RaiseZOrder();
        Redraw();
    }

    private void SaveManualPosition(int x)
    {
        double dip = _scale > 0 ? x / _scale : x;
        _settings.TaskbarLeftDip = Math.Round(dip, 2);
        _settings.TaskbarPlace = PlaceKey(TaskbarPlace.Custom);
        _settings.Save();
        LogSink.Write("[taskbar] 手动位置 x=" + x + " dip=" + _settings.TaskbarLeftDip.ToString("F2"));
    }

    // ---- 绘制 ----

    private static string Label(Snapshot snap)
        => !snap.Connected ? "--" : WattStyle.Format(snap.TotalWatts) + "W";

    private Font Font2()
    {
        float pt = (float)(FontPt * _scale);
        if (_font is not null && Math.Abs(_font.SizeInPoints - pt) < 0.05f) return _font;
        _font?.Dispose();
        _font = new Font("Segoe UI", pt, FontStyle.Bold, GraphicsUnit.Point);
        return _font;
    }

    private void Redraw()
    {
        if (!IsValid) return;
        if (!Native.GetWindowRect(Hwnd, out var me)) return;

        int w = me.Right - me.Left;
        int h = me.Bottom - me.Top;
        if (w <= 0 || h <= 0) return;

        int parentX = 0, parentY = 0;
        var parent = Native.GetParent(Hwnd);
        if (parent != IntPtr.Zero && Native.GetWindowRect(parent, out var pr))
        {
            parentX = pr.Left;
            parentY = pr.Top;
        }

        string text = Label(_snap);
        Color color = _snap.Connected ? WattStyle.Total(_snap.TotalWatts) : WattStyle.Dim;
        var font = Font2();

        // 子窗口的 UpdateLayeredWindow 坐标是「父客户区坐标」，不是屏幕坐标
        TaskbarPaint.PaintLayered(Hwnd, w, h, me.Left - parentX, me.Top - parentY, (g, cw, ch) =>
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

    // ---- 贴任务栏 ----

    private bool DockToTaskbar()
    {
        var tray = Native.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        if (Native.GetParent(Hwnd) != tray)
        {
            int style = Native.GetWindowLongW(Hwnd, Native.GWL_STYLE);
            Native.SetWindowLongW(Hwnd, Native.GWL_STYLE, (style & ~Native.WS_POPUP) | Native.WS_CHILD);

            IntPtr before = Native.GetParent(Hwnd);
            bool parented = Native.SetParent(Hwnd, tray);
            int err = Marshal.GetLastWin32Error();
            Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
            _lastDockError = err;
            LogSink.Write("[taskbar] setparent tray=0x" + tray.ToInt64().ToString("X") +
                          " before=0x" + before.ToInt64().ToString("X") +
                          " ok=" + parented + " err=" + err +
                          " after=0x" + Native.GetParent(Hwnd).ToInt64().ToString("X"));
        }

        if (Native.GetParent(Hwnd) != tray)
        {
            _parent = IntPtr.Zero;
            return false;
        }

        _parent = tray;
        Native.ShowWindow(Hwnd, Native.SW_SHOWNA);
        return Reposition(true);
    }

    /// <summary>
    /// 把自己提到兄弟窗口最前面。0.5.4 之前一辈子只调一次不带 z 序的 SetWindowPos，
    /// 于是拖到托盘区就被 TrayNotifyWnd 盖住 —— issue #7 的「看不到 / 挪不过去」。
    /// </summary>
    private void RaiseZOrder()
    {
        if (!IsValid) return;
        Native.SetWindowPos(Hwnd, new IntPtr(Native.HWND_TOP), 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    private bool Reposition(bool force = false)
    {
        if (_parent == IntPtr.Zero || !Native.IsWindow(_parent)) return false;
        if (!Native.GetWindowRect(_parent, out var tb)) return false;

        int tbW = tb.Right - tb.Left;
        int tbH = tb.Bottom - tb.Top;
        if (tbW <= tbH) return false;   // 竖向任务栏：这一版不处理

        uint dpi = Native.GetDpiForWindow(_parent);
        _scale = Math.Max(1.0, (dpi < 48 ? 96u : dpi) / 96.0);
        int w = (int)Math.Round(WidthDip * _scale);
        int gap = (int)Math.Round(GapDip * _scale);

        int startLeft = -1, startRight = -1;
        var start = Native.FindWindowExW(_parent, IntPtr.Zero, "Start", null);
        if (start != IntPtr.Zero && Native.GetWindowRect(start, out var sr) && sr.Left >= tb.Left && sr.Left < tb.Right)
        {
            startLeft = sr.Left - tb.Left;
            startRight = sr.Right - tb.Left;
        }

        int trayLeft = -1;
        var tray = Native.FindWindowExW(_parent, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tr) && tr.Left > tb.Left && tr.Left < tb.Right)
            trayLeft = tr.Left - tb.Left;

        int x = PlaceX(Place, tbW, w, gap, startLeft, startRight, trayLeft,
                       _settings.TaskbarLeftDip, _scale);

        bool same = !force
            && Native.GetWindowRect(Hwnd, out var cur)
            && cur.Left == tb.Left + x && cur.Top == tb.Top
            && cur.Right - cur.Left == w && cur.Bottom - cur.Top == tbH;

        if (!same)
        {
            Native.MoveWindow(Hwnd, x, 0, w, tbH, false);
            Width = w;
            Height = tbH;
            Redraw();
        }

        // 每拍都提一次 z 序：任务栏自己也会插新窗口（托盘展开、通知区更新），早点把位置抢回来
        RaiseZOrder();
        return true;
    }

    // ---- 定时自愈 ----

    protected override void OnTimer(int id)
    {
        if (id != 1) return;
        try
        {
            if (_parent == IntPtr.Zero
                || !Native.IsWindow(_parent)
                || Native.GetParent(Hwnd) != _parent)
            {
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
}

/// <summary>UpdateLayeredWindow 画一张 32 位 ARGB 位图贴到窗口上。</summary>
internal static class TaskbarPaint
{
    internal static bool PaintLayered(IntPtr hwnd, int w, int h, int dstX, int dstY, Action<Graphics, int, int> draw)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h;          // 负数 = 自上而下
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;      // BI_RGB

        IntPtr hDib = Native.CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (hDib == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        IntPtr old = Native.SelectObject(memDc, hDib);
        bool ok;
        try
        {
            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppArgb, bits))
            using (var g = Graphics.FromImage(bmp))
            {
                draw(g, w, h);

                var dst = new Native.POINT(dstX, dstY);
                var size = new Native.SIZE { cx = w, cy = h };
                var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Native.AC_SRC_ALPHA,
                };
                ok = Native.UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(hDib);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }

        return ok;
    }
}