using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using CuktechLogin;

namespace CuktechTray;

/// <summary>
/// 扫码配对向导（0.4.0 原生化重做）。0.3.3 那份是 WinForms 的 SetupWizardForm，
/// 搬到 NativeAOT 外壳后只能自己画：WinBase 已经提供了双缓冲绘制、DPI 缩放、
/// 圆角 region 这些基础设施，这里只负责排版和状态机。
///
/// 三步：欢迎页 → 二维码（米家 App 扫）→ 设备列表挑一台 → 写 settings.json。
/// 全程只读设备列表里的 Mac / token，别的账号信息用完就丢（session.json 除外，
/// 和 0.3.3 一样留着，下次重新配置可以少扫一次）。
/// </summary>
internal sealed class WizardWindow : WinBase
{
    private const int DesignW = 460;
    private const int DesignH = 520;
    private const int PadX = 26;
    private const int BtnH = 42;
    private const int BtnY = DesignH - 26 - BtnH;   // 452
    private const int ItemH = 50;
    private const int ItemGap = 8;
    private const int ListTop = 96;
    private const int QrBox = 240;

    private enum Stage { Welcome, Scanning, Picking }

    // 配色跟面板保持一致（深色系，读数字的界面不该刺眼）
    private static readonly Color BgTop = Color.FromArgb(34, 38, 45);
    private static readonly Color BgBottom = Color.FromArgb(20, 23, 28);
    private static readonly Color BorderColor = Color.FromArgb(58, 64, 74);
    private static readonly Color Fg = Color.FromArgb(232, 234, 237);
    private static readonly Color Dim = WattStyle.Dim;
    private static readonly Color Faint = Color.FromArgb(110, 117, 126);
    private static readonly Color Ok = WattStyle.Ok;
    private static readonly Color Bad = WattStyle.Bad;
    private static readonly Color CardBg = Color.FromArgb(30, 35, 43);
    private static readonly Color CardBgHover = Color.FromArgb(38, 44, 54);

    private const int HitNone = -1;
    private const int HitPrimary = 1;
    private const int HitSecondary = 2;
    private const int HitTertiary = 3;
    private const int HitClose = 4;
    private const int HitPrivacy = 5;
    private const int HitListBase = 100;

    private readonly Settings _settings;
    private readonly UiDispatcher _ui;

    private Stage _stage = Stage.Welcome;
    private string _status = "";
    private Color _statusColor = Dim;
    private bool _busy;
    private bool _demo;

    private byte[]? _qrPng;
    private Image? _qrImage;
    private DateTime _qrDeadline;
    private readonly List<MiDevice> _devices = new();
    private int _picked = -1;

    private int _hover = HitNone;
    private int _press = HitNone;
    private bool _dragging;
    private int _dragX, _dragY;

    private CancellationTokenSource? _cts;
    private XiaomiClient? _login;

    private GdiFont _fTitle = null!, _fBody = null!, _fSmall = null!, _fBtn = null!,
                    _fItem = null!, _fItemSub = null!, _fIcon = null!;
    private float _fontBase;

    /// <summary>配置写好了（调用方据此重启 DeviceClient）。</summary>
    public event Action? Completed;
    /// <summary>用户关掉了窗户（没存任何东西）。</summary>
    public event Action? Cancelled;

    public WizardWindow(Settings settings, UiDispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        CreateWin(DesignW, DesignH, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, IntPtr.Zero, "wizard");
        if (Hwnd == IntPtr.Zero)
        {
            LogSink.Write("[wizard] CreateWin 失败 err=" + Marshal.GetLastWin32Error());
            return;
        }
        ApplyScale();
        SetRoundedRegion(S(14));
        LogSink.Write("[wizard] 建立 ok hwnd=" + Hwnd + " scale=" + Scale.ToString("F2"));
    }

    // 跟面板一样用截断：1.25 倍下 6*1.25=7.5 必须落到 7，否则间距会一格格错开
    private int S(float v) => (int)(v * Scale);

    private void ApplyScale()
    {
        DisposeFonts();
        float s = Scale;
        float fb = _fontBase > 0f ? _fontBase : s;
        _fTitle = GdiFont.Create("Microsoft YaHei UI", 13, s, bold: true, fontBase: fb);
        _fBody = GdiFont.Create("Microsoft YaHei UI", 11.5f, s, fontBase: fb);
        _fSmall = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fBtn = GdiFont.Create("Microsoft YaHei UI", 11.5f, s, bold: true, fontBase: fb);
        _fItem = GdiFont.Create("Microsoft YaHei UI", 12, s, bold: true, fontBase: fb);
        _fItemSub = GdiFont.Create("Microsoft YaHei UI", 10.5f, s, fontBase: fb);
        _fIcon = GdiFont.Create("Segoe MDL2 Assets", 12, s, fontBase: fb);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, S(DesignW), S(DesignH),
            Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Invalidate();
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fBody?.Dispose(); _fSmall?.Dispose(); _fBtn?.Dispose();
        _fItem?.Dispose(); _fItemSub?.Dispose(); _fIcon?.Dispose();
    }

    protected override void OnDpiChanged(float scale, Native.RECT suggested)
    {
        ApplyScale();
        SetRoundedRegion(S(14));
    }

    // ==================== 显示 ====================

    public void ShowWizard()
    {
        if (!IsValid) return;
        int w = S(DesignW), h = S(DesignH);
        int x = 120, y = 80;
        if (Native.GetCursorPos(out var pt))
        {
            IntPtr mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfoW(mon, ref mi))
            {
                var wa = mi.rcWork;
                x = wa.Left + Math.Max(0, (wa.Right - wa.Left - w) / 2);
                y = wa.Top + Math.Max(0, (wa.Bottom - wa.Top - h) / 2);
            }
        }
        Native.SetWindowPos(Hwnd, new IntPtr(Native.HWND_TOPMOST), x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Native.ShowWindow(Hwnd, Native.SW_SHOW);
        Native.SetForegroundWindow(Hwnd);
        Native.SetFocus(Hwnd);
        LogSink.Write("[wizard] show at " + x + "," + y + " " + w + "x" + h + " vis=" + Native.IsWindowVisible(Hwnd));

        // 之前扫过码就存在 session.json 里：能直接拉设备列表，省一步扫码。
        // 过期/失效的 session 会抛异常，那就当没存过，老老实实扫码。
        if (_stage == Stage.Welcome) TrySessionFastPath();
    }

    private void TrySessionFastPath()
    {
        string path = SessionPath();
        if (!File.Exists(path)) return;
        var login = new XiaomiClient();
        _login = login;
        login.Log += msg => _ui.Post(_ => SetStatus(msg, Dim), null);
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _stage = Stage.Scanning;
        _busy = true;
        SetStatus("正在用上次的登录状态读设备…", Dim);
        _ = Task.Run(async () =>
        {
            try
            {
                login.LoadSession(path);
                if (!login.IsLoggedIn) { _ui.Post(_ => StartScan(), null); return; }
                var list = await login.GetDevicesAsync(ct).ConfigureAwait(false);
                _ui.Post(_ => OnDevices(list, "上次的登录还能用，没让你再扫一次"), null);
            }
            catch (OperationCanceledException) { }
            catch
            {
                // session 过期是最常见的情况，不当错误，直接转扫码
                _ui.Post(_ => StartScan(), null);
            }
        }, ct);
    }

    // ==================== 扫码流程 ====================

    private void StartScan()
    {
        if (!IsValid || _stage == Stage.Scanning && _busy && _qrImage != null) return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _stage = Stage.Scanning;
        _busy = true;
        _qrPng = null;
        _qrImage?.Dispose();
        _qrImage = null;
        _devices.Clear();
        _picked = -1;
        _hover = HitNone;
        SetStatus("正在申请二维码…", Dim);

        // 每次重扫都新建客户端：旧的里面可能留着没扫完的 cookie
        var login = new XiaomiClient();
        login.Log += msg => _ui.Post(_ => SetStatus(msg, Dim), null);
        _login?.Dispose();
        _login = login;

        _ = Task.Run(async () =>
        {
            try
            {
                var qr = await login.RequestQrAsync(ct).ConfigureAwait(false);
                byte[] png = await login.DownloadQrAsync(qr, ct).ConfigureAwait(false);
                Image? img = Decode(png);
                _ui.Post(_ =>
                {
                    _qrPng = png;
                    _qrImage = img;
                    _qrDeadline = DateTime.UtcNow + qr.Ttl;
                    _busy = false;
                    SetStatus("等着你扫…（" + Math.Max(1, (int)qr.Ttl.TotalMinutes) + " 分钟内有效）", Dim);
                }, null);

                bool scanned = await login.PollUntilScannedAsync(qr, ct).ConfigureAwait(false);
                if (!scanned)
                {
                    _ui.Post(_ =>
                    {
                        _busy = false;
                        SetStatus("二维码过期了，点「重新扫码」再试一次", Bad);
                    }, null);
                    return;
                }
                _ui.Post(_ => SetStatus("扫到了，正在读设备列表…", Dim), null);
                var list = await login.GetDevicesAsync(ct).ConfigureAwait(false);
                _ui.Post(_ => OnDevices(list, "扫码成功"), null);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _ui.Post(_ =>
                {
                    _busy = false;
                    SetStatus("扫码失败：" + Short(ex.Message), Bad);
                }, null);
            }
        }, ct);
    }

    private void OnDevices(IReadOnlyList<MiDevice> list, string why)
    {
        _devices.Clear();
        if (list is not null)
        {
            foreach (var d in list)
            {
                if (d is null) continue;
                if (string.IsNullOrEmpty(d.Mac)) continue;
                if (d.TokenHex is null || d.TokenHex.Length != 24) continue;   // 读不到 token 的连上也控制不了
                _devices.Add(d);
            }
        }
        // 酷态科的排前面：用户要连的就是那台，别让他从一堆灯泡插座里翻
        _devices.Sort((a, b) => b.IsCuktech.CompareTo(a.IsCuktech));
        _busy = false;
        _picked = _devices.Count > 0 ? 0 : -1;
        if (_devices.Count == 0)
        {
            _stage = Stage.Picking;
            SetStatus("这个账号下没有读到能控制的设备", Bad);
            return;
        }
        _stage = Stage.Picking;
        SetStatus(why + "，共 " + _devices.Count + " 台可用", Dim);

        string sessionPath = SessionPath();
        try { _login?.SaveSession(sessionPath); }
        catch (Exception ex) { LogSink.Write("[wizard] 保存 session 失败: " + ex.Message); }
    }

    private void Accept()
    {
        if (_picked < 0 || _picked >= _devices.Count) return;
        var d = _devices[_picked];
        _settings.Mac = d.Mac;
        _settings.TokenHex = d.TokenHex;
        try { _settings.Save(); }
        catch (Exception ex)
        {
            LogSink.Write("[wizard] 保存 settings 失败: " + ex.Message);
            SetStatus("写配置文件失败：" + Short(ex.Message), Bad);
            return;
        }
        LogSink.Write("[wizard] 已配置 " + d.Name + " mac=" + d.Mac);
        _cts?.Cancel();
        DoClose(true);
    }

    private static string SessionPath()
        => Path.Combine(AppContext.BaseDirectory, "session.json");

    private static Image? Decode(byte[] png)
    {
        if (png is null || png.Length == 0) return null;
        try
        {
            var ms = new MemoryStream(png, writable: false);
            return Image.FromStream(ms);
        }
        catch (Exception ex)
        {
            LogSink.Write("[wizard] 二维码解码失败: " + ex.Message);
            return null;
        }
    }

    private static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "未知错误";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
    }

    private void SetStatus(string text, Color color)
    {
        _status = text ?? "";
        _statusColor = color;
        Invalidate();
    }

    // ==================== 交互 ====================

    private Rectangle PrimaryRect() => _stage switch
    {
        Stage.Picking => new Rectangle(S(PadX), S(BtnY), S(180), S(BtnH)),
        _ => new Rectangle(S(PadX), S(BtnY), S(240), S(BtnH)),
    };

    private Rectangle SecondaryRect() => _stage switch
    {
        Stage.Picking => new Rectangle(S(218), S(BtnY), S(110), S(BtnH)),
        _ => new Rectangle(S(282), S(BtnY), S(152), S(BtnH)),
    };

    private Rectangle TertiaryRect() => new Rectangle(S(340), S(BtnY), S(94), S(BtnH));

    private Rectangle CloseRect() => new Rectangle(S(DesignW - PadX - 24), S(18), S(24), S(24));

    private Rectangle PrivacyRect() => new Rectangle(S(PadX), S(BtnY - 96), S(DesignW - PadX * 2), S(24));

    /// <summary>
    /// 隐私说明全文。用系统 MessageBox 而不是自绘弹窗：这是一段要用户慢慢读、
    /// 能选中复制的文字，交给系统窗口比自绘对话框划算。内容对齐 README 的「隐私」一节。
    /// </summary>
    private void ShowPrivacy()
    {
        const string text =
            "这是第三方小工具，不是小米或酷态科官方出的。\n" +
            "\n" +
            "一、扫码时会做什么\n" +
            "· 本程序只跟小米云要一张登录二维码，你在手机米家 App 里扫码、确认。\n" +
            "· 账号、密码、验证码都在你手机上，本程序看不见，也拿不到。\n" +
            "· 小米云只返回这台充电器的 Mac 和 12 字节 token。\n" +
            "\n" +
            "二、数据留在哪里\n" +
            "· 只写进本机的 settings.json。便携版就在 exe 同目录，\n" +
            "  安装版在 %LOCALAPPDATA%\\CuktechTray\\settings.json。\n" +
            "· 登录会话 session.json 也留在同一目录，\n" +
            "  只是为了下次重新配置时少扫一次码。\n" +
            "\n" +
            "三、不会做的事\n" +
            "· 不上传任何数据。日常读功率走电脑和充电器之间的蓝牙，不经过网络。\n" +
            "· 只有你点「检查更新」时，才访问 GitHub 一次。\n" +
            "\n" +
            "四、想清干净\n" +
            "· 删掉 settings.json 和 session.json，或用安装程序卸载，本机就什么都不剩。\n" +
            "· 蓝牙那一侧不留任何东西，充电器自身也不存电脑的信息。";
        LogSink.Write("[wizard] 用户点了隐私说明");
        Native.MessageBoxW(Hwnd, text, "隐私说明 · 酷态科托盘", Native.MB_OK | Native.MB_ICONINFORMATION);
    }

    private Rectangle ItemRect(int i)
        => new Rectangle(S(PadX), S(ListTop + i * (ItemH + ItemGap)), S(DesignW - PadX * 2), S(ItemH));

    private int HitTest(int x, int y)
    {
        if (CloseRect().Contains(x, y)) return HitClose;
        if (_stage == Stage.Welcome && PrivacyRect().Contains(x, y)) return HitPrivacy;
        if (PrimaryRect().Contains(x, y)) return HitPrimary;
        if (SecondaryRect().Contains(x, y)) return HitSecondary;
        if (_stage == Stage.Picking && TertiaryRect().Contains(x, y)) return HitTertiary;
        if (_stage == Stage.Picking)
        {
            int max = Math.Min(_devices.Count, MaxVisible());
            for (int i = 0; i < max; i++) if (ItemRect(i).Contains(x, y)) return HitListBase + i;
        }
        return HitNone;
    }

    private static int MaxVisible() => 5;

    protected override bool WantsHandCursor(int x, int y) => HitTest(x, y) != HitNone;

    protected override void OnMouseMove(int x, int y, bool entering)
    {
        if (_dragging && Native.GetCursorPos(out var pt))
        {
            Native.SetWindowPos(Hwnd, IntPtr.Zero, pt.X - _dragX, pt.Y - _dragY, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return;
        }
        int h = HitTest(x, y);
        if (h != _hover)
        {
            _hover = h;
            Invalidate();
        }
    }

    protected override void OnMouseLeave()
    {
        if (_hover != HitNone)
        {
            _hover = HitNone;
            Invalidate();
        }
    }

    protected override void OnLeftDown(int x, int y)
    {
        _press = HitTest(x, y);
        bool titleArea = y < S(56) && _press != HitClose;
        if (titleArea && Native.GetCursorPos(out var pt))
        {
            Native.GetWindowRect(Hwnd, out var r);
            _dragX = pt.X - r.Left;
            _dragY = pt.Y - r.Top;
            _dragging = true;
        }
    }

    protected override void OnLeftUp(int x, int y)
    {
        if (_dragging)
        {
            _dragging = false;
            _press = HitNone;
            return;
        }
        int h = HitTest(x, y);
        int p = _press;
        _press = HitNone;
        if (p == HitNone || h != p) return;
        if (p == HitClose) { DoClose(false); return; }
        if (p == HitPrivacy) { ShowPrivacy(); return; }
        if (p == HitPrimary) { Primary(); return; }
        if (p == HitSecondary)
        {
            if (_stage == Stage.Picking) StartScan(); else DoClose(false);
            return;
        }
        if (p == HitTertiary) { DoClose(false); return; }
        if (p >= HitListBase)
        {
            int idx = p - HitListBase;
            if (idx >= 0 && idx < _devices.Count)
            {
                _picked = idx;
                Invalidate();
            }
        }
    }

    protected override void OnKeyDown(int vk)
    {
        if (vk == 27) DoClose(false);          // Esc
        else if (vk == 13) Primary();          // Enter
    }

    private void Primary()
    {
        switch (_stage)
        {
            case Stage.Welcome: StartScan(); break;
            case Stage.Picking: Accept(); break;
        }
    }

    protected override bool OnClose()
    {
        LogSink.Write("[wizard] WM_CLOSE");
        DoClose(false);
        return false;   // 自己销毁，别让 DefWindowProc 再走一遍
    }

    private void DoClose(bool accepted)
    {
        LogSink.Write("[wizard] close accepted=" + accepted + " valid=" + IsValid);
        _cts?.Cancel();
        if (IsValid) Native.DestroyWindow(Hwnd);
        if (accepted) Completed?.Invoke(); else Cancelled?.Invoke();
    }

    public override void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _login?.Dispose();
        _qrImage?.Dispose();
        DisposeFonts();
        base.Dispose();
    }

    // ==================== 绘制 ====================

    private Color Backdrop(int y)
    {
        int h = Math.Max(1, Height);
        float t = Math.Clamp(y / (float)h, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(BgTop.R + (BgBottom.R - BgTop.R) * t),
            (int)Math.Round(BgTop.G + (BgBottom.G - BgTop.G) * t),
            (int)Math.Round(BgTop.B + (BgBottom.B - BgTop.B) * t));
    }

    private void Text(Graphics g, string? text, GdiFont font, Color color, int x, int y, int w, int h)
    {
        if (string.IsNullOrEmpty(text)) return;
        Color c = color.A < 255 ? GdiText.Blend(color, Backdrop(y + h / 2)) : color;
        GdiText.Draw(g, text, font, c, new Rectangle(x, y, w, h), TextAnchor.Left, ellipsis: true);
    }

    private void TextC(Graphics g, string? text, GdiFont font, Color color, int x, int y, int w, int h)
    {
        if (string.IsNullOrEmpty(text)) return;
        Color c = color.A < 255 ? GdiText.Blend(color, Backdrop(y + h / 2)) : color;
        GdiText.Draw(g, text, font, c, new Rectangle(x, y, w, h), TextAnchor.Center, ellipsis: true);
    }

    /// <summary>按 \n 手动分行（GdiText.Draw 是单行 DT_VCENTER，不做自动换行）。</summary>
    private void Lines(Graphics g, string text, GdiFont font, Color color, int x, int y, int w, int lineH, int maxLines)
    {
        int cy = y;
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            if (n++ >= maxLines) break;
            if (raw.Length > 0) Text(g, raw, font, color, x, cy, w, lineH);
            cy += lineH;
        }
    }

    protected override void Paint(Graphics g)
    {
        int w = Math.Max(1, Width), h = Math.Max(1, Height);
        var all = new RectangleF(0, 0, w, h);
        FillRoundGradient(g, all, S(14), BgTop, BgBottom);
        StrokeRound(g, all, S(14), BorderColor, 1f);

        // 抬头
        string title = _stage switch
        {
            Stage.Welcome => "连接你的充电器",
            Stage.Scanning => "用米家 App 扫一下",
            _ => "选一台充电器",
        };
        Text(g, title, _fTitle, Fg, S(PadX), S(18), S(340), S(30));

        // 右上角关闭
        var cr = CloseRect();
        if (_hover == HitClose)
            FillRound(g, new RectangleF(cr.X, cr.Y, cr.Width, cr.Height), S(6), Color.FromArgb(48, 54, 64));
        GdiText.Draw(g, "\uE711", _fIcon, _hover == HitClose ? Fg : Faint, cr, TextAnchor.Center);

        switch (_stage)
        {
            case Stage.Welcome: PaintWelcome(g); break;
            case Stage.Scanning: PaintScanning(g); break;
            case Stage.Picking: PaintPicking(g); break;
        }

        if (_status.Length > 0)
            TextC(g, _status, _fSmall, _statusColor, S(PadX), S(BtnY - 30), S(DesignW - PadX * 2), S(20));

        PaintButtons(g);
    }

    private void PaintWelcome(Graphics g)
    {
        const string body =
            "用米家 App 扫一下码，就能把这台充电器接到电脑上。\n" +
            "\n" +
            "扫码只是把它自己的设备凭据（Mac 和 token）读到本机，\n" +
            "之后每次开机自动连，不用再扫。\n" +
            "\n" +
            "凭据只写在本机的 settings.json 里，不会发到任何地方。";
        Lines(g, body, _fBody, Dim, S(PadX), S(66), S(DesignW - PadX * 2), S(24), 8);

        Text(g, "支持 Windows 10 / 11，需要电脑有蓝牙", _fSmall, Faint, S(PadX), S(BtnY - 62), S(320), S(20));

        // 隐私说明入口：一句话讲清「只换一次凭据」，点开看全文
        GdiText.Draw(g, "隐私说明：扫码只为换一次设备凭据，其余全留本机（点这里看全文）", _fSmall,
            _hover == HitPrivacy ? Fg : Ok, PrivacyRect(), TextAnchor.Left, ellipsis: true);
    }

    private void PaintScanning(Graphics g)
    {
        Lines(g, "打开米家 App，点右上角 + → 扫一扫，对着下面的码。",
            _fBody, Dim, S(PadX), S(62), S(DesignW - PadX * 2), S(22), 1);

        int box = S(QrBox);
        int bx = (Math.Max(1, Width) - box) / 2;
        int by = S(104);
        var qrRect = new Rectangle(bx, by, box, box);

        if (_qrImage is not null)
        {
            using (var white = new SolidBrush(Color.White))
            using (var p = RoundRect(new RectangleF(qrRect.X, qrRect.Y, qrRect.Width, qrRect.Height), S(12)))
                g.FillPath(white, p);
            var inner = new Rectangle(qrRect.X + S(10), qrRect.Y + S(10), qrRect.Width - S(20), qrRect.Height - S(20));
            var old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            try { g.DrawImage(_qrImage, inner); }
            finally { g.InterpolationMode = old; g.PixelOffsetMode = PixelOffsetMode.HighQuality; }
        }
        else
        {
            using var p = RoundRect(new RectangleF(qrRect.X, qrRect.Y, qrRect.Width, qrRect.Height), S(12));
            using var pen = new Pen(BorderColor, 1f);
            g.DrawPath(pen, p);
            TextC(g, _busy ? "二维码马上出来…" : "二维码不在这里", _fSmall, Faint,
                qrRect.X, qrRect.Y + qrRect.Height / 2 - S(10), qrRect.Width, S(20));
        }
    }

    private void PaintPicking(Graphics g)
    {
        Lines(g, "下面只列能读到 token 的设备。挑你要连的那台，然后点「就用这台」。",
            _fBody, Dim, S(PadX), S(62), S(DesignW - PadX * 2), S(22), 1);

        if (_devices.Count == 0)
        {
            TextC(g, "没有可用的设备。先确认充电器已经在米家 App 里配对过，然后重新扫码。",
                _fSmall, Faint, S(PadX), S(180), S(DesignW - PadX * 2), S(40));
            return;
        }

        int max = Math.Min(_devices.Count, MaxVisible());
        for (int i = 0; i < max; i++)
        {
            var d = _devices[i];
            var r = ItemRect(i);
            bool sel = i == _picked;
            bool hot = _hover == HitListBase + i;
            FillRound(g, new RectangleF(r.X, r.Y, r.Width, r.Height), S(10), hot && !sel ? CardBgHover : CardBg);
            StrokeRound(g, new RectangleF(r.X, r.Y, r.Width, r.Height), S(10),
                sel ? Ok : Color.FromArgb(44, 50, 60), sel ? 1.5f : 1f);

            string name = string.IsNullOrEmpty(d.Name) ? "未命名设备" : d.Name;
            Text(g, name, _fItem, Fg, r.X + S(16), r.Y + S(8), r.Width - S(90), S(22));

            string sub = (string.IsNullOrEmpty(d.Model) ? d.Did : d.Model) + "  ·  " + d.Mac;
            Text(g, sub, _fItemSub, Faint, r.X + S(16), r.Y + S(28), r.Width - S(90), S(18));

            if (d.IsCuktech)
                TextC(g, "酷态科", _fItemSub, Ok, r.Right - S(96), r.Y + S(8), S(58), S(22));
            else
                TextC(g, d.IsOnline ? "在线" : "离线", _fItemSub, d.IsOnline ? Faint : Color.FromArgb(90, 96, 106),
                    r.Right - S(96), r.Y + S(8), S(58), S(22));

            if (sel)
                GdiText.Draw(g, "\uE73E", _fIcon, Ok, new Rectangle(r.Right - S(36), r.Y + (r.Height - S(24)) / 2, S(20), S(24)), TextAnchor.Center);
        }

        if (_devices.Count > max)
            Text(g, "还有 " + (_devices.Count - max) + " 台没显示（先用米家 App 删掉不用的设备）",
                _fSmall, Faint, S(PadX), S(ListTop + max * (ItemH + ItemGap)), S(DesignW - PadX * 2), S(18));
    }

    private void PaintButtons(Graphics g)
    {
        switch (_stage)
        {
            case Stage.Welcome:
                DrawButton(g, PrimaryRect(), "用米家 App 扫码", true, true, _hover == HitPrimary);
                DrawButton(g, SecondaryRect(), "以后再说", false, true, _hover == HitSecondary);
                break;
            case Stage.Scanning:
                DrawButton(g, PrimaryRect(), _busy ? "正在申请二维码…" : "正在等待扫码…", true, false, false);
                DrawButton(g, SecondaryRect(), "取消", false, true, _hover == HitSecondary);
                break;
            case Stage.Picking:
                DrawButton(g, PrimaryRect(), "就用这台", true, _picked >= 0, _hover == HitPrimary);
                DrawButton(g, SecondaryRect(), "重新扫码", false, true, _hover == HitSecondary);
                DrawButton(g, TertiaryRect(), "取消", false, true, _hover == HitTertiary);
                break;
        }
    }

    private void DrawButton(Graphics g, Rectangle r, string text, bool primary, bool enabled, bool hot)
    {
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        float radius = S(10);
        using var p = RoundRect(rf, radius);
        if (primary)
        {
            Color fill = !enabled ? Color.FromArgb(40, 66, 57)
                : hot ? Color.FromArgb(74, 226, 172) : Ok;
            using var b = new SolidBrush(fill);
            g.FillPath(b, p);
            GdiText.Draw(g, text, _fBtn, enabled ? Color.FromArgb(14, 20, 18) : Color.FromArgb(112, 134, 124),
                r, TextAnchor.Center, ellipsis: true);
        }
        else
        {
            using var b = new SolidBrush(hot ? CardBgHover : Color.FromArgb(34, 39, 47));
            g.FillPath(b, p);
            using var pen = new Pen(BorderColor, 1f);
            g.DrawPath(pen, p);
            GdiText.Draw(g, text, _fBtn, enabled ? Fg : Faint, r, TextAnchor.Center, ellipsis: true);
        }
    }

    // ==================== 诊断渲染 ====================

    internal void ForceScale(float scale, float fontBase = 1.25f)
    {
        if (scale < 0.5f || scale > 4f) return;
        Scale = scale;
        _fontBase = fontBase > 0f ? fontBase : scale;
        ApplyScale();
    }

    /// <summary>把当前一帧画进 PNG（会话内自检用，不依赖截图工具）。</summary>
    public void SaveRender(string path)
    {
        int w = Math.Max(1, S(DesignW)), h = Math.Max(1, S(DesignH));
        int ow = Width, oh = Height;
        Width = w;
        Height = h;
        try
        {
            using var surf = Surface.Create(w, h);
            if (surf is null) return;
            surf.G.Clear(BgBottom);
            Paint(surf.G);
            using var bmp = surf.ToBitmap();
            // 四角抠成透明，跟面板导出图一致（窗口本身是靠 region 裁的圆角）。
            using var shot = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g2 = Graphics.FromImage(shot))
            {
                g2.Clear(Color.Transparent);
                using var clip = RoundRect(new RectangleF(0, 0, w, h), 14 * Scale);
                g2.SetClip(clip);
                g2.DrawImage(bmp, 0, 0, w, h);
            }
            // 走内存再落盘：Image.Save 直接写路径在某些 GDI+ 状态下会报
            // 「A generic error occurred in GDI+」，换成内存流 + 文件写入最稳。
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllBytes(path, ms.ToArray());
        }
        finally
        {
            Width = ow;
            Height = oh;
        }
    }

    /// <summary>诊断用：不连网也能把三个阶段的样子画出来核对排版。</summary>
    internal void DemoRender(int stage, string path)
    {
        _demo = true;
        _stage = (Stage)Math.Clamp(stage, 0, 2);
        _devices.Clear();
        if (_stage == Stage.Picking)
        {
            _devices.Add(new MiDevice { Name = "小米充电器", Model = "AD1204U", Mac = "AA:BB:CC:DD:EE:FF", TokenHex = new string('0', 24), IsCuktech = true, IsOnline = true });
            _devices.Add(new MiDevice { Name = "米家台灯", Model = "MJTD01YL", Mac = "AA:BB:CC:DD:EE:02", TokenHex = new string('0', 24) });
            _picked = 0;
            SetStatus("扫码成功，共 2 台可用", Dim);
        }
        else if (_stage == Stage.Scanning)
        {
            SetStatus("等着你扫…（20 分钟内有效）", Dim);
        }
        SaveRender(path);
    }
}
