import Foundation

/// 一个 sRGB 颜色。Core 层不 import AppKit，界面再自己转 NSColor。
public struct RGB: Equatable, Sendable {
    public let r: Double
    public let g: Double
    public let b: Double

    public init(_ r: Int, _ g: Int, _ b: Int) {
        self.r = Double(r) / 255.0
        self.g = Double(g) / 255.0
        self.b = Double(b) / 255.0
    }
}

/// 功率数字的取色与格式化。与 Windows 版 src/CuktechTray/WattStyle.cs 同一套分档，
/// 避免两个平台的颜色漂移。
public enum WattStyle {
    public static let dim  = RGB(154, 160, 166)
    public static let ok   = RGB(52, 211, 153)
    public static let warn = RGB(251, 191, 36)
    public static let bad  = RGB(248, 113, 113)
    public static let cool = RGB(125, 211, 252)

    /// 单个接口：0W 灰、小电流淡蓝、正常绿、偏高黄、接近单口上限红。
    public static func port(_ w: Double) -> RGB {
        if w >= 80 { return bad }
        if w >= 40 { return warn }
        if w >= 20 { return ok }
        if w > 0.05 { return cool }
        return dim
    }

    /// 总功率：空载灰、起步淡蓝、过半绿、偏高黄、接近满功率红。
    public static func total(_ w: Double) -> RGB {
        if w >= 100 { return bad }
        if w >= 60 { return warn }
        if w >= 20 { return ok }
        if w > 0.05 { return cool }
        return dim
    }

    /// 100W 以上不带小数，其余一位小数（三位数字再带小数就太长了）。
    public static func format(_ w: Double) -> String {
        w >= 100 ? String(format: "%.0f", w) : String(format: "%.1f", w)
    }

    public static func text(_ w: Double) -> String { format(w) + "W" }
}

/// 充电模式的四个取值（顺序即界面顺序）。对齐 Windows 版 DeviceClient.SceneModes。
public enum SceneMode {
    public static let all: [(value: Int, name: String, hint: String)] = [
        (1, "AI", "插上什么就按什么给，自动分配功率"),
        (2, "数码生态", "优先照顾自家设备，出厂默认"),
        (3, "单口", "只给一路快充，其余让路"),
        (4, "均衡", "四路平分，谁都不落下"),
    ]

    public static func name(_ value: Int) -> String {
        all.first { $0.value == value }?.name ?? "模式 \(value)"
    }

    public static func hint(_ value: Int) -> String {
        all.first { $0.value == value }?.hint ?? ""
    }
}

/// 一路端口的读数（SIID=2, PIID 1..4）。
public struct PortSlot: Equatable, Sendable {
    public var index: Int          // 1..4
    public var enabled: Bool
    public var hasData: Bool
    public var idle: Bool
    public var voltage: Double
    public var current: Double
    public var watts: Double
    public var state: UInt8
    public var code: UInt8

    public init(index: Int, enabled: Bool = true) {
        self.index = index
        self.enabled = enabled
        self.hasData = false
        self.idle = true
        self.voltage = 0
        self.current = 0
        self.watts = 0
        self.state = 0
        self.code = 0
    }

    public var name: String { PortData.portName(index) }

    /// 副标题：有读数就写 V·A，空载写「空载」。
    public var detail: String {
        if !enabled { return "已断电" }
        if !hasData { return "无应答" }
        if idle { return "空载" }
        return String(format: "%.1fV · %.2fA", voltage, current)
    }

    public mutating func apply(_ d: PortData) {
        hasData = true
        idle = d.isIdle
        voltage = d.voltage
        current = d.current
        watts = d.power
        state = d.state
        code = d.code
    }
}

/// 一次轮询拿到的完整状态。界面只读这个结构。
public struct DeviceSnapshot: Sendable {
    public var connected = false
    public var status = "未连接"
    public var error: String?
    public var firmware: String?
    public var portSwitch = 0
    public var sceneMode = 0
    public var ports: [PortSlot] = (1...4).map { PortSlot(index: $0) }
    public var at = Date()

    public init() {}

    public var totalWatts: Double { ports.reduce(0) { $0 + $1.watts } }
    public var totalText: String { WattStyle.text(totalWatts) }
    public var totalColor: RGB { WattStyle.total(totalWatts) }
    public var sceneName: String { SceneMode.name(sceneMode) }

    /// 状态行：已连接 · 固件版本 · 总输出 6.0W
    public var headline: String {
        guard connected else { return status }
        var parts: [String] = ["已连接"]
        if let fw = firmware, !fw.isEmpty { parts.append(fw) }
        parts.append("总输出 " + totalText)
        return parts.joined(separator: " · ")
    }
}
