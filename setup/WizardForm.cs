using System.Drawing;
using System.Windows.Forms;

namespace CuktechTraySetup;

/// <summary>图形安装向导：欢迎 → 选项 → 进度 → 完成。</summary>
internal sealed class WizardForm : Form
{
    private readonly SetupCommand _command;

    private readonly Label _lblHeaderTitle = new();
    private readonly Label _lblHeaderSubtitle = new();
    private readonly Panel _body = new();
    private readonly Label _lblFooter = new();
    private readonly Button _btnBack;
    private readonly Button _btnNext;
    private readonly Button _btnCancel;

    private readonly Panel _pageWelcome = NewPage();
    private readonly Panel _pageOptions = NewPage();
    private readonly Panel _pageProgress = NewPage();
    private readonly Panel _pageDone = NewPage();

    private readonly TextBox _txtDir = new();
    private Button _btnBrowse = null!;
    private readonly Label _lblDirWarn = new();
    private readonly CheckBox _chkDesktop = new();
    private readonly CheckBox _chkStartMenu = new();
    private readonly CheckBox _chkAutoStart = new();
    private readonly CheckBox _chkLaunch = new();
    private readonly Label _lblRunning = new();

    private readonly ProgressBar _progress = new();
    private readonly Label _lblProgress = new();
    private readonly TextBox _txtLog = new();

    private readonly Label _lblDoneHead = new();
    private readonly Label _lblDoneBody = new();

    private int _step;
    private bool _busy;

    /// <summary>已安装的旧版本号；null = 没装过，走首次安装。</summary>
    private readonly string? _upgradeFrom;

    /// <summary>注册表里记着的安装目录（升级时装回这里）。</summary>
    private readonly string? _existingDir;

    /// <summary>安装位置是否锁死。升级默认锁在老目录——改路径等于在别处又装一份。</summary>
    private bool _dirLocked;

    private bool IsUpgrade => _upgradeFrom is not null;

    public WizardForm(SetupCommand command)
    {
        _command = command;

        // 先探一遍是不是已经装过：文案、默认位置、勾选状态都据此走“升级”分支
        _existingDir = SetupCommand.ExistingInstallDir();
        _upgradeFrom = RegistryStore.HasUninstallEntry()
            ? RegistryStore.ReadUninstallValue("DisplayVersion") ?? "旧版本"
            : null;
        _dirLocked = command.InstallDir is null && _existingDir is not null;

        Text = AppInfo.AppName + (IsUpgrade ? " 升级程序" : " 安装程序");
        ClientSize = new Size(660, 472);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        Font = Ui.BodyFont;
        ShowIcon = false;

        _btnBack = Ui.SecondaryButton("上一步");
        _btnNext = Ui.PrimaryButton("下一步");
        _btnCancel = Ui.SecondaryButton("取消");

        BuildHeader();
        BuildFooter();
        BuildWelcomePage();
        BuildOptionsPage();
        BuildProgressPage();
        BuildDonePage();

        _body.Dock = DockStyle.Fill;
        _body.BackColor = Color.White;
        _body.Controls.Add(_pageWelcome);
        _body.Controls.Add(_pageOptions);
        _body.Controls.Add(_pageProgress);
        _body.Controls.Add(_pageDone);

        Controls.Add(_body);
        Controls.Add(_footerPanel);
        // Dock 布局按 z 序倒着算：Fill 的控件必须排在最后被布局，才是“剩下的空间”
        Controls.SetChildIndex(_body, 0);

        _btnBack.Click += (_, _) => GoTo(0);
        _btnNext.Click += OnNext;
        _btnCancel.Click += (_, _) => Close();
        _txtDir.TextChanged += (_, _) => ValidateDirUi();

        // 升级就装回老目录；命令行显式给了 --dir 时以命令行为准
        _txtDir.Text = _command.InstallDir is not null
            ? Paths.Normalize(_command.InstallDir)
            : _existingDir ?? AppInfo.DefaultInstallDir;

        // 升级时按现在装着的状态回填：这些勾选框决定“保留还是删掉”，别替用户改主意
        _chkDesktop.Checked = !IsUpgrade || Shortcuts.Exists(AppInfo.DesktopShortcutPath);
        _chkStartMenu.Checked = !IsUpgrade || Shortcuts.Exists(AppInfo.StartMenuShortcutPath);
        _chkAutoStart.Checked = IsUpgrade && !string.IsNullOrEmpty(RegistryStore.ReadAutoStart());
        _chkLaunch.Checked = true;
        ApplyDirLock();

        var running = EnvironmentProbe.CountRunning();
        if (running > 0)
        {
            _lblRunning.Text = "检测到 " + running + " 个正在运行的实例。安装前会先请你确认关闭它（它的托盘图标会暂时消失）。";
            _lblRunning.Visible = true;
        }

        ValidateDirUi();
        GoTo(0);
    }

    private Panel _footerPanel = null!;

    private static Panel NewPage() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.White,
        Visible = false,
    };

    private void BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 78,
            BackColor = Color.White,
            Padding = new Padding(0),
        };

        _lblHeaderTitle.Location = new Point(24, 14);
        _lblHeaderTitle.Font = Ui.TitleFont;
        _lblHeaderTitle.AutoSize = true;
        _lblHeaderTitle.Text = "欢迎";

        _lblHeaderSubtitle.Location = new Point(26, 48);
        _lblHeaderSubtitle.Font = Ui.SubtitleFont;
        _lblHeaderSubtitle.ForeColor = Ui.Muted;
        _lblHeaderSubtitle.AutoSize = true;

        var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(224, 224, 224) };

        header.Controls.Add(_lblHeaderTitle);
        header.Controls.Add(_lblHeaderSubtitle);
        header.Controls.Add(line);
        Controls.Add(header);
    }

    private void BuildFooter()
    {
        _footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = Ui.FooterBack,
        };

        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(216, 216, 216) };

        _lblFooter.Location = new Point(24, 20);
        _lblFooter.AutoSize = true;
        _lblFooter.ForeColor = Ui.Muted;
        _lblFooter.Font = Ui.BodyFont;

        const int right = 660 - 24 - 96;
        _btnNext.Location = new Point(right, 14);
        _btnBack.Location = new Point(right - 8 - 96, 14);
        _btnCancel.Location = new Point(right - 16 - 192, 14);

        _footerPanel.Controls.Add(line);
        _footerPanel.Controls.Add(_lblFooter);
        _footerPanel.Controls.Add(_btnNext);
        _footerPanel.Controls.Add(_btnBack);
        _footerPanel.Controls.Add(_btnCancel);
    }

    private void BuildWelcomePage()
    {
        var text = IsUpgrade ? UpgradeWelcomeText() : FreshWelcomeText();

        var label = new Label
        {
            Text = text,
            Location = new Point(24, 16),
            Size = new Size(612, 290),
            AutoSize = false,
            Font = Ui.BodyFont,
        };

        var ver = new Label
        {
            Text = "安装包内程序版本 " + PayloadInfo.AppVersion +
                   "  ·  " + (PayloadInfo.RawSize / 1024 / 1024) + " MB 单文件，无需另行安装 .NET 运行时",
            Location = new Point(24, 306),
            AutoSize = false,
            Size = new Size(612, 22),
            Font = Ui.BodyFont,
            ForeColor = Ui.Muted,
        };

        _pageWelcome.Controls.Add(label);
        _pageWelcome.Controls.Add(ver);
    }

    /// <summary>升级欢迎页：讲清楚“就地覆盖、配置和快捷方式都保留”。</summary>
    private string UpgradeWelcomeText() =>
        "检测到这台电脑上已经装过「" + AppInfo.AppName + "」" + _upgradeFrom + "。\r\n" +
        "\r\n" +
        "这次运行会把它就地升级到 " + PayloadInfo.AppVersion + "：\r\n" +
        "  · 安装位置不变：" + (_existingDir ?? AppInfo.DefaultInstallDir) + "\r\n" +
        "  · 你的配置 settings.json 和 logs 日志都保留，不会被清掉\r\n" +
        "  · 桌面 / 开始菜单快捷方式、开机自启按现在装着的状态保留\r\n" +
        "  · 「设置 → 应用」里那条卸载项的版本号会更新成 " + PayloadInfo.AppVersion + "\r\n" +
        "\r\n" +
        "如果旧版本正在运行，安装前会先把它关掉（托盘图标会消失一下）。\r\n" +
        "\r\n" +
        "只想看看不想升级？直接关掉这个窗口，什么都不会改。";

    /// <summary>首次安装的欢迎页。</summary>
    private static string FreshWelcomeText() =>
            "这个安装程序会把「" + AppInfo.AppName + "」装到你的用户目录，装完就能在托盘里看到充电功率。\r\n" +
            "\r\n" +
            "装到哪里\r\n" +
            "  默认 " + AppInfo.DefaultInstallDir + "，不需要管理员权限；下一步可以改。\r\n" +
            "\r\n" +
            "会写哪些东西\r\n" +
            "  · 主程序 " + AppInfo.ExecutableName + " 和卸载程序 " + AppInfo.SetupFileName + "\r\n" +
            "  · 按你的勾选创建桌面 / 开始菜单快捷方式、写入开机自启（只写当前用户，不碰系统目录）\r\n" +
            "  · 在「设置 → 应用 → 已安装的应用」里登记一条卸载项\r\n" +
            "\r\n" +
            "怎么卸载\r\n" +
            "  在「已安装的应用」里点卸载，或运行安装目录里的 " + AppInfo.SetupFileName + " --uninstall。\r\n" +
            "  卸载时会单独问要不要一起删掉配置和日志，默认保留。";

    private void BuildOptionsPage()
    {
        var lblDir = Ui.Label(IsUpgrade
            ? "安装位置（检测到已装 " + _upgradeFrom + "，将原地升级）："
            : "安装位置：");
        lblDir.Location = new Point(24, 18);

        _txtDir.Location = new Point(24, 42);
        _txtDir.Size = new Size(500, 24);
        _txtDir.Font = Ui.BodyFont;

        _btnBrowse = Ui.SecondaryButton("浏览…");
        _btnBrowse.Size = new Size(96, 26);
        _btnBrowse.Location = new Point(532, 41);
        _btnBrowse.Click += OnBrowse;

        _lblDirWarn.Location = new Point(24, 72);
        _lblDirWarn.Size = new Size(612, 36);
        _lblDirWarn.AutoSize = false;
        _lblDirWarn.Font = Ui.BodyFont;
        _lblDirWarn.ForeColor = Ui.Danger;
        _lblDirWarn.Visible = false;

        _chkDesktop.Text = "创建桌面快捷方式";
        _chkDesktop.Location = new Point(24, 116);
        _chkDesktop.AutoSize = true;
        _chkDesktop.Font = Ui.BodyFont;

        _chkStartMenu.Text = "创建开始菜单快捷方式";
        _chkStartMenu.Location = new Point(24, 144);
        _chkStartMenu.AutoSize = true;
        _chkStartMenu.Font = Ui.BodyFont;

        _chkAutoStart.Text = "开机时自动启动（只写当前用户启动项，启动后仅留托盘图标）";
        _chkAutoStart.Location = new Point(24, 172);
        _chkAutoStart.AutoSize = true;
        _chkAutoStart.Font = Ui.BodyFont;

        _chkLaunch.Text = "安装完成后立即启动";
        _chkLaunch.Location = new Point(24, 200);
        _chkLaunch.AutoSize = true;
        _chkLaunch.Font = Ui.BodyFont;

        _lblRunning.Location = new Point(24, 234);
        _lblRunning.Size = new Size(612, 34);
        _lblRunning.AutoSize = false;
        _lblRunning.Font = Ui.BodyFont;
        _lblRunning.ForeColor = Ui.Warning;
        _lblRunning.Visible = false;

        var hint = new Label
        {
            Text = "以上全部只影响当前用户（HKCU），不会弹 UAC、不写系统目录。",
            Location = new Point(24, 286),
            AutoSize = false,
            Size = new Size(612, 22),
            ForeColor = Ui.Muted,
            Font = Ui.BodyFont,
        };

        _pageOptions.Controls.Add(lblDir);
        _pageOptions.Controls.Add(_txtDir);
        _pageOptions.Controls.Add(_btnBrowse);
        _pageOptions.Controls.Add(_lblDirWarn);
        _pageOptions.Controls.Add(_chkDesktop);
        _pageOptions.Controls.Add(_chkStartMenu);
        _pageOptions.Controls.Add(_chkAutoStart);
        _pageOptions.Controls.Add(_chkLaunch);
        _pageOptions.Controls.Add(_lblRunning);
        _pageOptions.Controls.Add(hint);
    }

    private void BuildProgressPage()
    {
        _lblProgress.Location = new Point(24, 16);
        _lblProgress.Size = new Size(612, 22);
        _lblProgress.AutoSize = false;
        _lblProgress.Font = Ui.BodyFont;

        _progress.Location = new Point(24, 46);
        _progress.Size = new Size(612, 22);
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Style = ProgressBarStyle.Continuous;

        _txtLog.Location = new Point(24, 84);
        _txtLog.Size = new Size(612, 218);
        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.WordWrap = false;
        _txtLog.Font = Ui.MonoFont;
        _txtLog.BackColor = Color.FromArgb(250, 250, 250);

        _pageProgress.Controls.Add(_lblProgress);
        _pageProgress.Controls.Add(_progress);
        _pageProgress.Controls.Add(_txtLog);
    }

    private void BuildDonePage()
    {
        _lblDoneHead.Location = new Point(24, 12);
        _lblDoneHead.AutoSize = true;
        _lblDoneHead.Font = Ui.TitleFont;
        _lblDoneHead.ForeColor = Ui.Accent;
        _lblDoneHead.Text = "安装完成";

        _lblDoneBody.Location = new Point(24, 60);
        _lblDoneBody.Size = new Size(612, 250);
        _lblDoneBody.AutoSize = false;
        _lblDoneBody.Font = Ui.BodyFont;

        _pageDone.Controls.Add(_lblDoneHead);
        _pageDone.Controls.Add(_lblDoneBody);
    }

    private static string? SafeNormalize(string text)
    {
        try
        {
            return Paths.Normalize(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>升级时把安装位置锁在老目录：换路径等于在别处再装一份新的，不该是默认行为。</summary>
    private void ApplyDirLock()
    {
        _txtDir.ReadOnly = _dirLocked;
        _txtDir.BackColor = _dirLocked ? Ui.FooterBack : Color.White;
        _btnBrowse.Text = _dirLocked ? "改到别处…" : "浏览…";
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        if (_dirLocked)
        {
            var answer = MessageBox.Show(this,
                "这个程序已经装在：\r\n" + _txtDir.Text + "\r\n\r\n" +
                "升级默认就地覆盖旧版本，旧的那份会被换掉、不会多出一份。\r\n" +
                "换到别的目录等于在那边再装一份新的，原来的目录不会被删掉（也不会再出现在「已安装的应用」里）。\r\n\r\n" +
                "确实要换个位置吗？",
                "要换安装位置吗？",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            _dirLocked = false;
            ApplyDirLock();
        }

        var current = SafeNormalize(_txtDir.Text.Trim());
        var start = current is null ? AppInfo.DefaultInstallDir : Paths.ExistingAncestor(current);
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择安装位置",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = start,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
        {
            _txtDir.Text = dlg.SelectedPath;
        }
    }

    private (bool Ok, string Message) CheckDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return (false, "请填写安装位置。");

        string full;
        try
        {
            full = Paths.Normalize(dir);
        }
        catch (Exception ex)
        {
            return (false, "这个路径看不懂：" + ex.Message);
        }

        var unsafeReason = Paths.UnsafeReason(full);
        if (unsafeReason is not null) return (false, unsafeReason);

        if (EnvironmentProbe.RequiresElevation(full))
        {
            return (false,
                "当前用户没有权限写入 " + full + "（系统目录需要管理员权限）。" +
                "请改选默认位置 " + AppInfo.DefaultInstallDir + "，或者关掉本向导、右键选择「以管理员身份运行」。");
        }
        return (true, string.Empty);
    }

    private void ValidateDirUi()
    {
        var check = CheckDir(_txtDir.Text.Trim());

        // 锁着的老目录现在不可写（被删了、权限变了）就别锁着了，否则用户卡在这页过不去
        if (_dirLocked && !check.Ok)
        {
            _dirLocked = false;
            ApplyDirLock();
        }
        _lblDirWarn.Text = check.Ok ? string.Empty : check.Message;
        _lblDirWarn.Visible = !check.Ok;
        var normalized = SafeNormalize(_txtDir.Text.Trim());
        _lblFooter.Text = check.Ok && normalized is not null ? "将安装到：" + normalized : string.Empty;
        UpdateButtons();
    }

    private async void OnNext(object? sender, EventArgs e)
    {
        if (_busy) return;

        switch (_step)
        {
            case 0:
                GoTo(1);
                break;
            case 1:
                await StartInstallAsync();
                break;
            case 3:
                Close();
                break;
        }
    }

    private async Task StartInstallAsync()
    {
        var dir = SafeNormalize(_txtDir.Text.Trim());
        if (dir is null)
        {
            MessageBox.Show(this, "这个安装路径看不懂，请重新填写。", "安装位置不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var check = CheckDir(dir);
        if (!check.Ok)
        {
            MessageBox.Show(this, check.Message, "安装位置不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var force = _command.Force;
        var running = EnvironmentProbe.CountRunning();
        if (running > 0 && !force)
        {
            var answer = MessageBox.Show(this,
                "检测到 " + running + " 个正在运行的「" + AppInfo.AppName + "」实例。\r\n\r\n" +
                "安装要替换它的程序文件，所以必须先把它关掉——关掉之后托盘的功率表图标会消失，" +
                "安装完成后可以再打开。\r\n\r\n" +
                "要现在关闭并继续安装吗？（选“否”可以自己先去退出程序）",
                "需要先关闭正在运行的程序",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes) return;
            force = true;
        }

        var options = new InstallOptions
        {
            InstallDir = dir,
            DesktopShortcut = _chkDesktop.Checked,
            StartMenuShortcut = _chkStartMenu.Checked,
            AutoStart = _chkAutoStart.Checked,
            LaunchAfter = _chkLaunch.Checked,
            Force = force,
            Silent = false,
        };

        _busy = true;
        _txtLog.Clear();
        _progress.Value = 0;
        _lblProgress.Text = "准备中…";
        GoTo(2);

        InstallResult? result = null;
        Exception? failure = await Task.Run<Exception?>(() =>
        {
            try
            {
                result = InstallerEngine.Install(options, ReportProgress);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        _busy = false;

        if (failure is not null || result is null)
        {
            Log.Write("[gui] 安装失败：" + failure?.Message);
            MessageBox.Show(this,
                failure?.Message ?? "未知错误",
                "安装失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            GoTo(1);
            return;
        }

        ShowDonePage(result);
        GoTo(3);
    }

    private void ShowDonePage(InstallResult result)
    {
        var sb = new System.Text.StringBuilder();
        if (result.Upgraded)
        {
            sb.Append("已从 ").Append(result.PreviousVersion ?? "旧版本")
              .Append(" 升级到 ").Append(PayloadInfo.AppVersion).Append("。\r\n");
            sb.Append("就地覆盖，没有多装一份；配置和日志都保留着。\r\n\r\n");
        }
        else
        {
            sb.Append("「").Append(AppInfo.AppName).Append("」").Append(PayloadInfo.AppVersion).Append(" 已安装。\r\n\r\n");
        }

        sb.Append("安装位置：" + result.InstallDir + "\r\n");
        sb.Append("主程序：" + AppInfo.ExecutableName + "（" + (result.InstalledBytes / 1024 / 1024) + " MB，完整性已校验）\r\n");
        sb.Append("卸载程序：" + AppInfo.SetupFileName + "\r\n");
        sb.Append("桌面快捷方式：" + (result.DesktopShortcut ? "已创建" : "未创建") + "\r\n");
        sb.Append("开始菜单快捷方式：" + (result.StartMenuShortcut ? "已创建" : "未创建") + "\r\n");
        sb.Append("开机自启：" + (result.AutoStart ? "已设置（" + AppInfo.RunValueName + "）" : "未设置") + "\r\n");
        sb.Append("已自动启动：" + (result.Launched ? "是" : "否") + "\r\n");
        if (result.KilledPids.Count > 0)
        {
            sb.Append("安装前关闭的运行实例：" + string.Join("、", result.KilledPids) + "（PID）\r\n");
        }
        sb.Append("\r\n卸载：设置 → 应用 → 已安装的应用 → " + AppInfo.AppName + " → 卸载；\r\n");
        sb.Append("也可以运行 " + Path.Combine(result.InstallDir, AppInfo.SetupFileName) + " --uninstall");

        _lblDoneBody.Text = sb.ToString();
    }

    private void GoTo(int step)
    {
        _step = step;
        _pageWelcome.Visible = step == 0;
        _pageOptions.Visible = step == 1;
        _pageProgress.Visible = step == 2;
        _pageDone.Visible = step == 3;

        switch (step)
        {
            case 0:
                if (IsUpgrade)
                {
                    _lblHeaderTitle.Text = "升级 " + AppInfo.ShortAppName;
                    _lblHeaderSubtitle.Text = "已安装 " + _upgradeFrom + "，这次升级到 " + PayloadInfo.AppVersion + "，装回原目录";
                    _lblFooter.Text = "下一步确认选项；想取消就直接关掉窗口，什么都不会改。";
                }
                else
                {
                    _lblHeaderTitle.Text = "欢迎使用 " + AppInfo.ShortAppName;
                    _lblHeaderSubtitle.Text = "安装程序 · 版本 " + PayloadInfo.AppVersion + " · 只写当前用户，不需要管理员权限";
                    _lblFooter.Text = "下一步选择安装位置和选项。";
                }
                break;
            case 1:
                _lblHeaderTitle.Text = IsUpgrade ? "升级到 " + PayloadInfo.AppVersion : "选择安装位置";
                _lblHeaderSubtitle.Text = IsUpgrade
                    ? "会覆盖 " + (_existingDir ?? "原安装目录") + " 里的旧版本；位置已经锁好，一般不用改"
                    : "默认装到当前用户目录；装到 Program Files 需要管理员权限";
                _lblFooter.Text = string.Empty;
                break;
            case 2:
                _lblHeaderTitle.Text = "正在安装";
                _lblHeaderSubtitle.Text = "正在解压并写入文件，请稍候…";
                _lblFooter.Text = "安装过程中请不要关闭窗口。";
                break;
            case 3:
                _lblHeaderTitle.Text = IsUpgrade ? "升级完成" : "安装完成";
                _lblHeaderSubtitle.Text = "现在可以从托盘图标开始用了";
                _lblFooter.Text = "点“完成”关闭安装程序。";
                break;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _btnBack.Visible = _step == 1;
        _btnBack.Enabled = !_busy;
        _btnCancel.Visible = _step != 3;
        _btnCancel.Enabled = !_busy;
        _btnNext.Visible = _step != 2;
        _btnNext.Text = _step == 3 ? "完成" : "下一步";
        _btnNext.Enabled = !_busy && (_step != 1 || CheckDir(_txtDir.Text.Trim()).Ok);
    }

    private void ReportProgress(int percent, string text)
    {
        void Apply()
        {
            var value = Math.Clamp(percent, _progress.Minimum, _progress.Maximum);
            if (_progress.Value != value) _progress.Value = value;
            _lblProgress.Text = text;
            _txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\r\n");
        }

        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) BeginInvoke((Action)Apply);
            else Apply();
        }
        catch
        {
            // 窗体已经在关闭途中，忽略
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}
