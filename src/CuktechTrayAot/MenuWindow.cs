using System.Drawing.Drawing2D;

namespace CuktechTray;

/// <summary>
/// 一条菜单项。Sub 非空就是子菜单，Click 是点下去要做的事。
/// 颜色/间距照抄 WinForms 版的 UiTheme（MenuBg 30,34,41 / MenuLine 58,64,74 /
/// MenuHover 52,58,70 / MenuFg 232,234,237 / MenuFgDim 118,125,135 / MenuGreen 52,211,153），
/// 这样换成自绘之后右键菜单看起来还是原来那张。
/// </summary>
internal sealed class MenuEntry
{
    public string Text = string.Empty;
    public bool IsSeparator;

    /// <summary>true = 勾选框（多选），false + Radio = 单选项（画圆点）。</summary>
    public bool Checked;

    /// <summary>单选样式：左边画圆点而不是勾。用在一组互斥的选项上（位置档位、大小档位）。</summary>
    public bool Radio;

    /// <summary>
    /// 行尾那颗小红点：表示「这条菜单上有事要你注意」（现在只用在「检查更新」有新版本时）。
    /// 没选中的勾/圆点占的是左边，红点占右边，两者互不挡。
    /// </summary>
    public bool Dot;
    public bool Enabled = true;
    public bool Bold;
    public Action? Click;
    public MenuEntry[]? Sub;

    public bool HasSub => Sub is { Length: > 0 };

    public static MenuEntry Item(string text, Action? click = null) => new() { Text = text, Click = click };
    public static MenuEntry BoldItem(string text, Action? click = null) => new() { Text = text, Click = click, Bold = true };
    public static MenuEntry CheckItem(string text, bool on, Action? click = null) => new() { Text = text, Checked = on, Click = click };
    public static MenuEntry RadioItem(string text, bool on, Action? click = null) => new() { Text = text, Checked = on, Radio = true, Click = click };
    public static MenuEntry Dim(string text) => new() { Text = text, Enabled = false };
    public static MenuEntry DotItem(string text, Action? click = null) => new() { Text = text, Click = click, Dot = true };
    public static MenuEntry Sep() => new() { IsSeparator = true };
    public static MenuEntry WithSub(string text, params MenuEntry[] items) => new() { Text = text, Sub = items };
}

/// <summary>
/// 自绘的托盘右键菜单。裸 Win32 没有 ContextMenuStrip，所以自己画一个弹出窗口：
/// 圆角、深色、左边自画笔、右边子菜单箭头。
///
/// 鼠标统一由「根菜单」捕获（SetCapture）：这样点击菜单外面也能收到那条消息，
/// 用来实现「点别处先关菜单」以及子菜单悬停切换；子菜单窗口自己不捕获鼠标。
/// </summary>
internal sealed class MenuWindow : WinBase
{
    private static readonly Color MenuBg = Color.FromArgb(30, 34, 41);
    private static readonly Color MenuLine = Color.FromArgb(58, 64, 74);
    private static readonly Color MenuHover = Color.FromArgb(52, 58, 70);
    private static readonly Color MenuFg = Color.FromArgb(232, 234, 237);
    private static readonly Color MenuFgDim = Color.FromArgb(118, 125, 135);
    private static readonly Color MenuGreen = Color.FromArgb(52, 211, 153);
    private static readonly Color DotRed = Color.FromArgb(239, 68, 68);

    private const int DesignRowH = 30;
    private const int DesignSepH = 9;
    private const int DesignPadLeft = 24;   // 左边留给自绘的勾
    private const int DesignPadRight = 16;
    private const int DesignArrowW = 14;
    private const int DesignDotW = 16;     // 行尾红点占的宽度（含一点间距）
    private const int DesignPadV = 6;
    private const int DesignRadius = 10;

    private static readonly IntPtr HwndTopMost = new(-1);

    private static MenuWindow? _root;

    private readonly MenuEntry[] _items;
    private readonly MenuWindow? _owner;
    private readonly List<(int Top, int H, MenuEntry E)> _rows = new();
    private MenuWindow? _child;
    private GdiFont? _normal;
    private GdiFont? _bold;
    private int _hot = -1;
    private bool _ready;

    private MenuWindow(MenuEntry[] items, MenuWindow? owner)
    {
        _items = items;
        _owner = owner;
    }

    private bool IsRoot => _owner is null;

    private int S(float v) => (int)Math.Round(v * Scale);

    /// <summary>把菜单弹在屏幕坐标 (sx, sy)。已经在显示的话会先关掉旧的。</summary>
    public static void Show(MenuEntry[] items, int sx, int sy)
    {
        CloseAll();
        var m = new MenuWindow(items, null);
        if (!m.Create(sx, sy))
        {
            m.DisposeTree();
            return;
        }
        _root = m;
        Native.SetCapture(m.Handle);
    }

    public static void CloseAll()
    {
        var root = _root;
        _root = null;
        if (root is null) return;
        Native.ReleaseCapture();
        root.DisposeTree();
    }

    public static bool IsOpen => _root is not null;

    private bool Create(int sx, int sy)
    {
        CreateWin(8, 8, Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
            Native.WS_POPUP, IntPtr.Zero, "menu");
        if (!IsValid) return false;

        _normal = GdiFont.Create("Microsoft YaHei UI", 10f, Scale);
        _bold = GdiFont.Create("Microsoft YaHei UI", 10f, Scale, true);

        MeasureRows(out int w, out int h);
        ClampToWorkArea(ref sx, ref sy, w, h);

        Native.SetWindowPos(Hwnd, HwndTopMost, sx, sy, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW);
        Width = w;
        Height = h;
        SetRoundedRegion(S(DesignRadius));
        _ready = true;
        Invalidate();
        return true;
    }

    private void ClampToWorkArea(ref int x, ref int y, int w, int h)
    {
        if (Native.GetCursorPos(out var _)) { }
        var pt = new Native.POINT(x, y);
        IntPtr mon = Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (mon == IntPtr.Zero || !Native.GetMonitorInfoW(mon, ref mi)) return;
        if (x + w > mi.rcWork.Right) x = Math.Max(mi.rcWork.Left, mi.rcWork.Right - w);
        if (x < mi.rcWork.Left) x = mi.rcWork.Left;
        if (y + h > mi.rcWork.Bottom) y = Math.Max(mi.rcWork.Top, mi.rcWork.Bottom - h);
        if (y < mi.rcWork.Top) y = mi.rcWork.Top;
    }

    private void MeasureRows(out int width, out int height)
    {
        _rows.Clear();
        int maxText = 0;
        using (var g = Graphics.FromHwnd(Hwnd))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                foreach (var e in _items)
                {
                    if (e.IsSeparator) continue;
                    var font = e.Bold ? _bold! : _normal!;
                    int tw = font.Measure(e.Text, hdc);
                    if (e.HasSub) tw += S(DesignArrowW);
                    if (e.Dot) tw += S(DesignDotW);
                    if (tw > maxText) maxText = tw;
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        int rowH = S(DesignRowH);
        int sepH = S(DesignSepH);
        int y = S(DesignPadV);
        foreach (var e in _items)
        {
            int h = e.IsSeparator ? sepH : rowH;
            _rows.Add((y, h, e));
            y += h;
        }
        height = y + S(DesignPadV);
        width = S(DesignPadLeft) + maxText + S(DesignPadRight);
        int minW = S(132);
        if (width < minW) width = minW;
    }

    // ---------------- 绘制 ----------------

    protected override void Paint(Graphics g)
    {
        if (!_ready)
        {
            g.Clear(MenuBg);
            return;
        }
        FillRound(g, new RectangleF(0, 0, Width, Height), S(DesignRadius), MenuBg);
        StrokeRound(g, new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(DesignRadius), MenuLine, 1f);

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.E.IsSeparator)
            {
                using var pen = new Pen(MenuLine, 1f);
                float ly = row.Top + row.H / 2f;
                g.DrawLine(pen, S(10), ly, Width - S(10), ly);
                continue;
            }

            var e = row.E;
            if (i == _hot && e.Enabled)
                FillRound(g, new RectangleF(S(4), row.Top + S(1), Width - S(8), row.H - S(2)), S(6), MenuHover);

            if (e.Radio) DrawRadio(g, row.Top, row.H, e.Checked);
            else if (e.Checked) DrawCheck(g, row.Top, row.H);

            var font = e.Bold ? _bold! : _normal!;
            var color = e.Enabled ? MenuFg : MenuFgDim;
            int textX = S(DesignPadLeft);
            int textW = Width - textX - S(DesignPadRight)
                        - (e.HasSub ? S(DesignArrowW) : 0) - (e.Dot ? S(DesignDotW) : 0);
            GdiText.Draw(g, e.Text, font, color, new Rectangle(textX, row.Top, textW, row.H), TextAnchor.Left, false);

            if (e.HasSub) DrawArrow(g, row.Top, row.H, color);
            if (e.Dot) DrawDot(g, row.Top, row.H);
        }
    }

    private void DrawCheck(Graphics g, int top, int h)
    {
        float s = Scale;
        float cx = S(11.5f);
        float cy = top + h / 2f;
        using var pen = new Pen(MenuGreen, Math.Max(1.4f, 1.7f * s))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new PointF(cx - 4f * s, cy + 0.5f * s),
            new PointF(cx - 1f * s, cy + 3.5f * s),
            new PointF(cx + 4.5f * s, cy - 4f * s),
        });
    }

    /// <summary>单选项左边那颗圆点：没选就只有一个淡环，选了就是绿环 + 实心绿点。</summary>
    private void DrawRadio(Graphics g, int top, int h, bool on)
    {
        float s = Scale;
        float cx = S(11.5f);
        float cy = top + h / 2f;
        float r = 4.2f * s;
        var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var pen = new Pen(on ? MenuGreen : MenuFgDim, Math.Max(1f, 1.2f * s)))
            g.DrawEllipse(pen, rect);
        if (!on) return;
        float ir = 2.1f * s;
        using var brush = new SolidBrush(MenuGreen);
        g.FillEllipse(brush, cx - ir, cy - ir, ir * 2, ir * 2);
    }

    /// <summary>行尾那颗红点：跟标题栏/托盘角标的红一样，一眼能看见但不刺眼。</summary>
    private void DrawDot(Graphics g, int top, int h)
    {
        float s = Scale;
        float r = 3.2f * s;
        float cx = Width - S(DesignPadRight) - S(4);
        float cy = top + h / 2f;
        using var brush = new SolidBrush(DotRed);
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
    }

    private void DrawArrow(Graphics g, int top, int h, Color color)
    {
        float s = Scale;
        float ax = Width - S(DesignPadRight) + S(3);
        float ay = top + h / 2f;
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, new[]
        {
            new PointF(ax, ay - 4f * s),
            new PointF(ax + 5f * s, ay),
            new PointF(ax, ay + 4f * s),
        });
    }

    // ---------------- 鼠标 ----------------

    /// <summary>客户坐标 → 行号。-99 表示不在窗口里，-2 表示在窗口里但没落在可点行上。</summary>
    private int HitTest(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return -99;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (y < row.Top || y >= row.Top + row.H) continue;
            if (row.E.IsSeparator || !row.E.Enabled) return -2;
            return i;
        }
        return -2;
    }

    private int HitTestScreen(int sx, int sy)
    {
        if (!IsValid) return -99;
        Native.GetWindowRect(Hwnd, out var r);
        return HitTest(sx - r.Left, sy - r.Top);
    }

    private void SetHot(int index)
    {
        if (_hot == index) return;
        _hot = index;
        Invalidate();
    }

    protected override void OnMouseMove(int x, int y, bool entering)
    {
        if (!IsRoot) return;
        if (!Native.GetCursorPos(out var pt)) return;
        int sx = pt.X, sy = pt.Y;

        int rootHit = HitTestScreen(sx, sy);
        if (_child is not null)
        {
            int childHit = _child.HitTestScreen(sx, sy);
            if (childHit >= 0)
            {
                _child.SetHot(childHit);
            }
            else
            {
                _child.SetHot(-1);
                if (childHit == -99 && (rootHit < 0 || !_rows[rootHit].E.HasSub)) CloseChild();
            }
        }

        if (rootHit >= 0) SetHot(rootHit);
        else if (rootHit == -2) SetHot(-1);
    }

    protected override void OnLeftDown(int x, int y)
    {
        if (!IsRoot) return;
        if (!Native.GetCursorPos(out var pt)) return;
        int sx = pt.X, sy = pt.Y;

        if (_child is not null)
        {
            int childHit = _child.HitTestScreen(sx, sy);
            if (childHit >= 0)
            {
                _child.Activate(childHit);
                return;
            }
        }

        int rootHit = HitTestScreen(sx, sy);
        if (rootHit >= 0)
        {
            var e = _rows[rootHit].E;
            if (e.HasSub) OpenChild(rootHit);
            else Activate(rootHit);
            return;
        }
        CloseAll();
    }

    protected override void OnRightUp(int x, int y)
    {
        if (IsRoot) CloseAll();
    }

    private void Activate(int index)
    {
        var e = _rows[index].E;
        if (!e.Enabled || e.IsSeparator) return;
        var action = e.Click;
        CloseAll();
        if (action is null) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            LogSink.Write("[menu] 菜单动作异常: " + ex);
        }
    }

    private void OpenChild(int index)
    {
        CloseChild();
        var e = _rows[index].E;
        if (!e.HasSub) return;
        Native.GetWindowRect(Hwnd, out var r);
        int x = r.Right - S(6);
        int y = r.Top + _rows[index].Top - S(DesignPadV);
        var child = new MenuWindow(e.Sub!, this);
        if (child.Create(x, y)) _child = child;
        else child.DisposeTree();
        SetHot(index);
    }

    private void CloseChild()
    {
        var child = _child;
        _child = null;
        child?.DisposeTree();
    }

    private void DisposeTree()
    {
        CloseChild();
        _normal?.Dispose();
        _normal = null;
        _bold?.Dispose();
        _bold = null;
        Dispose();
    }

    public override void Dispose()
    {
        CloseChild();
        _normal?.Dispose();
        _normal = null;
        _bold?.Dispose();
        _bold = null;
        base.Dispose();
    }
}
