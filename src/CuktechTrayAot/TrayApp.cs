using Microsoft.Win32;

namespace CuktechTray;

/// <summary>
/// 托盘常驻主体（NativeAOT 版）：图标只是一枚状态徽标，左键开面板，右键功能菜单。
/// 实时功率交给任务栏那块数字（--taskbar）和悬停提示，托盘图标不再画数字。
///
/// 自己就是一个隐藏窗口 —— 托盘图标的回调消息、定时器、锁屏通知都发到它身上，
/// 这样不需要 WinForms 的 ApplicationContext，也不需要任何可见窗口。
/// </summary>
internal sealed class TrayApp : WinBase
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CuktechTray";
    private static readonly UIntPtr TimerId = new(1);

    /// <summary>已连接：图标底色用面板那套绿，跟通知/状态胶囊一个色系。</summary>
    private static readonly Color ColorOn = Color.FromArgb(26, 138, 86);
    /// <summary>未连接 / 未配置：灰。</summary>
    private static readonly Color ColorOff = Color.FromArgb(94, 99, 106);

    private readonly Settings _settings;
    private readonly UiDispatcher _ui;
    private readonly DeviceClient _client;
    private readonly PanelWindow _panel;
    private readonly TrayIcon _tray;

    private TaskbarWindow? _widget;
    private WizardWindow? _wizard;
    private long _wizardAt = -1;
    private Icon? _icon;
    private string _iconKey = string.Empty;
    private string _tip = string.Empty;
    private Snapshot _snap = new();
    private string _statusOverride = string.Empty;
    private DateTime _statusOverrideUntil = DateTime.MinValue;

    private long _controlAt = -1;
    private long _updateAt = -1;
    private string _newVersion = string.Empty;
    private bool _checking;
    private bool _clientStarted;
    private long _releaseAt = -1;
    private long _saveAt = -1;
    private bool _releasePending;
    private bool _showPending;
    private bool _quitting;
    private bool _updating;

    private Native.RECT _lastPanelRect;

    public TrayApp(Settings settings, ShowOnStart showOnStart, UiDispatcher ui, bool openControl = false)
    {
        _settings = settings;
        _ui = ui;

        // 宿主窗口：不可见，只用来收托盘回调 / 定时器 / 锁屏消息
        CreateWin(0, 0, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, IntPtr.Zero, "shell");
        if (!IsValid) throw new InvalidOperationException("托盘宿主窗口建不起来");
        _ui.Attach(Hwnd, Native.WM_APP_POST);

        // 面板要能直接下发开关命令，所以设备客户端得先建好
        _client = new DeviceClient(settings, ui);

        _panel = new PanelWindow(_client, settings, ui);
        _panel.RestorePinned(settings.PanelPinned);
        _panel.SetAlwaysOnTop(settings.PanelAlwaysOnTop);
        _panel.PinChanged += OnPinChanged;

        // 任务栏里那一小块实时功率
        if (settings.TaskbarWidget) _widget = new TaskbarWindow(settings, () => ShowPanel(true));

        _client.Updated += OnSnapshot;

        _tray = new TrayIcon(Hwnd, 1, Native.WM_TRAYICON);
        _icon = WattIcon.Create(ColorOff);
        _iconKey = "off";
        _tray.Add(_icon, "酷态科 10 号 Ultra");

        // 只在「蹲托盘、不弹面板」的启动方式下介绍一次自己的位置，免得第一次用的人找不到图标
        if (showOnStart == ShowOnStart.None && !settings.ShowedTrayTip)
        {
            settings.ShowedTrayTip = true;
            settings.Save();
            _tray.ShowBalloon("酷态科功率常驻在这里", "左键点这个图标看实时功率，右键有更多设置。");
        }

        if (!settings.IsComplete)
        {
            _statusOverride = "未配置：还没扫码配对";
            _statusOverrideUntil = DateTime.MaxValue;
            // 构造函数里还不能弹窗（消息循环没跑起来，窗口画不出来），
            // 交给定时器等 800ms 再开向导；这会儿托盘图标已经在了，用户不会两眼一抹黑。
            _wizardAt = Environment.TickCount64 + 800;
        }

        // 没配对就不起蓝牙循环：settings 里 Mac 是空的，DeviceClient 每轮都会抛
        // 「MAC 格式应为 AA:BB:CC:DD:EE:FF」刷一屏日志，还白占着适配器。
        // 配对完成时 OnWizardCompleted 会把循环起起来（见那边 _clientStarted 的判断）。
        if (settings.IsComplete)
        {
            _client.Start();
            _clientStarted = true;
        }

        // 锁屏 / 解锁：把「谁在最上面」这件事重新摆正一次
        Native.WTSRegisterSessionNotification(Hwnd, 0 /* NOTIFY_FOR_THIS_SESSION */);

        // --control：启动后直接把面板翻到控制页
        if (openControl) _controlAt = Environment.TickCount64 + 500;

        // 默认每次启动查一遍新版。等 8 秒再查：先让蓝牙连上，别跟握手抢网络和 CPU
        if (settings.CheckUpdatesOnStart) _updateAt = Environment.TickCount64 + 8000;

        // 启动后是否主动弹一下面板
        if (showOnStart != ShowOnStart.None) _showAt = Environment.TickCount64 + 700;
        _showOnStart = showOnStart;

        Native.SetTimer(Hwnd, TimerId, 100, IntPtr.Zero);
    }

    private ShowOnStart _showOnStart = ShowOnStart.None;
    private long _showAt = -1;

    protected override void Paint(Graphics g) { }

    protected override void OnWake()
    {
        _ui.Drain();
        if (_showPending)
        {
            _showPending = false;
            ShowPanel(false);
        }
    }

    /// <summary>别的实例（用户又双击了一次 exe）请求把面板弹出来。</summary>
    public void RequestShow()
    {
        _showPending = true;
        Native.PostMessageW(Hwnd, Native.WM_APP_POST, IntPtr.Zero, IntPtr.Zero);
    }

    protected override bool OnAppMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Native.WM_TRAYICON) return false;

        // NOTIFYICON_VERSION_4：低 16 位是鼠标事件，高 16 位是图标 ID
        uint evt = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (evt)
        {
            case Native.WM_LBUTTONUP:
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT:
                TogglePanel();
                return true;
            case Native.WM_RBUTTONUP:
            case Native.WM_CONTEXTMENU:
                ShowTrayMenu();
                return true;
        }
        return true;
    }

    protected override void OnSessionChange(int reason)
    {
        if (reason is Native.WTS_SESSION_LOCK or Native.WTS_CONSOLE_DISCONNECT or Native.WTS_REMOTE_DISCONNECT)
            _panel.OnSessionLocked();
    }

    protected override void OnTimer(int id)
    {
        if (id != 1 || _quitting) return;
        long now = Environment.TickCount64;
        try
        {
            if (_controlAt >= 0 && now >= _controlAt)
            {
                _controlAt = -1;
                ShowPanel(true, PanelWindow.Tab.Control);
            }

            if (_showAt >= 0 && now >= _showAt)
            {
                _showAt = -1;
                StartupShow();
            }

            if (_wizardAt >= 0 && now >= _wizardAt)
            {
                _wizardAt = -1;
                ShowWizard();
            }

            if (_updateAt >= 0 && now >= _updateAt)
            {
                _updateAt = -1;
                RunUpdateCheck(false);
            }

            if (_releasePending && _releaseAt >= 0 && now >= _releaseAt)
            {
                _releasePending = false;
                _releaseAt = -1;
                _panel.SuppressAutoHide = false;
                if (_panel.Visible && !_panel.IsForeground && !_panel.ContainsCursor())
                    _panel.HideNow("startup-timeout");
            }

            // 固定模式下面板可能被拖走：位置变了就延后 1.2 秒存盘，别每像素写一次盘
            if (_panel.Pinned)
            {
                var r = _panel.ScreenRect();
                if (r.Left != _lastPanelRect.Left || r.Top != _lastPanelRect.Top)
                {
                    _lastPanelRect = r;
                    _saveAt = now + 1200;
                }
            }

            if (_saveAt >= 0 && now >= _saveAt)
            {
                _saveAt = -1;
                if (_panel.Pinned)
                {
                    var r = _panel.ScreenRect();
                    _settings.PanelX = r.Left;
                    _settings.PanelY = r.Top;
                    _settings.Save();
                }
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("[tray] tick: " + ex.Message);
        }
    }

    private void StartupShow()
    {
        LogSink.Write("[panel] startup tick mode=" + _showOnStart);
        if (_panel.Pinned)
        {
            ShowPanel(false);
            return;
        }

        if (_showOnStart == ShowOnStart.Center)
        {
            // 开发用：屏幕中央弹出并且不自动收起，方便截图核对排版
            var wa = WorkArea();
            _panel.RestorePinned(true);
            _panel.SuppressAutoHide = true;
            _panel.ShowNear(new Native.POINT(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2 + PanelHeight()));
            return;
        }

        // 正常启动：锚在工作区右下角。先临时关掉「失焦自动收起」——
        // 启动瞬间窗口没有前台输入权，很容易刚显示就被抢焦点收掉。
        _panel.SuppressAutoHide = true;
        ShowPanel(false);
        _releasePending = true;
        _releaseAt = Environment.TickCount64 + 12000;
    }

    private int PanelHeight()
    {
        var r = _panel.ScreenRect();
        return r.Height;
    }

    private static Native.RECT WorkArea()
    {
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfoW(Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTONEAREST), ref mi);
        return mi.rcWork;
    }

    // ---------------- 数据 ----------------

    private void OnSnapshot(Snapshot snap)
    {
        _snap = snap ?? new Snapshot();
        PowerHistory.Add(_snap);   // 曲线页的数据只活在内存里，每分钟约 1800 点封顶
        _widget?.Apply(_snap);
        UpdateTray(_snap);
    }

    private string StatusLine()
    {
        if (_statusOverride.Length > 0 && DateTime.UtcNow < _statusOverrideUntil) return _statusOverride;
        if (!_settings.IsComplete) return "未配置：settings.json 缺少 MAC/token";
        return _snap.Connected
            ? "已连接 · 总 " + _snap.TotalWatts.ToString("F1") + " W"
            : "未连接 · " + (_snap.Error ?? _snap.Status);
    }

    private void SetStatus(string text, double seconds = 8)
    {
        _statusOverride = text;
        _statusOverrideUntil = DateTime.UtcNow.AddSeconds(seconds);
    }

    private void UpdateTray(Snapshot snap)
    {
        // 图标只表达「连上了没有」：实时功率看任务栏那块数字，不必再挤进 16px 的图标里
        Color color = snap.Connected ? ColorOn : ColorOff;

        bool badge = _newVersion.Length > 0;
        string key = (snap.Connected ? "on" : "off") + (badge ? "+new" : "");
        bool iconChanged = key != _iconKey;
        if (iconChanged)
        {
            _iconKey = key;
            var next = WattIcon.Create(color, badge);
            var previous = _icon;
            _icon = next;
            // 系统需要短暂保留旧句柄，延后释放
            if (previous is not null) Task.Delay(2000).ContinueWith(_ => { try { previous.Dispose(); } catch { } });
        }

        string tip;
        if (!snap.Connected)
        {
            tip = "酷态科 10 号 Ultra\n" + (snap.Error ?? snap.Status);
        }
        else
        {
            var parts = snap.Ports.Where(p => p.Working).Select(p => p.Name + " " + p.Watts.ToString("F0") + "W");
            var detail = string.Join(" · ", parts);
            tip = "总 " + snap.TotalWatts.ToString("F1") + " W" + (detail.Length > 0 ? "\n" + detail : "\n四口空闲");
        }
        if (tip.Length > 120) tip = tip[..120];

        if (iconChanged || tip != _tip)
        {
            _tip = tip;
            if (_icon is not null) _tray.Update(_icon, tip);
        }
    }

    // ---------------- 面板 ----------------

    private void OnPinChanged()
    {
        _settings.PanelPinned = _panel.Pinned;
        if (_panel.Pinned)
        {
            var r = _panel.ScreenRect();
            _settings.PanelX = r.Left;
            _settings.PanelY = r.Top;
            _lastPanelRect = r;
        }
        _settings.Save();
        LogSink.Write("[panel] pinned=" + _panel.Pinned);
    }

    /// <summary>
    /// 面板「始终在最前」。跟「固定面板到桌面」是两件事：那个只是不自动收起，
    /// 这个是压在别的窗口上面，两个开关可以同时开。
    /// </summary>
    private void ToggleAlwaysOnTop()
    {
        _panel.ToggleAlwaysOnTop();
        _settings.PanelAlwaysOnTop = _panel.AlwaysOnTop;
        _settings.Save();
    }

    private void TogglePanel()
    {
        if (_panel.Visible) { _panel.HideNow("toggle"); return; }
        // 刚因为点到别处而收起（其实就是在点托盘图标）：这次点击不要再弹出来
        if ((DateTime.UtcNow - _panel.LastAutoHiddenAt).TotalMilliseconds < 300) return;
        ShowPanel(true);
    }

    /// <param name="anchorToCursor">true = 出现在鼠标旁边；false = 停在右下角。</param>
    /// <param name="tab">指定停在面板的哪一页；null = 保持原样。</param>
    private void ShowPanel(bool anchorToCursor, PanelWindow.Tab? tab = null)
    {
        if (tab is { } wanted) _panel.SetTab(wanted);

        if (_panel.Pinned)
        {
            _panel.AutoHideAfterMs = 0;
            if (_settings.PanelX != int.MinValue)
            {
                _panel.ShowAtClamped(_settings.PanelX, _settings.PanelY);
                // 位置要是被挪回来了，就把修好的坐标写回去，别每次启动都再修一遍
                var fixedRect = _panel.ScreenRect();
                if (fixedRect.Left != _settings.PanelX || fixedRect.Top != _settings.PanelY)
                {
                    _settings.PanelX = fixedRect.Left;
                    _settings.PanelY = fixedRect.Top;
                    _settings.Save();
                }
            }
            else
            {
                var wa = WorkArea();
                _panel.ShowNear(new Native.POINT(wa.Right, wa.Bottom));
                var r = _panel.ScreenRect();
                _settings.PanelX = r.Left;
                _settings.PanelY = r.Top;
            }
            _lastPanelRect = _panel.ScreenRect();
            return;
        }

        _panel.AutoHideAfterMs = 0;   // 主动点开的浮层不自己消失，失焦才收
        Native.POINT anchor;
        if (anchorToCursor)
        {
            Native.GetCursorPos(out anchor);
        }
        else
        {
            var wa = WorkArea();
            anchor = new Native.POINT(wa.Right, wa.Bottom);
        }
        _panel.ShowNear(anchor);
        LogSink.Write("[panel] show anchor=" + anchor.X + "," + anchor.Y + " visible=" + _panel.Visible);
    }

    // ---------------- 菜单 ----------------

    /// <summary>--menu-demo：启动后把右键菜单摊在固定位置，方便截图比对（给文档/视频用）。</summary>
    public void DemoMenu()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            _ui.Post(_ =>
            {
                // 截图/视频要的是「已连接 · 总 6.0 W」那一行，跟面板渲染图同一组数；
                // 演示模式没有真设备，所以直接借 SetStatus 的临时状态行（10 分钟足够截图）。
                SetStatus("已连接 · 总 6.0 W", 600);
                NativeMenu.Show(Hwnd, BuildMenu(), 200, 200);
                LogSink.Write("[menu] demo shown");
            }, null);
        });
    }

    /// <summary>--menu-demo-taskbar：只摊出「任务栏数字」这一层子菜单，给截图与人工核对用。</summary>
    public void DemoMenuTaskbar()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            _ui.Post(_ =>
            {
                SetStatus("已连接 · 总 6.0 W", 600);
                NativeMenu.Show(Hwnd, TaskbarItems(), 200, 140);
                LogSink.Write("[menu] taskbar demo shown");
            }, null);
        });
    }

    private void ShowTrayMenu()
    {
        if (!Native.GetCursorPos(out var pt)) return;
        NativeMenu.Show(Hwnd, BuildMenu(), pt.X, pt.Y);
    }

    private MenuEntry[] BuildMenu()
    {
        var modeItems = new List<MenuEntry>();
        bool modeUsable = _snap.SceneMode > 0 && _client.IsReady;
        foreach (var m in DeviceClient.SceneModes)
        {
            int value = m.Value;
            modeItems.Add(new MenuEntry
            {
                Text = m.Name,
                Checked = _snap.SceneMode == value,
                Enabled = modeUsable,
                Click = () => SetSceneModeFromMenu(value),
            });
        }

        return new[]
        {
            MenuEntry.BoldItem("显示功率面板", () => ShowPanel(true)),
            MenuEntry.Item("显示控制页（四路开关）", () => ShowPanel(true, PanelWindow.Tab.Control)),
            MenuEntry.Item("设备设置（息屏 / 定时断电 / 协议）…", OpenDeviceSettings),
            MenuEntry.Item("单口采集（按秒算电量）…", OpenCapture),
            MenuEntry.WithSub("充电模式", modeItems.ToArray()),
            MenuEntry.CheckItem("固定面板到桌面", _panel.Pinned, () => _panel.TogglePin()),
            MenuEntry.CheckItem("面板始终在最前", _panel.AlwaysOnTop, ToggleAlwaysOnTop),
            MenuEntry.WithSub("任务栏数字", TaskbarItems()),
            MenuEntry.WithSub("功率曲线时长", ChartItems()),
            MenuEntry.Item("重置面板位置（收回右下角）", ResetPanelPosition),
            MenuEntry.Dim(StatusLine()),
            MenuEntry.Sep(),
            MenuEntry.Item("重新连接", () => _client.Restart()),
            MenuEntry.CheckItem("随 Windows 启动", IsAutoStartEnabled(), ToggleAutoStart),
            Installer.IsInstalledHere
                ? MenuEntry.Item("从这台电脑卸载…", UninstallFromMachine)
                : MenuEntry.Item("安装到本机…", InstallToMachine),
            MenuEntry.Item("重新配置设备…", ReconfigureDevice),
            _newVersion.Length > 0
                ? MenuEntry.DotItem("检查更新…（有 " + _newVersion + "）", CheckUpdates)
                : MenuEntry.Item("检查更新…", CheckUpdates),
            MenuEntry.Item("关于…", ShowAbout),
            MenuEntry.Item("打开日志", OpenLog),
            MenuEntry.Item("设置文件", OpenSettings),
            MenuEntry.Sep(),
            MenuEntry.Item("退出", ExitApp),
        };
    }

    private void SetSceneModeFromMenu(int mode)
    {
        string name = "模式 " + mode;
        foreach (var m in DeviceClient.SceneModes)
            if (m.Value == mode) name = m.Name;

        _ = Task.Run(async () =>
        {
            var res = await _client.SetSceneModeAsync(mode);
            _ui.Post(_ =>
            {
                if (res.Ok)
                {
                    SetStatus("充电模式已切到「" + name + "」（设备回读 " + _client.SceneMode + "）");
                }
                else
                {
                    SetStatus("切到「" + name + "」失败：" + res.Error);
                    _tray.ShowBalloon("充电模式没切成", res.Error ?? string.Empty, Native.NIIF_WARNING);
                }
            }, null);
        });
    }

    private void ToggleTaskbarWidget()
    {
        if (_widget is null)
        {
            _widget = new TaskbarWindow(_settings, () => ShowPanel(true));
            _widget.Apply(_snap);
        }
        else
        {
            _widget.Stop();
            _widget = null;
        }

        _settings.TaskbarWidget = _widget is not null;
        _settings.Save();
        LogSink.Write("[taskbar] enabled=" + (_widget is not null));
    }

    /// <summary>
    /// 任务栏数字那一摊的菜单（0.5.4，issue #7）：开关、四档位置、四档大小、两处自定义、状态行、重置。
    /// 系统菜单也只支持一层子菜单，所以这里全部压平成一行一行的单选。
    /// </summary>
    private MenuEntry[] TaskbarItems()
    {
        bool on = _widget is not null;
        var place = _widget?.Place ?? TaskbarWindow.ParsePlace(_settings.TaskbarPlace);
        int wdip = TaskbarWindow.ClampWidth(_settings.TaskbarWidthDip);

        string now = _widget is not null ? _widget.Describe() : "现在：任务栏数字没开";

        return new[]
        {
            MenuEntry.CheckItem("显示在任务栏（总功率）", on, ToggleTaskbarWidget),
            MenuEntry.Sep(),
            MenuEntry.RadioItem("贴在开始菜单左边（自动）", place == TaskbarPlace.Auto, () => SetTaskbarPlace(TaskbarPlace.Auto)),
            MenuEntry.RadioItem("屏幕正中", place == TaskbarPlace.Center, () => SetTaskbarPlace(TaskbarPlace.Center)),
            MenuEntry.RadioItem("靠托盘左边", place == TaskbarPlace.Right, () => SetTaskbarPlace(TaskbarPlace.Right)),
            MenuEntry.RadioItem(place == TaskbarPlace.Custom
                    ? "自定义左边距…（现在是 " + (int)Math.Round(_settings.TaskbarLeftDip) + "）"
                    : "自定义左边距…",
                place == TaskbarPlace.Custom, AskTaskbarLeft),
            MenuEntry.Sep(),
            MenuEntry.RadioItem("小（9.5 号 · 宽 72）", wdip == 72, () => SetTaskbarWidth(72)),
            MenuEntry.RadioItem("中（11 号 · 宽 84）", wdip == 84, () => SetTaskbarWidth(84)),
            MenuEntry.RadioItem("大（13 号 · 宽 104）", wdip == 104, () => SetTaskbarWidth(104)),
            MenuEntry.RadioItem(wdip != 72 && wdip != 84 && wdip != 104
                    ? "自定义宽度…（现在是 " + wdip + "）"
                    : "自定义宽度…",
                wdip != 72 && wdip != 84 && wdip != 104, AskTaskbarWidth),
            MenuEntry.Sep(),
            MenuEntry.Dim(now),
            MenuEntry.Item("重置位置与大小（回自动 + 宽 84）", ResetTaskbarPosition),
        };
    }

    /// <summary>
    /// 曲线页「最近多久」的档位。面板上点那颗胶囊也能换，两边读写的是同一个 settings.ChartSeconds，
    /// 所以菜单里选了之后回到面板就是新时长。
    /// </summary>
    private MenuEntry[] ChartItems()
    {
        int cur = _panel.ChartSpan;
        var items = new List<MenuEntry>();
        foreach (int sec in PanelWindow.SpanChoices)
        {
            int value = sec;
            items.Add(MenuEntry.RadioItem(PanelWindow.SpanLabel(value), cur == value, () => SetChartSpan(value)));
        }
        return items.ToArray();
    }

    private void SetChartSpan(int sec)
    {
        _panel.SetChartSpan(sec);
        SetStatus("曲线改成" + PanelWindow.SpanLabel(sec));
    }

    private void SetTaskbarPlace(TaskbarPlace place)
    {
        if (_widget is not null) _widget.SetPlace(place);
        else
        {
            _settings.TaskbarPlace = TaskbarWindow.PlaceKey(place);
            _settings.Save();
        }
        SetStatus("任务栏数字位置：" + TaskbarPlaceName(place));
    }

    private void SetTaskbarWidth(int dip)
    {
        if (_widget is not null) _widget.SetWidthDip(dip);
        else
        {
            _settings.TaskbarWidthDip = TaskbarWindow.ClampWidth(dip);
            _settings.Save();
        }
        SetStatus("任务栏数字宽度：" + dip + "（字号约 " + (11.0 * dip / 84).ToString("F1") + " 号）");
    }

    private static string TaskbarPlaceName(TaskbarPlace place) => place switch
    {
        TaskbarPlace.Center => "屏幕正中",
        TaskbarPlace.Right => "靠托盘左边",
        TaskbarPlace.Custom => "自定义左边距",
        _ => "贴开始菜单左边（自动）",
    };

    /// <summary>「自定义左边距」：弹一个输入窗，填 dip。</summary>
    private void AskTaskbarLeft()
    {
        int min = 0, max = 1200, cur = 0;
        if (_widget is not null)
        {
            var lim = _widget.PlaceLimits();
            min = lim.Min;
            max = lim.Max;
            cur = (int)Math.Round(_settings.TaskbarLeftDip >= 0 ? _settings.TaskbarLeftDip : 0);
        }

        var v = NumberWindow.Ask(
            "自定义左边距",
            "从任务栏最左边算起（拖过就记成这个值）",
            "dip",
            min,
            max,
            Math.Clamp(cur, min, max),
            "可以填 " + min + " 到 " + max);

        if (v is null)
        {
            SetStatus("没改任务栏数字位置");
            return;
        }

        if (_widget is not null) _widget.SetCustomLeft(v.Value);
        else
        {
            _settings.TaskbarLeftDip = v.Value;
            _settings.TaskbarPlace = TaskbarWindow.PlaceKey(TaskbarPlace.Custom);
            _settings.Save();
        }
        SetStatus("任务栏数字左边距：" + v.Value + " dip");
    }

    /// <summary>「自定义宽度」：宽度 56..160 dip，字号跟着变。</summary>
    private void AskTaskbarWidth()
    {
        int cur = TaskbarWindow.ClampWidth(_settings.TaskbarWidthDip);
        var v = NumberWindow.Ask(
            "自定义宽度",
            "字号跟着宽度等比变，84 就是 11 号字",
            "dip",
            TaskbarWindow.MinWidthDip,
            TaskbarWindow.MaxWidthDip,
            cur,
            "可以填 " + TaskbarWindow.MinWidthDip + " 到 " + TaskbarWindow.MaxWidthDip);

        if (v is null)
        {
            SetStatus("没改任务栏数字宽度");
            return;
        }

        SetTaskbarWidth(v.Value);
    }

    private void ResetTaskbarPosition()
    {
        if (_widget is null)
        {
            _settings.TaskbarLeftDip = -1;
            _settings.TaskbarPlace = TaskbarWindow.PlaceKey(TaskbarPlace.Auto);
            _settings.TaskbarWidthDip = TaskbarWindow.BaseWidthDip;
            _settings.Save();
            SetStatus("任务栏功率没开，位置与大小已重置");
            return;
        }

        _widget.ResetPosition();
        SetStatus("任务栏数字已回到开始按钮左边（宽 84）");
    }

    /// <summary>
    /// 面板位置记坏了（拖出屏幕、换过分辨率）时的后路：清掉记住的坐标，开着「固定」就立刻显一次，
    /// 让它按右下角重新算一遍位置存回去。
    /// </summary>
    private void ResetPanelPosition()
    {
        _settings.PanelX = int.MinValue;
        _settings.PanelY = int.MinValue;
        _settings.Save();
        _lastPanelRect = default;
        LogSink.Write("[panel] 位置已重置");

        if (_panel.Pinned)
        {
            ShowPanel(false);
            var r = _panel.ScreenRect();
            _settings.PanelX = r.Left;
            _settings.PanelY = r.Top;
            _settings.Save();
            SetStatus("面板已回到右下角");
        }
        else
        {
            SetStatus("面板位置已清空，下次打开在右下角");
        }
    }

    // ---------------- 杂项 ----------------

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string s && s.Trim().Length > 0;
        }
        catch { return false; }
    }

    private void ToggleAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (IsAutoStartEnabled()) key.DeleteValue(RunValueName, throwOnMissingValue: false);
            else key.SetValue(RunValueName, "\"" + (Environment.ProcessPath ?? string.Empty) + "\" --tray-only");
            _settings.AutoStart = IsAutoStartEnabled();
            _settings.Save();
        }
        catch (Exception ex)
        {
            LogSink.Write("[autostart] " + ex.Message);
            MessageBox("写入注册表失败：" + ex.Message, Native.MB_OK | Native.MB_ICONWARNING);
        }
    }

    private void ReconfigureDevice() => ShowWizard();

    // 三个自绘小窗共用一把「一次只开一个」的闸：它们各自跑自己的消息循环，
    // 叠成两层嵌套循环时 Esc / 关窗的归属会变得说不清。
    private bool _modalOpen;

    private void OpenDeviceSettings()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try
        {
            using var w = new DeviceSettingsWindow(_client);
            if (w.IsValid) w.ShowModal();
        }
        finally { _modalOpen = false; }
    }

    private void OpenCapture()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try
        {
            using var w = new CaptureWindow(_client);
            if (w.IsValid) w.ShowModal();
        }
        finally { _modalOpen = false; }
    }

    private void ShowAbout()
    {
        if (_modalOpen) return;
        _modalOpen = true;
        try
        {
            using var w = new AboutWindow();
            if (w.IsValid) w.ShowModal();
        }
        finally { _modalOpen = false; }
    }

    /// <summary>
    /// 扫码配对向导：菜单「重新配置设备…」和「首次启动还没配对」都走这里。
    /// 窗口自己画（0.3.3 那份是 WinForms，AOT 版没有 WinForms），完成后
    /// settings 已经落盘，这里只负责把设备连接重启一次。
    /// </summary>
    public void ShowWizard()
    {
        if (_wizard is { IsValid: true })
        {
            _wizard.ShowWizard();
            return;
        }
        if (_wizard is not null)
        {
            var dead = _wizard;
            _wizard = null;
            dead.Dispose();
        }

        var w = new WizardWindow(_settings, _ui);
        if (!w.IsValid)
        {
            w.Dispose();
            MessageBox("向导窗口建不起来（系统资源不足或 DPI 异常），先用托盘菜单里的「打开设置文件」手工填 Mac / TokenHex。",
                Native.MB_OK | Native.MB_ICONERROR);
            return;
        }
        _wizard = w;
        w.Completed += () =>
        {
            _wizard?.Dispose();
            _wizard = null;
            OnWizardCompleted();
        };
        w.Cancelled += () =>
        {
            _wizard?.Dispose();
            _wizard = null;
        };
        w.ShowWizard();
    }

    private void OnWizardCompleted()
    {
        _statusOverride = string.Empty;
        _statusOverrideUntil = DateTime.MinValue;
        SetStatus("配对好了，正在连…", 20);
        if (_clientStarted) _client.Restart();
        else { _client.Start(); _clientStarted = true; }   // 首次配对：循环这时候才第一次起来
        _tray.ShowBalloon("配对好了", "正在连这台充电器，连上就能看功率了。");
        ShowPanel(true);
    }

    /// <summary>菜单「检查更新…」：用户主动点的，那就什么都直说。</summary>
    private void CheckUpdates() => RunUpdateCheck(true);

    /// <param name="manual">
    /// true = 用户点的菜单，有话直说（弹框、告诉他已经是最新版）；
    /// false = 启动时自动查的那一遍，安静点：只有真发现新版才点红点 + 冒个气泡，不拿弹框拦人。
    /// </param>
    private void RunUpdateCheck(bool manual)
    {
        if (_updating)
        {
            if (manual) MessageBox("正在下载新版本，等它下完再说。", Native.MB_OK | Native.MB_ICONINFORMATION);
            return;
        }

        if (_checking)
        {
            if (manual) SetStatus("正在检查更新，稍等一下…", 6);
            return;
        }

        _checking = true;
        if (manual) SetStatus("正在检查更新…", 20);
        _ = Task.Run(async () =>
        {
            var res = await UpdateCheck.RunAsync();
            _ui.Post(_ =>
            {
                _checking = false;
                AfterUpdateCheck(res, manual);
            }, null);
        });
    }

    /// <summary>
    /// 查到版本之后：能原地替换就问「下不下」，不能就老实退回打开下载页。
    /// 「不能」的三种情况都在这里提前判掉，免得下完 9 MB 才发现装不进去。
    /// </summary>
    private void AfterUpdateCheck(UpdateCheck.Result res, bool manual)
    {
        if (_quitting) return;
        var cur = UpdateCheck.Current;

        if (!res.Ok)
        {
            LogSink.Write("[update] 检查失败: " + res.Error);
            if (manual)
                MessageBox("这次没检查成：" + res.Error + "\n\n可能是没网，或者 GitHub 一时连不上。稍后再试，也可以自己去这里看：\n" +
                          UpdateCheck.RepoUrl + "/releases", Native.MB_OK | Native.MB_ICONWARNING);
            return;
        }

        if (res.Latest is null)
        {
            LogSink.Write("[update] 没读出最新版本号");
            if (manual)
                MessageBox("没读出最新版本号，自己去这里看吧：\n" + UpdateCheck.RepoUrl + "/releases",
                    Native.MB_OK | Native.MB_ICONINFORMATION);
            return;
        }

        if (res.Latest <= cur)
        {
            // 之前查到的那个版本已经不新了（或者已经装上去了）：把红点收掉
            if (_newVersion.Length > 0)
            {
                _newVersion = string.Empty;
                UpdateTray(_snap);
            }
            if (manual)
                MessageBox("已经是最新版（" + cur + "）。", Native.MB_OK | Native.MB_ICONINFORMATION);
            return;
        }

        LogSink.Write("[update] 有新版本 " + res.Latest + "（当前 " + cur + "）asset=" + res.AssetName +
                      " size=" + res.AssetSize + " sha=" + res.AssetSha256);
        SetStatus("有新版本 " + res.Latest, manual ? 30 : 120);

        string latest = res.Latest.ToString();
        bool first = _newVersion != latest;
        _newVersion = latest;
        UpdateTray(_snap);   // 托盘图标右上角点亮红点，菜单里「检查更新…」也会带上版本号

        if (!manual)
        {
            // 启动时自动查到的：不拦人，只冒一个气泡；要不要装交给用户自己点
            if (first)
                _tray.ShowBalloon("有新版本 " + res.Latest,
                    "右键点这个图标 →「检查更新…」就能自动下载替换。不想每次启动都查，可以在设置文件里把 CheckUpdatesOnStart 改成 false。",
                    Native.NIIF_INFO);
            return;
        }

        string head = "有新版了：" + res.Latest + "（你现在用的是 " + cur + "）。\n\n";

        string? blocker = null;
        if (res.AssetUrl.Length == 0) blocker = "这次发布里没找到能直接替换的 exe";
        else if (res.AssetSize <= 0) blocker = "发布里没写文件大小，没法校验";
        else if (!Updater.CanWriteDir(Installer.SelfDir)) blocker = "程序所在目录不可写（" + Installer.SelfDir + "）";

        if (blocker is not null)
        {
            LogSink.Write("[update] 不能自动更新: " + blocker);
            int openPage = Native.MessageBoxW(Hwnd,
                head + "这一份没法自动更新：" + blocker + "。\n\n现在打开下载页？",
                "检查更新", Native.MB_YESNO | Native.MB_ICONINFORMATION);
            if (openPage == Native.IDYES) OpenUrl(res.ReleaseUrl);
            return;
        }

        int go = Native.MessageBoxW(Hwnd,
            head +
            "现在下载并更新吗？\n\n" +
            "• 下载 " + Mb(res.AssetSize) + "，先校验 SHA256 再替换\n" +
            "• 装在哪就更新到哪，不需要管理员权限\n" +
            "• 换好之后程序自动重启，充电器要重新连一次（约 4 秒）\n\n" +
            "选「否」只打开下载页，不动现在的程序。",
            "检查更新", Native.MB_YESNOCANCEL | Native.MB_ICONQUESTION);

        if (go == Native.IDYES) StartUpdate(res);
        else if (go == Native.IDNO) OpenUrl(res.ReleaseUrl);
    }

    /// <summary>下载到 exe 同目录（同卷改名才是原子的），进度写进托盘悬停提示。</summary>
    private void StartUpdate(UpdateCheck.Result res)
    {
        _updating = true;
        string staged = Updater.StagedPath;
        long reported = 0;
        SetStatus("正在下载更新 0%（共 " + Mb(res.AssetSize) + "）", 600);
        LogSink.Write("[update] 开始下载 " + res.AssetUrl + " -> " + staged);

        _ = Task.Run(async () =>
        {
            void Progress(long got, long total)
            {
                if (got == reported) return;
                reported = got;
                string text = "正在下载更新 " + Percent(got, total) + "%（" + Mb(got) + "/" + Mb(total) + "）";
                _ui.Post(_ => SetStatus(text, 600), null);
            }

            var r = await Updater.DownloadAsync(res.AssetUrl, res.AssetSize, res.AssetSha256, staged, Progress, CancellationToken.None);
            _ui.Post(_ => AfterUpdateDownloaded(res, r), null);
        });
    }

    private void AfterUpdateDownloaded(UpdateCheck.Result res, Updater.DownloadResult r)
    {
        _updating = false;
        if (_quitting) return;

        if (!r.Ok)
        {
            SetStatus("更新下载失败", 30);
            int go = Native.MessageBoxW(Hwnd,
                "新版本没下载成：\n" + r.Error + "\n\n要不要打开下载页手动下载？",
                "更新没成", Native.MB_YESNO | Native.MB_ICONWARNING);
            if (go == Native.IDYES) OpenUrl(res.ReleaseUrl);
            return;
        }

        LogSink.Write("[update] 校验通过，准备替换：" + r.Path);

        // 正在跑的 exe 换不掉，只能起一份副本在自己退出后动手
        if (!Updater.LaunchApply(out var err))
        {
            LogSink.Write("[update] 起更新助手失败: " + err);
            MessageBox("新版本下载好了，但没法自动替换（" + err + "）。\n\n" +
                       "关掉本程序，再把这个文件改名成 CuktechTray.exe 覆盖原文件：\n" + r.Path,
                Native.MB_OK | Native.MB_ICONWARNING);
            return;
        }

        SetStatus("正在重启完成更新…", 600);
        MessageBox("下载好了，点确定就开始重启，几秒后自动回来。\n\n升级完充电器会重新连一次。",
            Native.MB_OK | Native.MB_ICONINFORMATION);
        ExitApp();
    }

    private static string Mb(long bytes) => bytes <= 0 ? "?" : (bytes / 1048576.0).ToString("F1") + " MB";

    private static int Percent(long got, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(got * 100L / total, 0, 100);

// ---------------- 安装 / 卸载 ----------------

    /// <summary>
    /// 这一版把安装做进了主程序：绿色版和安装版共用一个 exe，
    /// 「安装」= 把正在跑的这份复制到用户目录 + 开始菜单快捷方式 + HKCU 卸载入口，无需 UAC。
    /// </summary>
    private void InstallToMachine()
    {
        string dir = Installer.DefaultDir;
        if (Installer.IsInstalledHere)
        {
            MessageBox("这一份就是从安装目录跑起来的，不用再装一遍。\n\n" + Installer.SelfDir,
                Native.MB_OK | Native.MB_ICONINFORMATION);
            return;
        }

        string? existing = Installer.InstalledDir;
        string hint = existing is not null && !Installer.SamePath(existing, dir)
            ? "\n\n（这台电脑现在记的安装位置是：" + existing + "，安装会把它改到上面这个目录）"
            : string.Empty;

        int r = Native.MessageBoxW(Hwnd,
            "把「" + Installer.ShortName + "」装到这台电脑？\n\n" +
            "安装位置：" + dir + "\n" +
            "会创建开始菜单快捷方式，并在「设置 → 应用」里留下卸载入口；\n" +
            "只写当前用户（HKCU + 用户目录），不需要管理员权限。" +
            hint + "\n\n装完请从开始菜单打开装好的那一份，这份绿色版可以直接退出。",
            "安装到本机", Native.MB_YESNO | Native.MB_ICONQUESTION);
        if (r != Native.IDYES) return;

        SetStatus("正在安装…");
        _ = Task.Run(() =>
        {
            bool ok = Installer.Install(dir, IsAutoStartEnabled(), false, out var error, out var warn);
            _ui.Post(_ =>
            {
                if (ok)
                {
                    LogSink.Write("[install] 用户从托盘菜单安装成功 dir=" + dir);
                    MessageBox("已经装好了：\n" + dir +
                        "\n\n开始菜单里能找到「" + Installer.ShortName + "」，" +
                        "「设置 → 应用」里也有卸载入口。\n\n现在可以从开始菜单打开装好的那一份了。" +
                        (string.IsNullOrEmpty(warn) ? "" : "\n\n（有件事没成：" + warn + "）"),
                        Native.MB_OK | Native.MB_ICONINFORMATION);
                }
                else
                {
                    MessageBox("安装没成：" + error, Native.MB_OK | Native.MB_ICONWARNING);
                }
            }, null);
        });
    }

    private void UninstallFromMachine()
    {
        string dir = Installer.InstalledDir ?? Installer.SelfDir;
        int r = Native.MessageBoxW(Hwnd,
            "从这台电脑上卸掉「" + Installer.ShortName + "」？\n\n" +
            "会删除：" + dir + "\n" +
            "还有开始菜单快捷方式，以及「设置 → 应用」里的这条记录。\n\n" +
            "不会动 %LOCALAPPDATA%\\CuktechTray 里的设置和日志。",
            "卸载", Native.MB_YESNO | Native.MB_ICONQUESTION);
        if (r != Native.IDYES) return;

        SetStatus("正在卸载…");
        _ = Task.Run(() =>
        {
            bool ok = Installer.Uninstall(out var error);
            _ui.Post(_ =>
            {
                if (ok)
                {
                    LogSink.Write("[uninstall] 用户从托盘菜单卸载成功");
                    MessageBox("已卸载，程序就要退出了。", Native.MB_OK | Native.MB_ICONINFORMATION);
                    ExitApp();
                }
                else
                {
                    MessageBox("卸载没成：" + error, Native.MB_OK | Native.MB_ICONWARNING);
                }
            }, null);
        });
    }

    private void MessageBox(string text, uint type)
    {
        Native.MessageBoxW(Hwnd, text, "酷态科托盘", type);
    }

    private void OpenUrl(string url)
    {
        try { Native.ShellExecuteW(Hwnd, "open", url, null, null, Native.SW_SHOWNORMAL); }
        catch (Exception ex) { LogSink.Write("[update] 打开链接失败: " + ex.Message); }
    }

    private void OpenLog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(path);
        Native.ShellExecuteW(Hwnd, "open", path, null, null, Native.SW_SHOWNORMAL);
    }

    private void OpenSettings()
    {
        if (File.Exists(Settings.FilePath))
            Native.ShellExecuteW(Hwnd, "open", Settings.FilePath, null, null, Native.SW_SHOWNORMAL);
        else
            MessageBox("配置文件不存在：\n" + Settings.FilePath + "\n\n请重新运行程序以自动导入，或手工创建。",
                Native.MB_OK | Native.MB_ICONINFORMATION);
    }

    public void ExitApp()
    {
        if (_quitting) return;
        _quitting = true;
        try { _widget?.Stop(); } catch { }
        _widget = null;
        try { _wizard?.Dispose(); } catch { }
        _wizard = null;
        try { _tray.Remove(); } catch { }
        try
        {
            if (IsValid) Native.WTSUnRegisterSessionNotification(Hwnd);
            Native.KillTimer(Hwnd, TimerId);
        }
        catch { }
        try
        {
            // 别让蓝牙断开卡住退出：最多等 1.5 秒
            var stop = Task.Run(() => _client.StopAsync());
            stop.Wait(1500);
        }
        catch { }
        _icon?.Dispose();
        _icon = null;
        try { _panel.Dispose(); } catch { }
        Native.PostQuitMessage(0);
    }

    public override void Dispose()
    {
        try { Native.KillTimer(Hwnd, TimerId); } catch { }
        _icon?.Dispose();
        _icon = null;
        base.Dispose();
    }
}

/// <summary>启动时是否主动弹一次面板。</summary>
internal enum ShowOnStart
{
    None,
    Corner,
    Center,
}