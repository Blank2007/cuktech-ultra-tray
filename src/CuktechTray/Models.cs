namespace CuktechTray;

/// <summary>单个输出口的读数（来自 SIID=2 PIID=1..4 的 4 字节应答）。</summary>
internal sealed class PortReading
{
    public string Name { get; init; } = "";
    public bool HasData { get; set; }
    public bool Idle { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public double Volts { get; set; }
    public double Amps { get; set; }
    public double Watts { get; set; }
    public byte State { get; set; }
    public byte Code { get; set; }

    /// <summary>state 只有 0x00 表示空闲；0x11 表示 C3 与 A 口合并在工作。</summary>
    public bool Working => HasData && !Idle;

    public PortReading Clone() => (PortReading)MemberwiseClone();
}

/// <summary>某一时刻的整机快照。</summary>
internal sealed class Snapshot
{
    public static readonly string[] PortNames = { "C1", "C2", "C3", "A" };

    public PortReading[] Ports { get; } = PortNames.Select(n => new PortReading { Name = n }).ToArray();
    public DateTime At { get; set; } = DateTime.MinValue;
    public bool Connected { get; set; }
    public string Status { get; set; } = "未连接";
    public string? Error { get; set; }
    public string Firmware { get; set; } = "";
    public int PortSwitch { get; set; } = 0x0F;

    /// <summary>充电模式（SIID=2 PIID=5）：1=AI 2=数码生态 3=单口 4=均衡。-1 = 还不知道（没连上或没读到）。</summary>
    public int SceneMode { get; set; } = -1;

    /// <summary>总输出：合并的那一对只算一次（owner 那一行才是 C3+A 的合计）。</summary>
    public double TotalWatts
    {
        get
        {
            int owner = MergedOwner;
            double sum = 0;
            for (int i = 0; i < Ports.Length; i++)
            {
                var p = Ports[i];
                if (!p.Working) continue;
                if (owner >= 0 && (i == 2 || i == 3) && i != owner) continue;
                sum += p.Watts;
            }
            return sum;
        }
    }

    /// <summary>合并行在界面上统一叫这个名字（两个口合成一行）。</summary>
    public const string MergedName = "C3+A";

    /// <summary>
    /// C3 与 A 共用一路功率计时，固件在这一路上报 state=0x11（值见 MiotCodec.cs:109）。
    /// 这种时候只有一路读得到数，另一路固件按「空闲」报 —— 我们以前照实画成「空闲」，
    /// 看着就像那口没通电，其实两个口都在供电（issue #1，报告人 @KingGxxT）。
    /// </summary>
    public bool C3AMerged =>
        (Ports[2].HasData && Ports[2].State == 0x11) ||
        (Ports[3].HasData && Ports[3].State == 0x11);

    /// <summary>
    /// 合并时这一路读数挂在哪个口上：谁有数算谁的；两个都没数就当没合并。
    /// -1 = 不按合并显示（界面保持原样）。
    /// </summary>
    public int MergedOwner
    {
        get
        {
            if (!C3AMerged) return -1;
            if (Ports[2].Working) return 2;
            if (Ports[3].Working) return 3;
            return -1;
        }
    }

    /// <summary>这一行是不是合并行的正主（名字显示 C3+A、读数照常）。</summary>
    public bool IsMergedOwner(int index) => MergedOwner >= 0 && index == MergedOwner;

    /// <summary>这一行是不是被并进合并行的那一路（自己不再显示读数，只说明「并入 C3+A」）。</summary>
    public bool IsMergedFollower(int index)
        => MergedOwner >= 0 && index != MergedOwner && (index == 2 || index == 3);

    public Snapshot Clone()
    {
        var c = new Snapshot
        {
            At = At, Connected = Connected, Status = Status, Error = Error,
            Firmware = Firmware, PortSwitch = PortSwitch, SceneMode = SceneMode,
        };
        for (int i = 0; i < Ports.Length; i++)
        {
            var cp = Ports[i].Clone();
            c.Ports[i] = cp;
        }
        return c;
    }
}
