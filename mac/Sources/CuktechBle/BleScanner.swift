#if canImport(CoreBluetooth)
import Foundation
import CoreBluetooth
import CuktechCore

/// 扫描时看到的一台设备。macOS 拿不到 BD_ADDR，这里的 mac 是从 MiBeacon
/// 广播的 FE95 服务数据里解出来的（与 CBMibleLink 同一套解析）。
public struct BleFoundDevice: Sendable, Identifiable, Equatable {
    public let mac: [UInt8]
    public let name: String
    public let rssi: Int
    public let productId: UInt16

    public var macString: String {
        mac.map { String(format: "%02X", $0) }.joined(separator: ":")
    }

    public var id: String { macString }

    public var productIdHex: String { String(format: "0x%04X", productId) }

    /// 26126 = 0x660E，酷态科 10 号 Ultra 的产品号
    public var isCuktechCharger: Bool { productId == XiaomiClient.targetProductID }

    public init(mac: [UInt8], name: String, rssi: Int, productId: UInt16) {
        self.mac = mac
        self.name = name
        self.rssi = rssi
        self.productId = productId
    }
}

/// 只负责「看看附近有哪些设备」，连上去不归它管（那是 CBMibleLink）。
/// 配对向导用它把列表显示出来，让用户点选。
public final class BleScanner: NSObject, @unchecked Sendable {
    private static let serviceUUID = CBUUID(string: "FE95")

    private let queue = DispatchQueue(label: "io.github.blank2007.cuktechtray.scan")
    private let lock = NSLock()

    private var central: CBCentralManager!
    private var managerState: CBManagerState = .unknown
    private var devices: [String: BleFoundDevice] = [:]
    private var continuation: CheckedContinuation<[BleFoundDevice], Error>?
    private var finished = false
    private var updateHandler: (@Sendable ([BleFoundDevice]) -> Void)?

    public override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: queue,
                                   options: [CBCentralManagerOptionShowPowerAlertKey: true])
    }

    /// 扫一段时间，返回期间见过的所有设备（按信号强弱排序）。
    /// onUpdate 每发现一台就回调一次，向导用它实时刷新列表。
    public func scan(timeout: TimeInterval = 8,
                     onUpdate: (@Sendable ([BleFoundDevice]) -> Void)? = nil) async throws -> [BleFoundDevice] {
        try await waitUntilPoweredOn()

        lock.lock()
        devices.removeAll()
        finished = false
        updateHandler = onUpdate
        lock.unlock()

        central.scanForPeripherals(withServices: [Self.serviceUUID],
                                   options: [CBCentralManagerScanOptionAllowDuplicatesKey: true])

        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<[BleFoundDevice], Error>) in
            lock.lock()
            let alreadyDone = finished
            if !alreadyDone { continuation = cont }
            let snapshot = sortedDevices()
            lock.unlock()
            if alreadyDone {
                central.stopScan()
                cont.resume(returning: snapshot)
                return
            }
            queue.asyncAfter(deadline: .now() + timeout) { [weak self] in
                self?.finish()
            }
        }
    }

    /// 提前结束（用户点了「重扫」或关掉向导）。
    public func stop() {
        finish()
    }

    // MARK: - 内部

    private func waitUntilPoweredOn() async throws {
        let deadline = Date().addingTimeInterval(5)
        while Date() < deadline {
            switch readState() {
            case .poweredOn:
                return
            case .unauthorized:
                throw CBLinkError.bluetoothUnavailable("系统没给蓝牙权限（系统设置 → 隐私与安全性 → 蓝牙）")
            case .poweredOff:
                throw CBLinkError.bluetoothUnavailable("蓝牙是关着的")
            case .unsupported:
                throw CBLinkError.bluetoothUnavailable("这台机器没有可用的蓝牙")
            default:
                break   // unknown / resetting：再等一会儿
            }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        throw CBLinkError.bluetoothUnavailable("蓝牙初始化超时")
    }

    private func readState() -> CBManagerState {
        lock.lock(); defer { lock.unlock() }
        return managerState
    }

    private func sortedDevices() -> [BleFoundDevice] {
        devices.values.sorted { $0.rssi > $1.rssi }
    }

    private func finish() {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        finished = true
        let cont = continuation
        continuation = nil
        updateHandler = nil
        let snapshot = sortedDevices()
        lock.unlock()
        central.stopScan()
        cont?.resume(returning: snapshot)
    }
}

extension BleScanner: CBCentralManagerDelegate {
    public func centralManagerDidUpdateState(_ central: CBCentralManager) {
        lock.lock(); managerState = central.state; lock.unlock()
    }

    public func centralManager(_ central: CBCentralManager,
                               didDiscover peripheral: CBPeripheral,
                               advertisementData: [String: Any],
                               rssi RSSI: NSNumber) {
        let sections = advertisementData[CBAdvertisementDataServiceDataKey] as? [CBUUID: Data]
        guard let payload = sections?[Self.serviceUUID],
              let adv = MiBeaconAdvertisement.parse(serviceData: payload) else { return }

        let name = (advertisementData[CBAdvertisementDataLocalNameKey] as? String) ?? peripheral.name ?? ""
        let device = BleFoundDevice(mac: adv.mac, name: name, rssi: RSSI.intValue, productId: adv.productId)

        lock.lock()
        devices[device.id] = device
        let handler = updateHandler
        let snapshot = sortedDevices()
        lock.unlock()

        handler?(snapshot)
    }
}
#endif
