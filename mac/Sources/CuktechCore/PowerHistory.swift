import Foundation

/// 一拍采样：单调时钟上的秒数 + 四路瓦数。
///
/// 某一路 < 0 表示这一拍画不出点（没连上 / 这一路没读数 / C3+A 合并时被并进去的那一路）。
/// 曲线在那里要**断开**，而不是连一条直线过去 —— 连过去会让「拔了线」看着像「功率平滑下降」。
public struct PowerPoint: Sendable, Equatable {
    public let t: Double
    public let watts: [Double]

    public init(t: Double, watts: [Double]) {
        self.t = t
        self.watts = watts
    }

    /// 第 i 路（0..3）这一刻的瓦数；越界或负数表示这一点不存在。
    public func at(_ index: Int) -> Double {
        index >= 0 && index < watts.count ? watts[index] : -1
    }
}

/// 内存里的功率历史。面板第四个页签（曲线）用它画四条线。
///
/// 为什么**不写盘**：用户要的是「看一眼刚才的走势」，不是长期记录；真要留数据请用
/// 「单口采集」——那条路每秒一行、结束能导出 CSV。写盘还要处理清理、隐私、权限一堆事，
/// 而这块只是给面板看一眼的。与 Windows 版 src/CuktechTrayAot/PowerHistory.cs 同一套语义。
///
/// 容量：1 Hz × 30 分钟 = 1800 点，到顶丢最老的。时间戳用 systemUptime（单调时钟），
/// 不用 Date：改系统时间、夏令时都不会让曲线打结。
public struct PowerHistory: Sendable {
    /// 最多留多少个点（1 Hz 下 30 分钟）。
    public static let capacity = 1800
    /// 两拍之间太近就不记：设备推送偶尔会连着来两次，记下来只是让曲线多几个重复点。
    public static let minStep: Double = 0.4

    private var points: [PowerPoint] = []
    private var lastT: Double = -.infinity

    public init() {}

    /// 当前单调时钟（秒）。
    public static var now: Double { ProcessInfo.processInfo.systemUptime }

    public var count: Int { points.count }

    public mutating func clear() {
        points.removeAll(keepingCapacity: true)
        lastT = -.infinity
    }

    /// 记一拍。四路各自的瓦数按 value(_:_:) 的规矩取。
    public mutating func add(_ snap: DeviceSnapshot) {
        add(snap, at: Self.now)
    }

    /// 显式给时间戳的版本：单元测试用，也方便演示数据对齐。
    public mutating func add(_ snap: DeviceSnapshot, at t: Double) {
        if lastT > -.infinity && t - lastT < Self.minStep { return }
        if t < lastT {
            // 时钟回退（极端情况：容器迁移、系统 bug）就当重来
            points.removeAll(keepingCapacity: true)
            lastT = -.infinity
        }
        points.append(PowerPoint(t: t, watts: [
            Self.value(snap, 0),
            Self.value(snap, 1),
            Self.value(snap, 2),
            Self.value(snap, 3),
        ]))
        if points.count > Self.capacity {
            points.removeFirst(points.count - Self.capacity)
        }
        lastT = t
    }

    /// 某一拍这一路的瓦数。-1 = 这一点不存在。
    static func value(_ snap: DeviceSnapshot, _ index: Int) -> Double {
        guard snap.connected else { return -1 }
        guard let slot = snap.ports.first(where: { $0.index == index + 1 }) else { return -1 }
        guard slot.hasData else { return -1 }
        // 合并时搭车的 C3 或 A 没有自己的数字，画出来会跟 owner 那条重合
        if snap.isMergedPassenger(slot) { return -1 }
        return max(0, slot.watts)
    }

    /// 取最近 windowSeconds 秒内的点（窗口右端可以指定，方便测试）。
    public func snapshot(_ windowSeconds: Double, at now: Double? = nil) -> [PowerPoint] {
        let window = max(1, windowSeconds)
        let from = (now ?? Self.now) - window
        return points.filter { $0.t >= from }
    }

    /// --panel-demo 与文档截图用：造一段固定形状的假数据（同一条曲线每次跑都一样）。
    /// c3Only = true 时只有 C3 带载、另外三路 0 瓦，用来看「单口输出」时曲线页认不认得出来。
    public static func demo(c3Only: Bool = false, samples: Int = 180, now: Double? = nil) -> PowerHistory {
        var h = PowerHistory()
        let base = now ?? Self.now
        // xorshift64：要的是「每次跑都一样」的可复现抖动，不是真随机数。
        var seed: UInt64 = 0x2545_F491_4F6C_DD1D
        func unit() -> Double {
            seed ^= seed << 13
            seed ^= seed >> 7
            seed ^= seed << 17
            return Double(seed % 1000) / 1000.0
        }
        let n = max(2, samples)
        for i in stride(from: n, through: 0, by: -1) {
            let phase = Double(n - i) / Double(n)
            let t = base - Double(i)
            var w = [0.0, 0.0, 0.0, 0.0]
            if c3Only {
                let solo = 9 + 5 * sin(phase * 9.0) + unit() * 0.6
                w[2] = (max(0.6, solo) * 10).rounded() / 10
            } else {
                let baseW = 42 + 28 * sin(phase * .pi * 2.1)
                w[0] = ((baseW + unit() * 4 - 2) * 10).rounded() / 10
                if w[0] < 0 { w[0] = 0 }
                w[2] = (phase > 0.35 && phase < 0.82) ? ((16 + 7 * sin(phase * 11)) * 10).rounded() / 10 : 0
                w[3] = -1
            }
            h.points.append(PowerPoint(t: t, watts: w))
            h.lastT = t
        }
        return h
    }
}
