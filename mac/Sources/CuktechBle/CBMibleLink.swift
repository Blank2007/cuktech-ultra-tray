#if canImport(CoreBluetooth)
import Foundation
import CoreBluetooth
import CuktechCore

/// MiBeacon (0xFE95) 广播里的身份信息。macOS 不给 BD_ADDR，
/// 但小米的广播里自带 MAC，所以匹配设备靠它（这是 kairui1108/cuktech-ble-server
/// darwin 分支的做法，也是 mac-plan.md §1 里写的那条）。
public struct MiBeaconAdvertisement: Equatable, Sendable {
    public let mac: [UInt8]        // 6 字节，从高到低，如 3C:CD:73:27:61:A0
    public let productId: UInt16
    public let frameCounter: UInt8

    public var macString: String {
        mac.map { String(format: "%02X", $0) }.joined(separator: ":")
    }

    /// 解析 0xFE95 服务数据。
    ///   帧控制(2, LE) | productId(2, LE) | 计数(1) | MAC(6, 小端) [| 能力位/对象...]
    /// macOS 交过来的是**不带服务 UUID 头**的裸 payload，这里也兼容多带 `95 FE` 的情况。
    public static func parse(serviceData: Data) -> MiBeaconAdvertisement? {
        var b = [UInt8](serviceData)
        // CoreBluetooth 交给我们的就是裸 payload（实测这台机器是
        // `10 59 | 0E 66 | 00 | 30 8B 31 73 CD 3C`，前两字节是帧控制而不是 95 FE）。
        // 但个别环境会把 2 字节 UUID 头一起塞进来，所以两种布局都接受。
        if b.count >= 2 && b[0] == 0x95 && b[1] == 0xFE {
            b.removeFirst(2)
        }
        guard b.count >= 11 else { return nil }
        let frameCtrl = UInt16(b[0]) | (UInt16(b[1]) << 8)
        guard (frameCtrl >> 4) & 1 == 1 else { return nil }   // 只有置位时后面才带 MAC
        let productId = UInt16(b[2]) | (UInt16(b[3]) << 8)
        let counter = b[4]
        let macLe = Array(b[5..<11])
        return MiBeaconAdvertisement(mac: macLe.reversed(), productId: productId, frameCounter: counter)
    }
}

public enum CBLinkError: Error, CustomStringConvertible {
    case bluetoothUnavailable(String)
    case deviceNotFound(String)
    case timeout(String)
    case connectFailed(String)
    case characteristicMissing(String)
    case writeFailed(String)

    public var description: String {
        switch self {
        case .bluetoothUnavailable(let s): return "蓝牙不可用：\(s)"
        case .deviceNotFound(let s): return "没扫到设备：\(s)"
        case .timeout(let s): return "超时：\(s)"
        case .connectFailed(let s): return "连接失败：\(s)"
        case .characteristicMissing(let s): return "缺少特征：\(s)"
        case .writeFailed(let s): return "写入失败：\(s)"
        }
    }
}

/// CoreBluetooth 传输层：扫广播解 MAC → 连接 → 按 MIoT 特征名映射到 MibleChannel。
/// 实现 MibleLink（异步写/订阅），收到的通知交给 MibleSession.handleNotification。
///
/// 线程模型：所有 CB 回调都在同一个串行队列上，状态用 NSLock 保护；
/// 上层用「等标志位」的方式等待（不用 continuation，避免回调丢失导致挂死）。
public final class CBMibleLink: NSObject, MibleLink, @unchecked Sendable {
    private static let serviceUUID = CBUUID(string: "FE95")

    public var onNotification: ((MibleChannel, [UInt8]) -> Void)?
    public var onDisconnect: ((String) -> Void)?
    public var onLog: ((String) -> Void)?

    private let lock = NSLock()
    private let queue = DispatchQueue(label: "io.github.blank2007.cuktechtray.ble")

    private var central: CBCentralManager!
    private var peripheral: CBPeripheral?
    private var service: CBService?
    private var chars: [MibleChannel: CBCharacteristic] = [:]

    private var centralState: CBManagerState = .unknown
    private var found: CBPeripheral?
    private var foundAdv: MiBeaconAdvertisement?
    private var connected = false
    private var servicesDiscovered = false
    private var charsDiscovered = false
    private var notifyState: [MibleChannel: Bool] = [:]
    private var writeDone = 0
    private var writeErrors: [String] = []
    private var targetMAC: [UInt8] = []

    // 固件版本特征 0x0004：Windows 版靠 link.ReadShortAsync(0x0004) 读，macOS 走 readValue。
    private var firmwareChar: CBCharacteristic?
    private var firmwareValue: [UInt8]?
    private var firmwareDone = false

    public override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: queue, options: [CBCentralManagerOptionShowPowerAlertKey: true])
    }

    // MARK: - 对外

    /// 扫到 MAC 匹配的设备并连上，订阅关系留着由 MibleSession 发起。
    public func connect(mac: [UInt8], scanTimeout: TimeInterval = 20) async throws {
        guard mac.count == 6 else { throw CBLinkError.deviceNotFound("MAC 格式不对") }
        lock.lock(); targetMAC = mac; found = nil; foundAdv = nil; connected = false
        servicesDiscovered = false; charsDiscovered = false; chars.removeAll(); lock.unlock()

        // 第一次启动时系统会弹「允许使用蓝牙」，弹窗没点掉之前 state 一直是 unknown，
        // 这里等 30 秒（而不是 10 秒），免得用户刚看到弹窗就被判定“连不上”进入退避。
        log("等蓝牙就绪（首次会弹权限框，请点“允许”）")
        do {
            try await waitUntil(30) { self.readState() == .poweredOn }
        } catch {
            throw CBLinkError.bluetoothUnavailable("蓝牙 30 秒内没就绪，检查「系统设置 → 隐私与安全性 → 蓝牙」是否允许了本程序")
        }
        log("蓝牙就绪，开始扫描")

        lock.lock(); central.scanForPeripherals(withServices: [Self.serviceUUID], options: nil); lock.unlock()
        do {
            try await waitUntil(scanTimeout) { self.readFound() != nil }
        } catch {
            lock.lock(); central.stopScan(); lock.unlock()
            throw CBLinkError.deviceNotFound("扫不到 MAC 为 \(macString(mac)) 的广播")
        }
        lock.lock(); central.stopScan(); let p = found!; lock.unlock()
        log("扫到 \(macString(mac))，开始连接")

        lock.lock(); peripheral = p; p.delegate = self; central.connect(p, options: nil); lock.unlock()
        try await waitUntil(15) { self.readConnected() }

        log("已连接，找服务")
        lock.lock(); p.discoverServices([Self.serviceUUID]); lock.unlock()
        try await waitUntil(10) { self.readServicesDiscovered() }
        guard let svc = readService() else { throw CBLinkError.characteristicMissing("FE95 服务") }

        lock.lock(); p.discoverCharacteristics(nil, for: svc); lock.unlock()
        try await waitUntil(10) { self.readCharsDiscovered() }
        log("特征齐了：\(readCharNames())")
    }

    public func disconnect() {
        lock.lock()
        let p = peripheral
        peripheral = nil
        connected = false
        chars.removeAll()
        lock.unlock()
        if let p = p { central.cancelPeripheralConnection(p) }
    }

    public var isConnected: Bool { readConnected() }

    /// 读固件版本字符串（特征 0x0004 的 ASCII，如 "2.1.2_0073"）。读不到就返回 nil，不影响认证。
    public func readFirmware(timeout: TimeInterval = 4) async throws -> String? {
        guard let p = currentPeripheral() else { return nil }
        lock.lock()
        let ch = firmwareChar
        firmwareValue = nil
        firmwareDone = false
        lock.unlock()
        guard let ch = ch else { return nil }

        p.readValue(for: ch)
        do {
            try await waitUntil(timeout) { self.readFirmwareDone() }
        } catch {
            return nil
        }
        lock.lock(); let raw = firmwareValue; lock.unlock()
        guard let raw = raw, !raw.isEmpty else { return nil }
        var s = ""
        for b in raw {
            if b == 0 { break }
            if b >= 0x20 && b < 0x7F { s.append(Character(UnicodeScalar(b))) }
        }
        return s.isEmpty ? nil : s
    }

    private func readFirmwareDone() -> Bool { lock.lock(); defer { lock.unlock() }; return firmwareDone }

    // MARK: - MibleLink

    public func subscribe(_ channel: MibleChannel) async throws {
        guard let ch = characteristic(channel) else { throw CBLinkError.characteristicMissing(name(of: channel)) }
        guard let p = currentPeripheral() else { throw CBLinkError.connectFailed("还没连上") }

        if ch.properties.contains(.notify) || ch.properties.contains(.indicate) {
            lock.lock(); notifyState[channel] = false; lock.unlock()
            p.setNotifyValue(true, for: ch)
            try await waitUntil(5) { self.readNotify(channel) }
        }
    }

    public func unsubscribe(_ channel: MibleChannel) async throws {
        guard let ch = characteristic(channel), let p = currentPeripheral() else { return }
        lock.lock(); notifyState[channel] = false; lock.unlock()
        p.setNotifyValue(false, for: ch)
    }

    public func write(_ channel: MibleChannel, _ data: [UInt8]) async throws {
        guard let ch = characteristic(channel) else { throw CBLinkError.characteristicMissing(name(of: channel)) }
        guard let p = currentPeripheral() else { throw CBLinkError.connectFailed("还没连上") }

        let type: CBCharacteristicWriteType = ch.properties.contains(.write) ? .withResponse : .withoutResponse
        let limit = max(20, p.maximumWriteValueLength(for: type))
        var offset = 0
        while offset < data.count {
            let n = min(limit, data.count - offset)
            let chunk = Data(data[offset..<(offset + n)])
            let expected: Int
            lock.lock()
            writeDone = 0
            writeErrors.removeAll()
            expected = 1
            lock.unlock()

            p.writeValue(chunk, for: ch, type: type)
            if type == .withResponse {
                try await waitUntil(4) { self.readWriteDone() >= expected }
                lock.lock(); let errs = writeErrors; lock.unlock()
                if let e = errs.first { throw CBLinkError.writeFailed(e) }
            } else {
                // 无响应写要自己限速，否则设备端会丢包
                try await Task.sleep(nanoseconds: 20_000_000)
            }
            offset += n
        }
    }

    // MARK: - 状态读取（都带锁）

    private func readState() -> CBManagerState { lock.lock(); defer { lock.unlock() }; return centralState }
    private func readFound() -> CBPeripheral? { lock.lock(); defer { lock.unlock() }; return found }
    private func readConnected() -> Bool { lock.lock(); defer { lock.unlock() }; return connected }
    private func readServicesDiscovered() -> Bool { lock.lock(); defer { lock.unlock() }; return servicesDiscovered }
    private func readCharsDiscovered() -> Bool { lock.lock(); defer { lock.unlock() }; return charsDiscovered }
    private func readService() -> CBService? { lock.lock(); defer { lock.unlock() }; return service }
    private func readNotify(_ ch: MibleChannel) -> Bool { lock.lock(); defer { lock.unlock() }; return notifyState[ch] ?? false }
    private func readWriteDone() -> Int { lock.lock(); defer { lock.unlock() }; return writeDone }
    private func characteristic(_ ch: MibleChannel) -> CBCharacteristic? {
        lock.lock(); defer { lock.unlock() }; return chars[ch]
    }
    private func currentPeripheral() -> CBPeripheral? { lock.lock(); defer { lock.unlock() }; return peripheral }
    private func readCharNames() -> String {
        lock.lock(); defer { lock.unlock() }
        return chars.keys.sorted { $0.rawValue < $1.rawValue }.map { name(of: $0) }.joined(separator: ",")
    }

    private func waitUntil(_ seconds: TimeInterval, _ cond: @escaping () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if cond() { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        throw CBLinkError.timeout("等了 \(Int(seconds)) 秒")
    }

    private func log(_ s: String) { onLog?(s) }

    private func name(of ch: MibleChannel) -> String {
        switch ch {
        case .deviceInfo: return "DeviceInfo(0x001c)"
        case .authCtrl: return "AuthCtrl(0x0010)"
        case .authData: return "AuthData(0x0019)"
        case .cmdSend: return "CmdSend(0x001a)"
        case .cmdRecv: return "CmdRecv(0x001b)"
        }
    }

    private func macString(_ mac: [UInt8]) -> String {
        mac.map { String(format: "%02X", $0) }.joined(separator: ":")
    }
}

extension CBMibleLink: CBCentralManagerDelegate {
    public func centralManagerDidUpdateState(_ central: CBCentralManager) {
        lock.lock(); centralState = central.state; lock.unlock()
        log("central state=\(central.state.rawValue)")
    }

    public func centralManager(_ central: CBCentralManager,
                               didDiscover peripheral: CBPeripheral,
                               advertisementData: [String: Any],
                               rssi RSSI: NSNumber) {
        guard let sd = advertisementData[CBAdvertisementDataServiceDataKey] as? [CBUUID: Data] else { return }
        var adv: MiBeaconAdvertisement?
        for (_, data) in sd {
            if let parsed = MiBeaconAdvertisement.parse(serviceData: data) { adv = parsed; break }
        }
        let name = peripheral.name ?? ""
        guard let a = adv else { return }

        lock.lock()
        let target = targetMAC
        var hit = false
        if a.mac == target {
            found = peripheral
            foundAdv = a
            hit = true
        }
        lock.unlock()

        if hit {
            log("命中 \(a.macString) product=0x\(String(format: "%04X", a.productId)) rssi=\(RSSI.intValue) name=\(name)")
        }
    }

    public func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        lock.lock(); connected = true; lock.unlock()
        log("didConnect")
    }

    public func centralManager(_ central: CBCentralManager,
                               didFailToConnect peripheral: CBPeripheral, error: Error?) {
        lock.lock(); connected = false; lock.unlock()
        log("didFailToConnect \(error?.localizedDescription ?? "")")
    }

    public func centralManager(_ central: CBCentralManager,
                               didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        lock.lock(); connected = false; chars.removeAll(); lock.unlock()
        let why = error?.localizedDescription ?? "设备主动断开"
        log("didDisconnect \(why)")
        onDisconnect?(why)
    }
}

extension CBMibleLink: CBPeripheralDelegate {
    public func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        lock.lock()
        service = peripheral.services?.first { $0.uuid == Self.serviceUUID }
        servicesDiscovered = true
        lock.unlock()
        if let e = error { log("didDiscoverServices error \(e.localizedDescription)") }
    }

    public func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        var mapped: [MibleChannel: CBCharacteristic] = [:]
        var fw: CBCharacteristic?
        for ch in service.characteristics ?? [] {
            for candidate in MibleChannel.allCases where ch.uuid == CBUUID(string: shortUUID(candidate)) {
                mapped[candidate] = ch
            }
            if ch.uuid == CBUUID(string: "0004") { fw = ch }
        }
        lock.lock(); chars = mapped; firmwareChar = fw; charsDiscovered = true; lock.unlock()
        if let e = error { log("didDiscoverCharacteristics error \(e.localizedDescription)") }
    }

    private func shortUUID(_ ch: MibleChannel) -> String {
        let v = ch.rawValue
        return String(format: "%04X", v)
    }

    public func peripheral(_ peripheral: CBPeripheral,
                           didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        guard let ch = channel(of: characteristic) else { return }
        lock.lock()
        notifyState[ch] = (error == nil && characteristic.isNotifying)
        lock.unlock()
        if let e = error { log("订阅 \(name(of: ch)) 失败 \(e.localizedDescription)") }
    }

    public func peripheral(_ peripheral: CBPeripheral,
                           didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard error == nil, let data = characteristic.value else { return }

        lock.lock(); let fw = firmwareChar; lock.unlock()
        if let fw = fw, fw == characteristic {
            lock.lock(); firmwareValue = [UInt8](data); firmwareDone = true; lock.unlock()
            return
        }
        guard let ch = channel(of: characteristic) else { return }
        onNotification?(ch, [UInt8](data))
    }

    public func peripheral(_ peripheral: CBPeripheral,
                           didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        lock.lock()
        writeDone += 1
        if let e = error { writeErrors.append(e.localizedDescription) }
        lock.unlock()
    }

    private func channel(of characteristic: CBCharacteristic) -> MibleChannel? {
        lock.lock(); defer { lock.unlock() }
        for (ch, c) in chars where c == characteristic { return ch }
        return nil
    }
}
#endif
