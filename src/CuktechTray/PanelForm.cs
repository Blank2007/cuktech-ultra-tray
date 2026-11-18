using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CuktechTray;

/// <summary>无边框深色浮层：显示总功率 + 四个口的电压 / 电流 / 功率。</summary>
internal sealed class PanelForm : Form
{
    private const int DesignW = 320;
    private const int DesignH = 328;

    private static readonly Color BgTop = Color.FromArgb(34, 38, 45);
    private static readonly Color BgBottom = Color.FromArgb(20, 23, 28);
    private static readonly Color Border = Color.FromArgb(58, 64, 74);
    private static readonly Color Fg = Color.FromArgb(232, 234, 237);
    private static readonly Color Dim = WattStyle.Dim;
    private static readonly Color Faint = Color.FromArgb(110, 117, 126);
    private static readonly Color Ok = WattStyle.Ok;
    private static readonly Color Warn = WattStyle.Warn;
    private static readonly Color Bad = WattStyle.Bad;

    private float _s = 1f;
    private Font _fTitle = null!, _fStatus = null!, _fBig = null!, _fUnit = null!,
                 _fHead = null!, _fName = null!, _fVal = null!, _fValBold = null!, _fFoot = null!;

    private const string PinGlyph = "\uE718";
    private const string UnpinGlyph = "\uE77A";

    private Snapshot _snap = new();
    private DateTime _shownAt;
    private bool _dragging;
    private Point _dragOffset;

    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _menuPin;
    private Font _fIcon = null!;

    /// <summary>守住「不许自己压在最上面」这条规矩的定时器，见 GuardTick。</summary>
    private readonly System.Windows.Forms.Timer _guard;
    /// <summary>看门狗连续几拍确认"没人在看这个浮层"。</summary>
    private int _awayTicks;

    /// <summary>
    /// 临时浮层的最长寿命（毫秒，0 = 不限）。开场自我介绍那种浮层会自己到点消失，
    /// 免得不小心一直压在最上面 —— 用户想看随时从托盘再点一次即可。
    /// </summary>
    public int AutoHideAfterMs { get; set; }

    /// <summary>固定在桌面上：不再失焦自动收起，可以一直摆在旁边看。</summary>
    public bool Pinned { get; private set; }

    public event EventHandler? PinChanged;
    public event EventHandler? CloseRequested;

    public PanelForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // 这是一个普通窗口：不置顶、不抢焦点、会被别的窗口盖住。
        // 本程序任何地方都不许把 TopMost 设成 true。
        TopMost = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 27, 32);
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _menuPin = new ToolStripMenuItem("固定到桌面", null, (_, _) => TogglePin()) { Font = new Font("Microsoft YaHei UI", 9.5f) };
        var menuClose = new ToolStripMenuItem("关闭面板", null, (_, _) =>
        {
            HideNow("menu-close");
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }) { Font = new Font("Microsoft YaHei UI", 9.5f) };
        _menu = new ContextMenuStrip { ShowImageMargin = false };
        UiTheme.Skin(_menu);
        _menu.Items.AddRange(new ToolStripItem[] { _menuPin, new ToolStripSeparator(), menuClose });
        _menu.Opening += (_, _) => _menuPin.Checked = Pinned;
        ContextMenuStrip = _menu;

        ApplyScale();

        // 350ms 一次的看门狗：拦住「面板莫名其妙压在别的窗口上面」这一类问题。
        // 光靠 OnDeactivate 不可靠——锁屏、切换用户、别的程序抢前台时，失焦事件可能根本收不到，
        // 窗口就会带着置顶状态一直挂在那儿。
        _guard = new System.Windows.Forms.Timer { Interval = 350 };
        _guard.Tick += (_, _) => GuardTick();
        _guard.Start();
    }

    /// <summary>开发/截图模式：不让失焦自动收起。</summary>
    public bool SuppressAutoHide { get; set; }

    /// <summary>本次显示是否希望把焦点拿过来。常驻面板为 false：它只是摆在桌面上，不该打断你手上的活。</summary>
    private bool _wantActivate = true;

    /// <summary>常驻（固定到桌面）时不抢焦点，像普通窗口一样安安静静地摆着。</summary>
    protected override bool ShowWithoutActivation => !_wantActivate;

    /// <summary>最后一次被自动收起的时间，用于避免"点托盘图标=先收起再弹出"的抖动。</summary>
    public DateTime LastAutoHiddenAt { get; private set; }

    public void Apply(Snapshot snap)
    {
        _snap = snap;
        if (Visible) Invalidate();
    }

    private void ApplyScale()
    {
        _s = Math.Max(1f, DeviceDpi / 96f);
        ClientSize = new Size((int)(DesignW * _s), (int)(DesignH * _s));
        _fTitle = MakeFont("Microsoft YaHei UI", 13f * _s, FontStyle.Bold);
        _fStatus = MakeFont("Microsoft YaHei UI", 11.5f * _s);
        _fBig = MakeFont("Segoe UI", 38f * _s, FontStyle.Bold);
        _fUnit = MakeFont("Segoe UI", 14f * _s);
        _fHead = MakeFont("Microsoft YaHei UI", 11f * _s);
        _fName = MakeFont("Segoe UI", 13f * _s, FontStyle.Bold);
        _fVal = MakeFont("Segoe UI", 13f * _s);
        _fValBold = MakeFont("Segoe UI", 14f * _s, FontStyle.Bold);
        _fFoot = MakeFont("Microsoft YaHei UI", 10.5f * _s);
        _fIcon = MakeFont("Segoe MDL2 Assets", 13f * _s);

        using var path = RoundRect(new Rectangle(0, 0, Width, Height), (int)(14 * _s));
        Region = new Region(path);
    }

    private static Font MakeFont(string family, float px, FontStyle style = FontStyle.Regular)
    {
        try { return new Font(family, px, style, GraphicsUnit.Pixel); }
        catch { return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScale();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        float s = _s;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), BgTop, BgBottom, 90f))
        using (var path = RoundRect(new Rectangle(0, 0, Width, Height), (int)(14 * s)))
            g.FillPath(bg, path);

        using (var pen = new Pen(Border))
        using (var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), (int)(14 * s)))
            g.DrawPath(pen, path);

        // ---- 标题行 ----
        float pad = 16 * s;
        Draw(g, "酷态科 10 号 Ultra", _fTitle, Fg, pad, 12 * s, 180 * s, 22 * s);

        var (dotColor, statusText) = _snap.Connected
            ? (Ok, _snap.Status)
            : (_snap.Status.Contains('…') || _snap.Status.Contains("连接") ? Warn : Bad, _snap.Status);
        var pinRect = PinButtonRect;
        DrawStatusChip(g, statusText, dotColor, pinRect.Left - 6 * s);

        // 图钉：固定后不再自动收起，可以一直摆在桌面上（鼠标靠近时给一层底色，看得出能点）
        using (var pinPath = RoundRect(pinRect, (int)(6 * s)))
        {
            Color pinBg = Pinned ? Color.FromArgb(40, Ok) : _pinHover ? Color.FromArgb(24, Fg) : Color.FromArgb(0, Fg);
            if (pinBg.A > 0)
                using (var b = new SolidBrush(pinBg)) g.FillPath(b, pinPath);
        }
        TextRenderer.DrawText(g, Pinned ? UnpinGlyph : PinGlyph, _fIcon, pinRect,
            Pinned ? Ok : (_pinHover ? Fg : Faint),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        // ---- 总功率：数字颜色随功率高低变（灰 → 绿 → 黄 → 红）----
        double total = _snap.TotalWatts;
        string big = _snap.Connected ? FormatWatts(total) : "--";
        Color bigColor = _snap.Connected ? TotalColor(total) : Faint;
        var bigRect = new Rectangle((int)pad, (int)(36 * s), (int)(220 * s), (int)(52 * s));
        TextRenderer.DrawText(g, big, _fBig, bigRect, bigColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        float bigW = Measure(g, big, _fBig);
        Draw(g, "W", _fUnit, Dim, pad + bigW + 4 * s, 50 * s, 30 * s, 24 * s);
        Draw(g, "总输出", _fHead, Faint, pad + bigW + 40 * s, 52 * s, 70 * s, 20 * s);

        using (var pen = new Pen(Color.FromArgb(44, 49, 57), Math.Max(1, s)))
            g.DrawLine(pen, pad, 96 * s, Width - pad, 96 * s);

        // ---- 表头 ----
        float yHead = 102 * s;
        Draw(g, "接口", _fHead, Faint, pad, yHead, 40 * s, 18 * s);
        Draw(g, "电压", _fHead, Faint, 56 * s, yHead, 76 * s, 18 * s, TextFormatFlags.Right);
        Draw(g, "电流", _fHead, Faint, 132 * s, yHead, 76 * s, 18 * s, TextFormatFlags.Right);
        Draw(g, "功率", _fHead, Faint, 208 * s, yHead, (int)(96 * s), 18 * s, TextFormatFlags.Right);

        // ---- 四口 ----
        for (int i = 0; i < 4; i++)
            DrawPort(g, _snap.Ports[i], 124 * s + i * 42 * s, i);

        // ---- 页脚 ----
        string foot = _snap.Error is { Length: > 0 } err
            ? err
            : _snap.At == DateTime.MinValue
                ? "等待数据…"
                : $"固件 {(_snap.Firmware.Length > 0 ? _snap.Firmware : "?")} · 更新 {_snap.At:HH:mm:ss}";
        Draw(g, foot, _fFoot, _snap.Error is { Length: > 0 } ? Bad : Faint, pad, 296 * s, Width - pad * 2, 18 * s);
    }

    /// <summary>右上角的连接状态：绿点 + 文字装在一个半透明胶囊里，比裸文字更像"状态"。</summary>
    private void DrawStatusChip(Graphics g, string text, Color dotColor, float right)
    {
        float s = _s;
        float dot = 7 * s;
        float tw = Measure(g, text, _fStatus);
        float h = 22 * s;
        float w = tw + dot + 8 * s + 9 * s;
        var rect = new Rectangle((int)(right - w), (int)(11 * s), (int)w, (int)h);

        using (var path = RoundRect(rect, (int)(h / 2)))
        {
            using (var b = new SolidBrush(Color.FromArgb(30, dotColor))) g.FillPath(b, path);
            using (var pen = new Pen(Color.FromArgb(46, dotColor))) g.DrawPath(pen, path);
        }
        using (var b = new SolidBrush(dotColor))
            g.FillEllipse(b, rect.X + 8 * s, rect.Y + (h - dot) / 2, dot, dot);
        Draw(g, text, _fStatus, Fg, rect.X + 8 * s + dot + 6 * s, rect.Y, tw + 2 * s, h);
    }

    private void DrawPort(Graphics g, PortReading p, float top, int index)
    {
        float s = _s;
        float pad = 16 * s;
        bool on = _snap.Connected && p.Enabled;
        bool working = on && p.Working;

        // 隔行一层极淡的底，四行数据扫起来不串行
        if (index % 2 == 1)
        {
            var band = new Rectangle((int)(10 * s), (int)(top - 5 * s),
                (int)(Width - 20 * s), (int)(32 * s));
            using var bandPath = RoundRect(band, (int)(8 * s));
            using var bandBrush = new SolidBrush(Color.FromArgb(11, 255, 255, 255));
            g.FillPath(bandBrush, bandPath);
        }

        // 行首一小段竖色条：颜色规则和功率数字一致，一眼看出哪路在供电
        var barColor = working ? WattColor(p.Watts) : Color.FromArgb(58, 63, 72);
        var bar = new Rectangle((int)(4 * s), (int)(top + 4 * s), (int)(3 * s), (int)(14 * s));
        using (var barPath = RoundRect(bar, (int)(1.5f * s)))
        using (var barBrush = new SolidBrush(barColor))
            g.FillPath(barBrush, barPath);

        var nameColor = working ? Fg : p.Enabled ? Faint : Color.FromArgb(80, 85, 94);
        Draw(g, p.Name, _fName, nameColor, pad, top, 40 * s, 22 * s);

        if (working)
        {
            Draw(g, $"{p.Volts:F1}", _fVal, Fg, 56 * s, top, 62 * s, 22 * s, TextFormatFlags.Right);
            Draw(g, "V", _fFoot, Faint, 56 * s + 62 * s + 1 * s, top, 12 * s, 22 * s);
            Draw(g, $"{p.Amps:F2}", _fVal, Fg, 132 * s, top, 62 * s, 22 * s, TextFormatFlags.Right);
            Draw(g, "A", _fFoot, Faint, 132 * s + 62 * s + 1 * s, top, 12 * s, 22 * s);
            Draw(g, $"{p.Watts:F1}", _fValBold, WattColor(p.Watts), 200 * s, top, (int)(92 * s), 22 * s, TextFormatFlags.Right);
            Draw(g, "W", _fFoot, Faint, Width - pad - 8 * s, top, 12 * s, 22 * s);
        }
        else
        {
            string hint = !p.Enabled ? "已关闭" : p.HasData ? "空闲" : "—";
            Draw(g, hint, _fVal, Color.FromArgb(95, 100, 109), 56 * s, top, Width - pad - 56 * s, 22 * s, TextFormatFlags.Right);
        }
    }

    // 取色和格式化都在 WattStyle 里，任务栏小窗用的是同一份规则
    private static Color WattColor(double w) => WattStyle.Port(w);
    private static Color TotalColor(double w) => WattStyle.Total(w);
    private static string FormatWatts(double w) => WattStyle.Format(w);

    private void Draw(Graphics g, string text, Font font, Color color,
        float x, float y, float w, float h, TextFormatFlags align = TextFormatFlags.Left)
    {
        TextRenderer.DrawText(g, text, font,
            new Rectangle((int)x, (int)y, (int)w, (int)h), color,
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.EndEllipsis);
    }

    private static float Measure(Graphics g, string text, Font font) =>
        TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---- 交互：点空白处拖动、Esc 关闭、失焦自动收起 ----

    private bool _pinHover;
    private ToolTip? _tip;

    /// <summary>图钉很小，鼠标划过去时用气泡把它的作用说清楚。</summary>
    private void ShowPinTip()
    {
        _tip ??= new ToolTip { InitialDelay = 200, AutoPopDelay = 3000, ShowAlways = true };
        var r = PinButtonRect;
        _tip.Show(Pinned ? "取消固定（收回到托盘）" : "固定到桌面，一直留着", this, r.Right - r.Width / 2, r.Bottom + 4, 2500);
    }

    private Rectangle PinButtonRect
    {
        get
        {
            int size = (int)(20 * _s);
            return new Rectangle(Width - (int)(16 * _s) - size, (int)(10 * _s), size, size);
        }
    }

    /// <summary>按配置恢复固定的状态（不触发事件）。</summary>
    public void RestorePinned(bool value)
    {
        if (Pinned == value) return;
        Pinned = value;
        SuppressAutoHide = value;
    }

    /// <summary>收起面板，并保证它不会带着「置顶」状态躲到后台，下次弹出来又压在别人上面。</summary>
    public void HideNow(string why)
    {
        if (!Visible)
        {
            if (TopMost) TopMost = false;
            return;
        }
        LastAutoHiddenAt = DateTime.UtcNow;
        if (TopMost) TopMost = false;
        Hide();
        LogSink.Write($"[panel] hide ({why})");
    }

    /// <summary>锁屏 / 切换用户 / 远程断开：没人看着的时候浮层没必要留着。</summary>
    public void OnSessionLocked()
    {
        if (!Pinned) HideNow("session-lock");
    }

    /// <summary>
    /// 看门狗。两条兜底：
    /// 1) 每 350ms 摆正一次置顶状态 —— 不管哪条分支漏了，这里都会把「不该置顶的」按下去；
    /// 2) 浮层该收了却没收到失焦事件（锁屏、别的程序抢前台）→ 连续两次确认没人看它，就自己收起来。
    /// </summary>
    private void GuardTick()
    {
        if (!Visible || Pinned || SuppressAutoHide) { _awayTicks = 0; return; }

        var alive = DateTime.UtcNow - _shownAt;
        if (AutoHideAfterMs > 0 && alive.TotalMilliseconds > AutoHideAfterMs) { HideNow("ttl"); return; }
        if (alive.TotalMilliseconds < 400) { _awayTicks = 0; return; }
        if (IsOwnWindow(Native.GetForegroundWindow())) { _awayTicks = 0; return; }

        // 连续两拍（~700ms）都不在前台才收，避免刚 Show 出来那一瞬间的抢前台竞态把面板闪没。
        if (++_awayTicks < 2) return;
        _awayTicks = 0;
        HideNow("guard");
    }

    /// <summary>前台窗口是不是本程序自己的（面板、右键菜单、向导都算）。</summary>
    private static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static class Native
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }

    /// <summary>切换"固定到桌面"。</summary>
    public void TogglePin()
    {
        Pinned = !Pinned;
        SuppressAutoHide = Pinned;
        if (Pinned) _shownAt = DateTime.UtcNow;
        PinChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (PinButtonRect.Contains(e.Location))
        {
            TogglePin();
            return;
        }
        _dragging = true;
        _dragOffset = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hover = PinButtonRect.Contains(e.Location);
        if (hover != _pinHover)
        {
            _pinHover = hover;
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            Invalidate(PinButtonRect);
            if (hover) ShowPinTip();
            else _tip?.Hide(this);
        }
        if (_dragging)
            Location = new Point(Location.X + e.X - _dragOffset.X, Location.Y + e.Y - _dragOffset.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!SuppressAutoHide && (DateTime.UtcNow - _shownAt).TotalMilliseconds > 350)
            HideNow("deactivate");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { HideNow("esc"); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// 按当前位置原样显示（固定模式下用，不要重新定位）。
    /// 已经显示着但被别的窗口盖住时，把它叫到前面一次——跟点任务栏上的窗口是一个道理；
    /// 但它不置顶，你随手点一下别的窗口就又把它盖回去了。
    /// </summary>
    public void ShowAtCurrent()
    {
        _shownAt = DateTime.UtcNow;
        if (Visible)
        {
            Activate();
        }
        else
        {
            _wantActivate = !Pinned;
            Show();
        }
        Invalidate();
    }

    public void ShowNear(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Clamp(anchor.X - Width / 2, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - Width - 8));
        int y = anchor.Y - Height - 8;
        if (y < wa.Top + 8) y = Math.Min(anchor.Y + 12, Math.Max(wa.Top + 8, wa.Bottom - Height - 8));
        Location = new Point(x, y);
        _shownAt = DateTime.UtcNow;
        _wantActivate = !Pinned;
        Show();
        Invalidate();
    }
}
