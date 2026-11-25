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

    public init() {}

    private enum CodingKeys: String, CodingKey {
        case mac, tokenHex, pollInterval, showWattsInMenuBar, privacyAccepted
    }

    /// 手写解码：缺字段一律回落到默认值，别让新增字段把老用户的 config.json 判成坏文件。
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        mac = try c.decodeIfPresent(String.self, forKey: .mac) ?? ""
        tokenHex = try c.decodeIfPresent(String.self, forKey: .tokenHex) ?? ""
        pollInterval = try c.decodeIfPresent(Double.self, forKey: .pollInterval) ?? 2.0
        showWattsInMenuBar = try c.decodeIfPresent(Bool.self, forKey: .showWattsInMenuBar) ?? true
        privacyAccepted = try c.decodeIfPresent(Bool.self, forKey: .privacyAccepted) ?? false
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
