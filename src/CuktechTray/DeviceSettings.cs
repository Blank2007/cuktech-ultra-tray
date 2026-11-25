namespace CuktechTray;

/// <summary>
/// 设备设置项（SIID=2 的 PIID 6/9~13/15/17~21）。全部可空：
/// 不同固件答得上来的项不一样，读不到就留 null，界面上显示「设备没答这一项」并禁用控件，
/// **绝不**把「没读到」当成「关着」—— 那样用户一点就不知道写进去了什么。
/// （协议细节来自 macOS 侧 PR #4 的实测，见 docs/protocol-research.md 第 7 节）
/// </summary>
internal sealed class DeviceSettingsState
{
    /// <summary>息屏时间（PIID 6）。取值见 <see cref="ScreenTimeouts"/>，注意 1 和 5 是反着来的。</summary>
    public int? ScreenTimeout { get; set; }

    /// <summary>空闲息屏（PIID 19）：没插设备时也把屏幕关掉。</summary>
    public bool? IdleScreenOff { get; set; }

    /// <summary>屏幕方向锁（PIID 20）。</summary>
    public bool? ScreenLock { get; set; }

    /// <summary>USB-A 小电流模式（PIID 15）：给耳机手环这类小东西充电时不握手快充。</summary>
    public bool? UsbLowCurrent { get; set; }

    /// <summary>屏幕语言（PIID 13）：0=English 1=中文。</summary>
    public int? Language { get; set; }

    /// <summary>四口快充协议开关的位掩码（PIID 21）。保留位固件固定为 1，写入必须读-改-写。</summary>
    public uint? ProtocolFlags { get; set; }

    /// <summary>四口定时断电（PIID 9~12，单位分钟，0 = 关闭）。下标 0=C1 1=C2 2=C3 3=A。</summary>
    public int?[] Countdowns { get; } = new int?[4];

    /// <summary>四口当前实际协商到的协议与功率（PIID 17 = C1/C2，PIID 18 = C3/A）。</summary>
    public Negotiation?[] Negotiations { get; } = new Negotiation?[4];

    /// <summary>这份设置是什么时候读的（界面上显示「几秒前」用）。</summary>
    public DateTime At { get; set; } = DateTime.MinValue;

    /// <summary>一共读到几项，用来区分「设备没答」和「确实是关」。</summary>
    public int LoadedCount
    {
        get
        {
            int n = 0;
            if (ScreenTimeout is not null) n++;
            if (IdleScreenOff is not null) n++;
            if (ScreenLock is not null) n++;
            if (UsbLowCurrent is not null) n++;
            if (Language is not null) n++;
            if (ProtocolFlags is not null) n++;
            foreach (var c in Countdowns) if (c is not null) n++;
            return n;
        }
    }

    /// <summary>某口的某种协议现在开着没有。整份掩码没读到就返回 null（不是 false）。</summary>
    public bool? ProtocolOn(int portIndex, ProtocolCap cap)
    {
        if (ProtocolFlags is not uint flags) return null;
        if (cap.Bit(portIndex) is not uint bit) return null;
        return (flags & (1u << (int)bit)) != 0;
    }

    public string ScreenTimeoutLabel => ScreenTimeout is int v ? ScreenTimeouts.Label(v) : "—";

    public string LanguageLabel => Language switch
    {
        0 => "English",
        1 => "中文",
        _ => "—",
    };

    public DeviceSettingsState Clone()
    {
        var c = new DeviceSettingsState
        {
            ScreenTimeout = ScreenTimeout,
            IdleScreenOff = IdleScreenOff,
            ScreenLock = ScreenLock,
            UsbLowCurrent = UsbLowCurrent,
            Language = Language,
            ProtocolFlags = ProtocolFlags,
            At = At,
        };
        Array.Copy(Countdowns, c.Countdowns, 4);
        Array.Copy(Negotiations, c.Negotiations, 4);
        return c;
    }
}

/// <summary>
/// 息屏时间的取值（PIID 6）。米家历史包袱：1 = 5 分钟、5 = 1 分钟，
/// 千万别按数字大小猜，按文案取。
/// </summary>
internal static class ScreenTimeouts
{
    /// <summary>界面上从左到右的顺序。</summary>
    public static readonly int[] Values = { 5, 1, 2, 3, 4 };

    public static string Label(int value) => value switch
    {
        5 => "1 分",
        1 => "5 分",
        2 => "10 分",
        3 => "30 分",
        4 => "常亮",
        _ => "—",
    };
}

/// <summary>
/// 快充协议开关（PIID 21 的 32 位掩码）：
/// C1 = bit0 PD / bit1 PPS / bit2 UFCS；C2 = bit8 PD / bit9 PPS / bit10 UFCS；
/// C3 = bit16 UFCS / bit17 SCP；A = bit24 UFCS / bit25 SCP。
/// bit3 / bit11 是保留位，固件固定为 1 —— 所以只能读-改-写，不能自己造整份值。
/// </summary>
internal enum ProtocolCap
{
    PD,
    PPS,
    UFCS,
    SCP,
}

internal static class ProtocolCaps
{
    /// <summary>某口支持哪几种协议（C1/C2 是 PD/PPS/UFCS，C3/A 是 UFCS/SCP，跟设备屏上的菜单一致）。</summary>
    public static ProtocolCap[] Available(int portIndex) => portIndex switch
    {
        0 or 1 => new[] { ProtocolCap.PD, ProtocolCap.PPS, ProtocolCap.UFCS },
        2 or 3 => new[] { ProtocolCap.UFCS, ProtocolCap.SCP },
        _ => Array.Empty<ProtocolCap>(),
    };

    public static string Label(this ProtocolCap cap) => cap switch
    {
        ProtocolCap.PD => "PD",
        ProtocolCap.PPS => "PPS",
        ProtocolCap.UFCS => "UFCS",
        ProtocolCap.SCP => "SCP",
        _ => "?",
    };
}

internal static class ProtocolCapBits
{
    /// <summary>端口（0=C1 … 3=A）+ 协议 → 位偏移；这个口不支持这种协议就是 null。</summary>
    public static uint? BitOf(int portIndex, ProtocolCap cap) => (portIndex, cap) switch
    {
        (0, ProtocolCap.PD) => 0,
        (0, ProtocolCap.PPS) => 1,
        (0, ProtocolCap.UFCS) => 2,
        (1, ProtocolCap.PD) => 8,
        (1, ProtocolCap.PPS) => 9,
        (1, ProtocolCap.UFCS) => 10,
        (2, ProtocolCap.UFCS) => 16,
        (2, ProtocolCap.SCP) => 17,
        (3, ProtocolCap.UFCS) => 24,
        (3, ProtocolCap.SCP) => 25,
        _ => null,
    };
}

static class ProtocolCapExtensions
{
    public static uint? Bit(this ProtocolCap cap, int portIndex) => ProtocolCapBits.BitOf(portIndex, cap);
}

/// <summary>
/// 设备此刻协商到的协议（PIID 17 = C1/C2，PIID 18 = C3/A）。
/// 一个 u32 塞两路：[高口协议][高口功率W][低口协议][低口功率W]。
/// </summary>
internal readonly record struct Negotiation(int Code, int Watts)
{
    public bool IsEmpty => Code == 0 && Watts == 0;

    /// <summary>「PD 65W」/「—」。</summary>
    public string Text
    {
        get
        {
            if (Code == 0) return "—";
            string name = ChargeProtocols.Name(Code);
            return Watts > 0 ? name + " " + Watts + "W" : name;
        }
    }

    /// <summary>把一个 u32 拆成高低两路。功率字节 255 = 不知道。</summary>
    public static (Negotiation High, Negotiation Low) Split(uint v)
    {
        int highCode = (int)((v >> 24) & 0xFF);
        int highW = (int)((v >> 16) & 0xFF);
        int lowCode = (int)((v >> 8) & 0xFF);
        int lowW = (int)(v & 0xFF);
        return (new Negotiation(highCode, highW >= 255 ? 0 : highW),
                new Negotiation(lowCode, lowW >= 255 ? 0 : lowW));
    }
}

/// <summary>米家协议号 → 人看的名字。</summary>
internal static class ChargeProtocols
{
    public static string Name(int code) => code switch
    {
        0 => "空闲",
        1 or 2 => "5V",
        3 => "QC",
        4 => "AFC",
        5 => "FCP",
        6 => "SCP",
        7 => "PD",
        8 or 9 => "PPS",
        10 => "UFCS",
        0x60 => "USB-A",
        0x70 => "QC",
        0x80 => "PD",
        _ => "0x" + code.ToString("X2"),
    };
}

/// <summary>每口定时断电的选项（PIID 9~12，单位分钟）。</summary>
internal static class CountdownOptions
{
    public static readonly int[] All = { 0, 15, 30, 60, 120, 240 };

    public static string Label(int minutes) => minutes switch
    {
        0 => "关闭",
        60 => "1 小时",
        120 => "2 小时",
        240 => "4 小时",
        _ => minutes + " 分钟",
    };

    /// <summary>点一下切到下一个档位（界面上是循环切换，避免为了一个下拉再写一套弹层）。</summary>
    public static int Next(int current)
    {
        int at = Array.IndexOf(All, current);
        return at < 0 ? 0 : All[(at + 1) % All.Length];
    }
}
