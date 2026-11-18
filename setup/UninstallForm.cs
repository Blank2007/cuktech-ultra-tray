using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace CuktechTraySetup;

/// <summary>图形卸载向导：确认（含“是否删配置”）→ 进度 → 完成。</summary>
internal sealed class UninstallForm : Form
{
    private readonly SetupCommand _command;
    private readonly string _installDir;
    private readonly string? _installedVersion;

    private readonly Label _lblHeaderTitle = new();
    private readonly Label _lblHeaderSubtitle = new();
    private readonly Panel _body = new();
    private readonly Button _btnGo;
    private readonly Button _btnCancel;

    private readonly Panel _pageConfirm = NewPage();
    private readonly Panel _pageProgress = NewPage();
    private readonly Panel _pageDone = NewPage();

    private readonly Label _lblInfo = new();
    private readonly CheckBox _chkPurge = new();
    private readonly Label _lblRunning = new();

    private readonly Label _lblProgress = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBox _txtLog = new();

    private readonly Label _lblDone = new();

    private int _step;
    private bool _busy;

    /// <summary>卸载流程是否真的跑完（成功或带残留），Program 据此决定要不要清理临时副本。</summary>
    public bool Completed { get; private set; }

    public UninstallForm(SetupCommand command, string installDir)
    {
        _command = command;
        _installDir = installDir;
        _installedVersion = RegistryStore.ReadUninstallValue("DisplayVersion");

        Text = "卸载 " + AppInfo.ShortAppName;
        ClientSize = new Size(580, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        Font = Ui.BodyFont;
        ShowIcon = false;

        _btnGo = Ui.PrimaryButton("卸载");
        _btnCancel = Ui.SecondaryButton("取消");

        BuildHeader();
        BuildFooter();
        BuildConfirmPage();
        BuildProgressPage();
        BuildDonePage();

        _body.Dock = DockStyle.Fill;
        _body.BackColor = Color.White;
        _body.Controls.Add(_pageConfirm);
        _body.Controls.Add(_pageProgress);
        _body.Controls.Add(_pageDone);

        Controls.Add(_body);
        Controls.Add(_footerPanel);
        Controls.SetChildIndex(_body, 0);

        _btnGo.Click += OnGo;
        _btnCancel.Click += (_, _) => Close();

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
            Height = 72,
            BackColor = Color.White,
        };

        _lblHeaderTitle.Location = new Point(24, 12);
        _lblHeaderTitle.Font = Ui.TitleFont;
        _lblHeaderTitle.AutoSize = true;

        _lblHeaderSubtitle.Location = new Point(26, 44);
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
            Height = 56,
            BackColor = Ui.FooterBack,
        };

        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(216, 216, 216) };

        _btnCancel.Location = new Point(580 - 24 - 96, 12);
        _btnGo.Location = new Point(580 - 24 - 96 - 8 - 96, 12);

        _footerPanel.Controls.Add(line);
        _footerPanel.Controls.Add(_btnCancel);
        _footerPanel.Controls.Add(_btnGo);
    }

    private void BuildConfirmPage()
    {
        var sb = new StringBuilder();
        sb.Append("将从下面这个位置卸载「").Append(AppInfo.AppName).Append("」：\r\n");
        sb.Append("  ").Append(_installDir).Append("\r\n\r\n");
        sb.Append("会删掉：主程序、卸载程序、快捷方式、开机自启项，以及「已安装的应用」里的登记。\r\n");
        if (_installedVersion is not null) sb.Append("已安装版本：").Append(_installedVersion).Append("\r\n");

        _lblInfo.Location = new Point(24, 14);
        _lblInfo.Size = new Size(532, 116);
        _lblInfo.AutoSize = false;
        _lblInfo.Font = Ui.BodyFont;
        _lblInfo.Text = sb.ToString();

        _chkPurge.Text = "同时删除配置和日志（settings.json、logs\\）——不勾选则保留";
        _chkPurge.Location = new Point(24, 138);
        _chkPurge.AutoSize = true;
        _chkPurge.Checked = false;
        _chkPurge.Font = Ui.BodyFont;

        var dataHint = new Label
        {
            Text = "配置和日志的位置：" + AppInfo.DataDir + " 或安装目录里的 settings.json / logs\\",
            Location = new Point(24, 166),
            AutoSize = false,
            Size = new Size(532, 40),
            ForeColor = Ui.Muted,
            Font = Ui.BodyFont,
        };

        _lblRunning.Location = new Point(24, 210);
        _lblRunning.Size = new Size(532, 40);
        _lblRunning.AutoSize = false;
        _lblRunning.ForeColor = Ui.Warning;
        _lblRunning.Font = Ui.BodyFont;
        _lblRunning.Visible = false;

        var running = EnvironmentProbe.CountRunning();
        if (running > 0)
        {
            _lblRunning.Text = "注意：现在有 " + running + " 个正在运行的实例，卸载前会先关闭它（托盘图标会消失）。";
            _lblRunning.Visible = true;
        }

        _pageConfirm.Controls.Add(_lblInfo);
        _pageConfirm.Controls.Add(_chkPurge);
        _pageConfirm.Controls.Add(dataHint);
        _pageConfirm.Controls.Add(_lblRunning);
    }

    private void BuildProgressPage()
    {
        _lblProgress.Location = new Point(24, 16);
        _lblProgress.Size = new Size(532, 22);
        _lblProgress.AutoSize = false;
        _lblProgress.Font = Ui.BodyFont;

        _progress.Location = new Point(24, 46);
        _progress.Size = new Size(532, 22);
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Style = ProgressBarStyle.Continuous;

        _txtLog.Location = new Point(24, 84);
        _txtLog.Size = new Size(532, 180);
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
        _lblDone.Location = new Point(24, 16);
        _lblDone.Size = new Size(532, 260);
        _lblDone.AutoSize = false;
        _lblDone.Font = Ui.BodyFont;

        _pageDone.Controls.Add(_lblDone);
    }

    private async void OnGo(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (_step == 2)
        {
            Close();
            return;
        }

        var running = EnvironmentProbe.CountRunning();
        if (running > 0 && !_command.Force)
        {
            var answer = MessageBox.Show(this,
                "检测到 " + running + " 个正在运行的实例。\r\n\r\n" +
                "卸载需要删掉它的程序文件，所以必须先关闭它——托盘图标会消失。\r\n\r\n" +
                "要现在关闭并继续卸载吗？",
                "需要先关闭正在运行的程序",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes) return;
        }

        var options = new UninstallOptions
        {
            InstallDir = _installDir,
            PurgeData = _chkPurge.Checked,
            Force = _command.Force || running > 0,
            Silent = false,
        };

        _busy = true;
        _txtLog.Clear();
        _progress.Value = 0;
        GoTo(1);

        UninstallResult? result = null;
        Exception? failure = await Task.Run<Exception?>(() =>
        {
            try
            {
                result = InstallerEngine.Uninstall(options, ReportProgress);
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
            Log.Write("[gui] 卸载失败：" + failure?.Message);
            MessageBox.Show(this, failure?.Message ?? "未知错误", "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            GoTo(0);
            return;
        }

        Completed = true;
        ShowDone(result);
        GoTo(2);
    }

    private void ShowDone(UninstallResult result)
    {
        var sb = new StringBuilder();
        sb.Append("「").Append(AppInfo.AppName).Append("」已经卸载。\r\n\r\n");
        sb.Append("安装目录：").Append(result.InstallDir)
          .Append(result.RemovedDirectory ? "（已删除）" : "（目录里还有删不掉的残留，见下）").Append("\r\n");
        sb.Append("快捷方式：" + "已删除\r\n");
        sb.Append("开机自启 / 卸载信息：" + "已清除\r\n");
        if (result.PurgedData)
        {
            sb.Append("配置和日志：" + (result.DataDirRemoved is null ? "已删除（本来就没有用户目录副本）" : "已删除 " + result.DataDirRemoved) + "\r\n");
        }
        else
        {
            sb.Append("配置和日志：已保留（" + AppInfo.DataDir + "）\r\n");
        }

        if (result.Leftovers.Count > 0)
        {
            sb.Append("\r\n下面这些文件删不掉（通常是被别的程序占用），已经登记为重启后删除：\r\n");
            foreach (var item in result.Leftovers) sb.Append("  ").Append(item).Append("\r\n");
        }
        else if (!result.RemovedDirectory)
        {
            sb.Append("\r\n目录没能完全删除，重试也没成功。可以重启电脑后再删一次。\r\n");
        }

        _lblDone.Text = sb.ToString();
    }

    private void GoTo(int step)
    {
        _step = step;
        _pageConfirm.Visible = step == 0;
        _pageProgress.Visible = step == 1;
        _pageDone.Visible = step == 2;

        switch (step)
        {
            case 0:
                _lblHeaderTitle.Text = "卸载 " + AppInfo.ShortAppName;
                _lblHeaderSubtitle.Text = "确认下面的内容，然后点“卸载”";
                break;
            case 1:
                _lblHeaderTitle.Text = "正在卸载";
                _lblHeaderSubtitle.Text = "正在删除文件和注册表项…";
                break;
            case 2:
                _lblHeaderTitle.Text = "卸载完成";
                _lblHeaderSubtitle.Text = "如果不再需要，这个安装程序副本也可以删掉了";
                break;
        }

        _btnGo.Text = step == 2 ? "关闭" : "卸载";
        _btnGo.Enabled = !_busy;
        _btnGo.Visible = step != 1;
        _btnCancel.Visible = step == 0;
        _btnCancel.Enabled = !_busy;
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
            // 窗体已在关闭途中
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
