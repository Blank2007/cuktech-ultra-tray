using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CuktechTray;

/// <summary>
/// 一键更新：下载 -> 校验 SHA256 -> 起一个替身进程等自己退出 -> 覆盖 exe -> 拉起新版。
/// <para>
/// 为什么非要替身：Windows 不允许替换正在运行的 exe（映像被映射住，连改名都不行），
/// 只能先让主进程退出，再由另一份没被映射的副本来动那块文件。
/// 跟 --uninstall 的扫尾是同一套路子，只是那次删目录、这次换文件。
/// </para>
/// </summary>
internal static class Updater
{
    /// <summary>下载中的临时名。必须跟 exe 同目录：同卷改名才是原子的，跨卷就成了「拷贝一半」。</summary>
    public const string StagedName = "CuktechTray.exe.download";

    /// <summary>替换前把旧 exe 备份成这个名字，新版本启动成功后由 CleanupStale 清掉。</summary>
    public const string BackupSuffix = ".old";

    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
    private const int BufferSize = 1 << 16;
    private const long ProgressStep = 512 * 1024;
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(120);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileExW(string existing, string? newName, uint flags);

    public static string StagedPath => Path.Combine(Installer.SelfDir, StagedName);

    public static string BackupPath => Installer.SelfExe + BackupSuffix;

    /// <summary>
    /// 更新完之后 exe 该叫什么名字。绿色版是下载下来就带着版本号的文件（CuktechTray-0.5.7-win.exe），
    /// 如果更新只覆盖内容不改名，文件名上的版本号就一直停在下载那一版 —— 用户从资源管理器看
    /// 会以为「更新没生效」。所以只要名字本来就是 CuktechTray-&lt;版本&gt;-win.exe 这个形状，
    /// 就让版本号跟着新 exe 走；安装版（固定叫 CuktechTray.exe）和用户自己改过的名字都不动。
    /// </summary>
    public static string VersionedTarget(string currentPath, string payload)
    {
        try
        {
            string full = Path.GetFullPath(currentPath);
            string dir = Path.GetDirectoryName(full) ?? ".";
            string name = Path.GetFileName(full);
            Match m = Regex.Match(name, @"^(?<head>.+?)-(?<ver>\d+\.\d+\.\d+)(?<tail>(?:-win)?\.exe)$", RegexOptions.IgnoreCase);
            if (!m.Success) return currentPath;

            string? ver = ExeVersionOf(payload);
            if (ver is null || ver == m.Groups["ver"].Value) return currentPath;

            string candidate = m.Groups["head"].Value + "-" + ver + m.Groups["tail"].Value;
            string target = Path.Combine(dir, candidate);
            LogSink.Write("[update] 文件名里的版本号跟着走：" + name + " -> " + candidate);
            return target;
        }
        catch (Exception ex)
        {
            LogSink.Write("[update] 算新文件名失败：" + ex.Message);
            return currentPath;
        }
    }

    /// <summary>从 exe 的版本资源里取 x.y.z（拿不到返回 null）。</summary>
    public static string? ExeVersionOf(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            string? raw = info.FileVersion ?? info.ProductVersion;
            if (string.IsNullOrEmpty(raw)) return null;
            Match m = Regex.Match(raw, @"\d+\.\d+\.\d+");
            return m.Success ? m.Value : null;
        }
        catch
        {
            return null;
        }
    }

    public readonly record struct DownloadResult(bool Ok, string Path, string Error);

    /// <summary>
    /// 目标目录能不能写。不能写就别下载了——下完一样装不进去，
    /// 不如当场告诉用户「去下载页自己拿」。
    /// </summary>
    public static bool CanWriteDir(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".write-probe-" + Environment.ProcessId);
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            LogSink.Write("[update] 目录不可写 " + dir + "：" + ex.Message);
            return false;
        }
    }

    // ---------------- 下载 ----------------

    /// <summary>下载到 exe 同目录并边下边算 SHA256；代理、直连各试一次（跟检查更新一致）。</summary>
    public static async Task<DownloadResult> DownloadAsync(string url, long expectedSize, string sha256,
        string dest, Action<long, long>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new DownloadResult(false, "", "下载地址不是 https：" + url);

        var proxy = UpdateCheck.Proxy();
        var errors = new List<string>();

        if (proxy is not null)
        {
            var viaProxy = await TryDownloadAsync(uri, proxy, expectedSize, sha256, dest, progress, "代理", ct).ConfigureAwait(false);
            if (viaProxy.Ok) return viaProxy;
            errors.Add(viaProxy.Error);
        }

        var direct = await TryDownloadAsync(uri, null, expectedSize, sha256, dest, progress, "直连", ct).ConfigureAwait(false);
        if (direct.Ok) return direct;
        errors.Add(direct.Error);

        return new DownloadResult(false, "", string.Join("；", errors));
    }

    private static async Task<DownloadResult> TryDownloadAsync(Uri uri, IWebProxy? proxy, long expectedSize,
        string sha256, string dest, Action<long, long>? progress, string via, CancellationToken ct)
    {
        string part = dest + ".part";
        try
        {
            using var http = UpdateCheck.CreateHttp(proxy, GenerateTimeout);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CuktechTray/" + UpdateCheck.Current);

            using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return Fail(part, via + "：HTTP " + (int)resp.StatusCode);

            long total = resp.Content.Headers.ContentLength ?? expectedSize;
            long got = 0;
            long reported = 0;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buf = new byte[BufferSize];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    hash.AppendData(buf, 0, n);
                    got += n;
                    if (progress is not null && got - reported >= ProgressStep)
                    {
                        reported = got;
                        progress(got, total);
                    }
                }
                fs.Flush();
            }

            if (expectedSize > 0 && got != expectedSize)
                return Fail(part, via + "：大小不符（下到 " + got + " 字节，发布里写的是 " + expectedSize + "）");

            string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (sha256.Length > 0 && !actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                return Fail(part, via + "：SHA256 不符（下到 " + actual[..12] + "…，发布里是 " + sha256[..Math.Min(12, sha256.Length)] + "…）");

            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest, overwrite: true);

            if (progress is not null) progress(got, total);
            LogSink.Write("[update] 下载完成 " + got + " 字节 via=" + via + " sha256=" + actual[..16] + "…");
            return new DownloadResult(true, dest, "");
        }
        catch (Exception ex)
        {
            string msg = ex is OperationCanceledException ? "下载超时或被取消" : ex.Message;
            return Fail(part, via + "：" + msg);
        }
    }

    private static DownloadResult Fail(string part, string error)
    {
        try { if (File.Exists(part)) File.Delete(part); } catch { }
        LogSink.Write("[update] 下载失败 " + error);
        return new DownloadResult(false, "", error);
    }

    // ---------------- 替换 ----------------

    /// <summary>
    /// 纯文件动作：备份旧 exe 一次，把新文件改名到位；失败就用备份还原。
    /// 不碰进程、不启动任何东西，所以 --update-selftest 能单独验它。
    /// </summary>
    public static bool Replace(string target, string payload, out string error)
    {
        error = string.Empty;
        if (!File.Exists(payload))
        {
            error = "新版本文件不见了：" + payload;
            return false;
        }

        string backup = target + BackupSuffix;
        bool backedUp = false;

        // 备份只做一次：ApplyUpdate 会重试到文件锁松开，重复拷 9 MB 没意义
        try
        {
            if (File.Exists(target) && !File.Exists(backup))
            {
                File.Copy(target, backup, overwrite: true);
                backedUp = true;
                LogSink.Write("[update] 旧版已备份 -> " + backup);
            }
            else if (File.Exists(backup))
            {
                backedUp = true;
            }

            File.Move(payload, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (backedUp)
            {
                try
                {
                    File.Copy(backup, target, overwrite: true);
                    LogSink.Write("[update] 替换失败，已用备份还原");
                }
                catch (Exception ex2)
                {
                    LogSink.Write("[update] 还原也失败了: " + ex2.Message);
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 替身进程（--apply-update）：等主进程退出 -> 替换 exe -> 拉起新版 -> 把自己标记成重启后删除。
    /// 这个进程是 %TEMP% 里的一份副本，它映射的不是目标文件，所以能改。
    /// </summary>
    public static int ApplyUpdate(string target, string payload, int pid)
    {
        LogSink.Write("[update] 助手启动 target=" + target + " payload=" + payload + " pid=" + pid);

        if (pid > 0) WaitExit(pid, ExitWait);

        // 上一次留下的 .old 不算数，先清掉，否则 Replace 会以为已经备份过
        try { if (File.Exists(target + BackupSuffix)) File.Delete(target + BackupSuffix); } catch { }

        // 绿色版的名字里带着版本号（CuktechTray-0.5.7-win.exe）：更新完要换成新版本号，
        // 否则资源管理器里看到的永远是下载那一版，用户会以为没升上去。
        string dest = VersionedTarget(target, payload);
        bool renamed = !Installer.SamePath(dest, target);

        bool replaced = false;
        string lastError = string.Empty;
        for (int i = 0; i < 60; i++)
        {
            if (Replace(dest, payload, out lastError)) { replaced = true; break; }
            Thread.Sleep(500);
        }

        if (!replaced)
        {
            LogSink.Write("[update] 替换失败: " + lastError);
            return 1;
        }

        LogSink.Write("[update] 已替换 " + dest);

        if (renamed)
        {
            // 旧名字那份已经没用了；删不掉就交给重启后删，别让用户看到两个 exe
            try
            {
                File.Delete(target);
                LogSink.Write("[update] 旧文件名已删除 " + target);
            }
            catch (Exception ex)
            {
                LogSink.Write("[update] 旧文件名删不掉（重启后删）：" + ex.Message);
                try { MoveFileExW(target, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
            }

            // 开机自启、桌面/开始菜单快捷方式、卸载项都还指着旧路径，一起搬过去
            Installer.FollowExeRename(target, dest);
        }

        try
        {
            string? dir = Path.GetDirectoryName(dest);
            var psi = new ProcessStartInfo(dest)
            {
                UseShellExecute = false,
                WorkingDirectory = dir ?? Environment.CurrentDirectory,
            };
            psi.ArgumentList.Add("--tray-only");
            Process.Start(psi);
            LogSink.Write("[update] 新版已拉起");
        }
        catch (Exception ex)
        {
            LogSink.Write("[update] 拉起新版失败: " + ex.Message);
            return 2;
        }

        SelfDeleteLater();
        return 0;
    }

    /// <summary>主进程侧：把自己复制到 %TEMP%，让那份副本在本进程退出后去替换。</summary>
    public static bool LaunchApply(out string error)
    {
        error = string.Empty;
        string self = Installer.SelfExe;
        string temp = Path.Combine(Path.GetTempPath(), "CuktechTray-update-" + Environment.ProcessId + ".exe");
        try
        {
            File.Copy(self, temp, overwrite: true);

            var psi = new ProcessStartInfo(temp)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            };
            psi.ArgumentList.Add("--apply-update=" + self);
            psi.ArgumentList.Add("--payload=" + StagedPath);
            psi.ArgumentList.Add("--pid=" + Environment.ProcessId);

            Process.Start(psi);
            LogSink.Write("[update] 助手已启动 " + temp);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }

    private static void WaitExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.WaitForExit((int)timeout.TotalMilliseconds)) LogSink.Write("[update] 原进程已退出");
            else LogSink.Write("[update] 等原进程超时，直接试替换");
        }
        catch (ArgumentException)
        {
            LogSink.Write("[update] 原进程已经不在了");
        }
        catch (Exception ex)
        {
            LogSink.Write("[update] 等待原进程出错: " + ex.Message);
        }
    }

    private static void SelfDeleteLater()
    {
        try { MoveFileExW(Installer.SelfExe, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
        catch { }
    }

    /// <summary>新版启动时顺手扫尾：删下载残留和旧版备份，外加一小时后没人管的助手副本。</summary>
    public static void CleanupStale()
    {
        try
        {
            if (File.Exists(StagedPath))
            {
                File.Delete(StagedPath);
                LogSink.Write("[update] 清掉下载残留");
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("[update] 清下载残留失败: " + ex.Message);
        }

        try
        {
            if (File.Exists(BackupPath))
            {
                File.Delete(BackupPath);
                LogSink.Write("[update] 更新完成，删掉旧版备份");
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("[update] 清旧版备份失败: " + ex.Message);
        }

        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "CuktechTray-update-*.exe"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromHours(1)) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 开发自测：在给定目录里演一遍「旧版 -> 新版」的替换与失败路径，不联网、不启动进程。
    /// 返回 0 表示替换、备份、回滚三条路都符合预期。
    /// </summary>
    public static int SelfTest(string workDir)
    {
        try
        {
            string dir = Path.GetFullPath(workDir);
            Directory.CreateDirectory(dir);

            string target = Path.Combine(dir, "CuktechTray.exe");
            string payload = Path.Combine(dir, StagedName);
            string backup = target + BackupSuffix;

            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            File.WriteAllText(target, "old-build");
            File.WriteAllText(payload, "new-build");

            bool replaced = Replace(target, payload, out var err1)
                            && File.ReadAllText(target) == "new-build"
                            && !File.Exists(payload)
                            && File.Exists(backup)
                            && File.ReadAllText(backup) == "old-build";

            // 失败路径：载荷不存在时不能动目标文件
            bool kept = !Replace(target, Path.Combine(dir, "missing.exe"), out _)
                        && File.ReadAllText(target) == "new-build";

            // 文件名里的版本号：只有「名字里本来带 x.y.z」的才跟着新 exe 走，
            // 固定名的安装版（CuktechTray.exe）必须原样不动。
            // 用自己这份 exe 当「新版载荷」来读版本号，纯文本那个载荷读不到版本资源。
            string selfVer = ExeVersionOf(Installer.SelfExe) ?? "";
            string named = Path.Combine(dir, "CuktechTray-0.5.7-win.exe");
            string keepName = Path.Combine(dir, "CuktechTray.exe");
            bool nameKept = Installer.SamePath(VersionedTarget(keepName, Installer.SelfExe), keepName);
            string expected = Path.Combine(dir, "CuktechTray-" + selfVer + "-win.exe");
            bool nameMoves = selfVer.Length > 0
                             && Installer.SamePath(VersionedTarget(named, Installer.SelfExe), expected);

            bool ok = replaced && kept && nameKept && nameMoves;
            LogSink.Write("[update-selftest] " + (ok ? "ok" : "失败")
                + " 替换=" + replaced + " 未误伤=" + kept
                + " 固定名不动=" + nameKept + " 带版本号改名=" + nameMoves
                + " 自身版本=" + (selfVer.Length > 0 ? selfVer : "?")
                + (replaced ? "" : " err=" + err1));

            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            LogSink.Write("[update-selftest] 异常: " + ex.Message);
            return 1;
        }
    }
}
