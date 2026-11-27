import Foundation

/// 一次「单口采集」的累计过程：每秒一个采样点，按实际时间间隔积分成 Wh。
///
/// 关于电压档位切换：设备会随协议协商在 5V/9V/15V/20V 之间跳，同一个 1 秒区间里
/// 前后两点电压可能差好几伏。这时候**不能**按梯形平均——5V→20V 的算术平均是
/// 12.5V，而设备其实是一瞬间切过去的，直接平均会把这一秒的能量算高一倍多。
/// 所以只在「同档位」（电压差 < 0.35V，留出 0.1V 分辨率的抖动余量）时用梯形，
/// 电压明显跳变的那一段按跳变**前**的功率走完，等下一段再按新档位累计。
///
/// 这样做的代价是跨档那一秒会略微**少**算（比如 5V→20V 只按 5V 记），
/// 但不会多算。采样越密、采集越长，这点误差占比越小。
/// 反过来用梯形或多算，用户会看到一个比实际更大的电量，那更糟。
/// 一个逐秒采样点。采集过程本身不落盘，只有用户点「导出 CSV」时才按这些点生成文本。
///
/// `t` 是相对采集开始的秒数（不是绝对时间）：导出去做图时横轴从 0 起最省事，
/// 绝对时间只出现在文件名和摘要里。
public struct CaptureSample: Sendable, Equatable {
    public let t: Double
    public let voltage: Double
    public let current: Double
    public let power: Double
    /// 这一秒是否真的在输出。空载样本也记下来 —— 曲线要靠它断开，而不是补成 0 瓦。
    public let live: Bool

    public init(t: Double, voltage: Double, current: Double, power: Double, live: Bool) {
        self.t = t
        self.voltage = voltage
        self.current = current
        self.power = power
        self.live = live
    }
}

public struct CaptureAccumulator: Sendable {
    /// 采样次数（含空载样本）。
    public private(set) var sampleCount = 0
    /// 累计充入电量（Wh）。
    public private(set) var energyWh: Double = 0
    /// 峰值功率（W）。
    public private(set) var peakPower: Double = 0
    /// 有效时长：真正在输出的秒数（空载的那些秒不计）。
    public private(set) var effectiveSeconds: Double = 0
    /// 电压档位跳变次数（差值 ≥ 0.5V 记一次）。
    public private(set) var voltageChanges = 0
    public private(set) var minVoltage: Double?
    public private(set) var maxVoltage: Double?
    /// 峰值电流（A）：只统计带载样本，拔线瞬间的 0A 不参与。
    public private(set) var peakCurrent: Double = 0
    /// 「电压 × 秒」的积分，除以 effectiveSeconds 就是平均电压。
    public private(set) var voltSeconds: Double = 0
    /// 「电流 × 秒」的积分，同上。
    public private(set) var ampSeconds: Double = 0
    public private(set) var lastVoltage: Double = 0
    public private(set) var lastCurrent: Double = 0
    public private(set) var lastPower: Double = 0
    public private(set) var lastSampleAt: Date?
    public private(set) var firstActiveAt: Date?
    public private(set) var lastActiveAt: Date?

    /// 逐秒采样点，最多 `sampleCap` 个。只在内存里，用户点「导出 CSV」才变成文件。
    public private(set) var samples: [CaptureSample] = []
    /// 约 2 小时（每秒一个点）。再长就让导出文件大得没人愿意打开；
    /// 采集本身不受影响，只是前 2 小时之后的点不再记，统计数字照常累计。
    public static let sampleCap = 7200

    /// 上一个采样点。带 active 是因为「换档」只能在前后都带载时才算：
    /// 拔线那一刻电压从 9V 掉到 0V，差值够大，但那是拔线不是换档。
    private var prev: (v: Double, i: Double, t: Date, active: Bool)?
    /// 第一个采样点的时刻，用来把 `samples` 里的时间换成「相对开始多少秒」。
    private var firstSampleAt: Date?

    public init() {}

    /// 平均功率按「有效时长」算：energy / 有输出的时间，而不是从开始到结束的总时长，
    /// 否则最后那 30 秒空载等待会把平均值拉低。
    public var averagePower: Double {
        guard effectiveSeconds > 0.5, energyWh > 0 else { return 0 }
        return energyWh / (effectiveSeconds / 3600)
    }

    /// 平均电压 / 平均电流：跟能量同口径——都用「有效秒数」做分母，
    /// 跨档那一秒按跳变前的档位记（和 avgW 一致），所以不会因为 5V→20V 的
    /// 算术平均把平均值抬高。有效时间太短（<= 0.5 秒）时直接给 0，没意义。
    public var averageVoltage: Double {
        guard effectiveSeconds > 0.5, voltSeconds > 0 else { return 0 }
        return voltSeconds / effectiveSeconds
    }

    public var averageCurrent: Double {
        guard effectiveSeconds > 0.5, ampSeconds > 0 else { return 0 }
        return ampSeconds / effectiveSeconds
    }

    /// 最近一次采样是否还在输出。
    public var isActive: Bool {
        lastVoltage * lastCurrent > 0.05 && lastVoltage > 0.05
    }

    public var hasAnyData: Bool { sampleCount > 0 }

    public mutating func add(_ d: PortData, at t: Date) {
        let power = d.voltage * d.current
        let active = d.state != 0x00 && power > 0.05

        sampleCount += 1
        lastSampleAt = t
        lastVoltage = d.voltage
        lastCurrent = d.current
        lastPower = power

        // 逐秒留一份原始读数，供「导出 CSV」用。空载样本也记，不然曲线会连成一条假直线。
        if firstSampleAt == nil { firstSampleAt = t }
        if samples.count < Self.sampleCap {
            samples.append(CaptureSample(t: t.timeIntervalSince(firstSampleAt ?? t),
                                         voltage: d.voltage,
                                         current: d.current,
                                         power: power,
                                         live: active))
        }

        // 电压区间/峰值电流只在带载时更新：拔线后设备会报 0V，
        // 把那个 0V 记进区间会让「电压区间」显示成 0–9V，看着像掉压。
        if active {
            peakPower = max(peakPower, power)
            peakCurrent = max(peakCurrent, d.current)
            minVoltage = min(minVoltage ?? d.voltage, d.voltage)
            maxVoltage = max(maxVoltage ?? d.voltage, d.voltage)
            if firstActiveAt == nil { firstActiveAt = t }
            lastActiveAt = t
        }

        if let p = prev {
            let dt = t.timeIntervalSince(p.t)
            // 间隔过大说明中间断过（重连 / 系统休眠），这段没有采样，硬算会凭空多出电量
            if active && dt > 0.2 && dt <= 4.0 {
                let sameRail = abs(d.voltage - p.v) < 0.35
                let wPrev = p.v * p.i
                // 同档用两端平均，跨档按跳变前的值——能量、电压、电流三段口径必须一致，
                // 否则会出现「电量对不上平均功率×时长」这种自相矛盾的数字。
                let avgW = sameRail ? (wPrev + power) / 2 : wPrev
                let vSeg = sameRail ? (p.v + d.voltage) / 2 : p.v
                let iSeg = sameRail ? (p.i + d.current) / 2 : p.i
                energyWh += avgW * dt / 3600
                voltSeconds += vSeg * dt
                ampSeconds += iSeg * dt
                effectiveSeconds += dt
            }
        }
        if let p = prev, active, p.active, abs(d.voltage - p.v) >= 0.5 { voltageChanges += 1 }
        prev = (d.voltage, d.current, t, active)
    }
}

/// 采集为什么结束。
public enum CaptureEndReason: String, Sendable {
    /// 这个口连续 30 秒没有输出（拔线 / 充满 / 设备关口）。
    case noLoad
    /// 连续 30 秒读不到端口数据（蓝牙链路还在但设备不应答）。
    case noData
    /// 蓝牙断开。
    case disconnected
    /// 用户手动停止。
    case stopped

    public var text: String {
        switch self {
        case .noLoad: return "这个口 30 秒没有输出，采集结束"
        case .noData: return "30 秒读不到数据，采集结束"
        case .disconnected: return "蓝牙断开了，采集结束"
        case .stopped: return "手动停止"
        }
    }

    public var shortText: String {
        switch self {
        case .noLoad: return "无输出"
        case .noData: return "无数据"
        case .disconnected: return "已断开"
        case .stopped: return "手动停止"
        }
    }
}

/// 采集结束后的结果。**只放在内存里**给弹窗看，不落盘、不进日志。
public struct CaptureResult: Sendable {
    public var portIndex: Int
    public var portName: String
    public var startedAt: Date
    public var endedAt: Date
    public var stats: CaptureAccumulator
    public var reason: CaptureEndReason
    public var firmware: String?

    public init(portIndex: Int, startedAt: Date, endedAt: Date,
                stats: CaptureAccumulator, reason: CaptureEndReason, firmware: String?) {
        self.portIndex = portIndex
        self.portName = PortData.portName(portIndex)
        self.startedAt = startedAt
        self.endedAt = endedAt
        self.stats = stats
        self.reason = reason
        self.firmware = firmware
    }

    public var totalSeconds: Double { endedAt.timeIntervalSince(startedAt) }

    /// 有效时长：优先用积分里的（真正在输出的秒数），没有就退回总时长。
    public var activeSeconds: Double {
        stats.effectiveSeconds > 0.5 ? stats.effectiveSeconds : 0
    }

    /// 平均电压/电流：没带载过就给「—」，别显示 0.00 让人以为坏了。
    public var averageVoltageText: String {
        stats.averageVoltage > 0.05 ? String(format: "%.2f", stats.averageVoltage) : "—"
    }

    public var peakVoltageText: String {
        guard let hi = stats.maxVoltage else { return "—" }
        return String(format: "%.2f", hi)
    }

    public var peakCurrentText: String {
        stats.peakCurrent > 0.005 ? String(format: "%.2f", stats.peakCurrent) : "—"
    }

    public var averageCurrentText: String {
        stats.averageCurrent > 0.005 ? String(format: "%.2f", stats.averageCurrent) : "—"
    }

    /// 电压区间有没有跨档（跨了才值得写成「5.0–20.0V」）。
    public var spansRails: Bool {
        guard let lo = stats.minVoltage, let hi = stats.maxVoltage else { return false }
        return abs(hi - lo) >= 0.3
    }

    public var voltageDetailText: String {
        guard let lo = stats.minVoltage, let hi = stats.maxVoltage else { return "—" }
        return String(format: "%.2f V（最大 %.2f V）", lo, hi)
    }

    public var currentDetailText: String {
        guard stats.peakCurrent > 0.005 else { return "—" }
        return String(format: "%.2f A（最大 %.2f A）", stats.averageCurrent, stats.peakCurrent)
    }

    public var energyText: String { String(format: "%.2f", stats.energyWh) }
    public var averageText: String { String(format: "%.1f", stats.averagePower) }
    public var peakText: String { String(format: "%.1f", stats.peakPower) }

    public var voltageText: String {
        guard let lo = stats.minVoltage, let hi = stats.maxVoltage else { return "—" }
        if abs(hi - lo) < 0.15 { return String(format: "%.1fV", hi) }
        return String(format: "%.1f–%.1fV", lo, hi)
    }

    /// 「1 分 12 秒」这种。超过 1 小时给「1 小时 03 分」。
    public static func duration(_ seconds: Double) -> String {
        let s = max(0, Int(seconds.rounded()))
        if s < 60 { return "\(s) 秒" }
        if s < 3600 {
            let m = s / 60, r = s % 60
            return r == 0 ? "\(m) 分" : "\(m) 分 \(r) 秒"
        }
        let h = s / 3600, m = (s % 3600) / 60
        return String(format: "%d 小时 %02d 分", h, m)
    }

    public var activeDurationText: String { Self.duration(activeSeconds) }
    public var totalDurationText: String { Self.duration(totalSeconds) }

    /// 导出 CSV 的默认文件名：`cuktech-capture-C1-20261127-220145.csv`。
    /// 端口名与 Windows 版一致（C1/C2/C3/A），两边的导出文件可以直接放在一起比。
    public var defaultFileName: String {
        var port = portName.isEmpty ? "P\(portIndex)" : portName
        for bad in ["/", "\\", ":", "?", "*", "|", "\"", "<", ">"] {
            port = port.replacingOccurrences(of: bad, with: "_")
        }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone.current
        f.dateFormat = "yyyyMMdd-HHmmss"
        return "cuktech-capture-\(port)-\(f.string(from: startedAt)).csv"
    }

    /// 逐秒数据导成 CSV：一行表头，之后每秒一行，行尾 CRLF。
    ///
    /// 数值一律按 `en_US_POSIX` 格式化。跟着系统区域走的话，法语/德语系统会把小数点
    /// 写成逗号，整张表从这一行开始错列 —— 这种文件发给别人，Excel 一打开就是一团。
    /// 列的顺序、精度、表头文字都与 Windows 版逐字一致，两边导出的文件可以直接对比。
    public func csvText() -> String {
        func num(_ v: Double, _ digits: Int) -> String {
            String(format: "%.\(digits)f", locale: Locale(identifier: "en_US_POSIX"), v)
        }
        var out = "时间(s),电压(V),电流(A),功率(W),带载\r\n"
        for s in stats.samples {
            out += num(s.t, 1) + ","
            out += num(s.voltage, 2) + ","
            out += num(s.current, 3) + ","
            out += num(s.power, 2) + ","
            out += (s.live ? "1" : "0") + "\r\n"
        }
        return out
    }

    /// 复制到剪贴板用的纯文本摘要。
    public var summaryText: String {
        var lines: [String] = []
        lines.append("酷态科 10 号 Ultra · \(portName) 采集结果")
        lines.append("充入电量：\(energyText) Wh")
        lines.append("平均功率：\(averageText) W")
        lines.append("峰值功率：\(peakText) W")
        lines.append("平均电压：\(averageVoltageText) V（最大 \(peakVoltageText) V）")
        lines.append("平均电流：\(averageCurrentText) A（最大 \(peakCurrentText) A）")
        lines.append("有效时长：\(activeDurationText)（总 \(totalDurationText)）")
        lines.append("电压区间：\(voltageText)，换档 \(stats.voltageChanges) 次")
        lines.append("采样：\(stats.sampleCount) 次")
        lines.append("结束原因：\(reason.text)")
        return lines.joined(separator: "\n")
    }
}
