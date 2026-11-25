using CuktechMible;

namespace CuktechTray;

/// <summary>
/// 一次「单口采集」的累计过程：每秒一个采样点，按实际时间间隔积分成 Wh。
///
/// 关于电压档位切换：设备会随协议协商在 5V/9V/15V/20V 之间跳，同一个 1 秒区间里
/// 前后两点电压可能差好几伏。这时候**不能**按梯形平均 —— 5V→20V 的算术平均是 12.5V，
/// 而设备其实是一瞬间切过去的，直接平均会把这一秒的能量算高一倍多。
/// 所以只在「同档位」（电压差 &lt; 0.35V，留出 0.1V 分辨率的抖动余量）时用梯形，
/// 电压明显跳变的那一段按跳变**前**的功率走完，等下一段再按新档位累计。
/// 代价是跨档那一秒会略微**少**算，但不会多算 —— 少算用户只是看到偏小的电量，
/// 多算就是错账，那更糟。（算法与 macOS 版一致，见 PR #4 的 Capture.swift）
///
/// 0.5.1 起对齐 macOS 0.1.6：
/// · 平均电压 / 平均电流不再拿「最后一次采样」凑数，而是各积一份 ∫V dt / ∫I dt 再除以有效时长；
///   电压、电流、能量三者的分段口径完全一致（同档取两端平均、跨档取跳变前的值），
///   所以「平均电压 × 平均电流」和「平均功率」能对得上。
/// · 最低 / 最高电压只认**带载**样本：空口插上充电器时那 5V 待机电、拔线之后的 0V 都不该是「最低电压」。
/// · 峰值电流同理只统计带载样本，空载时读到的零点几毫安不算。
/// · 换档次数要求前后两点都在带载，否则「拔线」这种 12V→0V 也会被记成换档。
/// </summary>
internal sealed class CaptureAccumulator
{
    /// <summary>采样次数（含空载样本）。</summary>
    public int SampleCount { get; private set; }

    /// <summary>累计充入电量（Wh）。</summary>
    public double EnergyWh { get; private set; }

    /// <summary>峰值功率（W）。</summary>
    public double PeakPower { get; private set; }

    /// <summary>有效时长：真正在输出的秒数（空载的那些秒不计）。</summary>
    public double EffectiveSeconds { get; private set; }

    /// <summary>电压档位跳变次数（相邻两个带载点差 ≥ 0.5V 记一次）。</summary>
    public int VoltageChanges { get; private set; }

    /// <summary>带载样本的最低 / 最高电压（V）。空口那 5V 待机电、拔线后的 0V 都不算。</summary>
    public double? MinVoltage { get; private set; }
    public double? MaxVoltage { get; private set; }

    /// <summary>带载样本的峰值电流（A）。空载时读到的零点几毫安不算。</summary>
    public double PeakCurrent { get; private set; }

    /// <summary>∫V dt（伏·秒），除以有效时长就是平均电压。</summary>
    public double VoltSeconds { get; private set; }

    /// <summary>∫I dt（安·秒），除以有效时长就是平均电流。</summary>
    public double AmpSeconds { get; private set; }

    public double LastVoltage { get; private set; }
    public double LastCurrent { get; private set; }
    public double LastPower { get; private set; }
    public DateTime? LastSampleAt { get; private set; }

    private double _prevV;
    private double _prevI;
    private DateTime _prevT;
    private bool _prevActive;
    private bool _hasPrev;

    /// <summary>
    /// 平均功率按「有效时长」算：energy / 有输出的时间，而不是从开始到结束的总时长，
    /// 否则最后那 30 秒空载等待会把平均值拉低。
    /// </summary>
    public double AveragePower => EffectiveSeconds > 0.5 && EnergyWh > 0
        ? EnergyWh / (EffectiveSeconds / 3600.0)
        : 0;

    /// <summary>平均电压（V）：带载时间的 ∫V dt / 有效时长。不到半秒的成功积分就按 0（跟平均功率一个规矩）。</summary>
    public double AverageVoltage => EffectiveSeconds > 0.5 ? VoltSeconds / EffectiveSeconds : 0;

    /// <summary>平均电流（A）：带载时间的 ∫I dt / 有效时长。</summary>
    public double AverageCurrent => EffectiveSeconds > 0.5 ? AmpSeconds / EffectiveSeconds : 0;

    /// <summary>最近一次采样是否还在输出。</summary>
    public bool IsActive => LastVoltage > 0.05 && LastVoltage * LastCurrent > 0.05;

    public void Add(PortData d, DateTime t)
    {
        double power = d.Voltage * d.Current;
        bool active = d.State != 0x00 && power > 0.05;

        SampleCount++;
        LastSampleAt = t;
        LastVoltage = d.Voltage;
        LastCurrent = d.Current;
        LastPower = power;

        // 最低 / 最高电压只认带载样本：空口插着充电器时那 5V 待机电、拔线之后的 0V 都不是「用户看到的电压」
        if (active)
        {
            MinVoltage = MinVoltage is double lo ? Math.Min(lo, d.Voltage) : d.Voltage;
            MaxVoltage = MaxVoltage is double hi ? Math.Max(hi, d.Voltage) : d.Voltage;
        }

        if (active)
        {
            if (power > PeakPower) PeakPower = power;
            if (d.Current > PeakCurrent) PeakCurrent = d.Current;
        }

        if (_hasPrev)
        {
            double dt = (t - _prevT).TotalSeconds;
            // 间隔过大说明中间断过（重连 / 系统休眠），这段没有采样，硬算会凭空多出电量
            if (active && dt > 0.2 && dt <= 4.0)
            {
                bool sameRail = Math.Abs(d.Voltage - _prevV) < 0.35;
                double wPrev = _prevV * _prevI;
                // 电压、电流、能量用同一套分段口径：同档取两端平均，跨档按跳变**前**的值走完这一段，
                // 这样三个积分量才能互相印证（平均电压 × 平均电流 ≈ 平均功率）
                double avgW = sameRail ? (wPrev + power) / 2 : wPrev;
                double avgV = sameRail ? (_prevV + d.Voltage) / 2 : _prevV;
                double avgI = sameRail ? (_prevI + d.Current) / 2 : _prevI;
                EnergyWh += avgW * dt / 3600.0;
                VoltSeconds += avgV * dt;
                AmpSeconds += avgI * dt;
                EffectiveSeconds += dt;
            }
            // 前后两点都在带载才算换档：否则拔线那一下 12V→0V 也会被记成一次换档
            if (active && _prevActive && Math.Abs(d.Voltage - _prevV) >= 0.5) VoltageChanges++;
        }

        _prevV = d.Voltage;
        _prevI = d.Current;
        _prevT = t;
        _prevActive = active;
        _hasPrev = true;
    }

    /// <summary>交给界面的快照（界面跨线程读，不能直接读正在累加的对象）。</summary>
    public CaptureAccumulator Clone()
    {
        var c = new CaptureAccumulator
        {
            SampleCount = SampleCount,
            EnergyWh = EnergyWh,
            PeakPower = PeakPower,
            EffectiveSeconds = EffectiveSeconds,
            VoltageChanges = VoltageChanges,
            MinVoltage = MinVoltage,
            MaxVoltage = MaxVoltage,
            PeakCurrent = PeakCurrent,
            VoltSeconds = VoltSeconds,
            AmpSeconds = AmpSeconds,
            LastVoltage = LastVoltage,
            LastCurrent = LastCurrent,
            LastPower = LastPower,
            LastSampleAt = LastSampleAt,
            _prevV = _prevV,
            _prevI = _prevI,
            _prevT = _prevT,
            _prevActive = _prevActive,
            _hasPrev = _hasPrev,
        };
        return c;
    }
}

/// <summary>
/// 采集数值的显示口径（0.5.1）：空载一律「—」，带载才给数字。
/// 结果页、窗口里的实时值、复制到剪贴板的摘要都走这里，免得三处各写一套阈值。
/// </summary>
internal static class CaptureFormat
{
    public const string None = "—";

    /// <summary>电压不到 0.05V 就是没输出（空口 / 拔线后的 0V）。</summary>
    public static string Voltage(double v) => v > 0.05 ? v.ToString("0.00") + " V" : None;

    /// <summary>电流不到 0.005A 就是没输出（空载时能读到零点几毫安）。</summary>
    public static string Current(double i) => i > 0.005 ? i.ToString("0.00") + " A" : None;

    /// <summary>功率不到 0.05W 同理。</summary>
    public static string Power(double w) => w > 0.05 ? w.ToString("0.0") + " W" : None;

    /// <summary>「19.98 V（最大 20.10 V）」；平均值本身是「—」时整条也是「—」。</summary>
    public static string Detail(string average, string peak) =>
        average == None ? None : average + "（最大 " + peak + "）";
}

/// <summary>采集为什么结束。</summary>
internal enum CaptureEndReason
{
    /// <summary>这个口连续 30 秒没有输出（拔线 / 充满 / 设备关口）。</summary>
    NoLoad,

    /// <summary>连续 30 秒读不到端口数据（蓝牙还在但设备不应答）。</summary>
    NoData,

    /// <summary>蓝牙断开。</summary>
    Disconnected,

    /// <summary>用户手动停止。</summary>
    Stopped,
}

/// <summary>采集结束后的结果。**只放在内存里**给窗口看，不落盘、不进日志正文。</summary>
internal sealed class CaptureResult
{
    public int PortIndex { get; init; }
    public string PortName { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime EndedAt { get; init; }
    public CaptureAccumulator Stats { get; init; } = new();
    public CaptureEndReason Reason { get; init; }
    public string Firmware { get; init; } = "";

    public double TotalSeconds => (EndedAt - StartedAt).TotalSeconds;

    /// <summary>有效时长：真正在输出的秒数（积分里没有就按 0 算，界面上显示「—」）。</summary>
    public double ActiveSeconds => Stats.EffectiveSeconds > 0.5 ? Stats.EffectiveSeconds : 0;

    public string EnergyText => Stats.EnergyWh.ToString("0.00");
    public string AverageText => Stats.AveragePower.ToString("0.0");
    public string PeakText => Stats.PeakPower.ToString("0.0");

    public string VoltageText
    {
        get
        {
            if (Stats.MinVoltage is not double lo || Stats.MaxVoltage is not double hi) return "—";
            if (Math.Abs(hi - lo) < 0.15) return hi.ToString("0.0") + "V";
            return lo.ToString("0.0") + "–" + hi.ToString("0.0") + "V";
        }
    }

    /// <summary>平均电压（V）：带载时间的 ∫V dt / 有效时长。</summary>
    public string AverageVoltageText => CaptureFormat.Voltage(Stats.AverageVoltage);

    /// <summary>带载样本里的最高电压（V）。</summary>
    public string PeakVoltageText => CaptureFormat.Voltage(Stats.MaxVoltage ?? 0);

    /// <summary>平均电流（A）。</summary>
    public string AverageCurrentText => CaptureFormat.Current(Stats.AverageCurrent);

    /// <summary>带载样本里的峰值电流（A）。</summary>
    public string PeakCurrentText => CaptureFormat.Current(Stats.PeakCurrent);

    /// <summary>「19.98 V（最大 20.10 V）」这种明细写法，摘要里用。</summary>
    public string VoltageDetailText => CaptureFormat.Detail(AverageVoltageText, PeakVoltageText);

    public string CurrentDetailText => CaptureFormat.Detail(AverageCurrentText, PeakCurrentText);

    /// <summary>这次采集到底换没换过档：最高最低电压差 ≥ 0.3V 才算（0.1V 分辨率下 0.3V 已经是实打实的换档）。</summary>
    public bool SpansRails =>
        Stats.MinVoltage is double lo && Stats.MaxVoltage is double hi && Math.Abs(hi - lo) >= 0.3;

    public string ReasonText => Reason switch
    {
        CaptureEndReason.NoLoad => "这个口 30 秒没有输出，采集结束",
        CaptureEndReason.NoData => "30 秒读不到数据，采集结束",
        CaptureEndReason.Disconnected => "蓝牙断开了，采集结束",
        _ => "手动停止",
    };

    /// <summary>「1 分 12 秒」这种；超过 1 小时给「1 小时 03 分」。</summary>
    public static string Duration(double seconds)
    {
        int s = (int)Math.Round(Math.Max(0, seconds));
        if (s < 60) return s + " 秒";
        if (s < 3600)
        {
            int m = s / 60, r = s % 60;
            return r == 0 ? m + " 分" : m + " 分 " + r + " 秒";
        }
        int h = s / 3600, mm = (s % 3600) / 60;
        return h + " 小时 " + mm.ToString("00") + " 分";
    }

    public string ActiveDurationText => ActiveSeconds > 0.5 ? Duration(ActiveSeconds) : "—";

    public string TotalDurationText => Duration(TotalSeconds);

    /// <summary>复制到剪贴板用的纯文本摘要。</summary>
    public string SummaryText =>
        "酷态科 10 号 Ultra · " + PortName + " 采集结果" + Environment.NewLine +
        "充入电量：" + EnergyText + " Wh" + Environment.NewLine +
        "平均功率：" + AverageText + " W" + Environment.NewLine +
        "峰值功率：" + PeakText + " W" + Environment.NewLine +
        "平均电压：" + VoltageDetailText + Environment.NewLine +
        "平均电流：" + CurrentDetailText + Environment.NewLine +
        "有效时长：" + ActiveDurationText + "（总 " + TotalDurationText + "）" + Environment.NewLine +
        "电压区间：" + VoltageText + "，换档 " + Stats.VoltageChanges + " 次" + Environment.NewLine +
        "采样：" + Stats.SampleCount + " 次" + Environment.NewLine +
        "结束原因：" + ReasonText;
}