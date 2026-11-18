using System.Windows.Forms;

namespace CuktechTraySetup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Log.Reset();
        Log.Write("==== 启动 ====");
        Log.Write("程序：" + (Environment.ProcessPath ?? "(未知)"));
        Log.Write("参数：" + (args.Length == 0 ? "(无)" : string.Join(" ", args)));

        var command = SetupCommand.Parse(args);
        var report = new SetupReport { Action = command.Uninstall ? "uninstall" : "install" };

        if (command.Error is not null)
        {
            ConsoleBridge.Attach();
            ConsoleBridge.WriteLine("参数错误：" + command.Error);
            ConsoleBridge.WriteLine("");
            ConsoleBridge.WriteLine(SetupCommand.HelpText);
            report.Fail(command.Error, ExitCodes.BadArguments);
            report.Write(command.ReportPath);
            return ExitCodes.BadArguments;
        }

        if (command.ShowHelp)
        {
            ConsoleBridge.Attach();
            ConsoleBridge.WriteLine(SetupCommand.HelpText);
            return ExitCodes.Ok;
        }

        if (command.Check)
        {
            ConsoleBridge.Attach();
            var checkReport = Diagnostics.Check(command);
            checkReport.ExitCode = ExitCodes.Ok;
            checkReport.Write(command.ReportPath);
            return ExitCodes.Ok;
        }

        var silent = command.Silent;
        if (silent) ConsoleBridge.Attach();

        try
        {
            return command.Uninstall
                ? RunUninstall(command, report)
                : RunInstall(command, report);
        }
        catch (InstallException ex)
        {
            Log.Write("失败：" + ex.Message);
            report.Fail(ex.Message, ex.ExitCode);
            report.Write(command.ReportPath);
            if (!silent)
            {
                InitWinForms();
                MessageBox.Show(ex.Message, command.Uninstall ? "卸载失败" : "安装失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                ConsoleBridge.WriteLine("失败：" + ex.Message);
            }
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Write("异常：" + ex);
            report.Fail(ex.ToString(), ExitCodes.Failed);
            report.Write(command.ReportPath);
            if (!silent)
            {
                InitWinForms();
                MessageBox.Show(ex.Message, command.Uninstall ? "卸载失败" : "安装失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                ConsoleBridge.WriteLine("异常：" + ex.Message);
            }
            return ExitCodes.Failed;
        }
    }

    // ---------------- 安装 ----------------

    private static int RunInstall(SetupCommand command, SetupReport report)
    {
        var options = command.ToInstallOptions();

        if (command.Silent)
        {
            report.Action = "install";
            ConsoleBridge.WriteLine(AppInfo.AppName + " " + PayloadInfo.AppVersion + " 静默安装");
            ConsoleBridge.WriteLine("  目标目录：" + options.InstallDir);
            ConsoleBridge.WriteLine("  内嵌主程序：" + (PayloadInfo.RawSize / 1024 / 1024) + " MB, SHA256 " + PayloadInfo.Sha256);

            var lastPercent = -1;
            var result = InstallerEngine.Install(options, (percent, text) =>
            {
                if (percent / 10 != lastPercent)
                {
                    lastPercent = percent / 10;
                    ConsoleBridge.WriteLine("  [" + percent.ToString().PadLeft(3) + "%] " + text);
                }
            });

            report.Step("主程序写入 " + result.ExePath);
            report.Step("卸载信息写入 HKCU\\" + AppInfo.UninstallKeyPath);
            if (result.DesktopShortcut) report.Step("桌面快捷方式 " + AppInfo.DesktopShortcutPath);
            if (result.StartMenuShortcut) report.Step("开始菜单快捷方式 " + AppInfo.StartMenuShortcutPath);
            if (result.AutoStart) report.Step("开机自启 " + AppInfo.RunValueName);
            Diagnostics.FillInstall(report, result);
            if (result.KilledPids.Count > 0) report.Warn("安装前关闭了正在运行的实例：" + string.Join(",", result.KilledPids));

            report.Result = "ok";
            report.ExitCode = ExitCodes.Ok;
            report.Write(command.ReportPath);

            ConsoleBridge.WriteLine("完成：已安装 " + AppInfo.AppName + " " + PayloadInfo.AppVersion);
            ConsoleBridge.WriteLine("  主程序：" + result.ExePath + "（" + (result.InstalledBytes / 1024 / 1024) + " MB）");
            ConsoleBridge.WriteLine("  卸载：\"" + result.SetupCopyPath + "\" --uninstall");
            return ExitCodes.Ok;
        }

        InitWinForms();
        using var form = new WizardForm(command);
        form.ShowDialog();
        return ExitCodes.Ok;
    }

    // ---------------- 卸载 ----------------

    private static int RunUninstall(SetupCommand command, SetupReport report)
    {
        var options = command.ToUninstallOptions();
        var installDir = InstallerEngine.ResolveInstallDir(options);

        if (installDir is null)
        {
            Log.Write("没有找到安装信息，按“已经卸载”处理。");
            report.Action = "uninstall";
            report.Result = "ok";
            report.ExitCode = ExitCodes.Ok;
            report.Warn("没有找到安装信息，看起来已经卸载过了。");
            report.Add("alreadyUninstalled", "yes");
            report.Write(command.ReportPath);
            ConsoleBridge.WriteLine("没有找到安装信息，看起来已经卸载过了。");
            return ExitCodes.Ok;
        }

        // 卸载器自己就在要删的目录里：先搬到 %TEMP% 再以 --relocated 重来，否则目录删不掉
        if (!command.Relocated && InstallerEngine.TryRelocateForUninstall(installDir, Environment.GetCommandLineArgs().Skip(1).ToArray()))
        {
            Log.Write("卸载器已搬到临时目录，父进程退出。");
            return ExitCodes.Ok;
        }

        options = new UninstallOptions
        {
            InstallDir = installDir,
            PurgeData = options.PurgeData,
            Force = options.Force,
            Silent = options.Silent,
        };

        if (command.Silent)
        {
            report.Action = "uninstall";
            ConsoleBridge.WriteLine("卸载 " + AppInfo.AppName + " ← " + installDir);

            var result = InstallerEngine.Uninstall(options, (percent, text) =>
                ConsoleBridge.WriteLine("  [" + percent.ToString().PadLeft(3) + "%] " + text));

            report.Step("删除安装目录 " + installDir);
            report.Step("删除快捷方式、开机自启、卸载信息项");
            if (result.PurgedData) report.Step("删除配置和日志");
            Diagnostics.FillUninstall(report, result);

            if (!result.RemovedDirectory)
            {
                report.Warn("安装目录没能完全删除，残留：" + string.Join(";", result.Leftovers));
            }
            if (result.PurgedData && !options.PurgeData) report.Warn("内部状态异常：未要求删配置却删了。");

            report.Result = "ok";
            report.ExitCode = ExitCodes.Ok;
            report.Write(command.ReportPath);

            ConsoleBridge.WriteLine("完成：已卸载（安装目录 " + (result.RemovedDirectory ? "已删除" : "有残留") + "，配置和日志 " + (result.PurgedData ? "已删除" : "已保留") + "）");

            InstallerEngine.DeleteSelfAndTempDir();
            return ExitCodes.Ok;
        }

        InitWinForms();
        using var form = new UninstallForm(command, installDir);
        form.ShowDialog();
        if (form.Completed)
        {
            report.Action = "uninstall";
            report.Result = "ok";
            report.ExitCode = ExitCodes.Ok;
            report.Step("图形卸载完成，目录：" + installDir);
            report.Add("installDir", installDir);
            report.Add("installDirExists", Directory.Exists(installDir) ? "yes" : "no");
            report.Add("uninstallKeyExists", RegistryStore.HasUninstallEntry() ? "yes" : "no");
            report.Add("desktopShortcutExists", Shortcuts.Exists(AppInfo.DesktopShortcutPath) ? "yes" : "no");
            report.Add("autostartValue", RegistryStore.ReadAutoStart());
            report.Write(command.ReportPath);
            InstallerEngine.DeleteSelfAndTempDir();
        }
        return ExitCodes.Ok;
    }

    private static void InitWinForms()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    }
}
