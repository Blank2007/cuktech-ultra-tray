using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CuktechTray;

/// <summary>
/// 安装 / 卸载：绿色版和安装版共用同一个 exe。
/// 「安装」就是把正在跑的这一份复制到用户目录，再补上开始菜单快捷方式和 HKCU 的卸载入口——
/// 所以不需要另做一个安装包，也就不需要第二个载荷，安装包体积 = 主程序体积。
/// 全部只写 HKCU 和用户目录，全程不需要管理员权限。
/// </summary>
internal static class Installer
{
    public const string AppName = "酷态科 10 号 Ultra 托盘功率表";
    public const string ShortName = "酷态科托盘功率表";
    public const string PublisherName = "CuktechTray";
    public const string ExeName = "CuktechTray.exe";
    public const string ShortcutName = "酷态科托盘功率表.lnk";
    public const string StartMenuGroup = "CuktechTray";
    public const string RunValueName = "CuktechTray";

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CuktechTray";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    /// <summary>默认安装位置：当前用户目录，跟旧安装包一致，升级时直接覆盖同一处。</summary>
    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CuktechTray");

    /// <summary>
    /// 进程启动时系统记下的 exe 路径。文件名在运行中被自己改掉之后（见 Updater.SelfHealName），
    /// 这里换成新路径 —— 不换的话自更新、卸载还会按已经不存在的旧名字找文件。
    /// </summary>
    public static string? OverrideSelfExe;

    public static string SelfExe => OverrideSelfExe ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);

    public static string SelfDir => Path.GetDirectoryName(Path.GetFullPath(SelfExe)) ?? AppContext.BaseDirectory;

    public static string StartMenuDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartMenuGroup);

    public static string StartMenuShortcut => Path.Combine(StartMenuDir, ShortcutName);

    public static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    /// <summary>注册表里记着的安装位置；没装过就是 null。</summary>
    public static string? InstalledDir
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
                return key?.GetValue("InstallLocation") as string;
            }
            catch { return null; }
        }
    }

    /// <summary>当前这份是不是从安装位置跑起来的（决定菜单显示「安装」还是「卸载」）。</summary>
    public static bool IsInstalledHere
    {
        get
        {
            var dir = InstalledDir;
            if (string.IsNullOrWhiteSpace(dir)) return false;
            return SamePath(SelfDir, Path.GetFullPath(dir));
        }
    }

    // ---------------- 命令行 ----------------

    /// <summary>处理 --install / --uninstall / --uninstall-clean；不是这些就返回 null 交给正常流程。</summary>
    public static int? RunCli(string[] args)
    {
        string? clean = ArgValue(args, "--uninstall-clean");
        if (clean is not null)
        {
            CleanUp(clean);
            return 0;
        }

        // 更新助手：等主进程退出后替换 exe 再拉起新版。放最前面是因为它天生要跑在别的进程里
        string? applyTarget = ArgValue(args, "--apply-update");
        if (applyTarget is not null)
        {
            string payload = ArgValue(args, "--payload") ?? "";
            int pid = int.TryParse(ArgValue(args, "--pid"), out var waitPid) ? waitPid : 0;
            if (payload.Length == 0)
            {
                LogSink.Write("[update] 助手缺少 --payload");
                return 1;
            }
            return Updater.ApplyUpdate(Path.GetFullPath(applyTarget), Path.GetFullPath(payload), pid);
        }

        // 开发自测：纯文件层面的替换 / 回滚，不联网也不启动进程
        string? updateTest = ArgValue(args, "--update-selftest");
        if (updateTest is not null) return Updater.SelfTest(updateTest);

        // 开发自测：真下载一次（含校验），用来确认代理 / 直连、进度、SHA256 都正常
        string? downloadTest = ArgValue(args, "--update-download-test");
        if (downloadTest is not null)
        {
            string url = ArgValue(args, "--url") ?? "";
            string sha = ArgValue(args, "--sha") ?? "";
            long size = long.TryParse(ArgValue(args, "--size"), out var wantSize) ? wantSize : 0;
            string dest = Path.Combine(Path.GetFullPath(downloadTest), "probe.download");
            var r = Updater.DownloadAsync(url, size, sha, dest, null, CancellationToken.None).GetAwaiter().GetResult();
            LogSink.Write("[update-download-test] " + (r.Ok
                ? "ok " + new FileInfo(r.Path).Length + " 字节 " + r.Path
                : "失败: " + r.Error));
            return r.Ok ? 0 : 1;
        }

        bool silent = Has(args, "--silent");

        // 开发自测：只验 .lnk 的生成与回读，不碰系统目录
        string? lnkTest = ArgValue(args, "--shortcut-test");
        if (lnkTest is not null)
        {
            string lnk = Path.GetFullPath(lnkTest);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
                Shortcut.Create(lnk, SelfExe, SelfDir, AppName);
                string? back = Shortcut.ReadTarget(lnk);
                bool good = back is not null && SamePath(back, SelfExe);
                LogSink.Write("[shortcut-test] " + (good ? "ok" : "回读不符") + " lnk=" + lnk + " 目标=" + (back ?? "(null)") + " 大小=" + (File.Exists(lnk) ? new FileInfo(lnk).Length : -1));
                return good ? 0 : 1;
            }
            catch (Exception ex)
            {
                LogSink.Write("[shortcut-test] 失败: " + ex.Message);
                return 1;
            }
        }

        if (Has(args, "--uninstall"))
        {
            bool ok = Uninstall(out var err);
            LogSink.Write("[uninstall] " + (ok ? "ok" : "failed: " + err));
            if (!ok && !silent)
            {
                Native.MessageBoxW(IntPtr.Zero, "卸载没成：" + err, "酷态科托盘", Native.MB_OK | Native.MB_ICONWARNING);
            }
            return ok ? 0 : 1;
        }

        if (!Has(args, "--install")) return null;

        string dir = ArgValue(args, "--dir") ?? DefaultDir;
        bool ok2 = Install(dir, Has(args, "--autostart"), Has(args, "--desktop"), out var error, out var warn);
        LogSink.Write("[install] " + (ok2 ? "ok dir=" + dir : "failed: " + error) +
            (string.IsNullOrEmpty(warn) ? "" : " 提醒: " + warn));
        if (!ok2 && !silent)
        {
            Native.MessageBoxW(IntPtr.Zero, "安装没成：" + error, "酷态科托盘", Native.MB_OK | Native.MB_ICONWARNING);
        }
        return ok2 ? 0 : 1;
    }

    // ---------------- 安装 ----------------

    public static bool Install(string targetDir, bool autoStart, bool desktopShortcut, out string error, out string warnings)
    {
        error = string.Empty;
        var notes = new List<string>();
        warnings = string.Empty;
        try
        {
            string dir = Path.GetFullPath(targetDir).TrimEnd('\\');
            if (dir.Length < 8 || IsDriveRoot(dir))
            {
                error = "安装路径不合法：" + dir;
                return false;
            }

            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, ExeName);
            string self = SelfExe;

            if (!SamePath(self, dest))
            {
                // 目标位置要是有份正在跑的，先请它退出，否则 exe 被占用复制不进去
                KillRunning(dest);
                File.Copy(self, dest, overwrite: true);
                LogSink.Write("[install] copied " + self + " -> " + dest);
            }
            else
            {
                LogSink.Write("[install] 已经在安装目录里跑，跳过复制");
            }

            // 便携配置跟着走：装完还是同一台设备，不用重新扫码
            try
            {
                string srcCfg = Settings.FilePath;
                string dstCfg = Path.Combine(dir, "settings.json");
                if (File.Exists(srcCfg) && !SamePath(srcCfg, dstCfg) && !File.Exists(dstCfg))
                {
                    File.Copy(srcCfg, dstCfg, overwrite: false);
                    LogSink.Write("[install] settings.json 一起搬过去了");
                }
            }
            catch (Exception ex)
            {
                LogSink.Write("[install] settings.json 没搬成: " + ex.Message);
            }

            // 快捷方式是锦上添花：某些环境（受限目录、第三方安全软件）写不进去，
            // 不该让整套安装作废，记一条提醒继续装。
            try
            {
                Shortcut.Create(StartMenuShortcut, dest, dir, AppName);
            }
            catch (Exception ex)
            {
                notes.Add("开始菜单快捷方式没建成：" + ex.Message);
                LogSink.Write("[install] 开始菜单快捷方式失败: " + ex.Message);
            }

            try
            {
                if (desktopShortcut) Shortcut.Create(DesktopShortcut, dest, dir, AppName);
                else if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut);
            }
            catch (Exception ex)
            {
                notes.Add("桌面快捷方式没建成：" + ex.Message);
                LogSink.Write("[install] 桌面快捷方式失败: " + ex.Message);
            }

            WriteRegistry(dir, dest, autoStart);
            warnings = string.Join("；", notes);
            LogSink.Write("[install] ok dir=" + dir + " autostart=" + autoStart + " desktop=" + desktopShortcut +
                (warnings.Length == 0 ? "" : " warnings=" + warnings));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            warnings = string.Join("；", notes);
            LogSink.Write("[install] 失败: " + ex);
            return false;
        }
    }

    private static void WriteRegistry(string dir, string exePath, bool autoStart)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法创建卸载信息注册表项：" + UninstallKeyPath);

        key.SetValue("DisplayName", AppName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
        key.SetValue("Publisher", PublisherName, RegistryValueKind.String);
        key.SetValue("DisplayIcon", exePath + ",0", RegistryValueKind.String);
        key.SetValue("InstallLocation", dir, RegistryValueKind.String);
        key.SetValue("UninstallString", Quote(exePath) + " --uninstall", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", Quote(exePath) + " --uninstall --silent", RegistryValueKind.String);
        key.SetValue("EstimatedSize", (int)Math.Max(1, DirSize(dir) / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
        key.SetValue("Language", "zh-CN", RegistryValueKind.String);

        using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (run is null) return;
        if (autoStart) run.SetValue(RunValueName, Quote(exePath) + " --tray-only", RegistryValueKind.String);
        else run.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// exe 改名之后，把还指着旧路径的东西一起搬过去：开机自启（HKCU\...\Run）、
    /// 桌面与开始菜单的快捷方式、卸载项里的显示图标与卸载命令。
    /// 只在这些条目确实指向旧路径时才动；用户自己的快捷方式、别处的启动项一概不碰。
    /// </summary>
    public static void FollowExeRename(string oldExe, string newExe)
    {
        string oldFull = Path.GetFullPath(oldExe);
        string newFull = Path.GetFullPath(newExe);
        string newDir = Path.GetDirectoryName(newFull) ?? SelfDir;

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            string? value = run?.GetValue(RunValueName) as string;
            if (run is not null && value is not null && value.IndexOf(oldFull, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                run.SetValue(RunValueName, Quote(newFull) + " --tray-only", RegistryValueKind.String);
                LogSink.Write("[install] 开机自启改指新文件名 " + newFull);
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("[install] 改自启失败：" + ex.Message);
        }

        try
        {
            foreach (string lnk in new[] { DesktopShortcut, StartMenuShortcut })
            {
                string? current = Shortcut.ReadTarget(lnk);
                if (current is null) continue;
                if (current.IndexOf(oldFull, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Shortcut.Create(lnk, newFull, newDir, AppName);
                LogSink.Write("[install] 快捷方式改指新文件名 " + lnk);
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("[install] 改快捷方式失败：" + ex.Message);
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: true);
            if (key is not null)
            {
                bool touched = false;
                foreach (string name in new[] { "DisplayIcon", "UninstallString", "QuietUninstallString" })
                {
                    string? v = key.GetValue(name) as string;
                    if (v is null || v.IndexOf(oldFull, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    key.SetValue(name, v.Replace(oldFull, newFull, StringComparison.OrdinalIgnoreCase), RegistryValueKind.String);
                    touched = true;
                }
                string? loc = key.GetValue("InstallLocation") as string;
                if (touched || loc is null)
                {
                    key.SetValue("InstallLocation", newDir, RegistryValueKind.String);
                }
                // 版本号要从新文件里读：写这段话的是旧版 exe 的助手副本，AppVersion 还是旧的
                string? newVer = Updater.ExeVersionOf(newFull);
                if (newVer is not null) key.SetValue("DisplayVersion", newVer + ".0", RegistryValueKind.String);
                LogSink.Write("[install] 卸载项改指新文件名（touched=" + touched + "）");
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("[install] 改卸载项失败：" + ex.Message);
        }
    }

    // ---------------- 卸载 ----------------

    public static bool Uninstall(out string error)
    {
        error = string.Empty;
        try
        {
            // 注册表是准的；万一没有，就看自己是不是躺在某个安装目录里
            string? dir = InstalledDir;
            if (dir is null && LooksLikeOurs(SelfDir)) dir = SelfDir;
            if (dir is null)
            {
                error = "没找到安装位置（注册表里没有 InstallLocation）。";
                return false;
            }

            dir = Path.GetFullPath(dir).TrimEnd('\\');
            if (!LooksLikeOurs(dir))
            {
                error = "安装位置看起来不对劲，为安全起见不动它：" + dir;
                return false;
            }

            KillRunning(Path.Combine(dir, ExeName));
            DeleteShortcut(StartMenuShortcut);
            DeleteShortcut(DesktopShortcut);
            try { if (Directory.Exists(StartMenuDir) && !Directory.EnumerateFileSystemEntries(StartMenuDir).Any()) Directory.Delete(StartMenuDir); }
            catch { }

            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); }
            catch (Exception ex) { LogSink.Write("[uninstall] 删卸载项失败: " + ex.Message); }
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                run?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
            catch { }
            LogSink.Write("[uninstall] 快捷方式与注册表已清理 dir=" + dir);

            // 自己不在安装目录里：直接删干净收工
            if (!IsSameOrUnder(SelfDir, dir))
            {
                bool clean = DeleteDirectory(dir);
                LogSink.Write("[uninstall] 目录" + (clean ? "已删除 " : "没删干净 ") + dir);
                if (!clean) error = "目录没删干净：" + dir;
                return true;
            }

            // 自己就在安装目录里：先把自己搬到 temp，让那份去删目录（运行中的 exe 删不掉自己）
            string tempDir = Path.Combine(Path.GetTempPath(), "CuktechTray-uninstall-" + Environment.ProcessId);
            Directory.CreateDirectory(tempDir);
            string tempExe = Path.Combine(tempDir, ExeName);
            File.Copy(SelfExe, tempExe, overwrite: true);

            var psi = new ProcessStartInfo(tempExe, "--uninstall-clean " + Quote(dir))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempDir,
            };
            using var proc = Process.Start(psi);
            LogSink.Write("[uninstall] 扫尾进程已启动 temp=" + tempExe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LogSink.Write("[uninstall] 失败: " + ex);
            return false;
        }
    }

    /// <summary>扫尾：等原来那份进程退出，删安装目录，最后把自己（temp 里的副本）标记为重启后删除。</summary>
    private static void CleanUp(string dir)
    {
        try
        {
            for (int i = 0; i < 24 && Directory.Exists(dir); i++)
            {
                if (DeleteDirectory(dir)) break;
                Thread.Sleep(250);
            }
            LogSink.Write("[uninstall] 扫尾目录 " + dir + (Directory.Exists(dir) ? " 仍在（可能被占用）" : " 已删除"));
        }
        catch (Exception ex)
        {
            LogSink.Write("[uninstall] 扫尾失败: " + ex.Message);
        }
        finally
        {
            string self = SelfExe;
            string? tempDir = Path.GetDirectoryName(self);
            try { NativeMoveFileEx(self, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
            try
            {
                if (tempDir is not null && Directory.Exists(tempDir))
                {
                    foreach (var f in Directory.EnumerateFiles(tempDir))
                    {
                        if (!SamePath(f, self)) { try { File.Delete(f); } catch { } }
                    }
                }
            }
            catch { }
        }
    }

    // ---------------- 小工具 ----------------

    public static string AppVersion => typeof(Installer).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    private static string Quote(string path) => "\"" + path + "\"";

    private static bool Has(string[] args, string flag) =>
        args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string? ArgValue(string[] args, string name)
    {
        foreach (var a in args)
        {
            if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a[(name.Length + 1)..].Trim().Trim('"');
                return v.Length == 0 ? null : v;
            }
        }
        return null;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool IsSameOrUnder(string path, string root)
    {
        string p = Path.GetFullPath(path).TrimEnd('\\');
        string r = Path.GetFullPath(root).TrimEnd('\\');
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriveRoot(string dir)
    {
        var parent = Path.GetDirectoryName(dir.TrimEnd('\\'));
        return parent is null || Path.GetFileName(dir).Length <= 1;
    }

    /// <summary>只认「目录里有我们的 exe，而且不在系统目录/用户配置目录里」的地方，防止误删。</summary>
    public static bool LooksLikeOurs(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            if (IsDriveRoot(full) || full.Length < 8) return false;
            if (!File.Exists(Path.Combine(full, ExeName))) return false;

            foreach (var special in new[]
                     {
                         Environment.SpecialFolder.Windows,
                         Environment.SpecialFolder.System,
                         Environment.SpecialFolder.SystemX86,
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.UserProfile,
                         Environment.SpecialFolder.DesktopDirectory,
                         Environment.SpecialFolder.Programs,
                         Environment.SpecialFolder.ApplicationData,
                         Environment.SpecialFolder.LocalApplicationData,
                     })
            {
                var sp = Environment.GetFolderPath(special);
                if (!string.IsNullOrEmpty(sp) && IsSameOrUnder(full, sp)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static bool DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return true;
        try
        {
            Directory.Delete(dir, recursive: true);
            return !Directory.Exists(dir);
        }
        catch (Exception ex)
        {
            LogSink.Write("[uninstall] 删目录重试: " + ex.Message);
            return false;
        }
    }

    /// <summary>把我们自己的另一份（同路径）请出去，否则覆盖 exe 会报占用。</summary>
    private static void KillRunning(string exePath)
    {
        try
        {
            string target = Path.GetFullPath(exePath);
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            {
                try
                {
                    if (p.Id == Environment.ProcessId) continue;
                    string? path = p.MainModule?.FileName;
                    if (path is null || !SamePath(path, target)) continue;
                    LogSink.Write("[install] 结束正在运行的实例 pid=" + p.Id);
                    p.Kill();
                    p.WaitForExit(2000);
                }
                catch { }
                finally { p.Dispose(); }
            }
            Thread.Sleep(150);
        }
        catch (Exception ex)
        {
            LogSink.Write("[install] 结束旧实例失败: " + ex.Message);
        }
    }

    private static void DeleteShortcut(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            LogSink.Write("[uninstall] 删快捷方式失败: " + ex.Message);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string? newName, uint flags);

    private static bool NativeMoveFileEx(string existing, string? newName, uint flags) =>
        MoveFileExW(existing, newName, flags);
}

/// <summary>
/// 创建 .lnk：直接调 COM 的 IShellLinkW，但走裸 vtable 调用而不是 [ComImport]，
/// 这样 NativeAOT 下不依赖任何反射 / 内置 COM 互操作，体积也是零开销。
/// </summary>
internal static unsafe class Shortcut
{
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010B-0000-0000-C000-000000000046");

    private const uint CLSCTX_INPROC_SERVER = 0x1;

    // IShellLinkW 的 vtable 序号（IUnknown 占 0..2）
    private const int SetDescription = 7;
    private const int SetWorkingDirectory = 9;
    private const int SetShowCmd = 15;
    private const int SetIconLocation = 17;
    private const int SetPath = 20;

    // IPersistFile 的 vtable 序号（IUnknown 占 0..2）
    private const int Save = 6;

    public static void Create(string lnkPath, string targetExe, string workDir, string description)
    {
        string? dir = Path.GetDirectoryName(lnkPath);
        if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("快捷方式路径无效：" + lnkPath);
        Directory.CreateDirectory(dir);

        // ShellLink 是 COM 对象；STA 里提前初始化一次（已经初始化过会返回 S_FALSE，无害）
        CoInitializeEx(IntPtr.Zero, 0x2);

        IntPtr link = IntPtr.Zero;
        try
        {
            int hr = CoCreateInstance(in ClsidShellLink, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IidShellLinkW, out link);
            if (hr < 0 || link == IntPtr.Zero)
                throw new InvalidOperationException("创建 ShellLink 失败 hr=0x" + hr.ToString("X8"));

            void** vtbl = *(void***)link;

            fixed (char* pTarget = targetExe)
            fixed (char* pWork = workDir)
            fixed (char* pDesc = description)
            fixed (char* pLnk = lnkPath)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtbl[SetPath])(link, pTarget);
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtbl[SetWorkingDirectory])(link, pWork);
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)vtbl[SetDescription])(link, pDesc);
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)vtbl[SetIconLocation])(link, pTarget, 0);
                ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtbl[SetShowCmd])(link, Native.SW_SHOWNORMAL);

                Guid iid = IidPersistFile;
                IntPtr persist = IntPtr.Zero;
                int hr2 = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0])(link, &iid, &persist);
                if (hr2 < 0 || persist == IntPtr.Zero)
                    throw new InvalidOperationException("ShellLink 不支持 IPersistFile hr=0x" + hr2.ToString("X8"));

                try
                {
                    void** pvtbl = *(void***)persist;
                    int hr3 = ((delegate* unmanaged[Stdcall]<IntPtr, char*, int, int>)pvtbl[Save])(persist, pLnk, 1);
                    if (hr3 < 0) throw new InvalidOperationException("保存快捷方式失败 hr=0x" + hr3.ToString("X8"));
                }
                finally
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)persist)[2])(persist);
                }

                LogSink.Write("[install] shortcut " + lnkPath);
            }
        }
        finally
        {
            if (link != IntPtr.Zero)
            {
                try { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)link)[2])(link); } catch { }
            }
        }
    }

    /// <summary>读回 .lnk 的目标路径（只用 IUnknown+IShellLinkW.GetPath，自测用）。</summary>
    public static string? ReadTarget(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        CoInitializeEx(IntPtr.Zero, 0x2);

        IntPtr link = IntPtr.Zero;
        try
        {
            int hr = CoCreateInstance(in ClsidShellLink, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IidShellLinkW, out link);
            if (hr < 0 || link == IntPtr.Zero) return null;
            void** vtbl = *(void***)link;

            Guid iid = IidPersistFile;
            IntPtr persist = IntPtr.Zero;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0])(link, &iid, &persist) < 0 || persist == IntPtr.Zero)
                return null;

            try
            {
                void** pvtbl = *(void***)persist;
                // IPersistFile.Load(pszFileName, STGM_READ)
                if (((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, int>)pvtbl[5])(persist, StrPtrAlloc(lnkPath), 0) < 0)
                    return null;
                FreeStr();

                char* buf = stackalloc char[520];
                buf[0] = '\0';
                if (((delegate* unmanaged[Stdcall]<IntPtr, char*, int, IntPtr, uint, int>)vtbl[3])(link, buf, 520, IntPtr.Zero, 0) < 0)
                    return null;
                return new string(buf);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)persist)[2])(persist);
            }
        }
        catch { return null; }
        finally
        {
            if (link != IntPtr.Zero)
            {
                try { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)link)[2])(link); } catch { }
            }
        }
    }

    [ThreadStatic] private static IntPtr _tempStr;

    private static char* StrPtrAlloc(string s)
    {
        _tempStr = Marshal.StringToHGlobalUni(s);
        return (char*)_tempStr;
    }

    private static void FreeStr()
    {
        if (_tempStr == IntPtr.Zero) return;
        try { Marshal.FreeHGlobal(_tempStr); } catch { }
        _tempStr = IntPtr.Zero;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);
}
