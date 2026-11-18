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

    public double TotalWatts => Ports.Sum(p => p.Working ? p.Watts : 0);

    public Snapshot Clone()
    {
        var c = new Snapshot
        {
            At = At, Connected = Connected, Status = Status, Error = Error,
            Firmware = Firmware, PortSwitch = PortSwitch,
        };
        for (int i = 0; i < Ports.Length; i++)
        {
            var cp = Ports[i].Clone();
            c.Ports[i] = cp;
        }
        return c;
    }
}
