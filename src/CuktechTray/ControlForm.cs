using System.Drawing.Drawing2D;

namespace CuktechTray;

/// <summary>
/// 控制页：四路端口的实时读数 + 每一路的通断开关。
///
/// 刻意做成独立窗口，不塞进 <see cref="PanelForm"/> 那个「看一眼就走」的浮层里：
/// 浮层跟着前台窗口自适隐，改设置要反复点，放在浮层里会很难用。
/// 视觉仍然沿用面板的一套：深色渐变 + 无边框 + 圆角 + Microsoft YaHei UI。
///
/// 依赖全部从构造函数注入（不使用静态单例），托盘程序里一行 new 就能用。
/// 内部所有命令都走 <see cref="DeviceClient"/> 的异步接口，UI 线程直接调用即可。
/// </summary>
internal sealed class ControlForm : Form
{
    // ---- 设计尺寸（96 DPI 下的逻辑像素，实际按 DeviceDpi 缩放）----
    private const int DesignW = 372;
    private const int DesignH = 458;

    private const int RowTop = 60;     // 第一行端口卡片的 y
    private const int RowStep = 84;    // 行间距
    private const int RowHeight = 76;  // 卡片高度
    private const int FooterY = 396;   // 底部按钮行 y

    // ---- 配色：与 PanelForm 保持一致 ----
    private static readonly Color BgTop = Color.FromArgb(34, 38, 45);
    private static readonly Color BgBottom = Color.FromArgb(20, 23, 28);
    private static readonly Color Border = Color.FromArgb(58, 64, 74);
    private static readonly Color Fg = Color.FromArgb(232, 234, 237);
    private static readonly Color Dim = Color.FromArgb(154, 160, 166);
    private static readonly Color Faint = Color.FromArgb(110, 117, 126);
    private static readonly Color Ok = Color.FromArgb(52, 211, 153);
    private static readonly Color Warn = Color.FromArgb(251, 191, 36);
    private static readonly Color Bad = Color.FromArgb(248, 113, 113);

    private static readonly Color CardOn = Color.FromArgb(44, 49, 59);
    private static readonly Color CardOff = Color.FromArgb(37, 41, 49);
    private static readonly Color BtnFill = Color.FromArgb(46, 52, 62);
    private static readonly Color BtnFillHover = Color.FromArgb(56, 63, 75);

    // ---- 命中测试的标识 ----
    private const int HitNone = -1;
    private const int HitClose = 100;
    private const int HitRefresh = 101;
    private const int HitAuto = 102;

    private enum StatusKind { None, Busy, Ok, Bad }

    private readonly DeviceClient _client;
    private readonly Settings _settings;
    private readonly List<Font> _fonts = new();

    private Font _fTitle = null!, _fSub = null!, _fName = null!, _fVal = null!,
                 _fBtn = null!, _fFoot = null!;

    private float _s = 1f;

    private Snapshot _snap;

    private bool _busy;                 // 有命令正在下发，防止连点
    private bool _autoRefresh = true;   // 自动刷新：跟随设备每秒的读数
    private int _hover = HitNone;

    private string _status = "";
    private StatusKind _statusKind = StatusKind.None;

    private bool _dragging;
    private bool _dragMoved;
    private Point _downAt;
    private Point _grabScreen;
    private Point _grabOrigin;

    /// <summary>
    /// 创建控制页。
    /// </summary>
    /// <param name="client">后台设备客户端（托盘程序共用的那一个，不要另建）。</param>
    /// <param name="settings">当前设置，仅用于显示轮询间隔等提示文字。</param>
    public ControlForm(DeviceClient client, Settings settings)
    {
        _client = client;
        _settings = settings;
        _snap = client.Last;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = true;      // 独立窗口，用户要能在任务栏/Alt+Tab 里找回来
        TopMost = false;           // 不做置顶窗口，避免压住别人的窗口
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 27, 32);
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        KeyPreview = true;

        ApplyScale();
        _client.Updated += OnSnapshot;
    }

    /// <summary>自动刷新：开启时跟随设备每秒推送的读数；关闭后只有点「刷新」才更新界面。</summary>
    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (_autoRefresh == value) return;
            _autoRefresh = value;
            if (value) _snap = _client.Last;
            Invalidate();
        }
    }

    /// <summary>在给定屏幕坐标附近弹出（会自动夹到屏幕工作区内）。</summary>
    public void ShowNear(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = anchor.X - Width / 2;
        int y = anchor.Y - Height - S(12);
        if (y < wa.Top + S(8)) y = anchor.Y + S(20);
        x = Math.Max(wa.Left + S(8), Math.Min(x, wa.Right - Width - S(8)));
        y = Math.Max(wa.Top + S(8), Math.Min(y, wa.Bottom - Height - S(8)));
        Location = new Point(x, y);
        Show();
        Activate();
    }

    /// <summary>在鼠标位置附近弹出（托盘菜单点击时用）。</summary>
    public void ShowAtCurrent() => ShowNear(Cursor.Position);

    // ==================== 与 DeviceClient 的交互 ====================

    private void OnSnapshot(Snapshot snap)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<Snapshot>(OnSnapshot), snap); }
            catch (InvalidOperationException) { /* 窗口正在销毁 */ }
            return;
        }
        if (!_autoRefresh) return; // 关掉自动刷新时，界面只认「刷新」按钮
        _snap = snap;
        Invalidate();
    }

    /// <summary>点某一路的开关。</summary>
    private async Task ToggleAsync(int index)
    {
        if (_busy)
        {
            SetStatus("上一条命令还在下发，请稍等", StatusKind.Busy);
            return;
        }

        var port = _snap.Ports[index];
        bool want = !port.Enabled;
        string act = want ? "打开" : "关闭";

        if (!_client.IsReady)
        {
            SetStatus($"还没连上充电器，无法{act} {port.Name}（等状态变成「已连接」再试）", StatusKind.Bad);
            return;
        }

        _busy = true;
        SetStatus($"正在{act} {port.Name} …", StatusKind.Busy);
        Invalidate();
        try
        {
            var res = await _client.SetPortEnabledAsync(index, want);
            if (res.Ok)
                SetStatus($"{port.Name} 已{act}（设备回读 0x{_client.PortSwitch:x2}）", StatusKind.Ok);
            else
                SetStatus($"{port.Name} {act}失败：{res.Error}", StatusKind.Bad);
        }
        catch (Exception ex)
        {
            // 兜底：控制命令本来不会抛，但绝不能让异常把窗口打死
            SetStatus($"{port.Name} {act}失败：{ex.Message}", StatusKind.Bad);
        }
        finally
        {
            _busy = false;
            _snap = _client.Last;
            Invalidate();
        }
    }

    /// <summary>点「刷新」按钮。</summary>
    private async Task RefreshNowAsync()
    {
        if (_busy)
        {
            SetStatus("上一条命令还在下发，请稍等", StatusKind.Busy);
            return;
        }

        _busy = true;
        SetStatus("正在读取 …", StatusKind.Busy);
        Invalidate();
        try
        {
            var res = await _client.RefreshAsync();
            if (res.Ok)
                SetStatus($"已刷新（端口开关 0x{_client.PortSwitch:x2}）", StatusKind.Ok);
            else
                SetStatus("刷新失败：" + res.Error, StatusKind.Bad);
        }
        catch (Exception ex)
        {
            SetStatus("刷新失败：" + ex.Message, StatusKind.Bad);
        }
        finally
        {
            _busy = false;
            _snap = _client.Last;
            Invalidate();
        }
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _status = text;
        _statusKind = kind;
        Invalidate();
    }

    // ==================== 缩放与尺寸 ====================

    private int S(float v) => (int)Math.Round(v * _s);

    private void ApplyScale()
    {
        foreach (var f in _fonts) f.Dispose();
        _fonts.Clear();

        _s = Math.Max(1f, DeviceDpi / 96f);
        ClientSize = new Size(S(DesignW), S(DesignH));

        _fTitle = F("Microsoft YaHei UI", 14f, FontStyle.Bold);
        _fSub = F("Microsoft YaHei UI", 10.5f);
        _fName = F("Segoe UI", 16f, FontStyle.Bold);
        _fVal = F("Segoe UI", 12.5f);
        _fBtn = F("Microsoft YaHei UI", 11.5f);
        _fFoot = F("Microsoft YaHei UI", 10.5f);

        using var path = RoundRect(new Rectangle(0, 0, Width, Height), S(14));
        Region = new Region(path);
    }

    private Font F(string family, float px, FontStyle style = FontStyle.Regular)
    {
        Font f;
        try { f = new Font(family, px * _s, style, GraphicsUnit.Pixel); }
        catch { f = new Font(FontFamily.GenericSansSerif, px * _s, style, GraphicsUnit.Pixel); }
        _fonts.Add(f);
        return f;
    }

    private static GraphicsPath RoundRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyScale();
        Invalidate();
    }

    // ==================== 命中区域 ====================

    private Rectangle CloseRect() => new(Width - S(44), S(13), S(26), S(26));

    private Rectangle RowRect(int i) => new(S(14), S(RowTop) + i * S(RowStep), Width - S(28), S(RowHeight));

    private Rectangle SwitchRect(int i)
    {
        var r = RowRect(i);
        var sw = new Size(S(50), S(28));
        return new Rectangle(r.Right - S(14) - sw.Width, r.Y + (r.Height - sw.Height) / 2, sw.Width, sw.Height);
    }

    private Rectangle RefreshRect() => new(S(14), S(FooterY), S(84), S(30));

    private Rectangle AutoHitRect() => new(S(110), S(FooterY - 2), S(130), S(34));

    private int HitTest(Point p)
    {
        if (CloseRect().Contains(p)) return HitClose;
        if (RefreshRect().Contains(p)) return HitRefresh;
        if (AutoHitRect().Contains(p)) return HitAuto;
        for (int i = 0; i < _snap.Ports.Length; i++)
                if (SwitchRect(i).Contains(p)) return i;
        return HitNone;
    }

    // ==================== 绘制 ====================

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var outer = new Rectangle(0, 0, Width, Height);
        using (var path = RoundRect(outer, S(14)))
        using (var fill = new LinearGradientBrush(outer, BgTop, BgBottom, 90f))
        {
            g.FillPath(fill, path);
            using var pen = new Pen(Border);
            g.DrawPath(pen, path);
        }

        PaintHeader(g);

        for (int i = 0; i < _snap.Ports.Length && i < 4; i++) PaintRow(g, i);

        PaintFooter(g);
    }

    private void PaintHeader(Graphics g)
    {
        Draw(g, "控制台", _fTitle, Fg, S(18), S(12));

        string sub;
        Color subColor;
        if (_snap.Connected)
        {
            sub = $"已连接 · {_snap.Firmware} · 总输出 {_snap.TotalWatts:0.0}W";
            subColor = Dim;
        }
        else
        {
            sub = string.IsNullOrEmpty(_snap.Error) ? "未连接" : "未连接 · " + _snap.Error!;
            subColor = Faint;
        }
        // 副标题前一颗状态点：绿=连着，灰=没连。比只改文字颜色更早被眼睛抓到
        using (var b = new SolidBrush(_snap.Connected ? Ok : Faint))
        {
            int d = S(6);
            g.FillEllipse(b, S(19), S(34) + (_fSub.Height - d) / 2, d, d);
        }
        Draw(g, sub, _fSub, subColor, S(31), S(34), Width - S(92));

        // 关闭按钮
        var c = CloseRect();
        bool hot = _hover == HitClose;
        using (var path = RoundRect(c, S(7)))
        using (var b = new SolidBrush(hot ? Color.FromArgb(58, 36, 38) : Color.FromArgb(38, 43, 52)))
        {
            g.FillPath(b, path);
            using var pen = new Pen(hot ? Bad : Border);
            g.DrawPath(pen, path);
        }
        using (var pen = new Pen(hot ? Bad : Dim, Math.Max(1.4f, S(1.4f))))
        {
            int pad = S(9);
            g.DrawLine(pen, c.Left + pad, c.Top + pad, c.Right - pad, c.Bottom - pad);
            g.DrawLine(pen, c.Right - pad, c.Top + pad, c.Left + pad, c.Bottom - pad);
        }
    }

    private void PaintRow(Graphics g, int i)
    {
        var r = RowRect(i);
        var p = _snap.Ports[i];
        bool on = p.Enabled;

        using (var path = RoundRect(r, S(10)))
        {
            using (var b = new SolidBrush(on ? CardOn : CardOff)) g.FillPath(b, path);
            using var pen = new Pen(_hover == i ? Color.FromArgb(84, 92, 106) : Color.FromArgb(54, 60, 70));
            g.DrawPath(pen, path);
        }

        // 卡片左边一条状态色：和面板里的行首色条是同一套语言
        Color accent = !on ? Color.FromArgb(70, 76, 86)
                     : !p.HasData ? Dim
                     : p.Idle ? Dim
                     : Ok;
        using (var barPath = RoundRect(new Rectangle(r.X + S(5), r.Y + S(12), S(3), r.Height - S(24)), S(2)))
        using (var barBrush = new SolidBrush(accent))
            g.FillPath(barBrush, barPath);

        // 端口名
        Draw(g, p.Name, _fName, on ? Fg : Faint, r.X + S(14), r.Y + S(10));

        // 状态词
        string state;
        Color stateColor;
        if (!on) { state = "已断电"; stateColor = Faint; }
        else if (!p.HasData) { state = "读数中…"; stateColor = Dim; }
        else if (p.Idle) { state = "空载"; stateColor = Dim; }
        else { state = "输出中"; stateColor = Ok; }
        Draw(g, state, _fSub, stateColor, r.X + S(58), r.Y + S(16));

        // 数值行
        string vals;
        if (!on) vals = "开关已关闭 · 该口无输出";
        else if (!p.HasData) vals = "等待读数";
        else vals = $"{p.Volts:0.0}V · {p.Amps:0.00}A · {p.Watts:0.0}W";
        Draw(g, vals, _fVal, on && p.HasData && !p.Idle ? Fg : Dim, r.X + S(14), r.Y + S(41), r.Width - S(96));

        PaintSwitch(g, SwitchRect(i), on, _hover == i);
    }

    private void PaintSwitch(Graphics g, Rectangle r, bool on, bool hover)
    {
        bool canWrite = _client.IsReady && !_busy;
        Color onColor = canWrite ? Ok : Color.FromArgb(52, 92, 78);
        Color offColor = canWrite ? Color.FromArgb(72, 78, 90) : Color.FromArgb(52, 56, 64);
        Color fill = on ? onColor : offColor;

        using (var path = RoundRect(r, r.Height / 2))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (hover)
            {
                using var pen = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, S(1f)));
                g.DrawPath(pen, path);
            }
        }

        int d = r.Height - S(6);
        int kx = on ? r.Right - d - S(3) : r.X + S(3);
        using (var knob = new SolidBrush(Color.FromArgb(240, 244, 246)))
            g.FillEllipse(knob, kx, r.Y + S(3), d, d);

        // 开关上的「开 / 关」字样
        string label = on ? "开" : "关";
        var labelColor = on ? Color.FromArgb(18, 40, 32) : Color.FromArgb(220, 226, 232);
        int lw = on ? r.Width - d - S(10) : r.Width - d - S(10);
        int lx = on ? r.X + S(9) : r.X + d + S(8);
        Draw(g, label, _fSub, labelColor, lx, r.Y + (r.Height - _fSub.Height) / 2, Math.Max(S(10), lw));
    }

    private void PaintFooter(Graphics g)
    {
        // 刷新按钮
        var rr = RefreshRect();
        bool hot = _hover == HitRefresh;
        using (var path = RoundRect(rr, S(8)))
        {
            using (var b = new SolidBrush(hot ? BtnFillHover : BtnFill)) g.FillPath(b, path);
            using var pen = new Pen(Border);
            g.DrawPath(pen, path);
        }
        var ts = TextRenderer.MeasureText("刷新", _fBtn,
            new Size(rr.Width, rr.Height), TextFormatFlags.NoPadding);
        Draw(g, "刷新", _fBtn, _busy ? Faint : Fg,
            rr.X + (rr.Width - ts.Width) / 2, rr.Y + (rr.Height - ts.Height) / 2);

        // 自动刷新勾选框
        var box = new Rectangle(S(116), S(FooterY + 8), S(16), S(16));
        using (var path = RoundRect(box, S(4)))
        {
            using (var b = new SolidBrush(Color.FromArgb(30, 34, 41))) g.FillPath(b, path);
            using var pen = new Pen(_hover == HitAuto ? Color.FromArgb(120, 128, 140) : Border);
            g.DrawPath(pen, path);
        }
        if (_autoRefresh)
        {
            using var pen = new Pen(Ok, Math.Max(1.6f, S(1.6f)));
            g.DrawLines(pen, new[]
            {
                new Point(box.Left + S(4), box.Top + S(8)),
                new Point(box.Left + S(7), box.Top + S(11)),
                new Point(box.Left + S(12), box.Top + S(5)),
            });
        }
        Draw(g, "自动刷新", _fSub, _hover == HitAuto ? Fg : Dim, S(140), S(FooterY + 8));

        // 状态行
        string line;
        Color color;
        if (_status.Length > 0) { line = _status; color = StatusColor(_statusKind); }
        else
        {
            int sec = Math.Max(1, _settings.PollMs / 1000);
            line = $"每 {sec} 秒自动刷新 · 开关立即生效，互不影响";
            color = Faint;
        }
        Draw(g, line, _fFoot, color, S(16), S(436), Width - S(32));
    }

    private Color StatusColor(StatusKind kind) => kind switch
    {
        StatusKind.Ok => Ok,
        StatusKind.Bad => Bad,
        StatusKind.Busy => Warn,
        _ => Dim,
    };

    private void Draw(Graphics g, string s, Font f, Color c, int x, int y, int maxW = 0)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (maxW <= 0)
        {
            TextRenderer.DrawText(g, s, f, new Point(x, y), c, flags);
            return;
        }
        var size = new Size(maxW, f.Height + S(4));
        TextRenderer.DrawText(g, s, f, new Rectangle(new Point(x, y), size), c, flags);
    }

    // ==================== 鼠标 / 键盘 ====================

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int hit = HitTest(e.Location);
        _downAt = e.Location;
        _dragMoved = false;
        _dragging = hit == HitNone;   // 只有点在空白/卡片上才拖窗口，按钮区域不拖
        _grabScreen = PointToScreen(e.Location);
        _grabOrigin = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging && e.Button == MouseButtons.Left)
        {
            var now = PointToScreen(e.Location);
            if (!_dragMoved && Math.Abs(now.X - _grabScreen.X) + Math.Abs(now.Y - _grabScreen.Y) > S(4))
                _dragMoved = true;
            if (_dragMoved)
                Location = new Point(_grabOrigin.X + now.X - _grabScreen.X,
                                     _grabOrigin.Y + now.Y - _grabScreen.Y);
            return;
        }

        int hover = HitTest(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover == HitNone ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;

        bool wasDrag = _dragMoved;
        _dragging = false;
        _dragMoved = false;
        if (wasDrag) return;   // 拖窗口结束，不当成点击

        switch (HitTest(e.Location))
        {
            case HitClose:
                Close();
                break;
            case HitRefresh:
                _ = RefreshNowAsync();
                break;
            case HitAuto:
                AutoRefresh = !_autoRefresh;
                SetStatus(_autoRefresh ? "自动刷新已开启（跟随设备读数）" : "自动刷新已关闭（只能手点「刷新」）",
                          StatusKind.None);
                break;
            default:
                int hit = HitTest(e.Location);
                if (hit is >= 0 and <= 3) _ = ToggleAsync(hit);
                break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == HitNone) return;
        _hover = HitNone;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _client.Updated -= OnSnapshot; } catch { }
            foreach (var f in _fonts) f.Dispose();
            _fonts.Clear();
        }
        base.Dispose(disposing);
    }
}
