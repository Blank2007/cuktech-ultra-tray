using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CuktechTray;


/// <summary>
/// 一个裸 Win32 窗口的最小骨架：注册类、建窗、双缓冲自绘、鼠标跟踪、DPI 变化。
/// 没有 WinForms，全靠 P/Invoke 和 GDI+ 自己画。
/// </summary>
internal abstract class WinBase : IDisposable
{
    private static readonly Dictionary<IntPtr, WinBase> Registry = new();
    private static readonly HashSet<string> RegisteredClasses = new();
    private static IntPtr _procPtr;
    private static IntPtr _cursorArrow;
    private static IntPtr _cursorHand;
    private static int _classSeq;

    protected IntPtr Hwnd;
    protected int Width;
    protected int Height;
    protected float Scale = 1f;
    protected bool TrackingLeave;

    private Surface? _buffer;
    private string _className = string.Empty;
    private bool _disposed;

    public IntPtr Handle => Hwnd;
    public bool IsValid => Hwnd != IntPtr.Zero && Native.IsWindow(Hwnd);
    public bool Visible => IsValid && Native.IsWindowVisible(Hwnd);

    protected abstract void Paint(Graphics g);

    /// <summary>鼠标是否落在某个「可点」区域上，用来决定手型光标。返回 true 表示手型。</summary>
    protected virtual bool WantsHandCursor(int x, int y) => false;

    protected virtual void OnLeftDown(int x, int y) { }
    protected virtual void OnLeftUp(int x, int y) { }

    /// <summary>
    /// 鼠标捕获被别人抢走了（最常见：点着窗口拖动时 explorer 或别的窗口抢了 SetCapture）。
    /// 不处理的话，按下时进入的「拖动中」状态会一直卡住，松手事件也永远收不到。
    /// </summary>
    protected virtual void OnCaptureLost() { }
    protected virtual void OnRightUp(int x, int y) { }
    protected virtual void OnMouseMove(int x, int y, bool entering) { }
    protected virtual void OnMouseLeave() { }
    protected virtual void OnKeyDown(int vk) { }
    protected virtual void OnChar(char c) { }
    protected virtual void OnTimer(int id) { }
    /// <summary>有人用 PostMessageW(WM_APP_POST) 敲了一下：UI 线程到了，去把排队的回调跑掉。</summary>
    protected virtual void OnWake() { }

    /// <summary>自定消息（托盘图标回调、注册的自定义消息等）。返回 true 表示已处理。</summary>
    protected virtual bool OnAppMessage(uint msg, IntPtr wParam, IntPtr lParam) => false;

    /// <summary>锁屏 / 切换用户 / 远程断开（WTS_SESSION_*）。</summary>
    protected virtual void OnSessionChange(int reason) { }
    protected virtual void OnDpiChanged(float scale, Native.RECT suggested) { }
    protected virtual void OnActivate(bool active) { }
    protected virtual void OnDestroy() { }
    protected virtual bool OnClose() => true;

    protected static IntPtr CursorArrow => _cursorArrow != IntPtr.Zero ? _cursorArrow : Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW_ID);
    protected static IntPtr CursorHand => _cursorHand != IntPtr.Zero ? _cursorHand : Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_HAND_ID);

    private static unsafe IntPtr ProcPointer =>
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc;

    private static string MakeClassName(string hint)
    {
        if (_cursorArrow == IntPtr.Zero) _cursorArrow = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW_ID);
        if (_cursorHand == IntPtr.Zero) _cursorHand = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_HAND_ID);
        return "CuktechAot." + hint + "." + (++_classSeq);
    }

    /// <summary>建窗。尺寸为物理像素。</summary>
    protected void CreateWin(int width, int height, int exStyle, int style, IntPtr parent, string classHint)
    {
        _procPtr = ProcPointer;
        _className = MakeClassName(classHint);
        if (!RegisteredClasses.Contains(_className))
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                style = 0x0002 | 0x0020,   // CS_HREDRAW | CS_DBLCLKS
                lpfnWndProc = _procPtr,
                hInstance = Native.GetModuleHandleW(null),
                hCursor = _cursorArrow,
                hbrBackground = IntPtr.Zero,
                lpszClassName = _className,
                hIcon = IntPtr.Zero,
                hIconSm = IntPtr.Zero,
            };
            if (Native.RegisterClassExW(ref wc) == 0)
                LogSink.Write("[win] RegisterClassExW 失败 " + _className + " err=" + Marshal.GetLastWin32Error());
            else RegisteredClasses.Add(_className);
        }

        // 这里刻意不走「GCHandle 塞进 lpParam、WM_NCCREATE 里取回」的老套路：
        // NativeAOT 下 GCHandle.FromIntPtr(lParam).Target 会抛 NullReferenceException，
        // 结果窗口建得起来但 WndProc 永远找不到 this。改成建完窗直接注册，简单可靠。
        Hwnd = Native.CreateWindowExW(exStyle, _className, string.Empty, style | Native.WS_CLIPCHILDREN,
            0, 0, Math.Max(1, width), Math.Max(1, height), parent, IntPtr.Zero, Native.GetModuleHandleW(null),
            IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
        {
            LogSink.Write("[win] CreateWindowExW 失败 " + _className + " err=" + Marshal.GetLastWin32Error());
            return;
        }
        Registry[Hwnd] = this;
        Width = width;
        Height = height;
        Scale = ReadScale();
    }

    /// <summary>
    /// 窗口过程入口。NativeAOT 下必须用 [UnmanagedCallersOnly] 直接给函数指针：
    /// Marshal.GetFunctionPointerForDelegate 生成的 thunk 在这里一旦被 user32 回调就是访问违例
    /// （进程静默消失、连 catch 都进不去）。
    /// </summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try { return WndProcCore(hWnd, msg, wParam, lParam); }
        catch (Exception ex)
        {
            LogSink.Write("[win] WndProc 异常 msg=0x" + msg.ToString("X") + " " + ex.GetType().Name + ": " + ex.Message);
            return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private static IntPtr WndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Registry.TryGetValue(hWnd, out var self) && self is not null)
            return self.HandleMessage(msg, wParam, lParam);
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private float ReadScale()
    {
        uint dpi = Hwnd != IntPtr.Zero ? Native.GetDpiForWindow(Hwnd) : 96;
        if (dpi < 48) dpi = 96;
        return Math.Max(1f, dpi / 96f);
    }

    private IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
                return (IntPtr)1;

            case 0x0005: // WM_SIZE
            {
                Width = Native.LoWord(lParam);
                Height = Native.HiWord(lParam);
                DropBuffer();
                Native.InvalidateRect(Hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            case Native.WM_PAINT:
                PaintNow();
                return IntPtr.Zero;

            case Native.WM_DPICHANGED:
            {
                float old = Scale;
                Scale = ReadScale();
                OnDpiChanged(Scale, new Native.RECT());
                if (Math.Abs(old - Scale) > 0.001f) DropBuffer();
                Native.InvalidateRect(Hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            }

            case Native.WM_MOUSEMOVE:
            {
                int x = Native.LoWord(lParam), y = Native.HiWord(lParam);
                if (!TrackingLeave)
                {
                    TrackingLeave = true;
                    var tme = new Native.TRACKMOUSEEVENT
                    {
                        cbSize = Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
                        dwFlags = 0x00000002, // TME_LEAVE
                        hwndTrack = Hwnd,
                        dwHoverTime = 0,
                    };
                    Native.TrackMouseEvent(ref tme);
                }
                OnMouseMove(x, y, false);
                Native.SetCursor(WantsHandCursor(x, y) ? CursorHand : CursorArrow);
                return IntPtr.Zero;
            }

            case Native.WM_MOUSELEAVE:
                TrackingLeave = false;
                OnMouseLeave();
                return IntPtr.Zero;

            case Native.WM_SETCURSOR:
                return (IntPtr)1;

            case Native.WM_LBUTTONDOWN:
            {
                Native.SetCapture(Hwnd);
                OnLeftDown(Native.LoWord(lParam), Native.HiWord(lParam));
                return IntPtr.Zero;
            }

            case Native.WM_LBUTTONUP:
            {
                Native.ReleaseCapture();
                OnLeftUp(Native.LoWord(lParam), Native.HiWord(lParam));
                return IntPtr.Zero;
            }

            case 0x0215: // WM_CAPTURECHANGED
                OnCaptureLost();
                return IntPtr.Zero;

            case Native.WM_RBUTTONUP:
                OnRightUp(Native.LoWord(lParam), Native.HiWord(lParam));
                return IntPtr.Zero;

            case Native.WM_KEYDOWN:
            case Native.WM_SYSKEYDOWN:
                OnKeyDown((int)wParam);
                return IntPtr.Zero;

            case Native.WM_CHAR:
                OnChar((char)wParam);
                return IntPtr.Zero;

            case Native.WM_TIMER:
                OnTimer((int)wParam);
                return IntPtr.Zero;

            case Native.WM_APP_POST:
                OnWake();
                return IntPtr.Zero;

            case Native.WM_WTSSESSION_CHANGE:
                OnSessionChange((int)wParam);
                return IntPtr.Zero;

            case Native.WM_ACTIVATE:
                OnActivate((int)wParam != 0);
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                if (!OnClose()) return IntPtr.Zero;
                break;

            case Native.WM_DESTROY:
                OnDestroy();
                break;

            case Native.WM_NCDESTROY:
                Registry.Remove(Hwnd);
                DropBuffer();
                OnDestroy();
                return Native.DefWindowProcW(Hwnd, msg, wParam, lParam);
        }
        if (OnAppMessage(msg, wParam, lParam)) return IntPtr.Zero;
        return Native.DefWindowProcW(Hwnd, msg, wParam, lParam);
    }

    /// <summary>双缓冲绘制：先画到 Bitmap，再一次性贴上去，避免闪烁。</summary>
    private void PaintNow()
    {
        IntPtr hdc = Native.BeginPaint(Hwnd, out Native.PAINTSTRUCT ps);
        try
        {
            Native.GetClientRect(Hwnd, out Native.RECT cr);
            int w = Math.Max(1, cr.Width), h = Math.Max(1, cr.Height);
            if (w != Width || h != Height)
            {
                Width = w;
                Height = h;
                DropBuffer();
            }
            if (_buffer is null || _buffer.Width != w || _buffer.Height != h)
            {
                DropBuffer();
                // 用「屏幕兼容 DDB」而不是 Bitmap：GDI 只在显示设备 DC 上按系统那套
                // ClearType 参数做子像素渲染，画到独立 DIB 上文字会变灰变细（发虚）。
                // 浮层和菜单都是用 SetWindowRgn 裁圆角的不透明窗口，不需要 alpha 通道。
                _buffer = Surface.Create(w, h);
            }
            if (_buffer is null) return;
            Graphics g = _buffer.G;
            g.ResetClip();
            g.ResetTransform();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Black);
            try
            {
                Paint(g);
            }
            catch (Exception ex)
            {
                LogSink.Write("[paint] 绘制异常: " + ex);
            }
            _buffer.Blit(hdc);
        }
        finally
        {
            Native.EndPaint(Hwnd, ref ps);
        }
    }

    private void DropBuffer()
    {
        _buffer?.Dispose();
        _buffer = null;
    }

    public void Invalidate() => Native.InvalidateRect(Hwnd, IntPtr.Zero, false);

    protected void SetRoundedRegion(int radius)
    {
        if (radius <= 0) return;
        IntPtr rgn = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, radius * 2, radius * 2);
        if (rgn != IntPtr.Zero) Native.SetWindowRgn(Hwnd, rgn, true);
    }



    protected static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(0.5f, radius * 2);
        if (radius <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected static void FillRound(Graphics g, RectangleF r, float radius, Color color)
    {
        using var path = RoundRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    protected static void StrokeRound(Graphics g, RectangleF r, float radius, Color color, float width)
    {
        using var path = RoundRect(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    protected static void FillRoundGradient(Graphics g, RectangleF r, float radius, Color top, Color bottom)
    {
        using var path = RoundRect(r, radius);
        using var brush = new LinearGradientBrush(new PointF(r.X, r.Y), new PointF(r.X, r.Bottom), top, bottom);
        g.FillPath(brush, path);
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsValid) Native.DestroyWindow(Hwnd);
        Registry.Remove(Hwnd);
        DropBuffer();
        Hwnd = IntPtr.Zero;
    }
}
