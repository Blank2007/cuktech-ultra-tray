using CuktechLogin;

namespace CuktechTray;

/// <summary>
/// 首次使用向导：没有配置时自动弹出，引导用户用自己的小米账号扫码，
/// 自动取回设备的 MAC 与 12 字节 BLE token，写进 settings.json。
/// 这样程序对任何一台装了米家 App 的电脑都能开箱即用，而不是只认某台写死的设备。
/// </summary>
internal sealed class SetupWizardForm : Form
{
    private static readonly Color Bg = Color.FromArgb(24, 27, 32);
    private static readonly Color Card = Color.FromArgb(34, 38, 45);
    private static readonly Color Fg = Color.FromArgb(232, 234, 237);
    private static readonly Color Dim = Color.FromArgb(150, 156, 165);
    private static readonly Color Accent = Color.FromArgb(52, 211, 153);

    // 设计尺寸（逻辑像素）：窗口 + 各区域，按 DeviceDpi 统一缩放
    private const int W = 480;
    private const int H = 560;
    private const int Pad = 28;

    private readonly Settings _settings;
    private readonly float _s;

    private readonly Label _title;
    private readonly Label _body;
    private readonly Label _status;
    private readonly PictureBox _qr;
    private readonly ListView _list;
    private readonly Button _primary;
    private readonly Button _cancel;

    private CancellationTokenSource? _cts;
    private XiaomiClient? _client;
    private Stage _stage = Stage.Welcome;

    private enum Stage { Welcome, Scanning, Picking, Done }

    public SetupWizardForm(Settings settings)
    {
        _settings = settings;
        _s = DeviceDpi / 96f;

        Text = "连接到你的酷态科充电器";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        ClientSize = new Size(S(W), S(H));
        DoubleBuffered = true;

        _title = MakeLabel("把充电器接到你的账号", 19f, FontStyle.Bold, Fg, Pad, 20, W - Pad * 2, 34);

        _body = MakeLabel("", 12.5f, FontStyle.Regular, Dim, Pad, 68, W - Pad * 2, 250);

        _qr = new PictureBox
        {
            Location = new Point(S(120), S(150)),
            Size = new Size(S(240), S(240)),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Visible = false,
        };

        _status = MakeLabel("", 11f, FontStyle.Regular, Dim, Pad, 398, W - Pad * 2, 40);
        _status.TextAlign = ContentAlignment.MiddleCenter;

        _list = new ListView
        {
            Location = new Point(S(Pad), S(96)),
            Size = new Size(S(W - Pad * 2), S(280)),
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BackColor = Card,
            ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle,
            Visible = false,
        };
        _list.Columns.Add("设备", S(190));
        _list.Columns.Add("型号", S(150));
        _list.Columns.Add("连接方式", S(84));

        _primary = MakeButton("用米家 App 扫码", Pad, 466, 280, 44, primary: true);
        _primary.Click += (_, _) => OnPrimary();

        _cancel = MakeButton("取消", W - Pad - 140, 466, 140, 44, primary: false);
        _cancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.AddRange(new Control[] { _title, _body, _qr, _status, _list, _primary, _cancel });
        ShowPage(Stage.Welcome);

        FormClosing += (_, _) =>
        {
            _cts?.Cancel();
            _client?.Dispose();
        };
    }

    /// <summary>运行向导；返回 true 表示已经写好可用的配置。</summary>
    public static bool Run(Settings settings, bool autoScan = false)
    {
        using var form = new SetupWizardForm(settings);
        if (autoScan) form.Shown += (_, _) => form.BeginLogin();
        return form.ShowDialog() == DialogResult.OK && settings.IsComplete;
    }

    /// <summary>直接进入扫码页（给 --setup-scan 用，也方便截图核对界面）。</summary>
    internal void BeginLogin() => _ = StartLoginAsync();

    private int S(int v) => (int)Math.Round(v * _s);

    private Label MakeLabel(string text, float px, FontStyle style, Color color, int x, int y, int w, int h) => new()
    {
        Text = text,
        Font = new Font("Microsoft YaHei UI", px, style, GraphicsUnit.Pixel),
        ForeColor = color,
        BackColor = Color.Transparent,
        Location = new Point(S(x), S(y)),
        Size = new Size(S(w), S(h)),
        AutoSize = false,
        TextAlign = ContentAlignment.TopLeft,
    };

    private Button MakeButton(string text, int x, int y, int w, int h, bool primary)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(S(x), S(y)),
            Size = new Size(S(w), S(h)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Card,
            ForeColor = primary ? Color.FromArgb(12, 26, 20) : Fg,
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = primary ? 0 : 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 76, 86);
        return b;
    }

    private void ShowPage(Stage stage)
    {
        _stage = stage;
        _qr.Visible = stage == Stage.Scanning;
        _list.Visible = stage == Stage.Picking;

        switch (stage)
        {
            case Stage.Welcome:
                _title.Text = "把充电器接到你的账号";
                _body.Text =
                    "这个工具通过蓝牙直连充电器，读四个接口的电压 / 电流 / 功率。\n" +
                    "连上之后完全本地运行，不需要联网，也不经过任何第三方服务器。\n\n" +
                    "直连设备需要一把「钥匙」（token），它只存在于你自己的小米账号里。\n" +
                    "扫一次码就能自动取回，账号密码不会经过这个程序，也不会被保存。\n\n" +
                    "扫码之前请确认：\n" +
                    "     · 充电器已经绑在手机米家 App 里\n" +
                    "     · 手机就在手边\n" +
                    "     · 电脑的蓝牙是打开的";
                _body.SetBounds(S(Pad), S(66), S(W - Pad * 2), S(300));
                _primary.Text = "用米家 App 扫码";
                _primary.Enabled = true;
                _status.Text = "支持 Windows 10 / 11 · 需要蓝牙适配器";
                break;

            case Stage.Scanning:
                _title.Text = "用米家 App 扫码";
                _body.Text = "打开米家 App → 右上角扫一扫 → 扫描下面的二维码。";
                _body.SetBounds(S(Pad), S(66), S(W - Pad * 2), S(60));
                _primary.Text = "正在等待扫码…";
                _primary.Enabled = false;
                break;

            case Stage.Picking:
                _title.Text = "选择你的充电器";
                _body.Text = "选中一台设备，它会被设成默认设备。";
                _body.SetBounds(S(Pad), S(60), S(W - Pad * 2), S(30));
                _primary.Text = "就用这台";
                _primary.Enabled = true;
                break;

            case Stage.Done:
                _primary.Enabled = false;
                _status.Text = "完成，正在启动…";
                break;
        }
    }

    private void OnPrimary()
    {
        if (_stage == Stage.Welcome) _ = StartLoginAsync();
        else if (_stage == Stage.Picking) AcceptPicked();
    }

    private async Task StartLoginAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        ShowPage(Stage.Scanning);
        _status.Text = "正在申请二维码…";
        _client?.Dispose();
        _client = new XiaomiClient();
        _client.Log += msg => BeginInvoke(new Action(() => _status.Text = msg));

        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(20);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var qr = await _client.RequestQrAsync(ct);
                var png = await _client.DownloadQrAsync(qr, ct);
                using (var ms = new MemoryStream(png))
                {
                    var img = Image.FromStream(ms);
                    var old = _qr.Image;
                    _qr.Image = img;
                    old?.Dispose();
                }

                _status.Text = $"等待扫码（二维码 {qr.Ttl.TotalSeconds:F0} 秒内有效）";

                if (await _client.PollUntilScannedAsync(qr, ct)) break;
                if (ct.IsCancellationRequested) return;
                _status.Text = "二维码过期了，正在换一张…";
            }

            _status.Text = "已登录，正在读取账号下的设备…";
            var devices = await _client.GetDevicesAsync(ct);
            ShowDevices(devices);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，忽略
        }
        catch (Exception ex)
        {
            LogSink.Write("[wizard] " + ex);
            _status.Text = "出错了：" + ex.Message + "（可以直接再点一次扫码重试）";
            _primary.Enabled = true;
            _primary.Text = "重新扫码";
        }
    }

    private void ShowDevices(IReadOnlyList<MiDevice> devices)
    {
        _list.Items.Clear();
        var usable = devices.Where(d => d.TokenHex.Length == 24).ToList();
        foreach (var d in usable.OrderByDescending(d => d.IsCuktech))
        {
            var port = d.Mac.Length > 0 ? d.Mac : "无蓝牙地址";
            var item = new ListViewItem(new[] { d.Name.Length > 0 ? d.Name : d.Did, d.Model, port })
            {
                Tag = d,
            };
            _list.Items.Add(item);
        }

        if (_list.Items.Count == 0)
        {
            _status.Text = "账号下没有找到带蓝牙 token 的设备。请确认充电器已经绑在米家 App 里。";
            _primary.Enabled = true;
            _primary.Text = "重新扫码";
            return;
        }

        var first = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((MiDevice)i.Tag!).IsCuktech);
        (first ?? _list.Items[0]).Selected = true;
        ShowPage(Stage.Picking);
        _status.Text = $"找到 {_list.Items.Count} 台可用设备";
    }

    private void AcceptPicked()
    {
        if (_list.SelectedItems.Count == 0) return;
        var dev = (MiDevice)_list.SelectedItems[0].Tag!;
        if (dev.Mac.Length == 0)
        {
            MessageBox.Show(this, "这台设备没有上报蓝牙 MAC 地址，无法直连。\n请在米家 App 里打开一次设备，再重新扫码试试。",
                "无法使用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.Mac = dev.Mac;
        _settings.TokenHex = dev.TokenHex.ToLowerInvariant();
        _settings.Save();
        try { _client?.SaveSession(Path.Combine(AppContext.BaseDirectory, "session.json")); } catch { }
        LogSink.Write($"[wizard] saved mac={dev.Mac} tokenLen={dev.TokenHex.Length} model={dev.Model}");

        ShowPage(Stage.Done);
        DialogResult = DialogResult.OK;
        Close();
    }
}
