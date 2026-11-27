using System.Text.Json;
using System.Text.Json.Serialization;

namespace CuktechTray;

/// <summary>
/// AOT 下的序列化入口：ILC 会把反射版 JsonSerializer 的反射元数据裁掉，
/// 所以这里用源生成器把 Settings 的读写编译成直接代码（WinForms 老版本也走这条，行为一致）。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>托盘程序的本地配置：设备 MAC + 12 字节登录 token（从工具链导出的 result.json 自动导入）。</summary>
internal sealed class Settings
{
    private const int CurrentConfigVersion = 2;

    /// <summary>配置结构版本，用来把老配置迁移到新语义（0 = 旧版，没写过这个字段）。</summary>
    public int ConfigVersion { get; set; }

    public string Mac { get; set; } = "";
    public string TokenHex { get; set; } = "";
    public int PollMs { get; set; } = 1000;
    public bool AutoStart { get; set; }
    public bool ShowTenthsOfWatt { get; set; } = true;

    /// <summary>托盘图标风格：Badge / Dark / Flat / Minimal / Bolt。</summary>
    public string IconStyle { get; set; } = "Dark";

    /// <summary>是否已经提示过「我常驻在托盘里」，只提示一次。</summary>
    public bool ShowedTrayTip { get; set; }

    /// <summary>面板是否固定常驻在桌面上。</summary>
    public bool PanelPinned { get; set; }

    /// <summary>面板固定时的位置（int.MinValue 表示没记过）。</summary>
    public int PanelX { get; set; } = int.MinValue;
    public int PanelY { get; set; } = int.MinValue;

    /// <summary>
    /// 面板是否「始终在最前」（压在其他窗口上面）。与 PanelPinned 是两件事：
    /// 那个是「固定到桌面、不自己收起」，这个是 Z 序，两个开关可以同时开。
    /// </summary>
    public bool PanelAlwaysOnTop { get; set; }

    /// <summary>任务栏里是否显示一块实时功率小窗。</summary>
    public bool TaskbarWidget { get; set; } = true;

    /// <summary>
    /// 任务栏小窗左边距（dip，从任务栏左沿算起）。负数表示自动，摆在开始按钮左边；
    /// 用户在数字上按住拖动后写这里，下次启动照旧放在那。
    /// </summary>
    public double TaskbarLeftDip { get; set; } = -1;

    /// <summary>
    /// 任务栏数字的摆位方式：auto（开始按钮左边）/ center（屏幕正中）/ right（靠托盘左边）/ custom（用 TaskbarLeftDip）。
    /// 0.5.4 起不再只有「拖到哪算哪」一条路 —— issue #7 的用户说拖不到右边，那就让它能直接选。
    /// </summary>
    public string TaskbarPlace { get; set; } = "auto";

    /// <summary>
    /// 任务栏数字块的宽度（dip）。字号跟着宽度走：11 × 宽 / 84，所以拉宽不会把字切掉。
    /// 允许范围 56..160，超出按边界算。
    /// </summary>
    public double TaskbarWidthDip { get; set; } = 84;

    /// <summary>
    /// 面板「曲线」页签显示多长的时间窗（秒）。10..1800，默认 60。
    /// 点页签右上角那枚胶囊会在 1/5/15/30 分钟之间循环，右键菜单里还能自己填。
    /// </summary>
    public int ChartSeconds { get; set; } = 60;

    /// <summary>
    /// 打开软件时要不要自己查一次更新。默认开：用户要的是「每次打开自己查一遍」，
    /// 但又不想被强制 —— 菜单里可以关掉，关掉之后只有手点「检查更新…」才查。
    /// </summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>
    /// 用户是否已经在第一次打开时点过「同意并继续」。
    /// 0.4.6 之前没有这扇门（隐私说明只是向导里一行字），0.4.7 起第一次启动必须先过这一屏。
    /// 老版本升级上来的用户这里是 false，会再看一次，看完就写进 settings.json。
    /// </summary>
    public bool PrivacyAccepted { get; set; }

    private static string? _resolvedPath;

    /// <summary>
    /// 配置文件的落点。优先跟 exe 同目录（便携模式，拷走就带走配置）；
    /// 如果 exe 放在 Program Files 这类不可写的地方，就退到 %LOCALAPPDATA%\CuktechTray\settings.json。
    /// </summary>
    public static string FilePath
    {
        get
        {
            if (_resolvedPath is not null) return _resolvedPath;

            var portable = Path.Combine(AppContext.BaseDirectory, "settings.json");
            if (File.Exists(portable) || CanWriteTo(AppContext.BaseDirectory))
                return _resolvedPath = portable;

            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CuktechTray");
                Directory.CreateDirectory(dir);
                return _resolvedPath = Path.Combine(dir, "settings.json");
            }
            catch
            {
                return _resolvedPath = portable; // 两边都写不了：还是指向 exe 目录，Save 会静默失败
            }
        }
    }

    private static bool CanWriteTo(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-probe");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch { return false; }
    }

    public static Settings Load()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var s = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.Settings);
                if (s is not null && s.IsComplete)
                {
                    // v1：常驻面板不再等于「总在最前」。旧配置里那个 true 是程序自己写的，不是用户选的。
                    // v2：修「锁屏后回来面板偶尔还压在最上面」——把历史遗留的置顶标志再清一次，
                    //     真想要置顶在面板右键菜单里重新勾一次即可（默认就是关的）。
                    if (s.ConfigVersion < CurrentConfigVersion)
                    {
                        s.ConfigVersion = CurrentConfigVersion;
                                    s.Save();
                    }
                    return s;
                }
            }
            catch { /* 配置损坏就重新导入 */ }
        }

        var fresh = new Settings { ConfigVersion = CurrentConfigVersion };
        if (fresh.ImportFromTokenResult()) fresh.Save();
        return fresh;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Mac) &&
        TokenHex.Length == 24 && TokenHex.All(Uri.IsHexDigit);

    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] TokenBytes => Convert.FromHexString(TokenHex);

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings));
        }
        catch { /* 只读目录下静默失败，不影响运行 */ }
    }

    /// <summary>从 tools/token-fetch/out/result.json 里挑出 AD1204 这台设备的 MAC 和 token。</summary>
    public bool ImportFromTokenResult()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "token-fetch", "out", "result.json");
            if (!File.Exists(candidate)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                if (!doc.RootElement.TryGetProperty("targets", out var targets)) continue;
                foreach (var t in targets.EnumerateArray())
                {
                    var model = t.TryGetProperty("model", out var mv) ? mv.GetString() : null;
                    var mac = t.TryGetProperty("mac", out var v2) ? v2.GetString() : null;
                    var tok = t.TryGetProperty("token_hex", out var v3) ? v3.GetString() : null;
                    if (tok is null || tok.Length != 24 || mac is null) continue;
                    // 优先挑 model 里带 ad1204 的（10 号 Ultra），否则取第一台能用 BLE 的
                    var isTarget = model?.Contains("ad1204", StringComparison.OrdinalIgnoreCase) == true;
                    if (!isTarget && Mac.Length > 0) continue;
                    Mac = mac;
                    TokenHex = tok.ToLowerInvariant();
                    if (isTarget) return true;
                }
                if (IsComplete) return true;
            }
            catch { }
        }
        return IsComplete;
    }
}