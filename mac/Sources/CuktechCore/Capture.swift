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
public struct CaptureAccumulator: Sendable {
    /// 采样次数（含空载样本）。
    public private(set) var sampleCount = 0
    /// 累计充入电量（Wh）。
    public private(set) var energyWh: Double = 0
    /// 峰值功率（W）。
    public private(set) var peakPower: Double = 0
    /// 峰值电流（A）。只统计真正在输出的样本，空载那 30 秒的 0.00A 不算。
    public private(set) var peakCurrent: Double = 0
    /// 有效时长：真正在输出的秒数（空载的那些秒不计）。
    public private(set) var effectiveSeconds: Double = 0
    /// 电压档位跳变次数（差值 ≥ 0.5V 记一次）。
    public private(set) var voltageChanges = 0
    public private(set) var minVoltage: Double?
    public private(set) var maxVoltage: Double?
    /// 电压、电流按时间加权累计（V·s、A·s），除以 `effectiveSeconds` 就是平均值。
    /// 和能量走同一个分段（跨档那段用跳变前的值），所以 平均值 × 平均值 与平均功率同口径。
    public private(set) var voltSeconds: Double = 0
    public private(set) var ampSeconds: Double = 0
    public private(set) var lastVoltage: Double = 0
    public private(set) var lastCurrent: Double = 0
    public private(set) var lastPower: Double = 0
    public private(set) var lastSampleAt: Date?
    public private(set) var firstActiveAt: Date?
    public private(set) var lastActiveAt: Date?

    private var prev: (v: Double, i: Double, t: Date, active: Bool)?

    public init() {}

    /// 平均功率按「有效时长」算：energy / 有输出的时间，而不是从开始到结束的总时长，
    /// 否则最后那 30 秒空载等待会把平均值拉低。
    public var averagePower: Double {
        guard effectiveSeconds > 0.5, energyWh > 0 else { return 0 }
        return energyWh / (effectiveSeconds / 3600)
    }

    /// 最近一次采样是否还在输出。
    public var isActive: Bool {
        lastVoltage * lastCurrent > 0.05 && lastVoltage > 0.05
    }

    /// 时间加权的平均电压（V）。和电流表那种「平均电压」同口径：按输出时长平均，
    /// 不是简单把每秒读数相加再除以次数——两者在空载段长度不同时会差很多。
    public var averageVoltage: Double {
        guard effectiveSeconds > 0.5 else { return 0 }
        return voltSeconds / effectiveSeconds
    }

    /// 时间加权的平均电流（A）。
    public var averageCurrent: Double {
        guard effectiveSeconds > 0.5 else { return 0 }
        return ampSeconds / effectiveSeconds
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

        if active {
            peakPower = max(peakPower, power)
            peakCurrent = max(peakCurrent, d.current)
            // 电压上下限也只统计带载样本：空口的 5V 待机电、拔线后的 0V 都不是「充进去的时候用了多少伏」。
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
                let avgW = sameRail ? (wPrev + power) / 2 : wPrev
                energyWh += avgW * dt / 3600
                effectiveSeconds += dt
                // 电压/电流用同一套分段：同档位取两端平均，跨档取跳变前的值。
                // 这样 平均电压 × 平均电流 与平均功率口径一致，不会出现「电压电流都偏高、
                // 乘积却比功率大一大截」的怪现象。
                let vSeg = sameRail ? (p.v + d.voltage) / 2 : p.v
                let iSeg = sameRail ? (p.i + d.current) / 2 : p.i
                voltSeconds += vSeg * dt
                ampSeconds += iSeg * dt
            }
        }
        // 换档只在「前后两次都有输出」时才算：拔线/充满那一刻电压会掉到 0，
        // 9.1V→0V 差得比谁都多，但它不是换档（第一版就把它算成了一次）。
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

    public var energyText: String { String(format: "%.2f", stats.energyWh) }
    public var averageText: String { String(format: "%.1f", stats.averagePower) }
    public var peakText: String { String(format: "%.1f", stats.peakPower) }
    /// 没有带载样本时（空口采集）这些数字就是 0，直接显示「—」比显示 0.00 V 更老实。
    public var averageVoltageText: String {
        stats.averageVoltage > 0.05 ? String(format: "%.2f", stats.averageVoltage) : "—"
    }
    public var averageCurrentText: String {
        stats.averageCurrent > 0.005 ? String(format: "%.2f", stats.averageCurrent) : "—"
    }
    public var peakCurrentText: String {
        stats.peakCurrent > 0.005 ? String(format: "%.2f", stats.peakCurrent) : "—"
    }
    public var peakVoltageText: String {
        stats.maxVoltage.map { String(format: "%.2f", $0) } ?? "—"
    }
    /// 电压真的跨过档位（最小和最大差得明显）时，才额外显示一行「电压区间」，
    /// 否则它跟上面的「平均/最大电压」是同一条信息。
    public var spansRails: Bool {
        guard let lo = stats.minVoltage, let hi = stats.maxVoltage else { return false }
        return abs(hi - lo) >= 0.3
    }
    /// 平均电压带上最大值，和电流表读数对得上。
    public var voltageDetailText: String {
        guard stats.averageVoltage > 0.05 else { return "—" }
        return "\(averageVoltageText) V（最大 \(peakVoltageText) V）"
    }
    public var currentDetailText: String {
        guard stats.averageCurrent > 0.005 else { return "—" }
        return "\(averageCurrentText) A（最大 \(peakCurrentText) A）"
    }

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

    /// 复制到剪贴板用的纯文本摘要。
    public var summaryText: String {
        var lines: [String] = []
        lines.append("酷态科 10 号 Ultra · \(portName) 采集结果")
        lines.append("充入电量：\(energyText) Wh")
        lines.append("平均功率：\(averageText) W")
        lines.append("峰值功率：\(peakText) W")
        lines.append("平均电压：\(voltageDetailText)")
        lines.append("平均电流：\(currentDetailText)")
        lines.append("有效时长：\(activeDurationText)（总 \(totalDurationText)）")
        lines.append("电压区间：\(voltageText)，换档 \(stats.voltageChanges) 次")
        lines.append("采样：\(stats.sampleCount) 次")
        lines.append("结束原因：\(reason.text)")
        return lines.joined(separator: "\n")
    }
}
