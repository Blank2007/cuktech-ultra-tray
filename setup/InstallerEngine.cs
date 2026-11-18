using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CuktechTraySetup;

internal sealed class InstallResult
{
    public string InstallDir = "";
    public string ExePath = "";
    public string SetupCopyPath = "";
    public long InstalledBytes;
    public string PayloadSha256 = "";
    public bool Upgraded;
    public string? PreviousVersion;
    public List<int> KilledPids = new();
    public bool DesktopShortcut;
    public bool StartMenuShortcut;
    public bool AutoStart;
    public bool Launched;
}

internal sealed class UninstallResult
{
    public string InstallDir = "";
    public bool RemovedDirectory;
    public List<string> Leftovers = new();
    public bool PurgedData;
    public string? DataDirRemoved;
}

/// <summary>安装/卸载的实际动作。GUI 和静默模式都走这里，只是进度回调不同。</summary>
internal static class InstallerEngine
{
    private const int BufferSize = 1024 * 1024;

    // ---------------- 安装 ----------------

    public static InstallResult Install(InstallOptions options, Action<int, string> progress)
    {
        var dir = Paths.Normalize(options.InstallDir);
        var unsafeReason = Paths.UnsafeReason(dir);
        if (unsafeReason is not null) throw new InstallException(unsafeReason);

        var result = new InstallResult
        {
            InstallDir = dir,
            ExePath = Path.Combine(dir, AppInfo.ExecutableName),
            SetupCopyPath = Path.Combine(dir, AppInfo.SetupFileName),
        };

        // 已经装过就是升级，记下版本号
        if (RegistryStore.HasUninstallEntry())
        {
            result.PreviousVersion = RegistryStore.ReadUninstallValue("DisplayVersion");
            result.Upgraded = true;
        }

        progress(2, "检查目标位置…");
        if (EnvironmentProbe.RequiresElevation(dir))
        {
            throw new InstallException(
                "没有权限写入 " + dir + "。\r\n\r\n" +
                "请换一个当前用户可写的位置（默认的 " + AppInfo.DefaultInstallDir + " 就不需要管理员权限），" +
                "或者右键以管理员身份运行本安装程序。")
            { ExitCode = ExitCodes.NeedsElevation };
        }

        progress(6, "检查正在运行的实例…");
        if (EnvironmentProbe.CountRunning() > 0)
        {
            if (!options.Force)
            {
                throw new InstallException("托盘程序正在运行，请先退出后再安装。")
                { ExitCode = ExitCodes.AlreadyRunning };
            }
            result.KilledPids = EnvironmentProbe.KillRunning();
        }

        progress(10, "创建安装目录…");
        Directory.CreateDirectory(dir);

        // 1) 解压内嵌的主程序（20% → 78%）
        // 解压到安装目录里的临时文件，而不是 %TEMP%：最后一步就变成同目录改名（原子操作，
        // 跨卷也不会失败），也不会在用户把程序装到别的盘时白占系统盘空间。
        var tempExe = Path.Combine(dir, AppInfo.ExecutableName + ".partial");
        try
        {
            progress(12, "解压主程序…");
            var sha = ExtractPayload(tempExe, percent => progress(12 + (int)(percent * 0.66), "解压主程序…"));

            progress(80, "校验主程序完整性…");
            if (!string.IsNullOrEmpty(PayloadInfo.Sha256) &&
                !sha.Equals(PayloadInfo.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstallException(
                    "安装包里的主程序校验失败（SHA256 不一致），文件可能在下载/拷贝过程中损坏。\r\n" +
                    "请重新获取安装包后再试。");
            }
            result.PayloadSha256 = sha;

            progress(82, "写入主程序…");
            // 覆盖旧版本：先删掉旧文件（它可能正被占用，前面已经关过进程了）
            if (File.Exists(result.ExePath))
            {
                try
                {
                    File.Delete(result.ExePath);
                }
                catch (Exception ex)
                {
                    throw new InstallException(
                        "无法覆盖旧的 " + AppInfo.ExecutableName + "：" + ex.Message + "\r\n" +
                        "请确认程序已经退出（包括托盘图标），或者先卸载旧版本。", ex);
                }
            }
            File.Move(tempExe, result.ExePath);
            result.InstalledBytes = new FileInfo(result.ExePath).Length;
        }
        finally
        {
            TryDelete(tempExe);
        }

        // 2) 把自己复制一份到安装目录，卸载时才有东西可跑
        progress(86, "复制安装/卸载程序…");
        var self = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppInfo.SetupFileName);
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(result.SetupCopyPath), StringComparison.OrdinalIgnoreCase))
        {
            CopySelfIntoInstallDir(self, result.SetupCopyPath);
        }
        else
        {
            Log.Write("[install] 安装程序已经在目标目录里运行，跳过自我复制。");
        }

        // 3) 注册表
        progress(90, "写入卸载信息…");
        RegistryStore.WriteUninstallEntry(dir, result.SetupCopyPath, result.ExePath, result.InstalledBytes);

        progress(92, "设置开机自启…");
        result.AutoStart = options.AutoStart;
        if (options.AutoStart) RegistryStore.SetAutoStart(result.ExePath);
        else RegistryStore.DeleteAutoStart();

        // 4) 快捷方式
        progress(94, "创建快捷方式…");
        if (options.DesktopShortcut)
        {
            Shortcuts.Create(AppInfo.DesktopShortcutPath, result.ExePath, dir);
            result.DesktopShortcut = true;
        }
        else
        {
            Shortcuts.Delete(AppInfo.DesktopShortcutPath);
        }

        if (options.StartMenuShortcut)
        {
            Shortcuts.Create(AppInfo.StartMenuShortcutPath, result.ExePath, dir);
            result.StartMenuShortcut = true;
        }
        else
        {
            Shortcuts.Delete(AppInfo.StartMenuShortcutPath);
        }

        // 5) 启动
        if (options.LaunchAfter)
        {
            progress(97, "启动程序…");
            try
            {
                Process.Start(new ProcessStartInfo(result.ExePath)
                {
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                });
                result.Launched = true;
            }
            catch (Exception ex)
            {
                Log.Write("[install] 启动失败：" + ex.Message);
            }
        }

        progress(100, result.Upgraded ? "升级完成" : "安装完成");
        return result;
    }

    /// <summary>把 setup.exe 自己拷进安装目录。自己就在目标路径上时（用户从安装目录再次运行）先把旧文件改名腾位。</summary>
    private static void CopySelfIntoInstallDir(string source, string destination)
    {
        try
        {
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (File.Exists(destination))
            {
                try
                {
                    File.Delete(destination);
                }
                catch (IOException)
                {
                    // 目标文件就在被自己占用（从安装目录运行安装程序），改名后重试
                    var stale = destination + ".old";
                    TryDelete(stale);
                    File.Move(destination, stale);
                }
            }
            File.Copy(source, destination, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new InstallException("复制安装/卸载程序到安装目录失败：" + ex.Message, ex);
        }
    }

    public static string ExtractPayload(string destination, Action<double>? onProgress)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var raw = assembly.GetManifestResourceStream("payload.bin")
            ?? throw new InstallException("安装包损坏：找不到内嵌的主程序资源。");

        using var deflate = new DeflateStream(raw, CompressionMode.Decompress);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
        using var sha = SHA256.Create();

        var total = PayloadInfo.RawSize > 0 ? PayloadInfo.RawSize : 1L;
        var buffer = new byte[BufferSize];
        long written = 0;
        int read;
        var lastPercent = -1.0;
        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            sha.TransformBlock(buffer, 0, read, null, 0);
            written += read;

            var percent = Math.Min(1.0, (double)written / total);
            if (onProgress is not null && percent - lastPercent >= 0.01)
            {
                lastPercent = percent;
                onProgress(percent);
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        output.Flush();

        onProgress?.Invoke(1.0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    // ---------------- 卸载 ----------------

    public static UninstallResult Uninstall(UninstallOptions options, Action<int, string> progress)
    {
        var dir = ResolveInstallDir(options);
        if (dir is null) throw new InstallException("没有找到安装信息，可能已经卸载过了。", null);

        var result = new UninstallResult { InstallDir = dir };

        progress(5, "检查正在运行的实例…");
        if (EnvironmentProbe.CountRunning() > 0 && !options.Force)
        {
            throw new InstallException("托盘程序正在运行，请先退出后再卸载。") { ExitCode = ExitCodes.AlreadyRunning };
        }
        EnvironmentProbe.KillRunning();

        progress(20, "删除快捷方式…");
        Shortcuts.Delete(AppInfo.DesktopShortcutPath);
        Shortcuts.Delete(AppInfo.StartMenuShortcutPath);

        progress(30, "删除开机自启…");
        RegistryStore.DeleteAutoStart();

        progress(40, "删除卸载信息…");
        RegistryStore.DeleteUninstallEntry();

        if (options.PurgeData)
        {
            progress(50, "删除配置和日志…");
            result.PurgedData = true;
            result.DataDirRemoved = PurgeUserData(dir);
        }

        progress(70, "删除安装目录…");
        var sweep = DeleteDirectory(dir);
        result.RemovedDirectory = sweep.Removed;
        result.Leftovers.AddRange(sweep.Leftovers);

        progress(100, "卸载完成");
        return result;
    }

    /// <summary>--dir 优先，其次注册表里的 InstallLocation，最后默认位置。</summary>
    public static string? ResolveInstallDir(UninstallOptions options)
    {
        if (!string.IsNullOrEmpty(options.InstallDir)) return Paths.Normalize(options.InstallDir!);

        var fromRegistry = RegistryStore.ReadInstallLocation();
        if (!string.IsNullOrEmpty(fromRegistry)) return Paths.Normalize(fromRegistry!);

        var hasEntry = RegistryStore.HasUninstallEntry();
        if (Directory.Exists(AppInfo.DefaultInstallDir)) return Paths.Normalize(AppInfo.DefaultInstallDir);
        return hasEntry ? Paths.Normalize(AppInfo.DefaultInstallDir) : null;
    }

    /// <summary>删 %LOCALAPPDATA%\CuktechTray，以及安装目录里的 settings.json 和 logs。</summary>
    private static string? PurgeUserData(string installDir)
    {
        string? removed = null;
        if (Directory.Exists(AppInfo.DataDir))
        {
            DeleteDirectory(AppInfo.DataDir);
            removed = AppInfo.DataDir;
        }

        foreach (var file in new[] { "settings.json", "settings.json.bak" })
        {
            TryDelete(Path.Combine(installDir, file));
        }
        var logs = Path.Combine(installDir, "logs");
        if (Directory.Exists(logs)) DeleteDirectory(logs);

        return removed;
    }

    public sealed class SweepResult
    {
        public bool Removed;
        public List<string> Leftovers = new();
    }

    /// <summary>重试删除目录；实在删不掉的登记为“重启后删除”，并把路径报给调用方。</summary>
    public static SweepResult DeleteDirectory(string path)
    {
        var result = new SweepResult();
        if (!Directory.Exists(path))
        {
            result.Removed = true;
            return result;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                result.Removed = true;
                return result;
            }
            catch (Exception ex)
            {
                Log.Write("[remove] 第 " + (attempt + 1) + " 次删除目录失败：" + ex.Message);
                Thread.Sleep(300);
            }
        }

        // 目录还在：登记重启后删除，并如实报告残留
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                result.Leftovers.Add(file);
                ScheduleDeleteOnReboot(file);
            }
            foreach (var sub in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                ScheduleDeleteOnReboot(sub);
            }
            ScheduleDeleteOnReboot(path);
        }
        return result;
    }

    // ---------------- 自我搬迁 + 自删 ----------------

    /// <summary>
    /// 卸载时如果安装程序自己就在要删的目录里，先把自己拷到 %TEMP% 再以 --relocated 重启，
    /// 否则文件永远被自己占着，目录删不掉。
    /// </summary>
    public static bool TryRelocateForUninstall(string installDir, string[] originalArgs)
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self) || !Paths.IsSameOrUnder(self, installDir)) return false;

        try
        {
            // 优先 %TEMP%；受限环境里 %TEMP% 不可写时退到 %LOCALAPPDATA%\CuktechTray\staging\
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(Path.GetTempPath(), "CuktechTray-Setup-" + Guid.NewGuid().ToString("N")),
                Path.Combine(localAppData, "CuktechTray", "staging", "CuktechTray-Setup-" + Guid.NewGuid().ToString("N")),
            };

            string? tempDir = null;
            string? tempExe = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    var target = Path.Combine(candidate, AppInfo.SetupFileName);
                    File.Copy(self, target, overwrite: true);
                    tempDir = candidate;
                    tempExe = target;
                    break;
                }
                catch (Exception ex)
                {
                    Log.Write("[relocate] 候选目录不可用（" + candidate + "）：" + ex.Message);
                }
            }

            if (tempDir is null || tempExe is null)
            {
                Log.Write("[relocate] 找不到可写的临时目录，改为原地卸载。");
                return false;
            }

            var args = new List<string>();
            foreach (var a in originalArgs)
            {
                if (!a.Equals("--relocated", StringComparison.OrdinalIgnoreCase)) args.Add(a);
            }
            args.Add("--relocated");
            args.Add("--dir");
            args.Add(installDir);

            var psi = new ProcessStartInfo(tempExe) { UseShellExecute = false, WorkingDirectory = tempDir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Log.Write("[relocate] 安装程序已搬到 " + tempExe + " 继续卸载。");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("[relocate] 搬迁失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>删除当前正在运行的 exe（以及它的临时目录）：交给 cmd 在进程退出后再删。</summary>
    public static void DeleteSelfAndTempDir()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self)) return;
        // 只删“我们自己搬出来的临时目录”（%TEMP% 或 %LOCALAPPDATA%\CuktechTray\staging 下），
        // 其它位置的副本留给安装目录清理流程处理。
        var selfFolder = Path.GetFileName(Path.GetDirectoryName(self) ?? "");
        if (selfFolder.IndexOf("CuktechTray-Setup-", StringComparison.OrdinalIgnoreCase) < 0) return;

        var dir = Path.GetDirectoryName(self);
        if (string.IsNullOrEmpty(dir)) return;

        try
        {
            var script = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + self + "\" & rmdir /s /q \"" + dir + "\"";
            var psi = new ProcessStartInfo("cmd.exe", script)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            Log.Write("[cleanup] 已安排删除自身的临时副本：" + dir);
        }
        catch (Exception ex)
        {
            Log.Write("[cleanup] 删除自身失败：" + ex.Message);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);

    private const int MoveFileDelayUntilReboot = 0x4;

    private static void ScheduleDeleteOnReboot(string path)
    {
        try
        {
            MoveFileEx(path, null, MoveFileDelayUntilReboot);
        }
        catch (Exception ex)
        {
            Log.Write("[remove] 登记重启后删除失败：" + ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>判断安装目录里是否已经有我们的文件（用于卸载前确认目标没搞错）。</summary>
    public static bool LooksLikeOurInstall(string dir) =>
        File.Exists(Path.Combine(dir, AppInfo.ExecutableName)) ||
        File.Exists(Path.Combine(dir, AppInfo.SetupFileName));
}
