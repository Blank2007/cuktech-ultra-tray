import Foundation
import CuktechCore

/// mac 版的配置。放在 ~/Library/Application Support/CuktechTray/config.json，
/// 与 Windows 版的 settings.json 字段对齐（mac 是 "AA:BB:CC:DD:EE:FF"，token 是 24 位 hex = 12 字节）。
public struct AppSettings: Codable, Sendable {
    public var mac: String = ""
    public var tokenHex: String = ""
    public var pollInterval: Double = 2.0
    public var showWattsInMenuBar: Bool = true
    /// 首次启动的隐私说明有没有点过「同意并继续」。老配置里没有这个字段，按 false 算（继续问一次）。
    public var privacyAccepted: Bool = false
    /// 上次连上的那台设备（CBPeripheral.identifier 的字符串）。
    /// 有了它，重连 / 系统唤醒后可以直接从「系统已连接的设备」里认出它，不用等广播。
    /// **必须是 String? 而不是 String = ""**：非可选会让老配置（没这个键）整个解码失败，
    /// 而解码失败会退回默认值、下一次存盘就把 mac / token 冲掉，用户得重新配对。
    public var peripheralId: String?
    /// 面板窗口是否「始终在最前」（跨 App 置顶，不只在本 App 内固定）。
    /// 老配置没有这个键，按 false 算；解不出来也不该影响其它字段。
    public var alwaysOnTop: Bool = false
    /// 菜单栏那一格是否用**固定宽度**（58pt）。
    /// 默认开：读数每秒在变，宽度跟着变会把右边其它菜单栏图标推来推去。
    /// 老配置没有这个键，按 true 算（这才是「修好抖动」的默认行为）。
    public var fixedMenuBarWidth: Bool = true
    /// 面板背景是否用毛玻璃质感（`NSVisualEffectView`，13 以上都走这条路）。
    /// 老配置没有这个键，按 **false** 算 —— 0.1.6 默认开，结果 26/27 上把面板冲成中灰、次要文字看不清
    /// （issue #6），0.1.7 起改成默认关、要的话在托盘菜单里自己勾。
    public var glassPanel: Bool = false
    /// 启动后是否自己在后台查一次新版本（约 8 秒后，等蓝牙先连上）。
    /// 查到就在菜单那行点个红点，不弹窗；不想要就把它改成 false。
    /// 老配置没有这个键，按 **true** 算 —— 和 Windows 版一致。
    public var checkUpdatesOnStart: Bool = true
    /// 曲线页看多久（秒）：60 / 300 / 900 / 1800。点曲线页右上角胶囊循环切换。
    /// 老配置没有这个键，按 60 算（和 Windows 版 Settings.ChartSeconds 的默认值一致）。
    public var chartSeconds: Int = 60

    public init() {}

    private enum CodingKeys: String, CodingKey {
        case mac, tokenHex, pollInterval, showWattsInMenuBar, privacyAccepted
        case peripheralId, alwaysOnTop
        case fixedMenuBarWidth, glassPanel, checkUpdatesOnStart
        case chartSeconds
    }

    /// 手写解码：缺字段一律回落到默认值，别让新增字段把老用户的 config.json 判成坏文件。
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        mac = try c.decodeIfPresent(String.self, forKey: .mac) ?? ""
        tokenHex = try c.decodeIfPresent(String.self, forKey: .tokenHex) ?? ""
        pollInterval = try c.decodeIfPresent(Double.self, forKey: .pollInterval) ?? 2.0
        showWattsInMenuBar = try c.decodeIfPresent(Bool.self, forKey: .showWattsInMenuBar) ?? true
        privacyAccepted = try c.decodeIfPresent(Bool.self, forKey: .privacyAccepted) ?? false
        // 可选：老配置没有这个键就是 nil（千万别写成 ?? ""，语义上是「还不知道」而不是「空」）
        peripheralId = try c.decodeIfPresent(String.self, forKey: .peripheralId)
        alwaysOnTop = try c.decodeIfPresent(Bool.self, forKey: .alwaysOnTop) ?? false
        fixedMenuBarWidth = try c.decodeIfPresent(Bool.self, forKey: .fixedMenuBarWidth) ?? true
        glassPanel = try c.decodeIfPresent(Bool.self, forKey: .glassPanel) ?? false
        checkUpdatesOnStart = try c.decodeIfPresent(Bool.self, forKey: .checkUpdatesOnStart) ?? true
        let chart = try c.decodeIfPresent(Int.self, forKey: .chartSeconds) ?? 60
        chartSeconds = (chart >= 10 && chart <= 1800) ? chart : 60
    }

    public static var directory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/CuktechTray", isDirectory: true)
    }

    public static var fileURL: URL { directory.appendingPathComponent("config.json") }
    public static var logURL: URL { directory.appendingPathComponent("tray.log") }

    public static func load() -> AppSettings {
        guard let data = try? Data(contentsOf: fileURL),
              let s = try? JSONDecoder().decode(AppSettings.self, from: data) else {
            return AppSettings()
        }
        return s
    }

    public func save() throws {
        try FileManager.default.createDirectory(at: AppSettings.directory, withIntermediateDirectories: true)
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(self).write(to: AppSettings.fileURL, options: .atomic)
    }

    public var macBytes: [UInt8]? {
        let parts = mac.split(separator: ":").map { String($0) }
        guard parts.count == 6 else { return nil }
        var out: [UInt8] = []
        for p in parts {
            guard p.count == 2, let v = UInt8(p, radix: 16) else { return nil }
            out.append(v)
        }
        return out
    }

    public var tokenBytes: [UInt8]? {
        let clean = tokenHex.trimmingCharacters(in: .whitespacesAndNewlines)
        guard clean.count == 24 else { return nil }
        var out: [UInt8] = []
        var idx = clean.startIndex
        while idx < clean.endIndex {
            let next = clean.index(idx, offsetBy: 2)
            guard let v = UInt8(clean[idx..<next], radix: 16) else { return nil }
            out.append(v)
            idx = next
        }
        return out
    }

    /// 配好了才轮询蓝牙；不然菜单只显示「未配对」。
    public var isComplete: Bool { macBytes != nil && tokenBytes != nil }
}

/// 极简日志：追加写，超过 1MB 就滚一份 .1。日志里有 MAC 但没有 token。
public final class LogFile: @unchecked Sendable {
    private let url: URL
    private let lock = NSLock()

    public init(url: URL = AppSettings.logURL) {
        self.url = url
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    }

    public func write(_ line: String) {
        lock.lock(); defer { lock.unlock() }
        let stamp = ISO8601DateFormatter().string(from: Date())
        let text = "[\(stamp)] \(line)\n"
        guard let data = text.data(using: .utf8) else { return }
        if let size = try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int, size > 1_048_576 {
            let old = url.appendingPathExtension("1")
            try? FileManager.default.removeItem(at: old)
            try? FileManager.default.moveItem(at: url, to: old)
        }
        if let h = try? FileHandle(forWritingTo: url) {
            h.seekToEndOfFile()
            h.write(data)
            try? h.close()
        } else {
            try? data.write(to: url)
        }
    }
}
