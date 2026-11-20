using Microsoft.Win32;

namespace CuktechTray;

/// <summary>托盘常驻主体：图标显示总功率，左键开面板，右键功能菜单。</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CuktechTray";

    private static readonly Color ColorIdle = Color.FromArgb(69, 90, 100);
    private static readonly Color ColorLow = Color.FromArgb(46, 125, 50);
    private static readonly Color ColorMid = Color.FromArgb(239, 108, 0);
    private static readonly Color ColorHigh = Color.FromArgb(198, 40, 40);
    private static readonly Color ColorOff = Color.FromArgb(94, 99, 106);

    private readonly Settings _settings;
    private readonly DeviceClient _client;
    private readonly PanelForm _panel;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pinItem;
    private readonly ToolStripMenuItem _taskbarItem;
    private readonly ToolStripMenuItem _updateItem;

    private TaskbarWidget? _widget;

    private IconStyle _iconStyle;
    private System.Windows.Forms.Timer? _saveTimer;

    private Icon? _icon;
    private string _iconKey = "";
    private Snapshot _snap = new();

    private volatile bool _showRequested;

    public TrayApp(Settings settings, ShowOnStart showOnStart = ShowOnStart.Corner,
        EventWaitHandle? showSignal = null, bool openControl = false)
    {
        _settings = settings;
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _iconStyle = Enum.TryParse<IconStyle>(settings.IconStyle, ignoreCase: true, out var parsed) ? parsed : IconStyle.Dark;

        _pinItem = new ToolStripMenuItem("固定面板到桌面", null, (_, _) => _panel!.TogglePin()) { Checked = settings.PanelPinned };
        _taskbarItem = new ToolStripMenuItem("在任务栏显示功率", null, (_, _) => ToggleTaskbarWidget()) { Checked = settings.TaskbarWidget };

        // 面板要能直接下发开关命令，所以设备客户端得先建好——但事件订阅放到下面，
        // 那会儿 _panel 才存在（OnSnapshot 里会碰它）。
        _client = new DeviceClient(settings, ui);

        _panel = new PanelForm(_client, settings);
        _panel.RestorePinned(settings.PanelPinned);
        _panel.PinChanged += (_, _) =>
        {
            _settings.PanelPinned = _panel.Pinned;
            if (_panel.Pinned)
            {
                _settings.PanelX = _panel.Left;
                _settings.PanelY = _panel.Top;
            }
            _settings.Save();
            _pinItem.Checked = _panel.Pinned;
            LogSink.Write($"[panel] pinned={_panel.Pinned} at {_panel.Left},{_panel.Top}");
        };
        _panel.LocationChanged += (_, _) => ScheduleSave();

        // 任务栏里那一小块实时功率（任务栏的子窗口，不置顶、不进 Alt+Tab）
        if (settings.TaskbarWidget) _widget = new TaskbarWidget(settings, () => ShowPanel(true));

        _client.Updated += OnSnapshot;


        _tray = new NotifyIcon
        {
            Visible = true,
            Icon = WattIcon.Create("--", ColorOff, _iconStyle),
            Text = "酷态科 10 号 Ultra",
        };
        _icon = _tray.Icon;
        _iconKey = "--off";

        // 只在「蹲托盘、不弹面板」的启动方式下介绍一次自己的位置，免得第一次用的人找不到图标
        if (showOnStart == ShowOnStart.None && !settings.ShowedTrayTip)
        {
            settings.ShowedTrayTip = true;
            settings.Save();
            _tray.BalloonTipTitle = "酷态科功率常驻在这里";
            _tray.BalloonTipText = "左键点这个图标看实时功率，右键有更多设置。";
            _tray.ShowBalloonTip(6000);
        }

        _statusItem = new ToolStripMenuItem("正在启动…") { Enabled = false };
        _autoStartItem = new ToolStripMenuItem("随 Windows 启动", null, (_, _) => ToggleAutoStart())
        {
            CheckOnClick = false,
            Checked = IsAutoStartEnabled(),
        };

        _updateItem = new ToolStripMenuItem("检查更新…", null, (_, _) => CheckUpdates());

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        UiTheme.Skin(menu);
        menu.Items.Add(new ToolStripMenuItem("显示功率面板", null, (_, _) => ShowPanel(true)) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("显示控制页（四路开关）", null, (_, _) => ShowPanel(true, PanelForm.Tab.Control)));
        menu.Items.Add(_pinItem);
        menu.Items.Add(_taskbarItem);
        var posItem = new ToolStripMenuItem("重置任务栏数字位置", null, (_, _) => ResetTaskbarPosition());
        menu.Items.Add(posItem);
        menu.Items.Add(BuildIconStyleMenu());
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("重新连接", null, (_, _) => _client.Restart()));
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripMenuItem("重新配置设备…", null, (_, _) => ReconfigureDevice()));
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripMenuItem("设置文件", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
        _tray.ContextMenuStrip = menu;

        // 每次弹菜单都看一眼当前是"自动"还是"用户拖过"
        menu.Opening += (_, _) =>
        {
            posItem.Enabled = settings.TaskbarWidget;
            posItem.Text = settings.TaskbarLeftDip >= 0
                ? "重置任务栏数字位置（现在是拖动后的位置）"
                : "重置任务栏数字位置（现在是自动）";
        };

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePanel();
        };

        if (!settings.IsComplete)
        {
            _statusItem.Text = "未配置：settings.json 缺少 MAC/token";
            _tray.BalloonTipTitle = "酷态科托盘";
            _tray.BalloonTipText = "没有找到设备 token。请先运行 tools\\token-fetch 扫码，或手工编辑 settings.json。";
            _tray.ShowBalloonTip(8000);
        }

        // --control：启动后直接把面板翻到控制页（做桌面快捷方式、截图都用得上）
        if (openControl)
        {
            var open = new System.Windows.Forms.Timer { Interval = 500 };
            open.Tick += (_, _) =>
            {
                open.Stop();
                open.Dispose();
                ShowPanel(true, PanelForm.Tab.Control);
            };
            open.Start();
        }

        _client.Start();

        // 锁屏 / 解锁：把「谁在最上面」这件事重新摆正一次。
        // 之前就是这里漏了——锁屏后回来面板偶尔还压在别的窗口上面。
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // 用户再次双击 exe 时，把面板弹出来（而不是毫无反应）
        if (showSignal is not null)
        {
            var pump = new System.Windows.Forms.Timer { Interval = 250 };
            pump.Tick += (_, _) =>
            {
                if (!_showRequested) return;
                _showRequested = false;
                ShowPanel(false);
            };
            pump.Start();

            var waiter = new Thread(() =>
            {
                while (true)
                {
                    try { if (!showSignal.WaitOne()) return; }
                    catch { return; }
                    _showRequested = true;
                }
            }) { IsBackground = true, Name = "show-signal" };
            waiter.Start();
        }

        // 启动后是否主动弹一下面板：让「双击了但没看到窗口」的人立刻知道程序活着
        if (showOnStart != ShowOnStart.None)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 700 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                LogSink.Write($"[panel] startup tick mode={showOnStart}");
                if (_panel.Pinned)
                {
                    ShowPanel(false);
                    return;
                }
                if (showOnStart == ShowOnStart.Center)
                {
                    // 开发用：屏幕中央弹出并且不自动收起，方便截图核对排版
                    var wa = Screen.PrimaryScreen!.WorkingArea;
                    _panel.RestorePinned(true);
                    _panel.SuppressAutoHide = true;
                    _panel.Apply(_snap);
                    _panel.ShowNear(new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2 + _panel.Height));
                }
                else
                {
                    // 正常启动：锚在工作区右下角。先临时关掉「失焦自动收起」——
                    // 启动瞬间窗口没有前台输入权，很容易刚显示就被抢焦点收掉，用户就又「什么都没看到」。
                    _panel.SuppressAutoHide = true;
                    ShowPanel(false);
                    // 开场浮层最多活 20 秒：到点自己收，不给"一直压在最上面"留任何机会
                    _panel.AutoHideAfterMs = 20000;
                    var release = new System.Windows.Forms.Timer { Interval = 12000 };
                    release.Tick += (_, _) =>
                    {
                        release.Stop();
                        release.Dispose();
                        _panel.SuppressAutoHide = false;
                        // 十几秒还没人碰它（鼠标也没停在上面），就当「开场提示」收起来
                        if (_panel.Visible && !_panel.Focused && !_panel.Bounds.Contains(Cursor.Position))
                            _panel.HideNow("startup-timeout");
                    };
                    release.Start();
                }
            };
            timer.Start();
        }
    }

    /// <summary>图标风格子菜单：切换后立即重画托盘图标并写回配置。</summary>
    private ToolStripMenuItem BuildIconStyleMenu()
    {
        var sub = new ToolStripMenuItem("托盘图标风格");
        var options = new (IconStyle Style, string Label)[]
        {
            (IconStyle.Badge, "彩色方块 + 白数字"),
            (IconStyle.Dark, "深色方块 + 底部彩条"),
            (IconStyle.Flat, "纯色方块 + 白数字"),
            (IconStyle.Minimal, "无底色，只有数字"),
            (IconStyle.Bolt, "闪电（不显示数字）"),
        };

        foreach (var (style, label) in options)
        {
            var item = new ToolStripMenuItem(label) { Tag = style, Checked = style == _iconStyle };
            item.Click += (_, _) =>
            {
                _iconStyle = style;
                _settings.IconStyle = style.ToString();
                _settings.Save();
                foreach (ToolStripItem entry in sub.DropDownItems)
                    if (entry is ToolStripMenuItem mi && mi.Tag is IconStyle)
                        mi.Checked = (IconStyle)mi.Tag! == style;
                _iconKey = "";
                UpdateIcon(_snap);
            };
            sub.DropDownItems.Add(item);
        }
        return sub;
    }

    /// <summary>换账号 / 换设备时重新走一遍扫码向导。</summary>
    private void ReconfigureDevice()
    {
        using var wizard = new SetupWizardForm(_settings);
        if (wizard.ShowDialog(_panel.Visible ? _panel : null) != DialogResult.OK) return;
        LogSink.Write($"[cfg] reconfigured mac={_settings.Mac}");
        _client.Restart();
    }

    /// <summary>拖动面板时不要每移动一像素就写盘。</summary>
    private void ScheduleSave()
    {
        if (!_panel.Pinned) return;
        _settings.PanelX = _panel.Left;
        _settings.PanelY = _panel.Top;
        _saveTimer ??= new System.Windows.Forms.Timer { Interval = 1200 };
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimer;
        _saveTimer.Tick += OnSaveTimer;
        _saveTimer.Start();
    }

    private void OnSaveTimer(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
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
    private void ShowPanel(bool anchorToCursor, PanelForm.Tab? tab = null)
    {
        _panel.Apply(_snap);
        if (tab is { } wanted) _panel.SetTab(wanted);
        if (_panel.Pinned)
        {
            // 常驻模式：就是一个普通窗口，不置顶、不抢焦点，被别的窗口盖住就盖住。
            _panel.AutoHideAfterMs = 0; // 常驻不限时，用户自己决定它留多久
            // 位置由用户摆，托盘点击只负责显示/隐藏
            if (_settings.PanelX != int.MinValue)
            {
                _panel.Location = new Point(_settings.PanelX, _settings.PanelY);
                _panel.ShowAtCurrent();
            }
            else
            {
                var wa = Screen.PrimaryScreen!.WorkingArea;
                _panel.ShowNear(new Point(wa.Right, wa.Bottom));
                _settings.PanelX = _panel.Left;
                _settings.PanelY = _panel.Top;
            }
            return;
        }
        // 临时浮层（点托盘弹出来的那种）：跟普通窗口一样出现在最前面，但不置顶；
        // 失焦、锁屏、看门狗任何一条触发就立刻收起来。
        _panel.AutoHideAfterMs = 0; // 主动点开的浮层不自己消失，失焦才收
        var anchor = anchorToCursor ? Cursor.Position : new Point(Screen.PrimaryScreen!.WorkingArea.Right, Screen.PrimaryScreen.WorkingArea.Bottom);
        _panel.ShowNear(anchor);
        LogSink.Write($"[panel] show anchor={anchor.X},{anchor.Y} visible={_panel.Visible} bounds={_panel.Bounds} suppress={_panel.SuppressAutoHide}");
    }

    private void OnSnapshot(Snapshot snap)
    {
        _snap = snap;
        _panel.Apply(snap);
        _widget?.Apply(snap);
        UpdateIcon(snap);
        UpdateTooltip(snap);
        _statusItem.Text = snap.Connected
            ? $"已连接 · 总 {snap.TotalWatts:F1} W"
            : $"未连接 · {snap.Error ?? snap.Status}";
    }

    private void UpdateIcon(Snapshot snap)
    {
        string text;
        Color color;
        if (!snap.Connected)
        {
            text = "--";
            color = ColorOff;
        }
        else
        {
            int watts = (int)Math.Round(snap.TotalWatts);
            text = Math.Clamp(watts, 0, 999).ToString();
            color = watts >= 80 ? ColorHigh : watts >= 40 ? ColorMid : watts >= 1 ? ColorLow : ColorIdle;
        }

        var key = text + "|" + color.ToArgb() + "|" + _iconStyle;
        if (key == _iconKey) return;
        _iconKey = key;

        var next = WattIcon.Create(text, color, _iconStyle);
        var previous = _icon;
        _tray.Icon = next;
        _icon = next;
        // 系统需要短暂保留旧句柄，延后释放
        if (previous is not null) Task.Delay(2000).ContinueWith(_ => { try { previous.Dispose(); } catch { } });
    }

    private void UpdateTooltip(Snapshot snap)
    {
        string text;
        if (!snap.Connected)
        {
            text = $"酷态科 10 号 Ultra\n{snap.Error ?? snap.Status}";
        }
        else
        {
            var parts = snap.Ports.Where(p => p.Working).Select(p => $"{p.Name} {p.Watts:F0}W");
            var detail = string.Join(" · ", parts);
            text = $"总 {snap.TotalWatts:F1} W" + (detail.Length > 0 ? "\n" + detail : "\n四口空闲");
        }
        if (text.Length > 120) text = text[..120];
        _tray.Text = text;
    }

    // ---- 菜单动作 ----

    /// <summary>托盘菜单里的「在任务栏显示功率」：现场挂上或摘掉，不用重启程序。</summary>
    private void ToggleTaskbarWidget()
    {
        if (_widget is null)
        {
            _widget = new TaskbarWidget(_settings, () => ShowPanel(true));
            _widget.Apply(_snap);
        }
        else
        {
            _widget.Stop();
            _widget = null;
        }

        _settings.TaskbarWidget = _widget is not null;
        _settings.Save();
        _taskbarItem.Checked = _widget is not null;
        LogSink.Write($"[taskbar] enabled={_widget is not null}");
    }

    /// <summary>
    /// 托盘菜单里的「重置任务栏数字位置」：把数字放回开始按钮左边。
    /// 用户自己在任务栏上按住数字拖过之后，想复原就用这个。
    /// </summary>
    private void ResetTaskbarPosition()
    {
        if (_widget is null)
        {
            _settings.TaskbarLeftDip = -1;
            _settings.Save();
            _statusItem.Text = "任务栏功率没开，位置已记为自动";
            return;
        }

        _widget.ResetPosition();
        _statusItem.Text = "任务栏数字已放回开始按钮左边";
    }

    private void ToggleAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (IsAutoStartEnabled()) key.DeleteValue(RunValueName, throwOnMissingValue: false);
            else key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\" --tray-only");
            _autoStartItem.Checked = IsAutoStartEnabled();
            _settings.AutoStart = _autoStartItem.Checked;
            _settings.Save();
        }
        catch (Exception ex)
        {
            LogSink.Write("[autostart] " + ex.Message);
            MessageBox.Show("写入注册表失败：" + ex.Message, "酷态科托盘", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string s && s.Trim().Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 只有点了菜单才联网问一次 GitHub，平时（启动、连充电器、轮询读数）不发任何网络请求。
    /// </summary>
    private async void CheckUpdates()
    {
        var item = _updateItem;
        item.Enabled = false;
        item.Text = "正在检查更新…";
        try
        {
            var r = await Task.Run(() => UpdateCheck.RunAsync());
            var cur = UpdateCheck.Current;

            if (!r.Ok)
            {
                LogSink.Write($"[update] 检查失败: {r.Error}");
                MessageBox.Show(
                    $"这次没检查成：{r.Error}\n\n" +
                    $"可能是没网，或者 GitHub 一时连不上。稍后再试，也可以自己去这里看：\n{UpdateCheck.RepoUrl}/releases",
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (r.Latest is null)
            {
                MessageBox.Show($"没读出最新版本号，自己去这里看吧：\n{UpdateCheck.RepoUrl}/releases",
                    "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (r.Latest > cur)
            {
                LogSink.Write($"[update] 有新版本 {r.Latest}（当前 {cur}）");
                var go = MessageBox.Show(
                    $"有新版了：{r.Latest}（你现在用的是 {cur}）。\n\n现在打开下载页？",
                    "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (go == DialogResult.Yes) OpenUrl(r.ReleaseUrl);
                item.Text = $"有新版本 {r.Latest}";
                return;
            }

            MessageBox.Show($"已经是最新版（{cur}）。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            item.Text = "检查更新…";
        }
        finally
        {
            item.Enabled = true;
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogSink.Write($"[update] 打开链接失败: {ex.Message}");
        }
    }

    private void OpenLog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OpenSettings()
    {
        if (File.Exists(Settings.FilePath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true });
        else
            MessageBox.Show($"配置文件不存在：\n{Settings.FilePath}\n\n请重新运行程序以自动导入，或手工创建。",
                "酷态科托盘", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>锁屏 / 切换用户时，把不该留着的临时浮层收掉。</summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                Post(() => _panel.OnSessionLocked());
                break;
        }
    }

    /// <summary>SystemEvents 在别的线程上回调，必须扔回 UI 线程再碰窗口。</summary>
    private void Post(Action action)
    {
        try
        {
            if (_panel.IsDisposed) return;
            if (_panel.InvokeRequired) _panel.BeginInvoke(action);
            else action();
        }
        catch (Exception ex)
        {
            LogSink.Write("[ui-post] " + ex.Message);
        }
    }

    private void ExitApp()
    {
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        _widget?.Stop();
        _widget = null;
        _tray.Visible = false;
        try { _client.StopAsync().GetAwaiter().GetResult(); } catch { }
        _icon?.Dispose();
        _tray.Dispose();
        ExitThread();
    }
}

/// <summary>启动时是否主动弹一次面板。</summary>
internal enum ShowOnStart
{
    None,
    Corner,
    Center,
}
