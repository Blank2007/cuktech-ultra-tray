#if canImport(CoreBluetooth)
import Foundation
import CuktechCore

/// 把「扫广播 → 连接 → 认证 → 定时读四路端口」串起来。
/// 界面只跟它打交道；断线自动重连，退避策略与 Windows 版 DeviceClient 一致：
/// 连不上（设备不在范围）快速重试，认证失败才长退避，免得把设备打挂。
public actor DeviceSession {
    public private(set) var snapshot = DeviceSnapshot()
    public var onSnapshot: ((DeviceSnapshot) -> Void)?
    public var onLog: ((String) -> Void)?

    private let mac: [UInt8]
    private let token: [UInt8]
    public var pollInterval: TimeInterval = 2.0

    private var link: CBMibleLink?
    private var mible: MibleSession?
    private var running = false
    private var failures = 0

    public init(mac: [UInt8], token: [UInt8]) {
        self.mac = mac
        self.token = token
    }

    public var macText: String {
        mac.map { String(format: "%02X", $0) }.joined(separator: ":")
    }

    public func isRunning() -> Bool { running }

    /// 界面侧要在启动前注入这几项；actor 的存储属性不能从外面直接改，所以开三个门。
    public func setPollInterval(_ v: TimeInterval) { pollInterval = max(0.5, v) }
    public func setLogHandler(_ handler: ((String) -> Void)?) { onLog = handler }
    public func setSnapshotHandler(_ handler: ((DeviceSnapshot) -> Void)?) { onSnapshot = handler }

    public func start() async {
        guard !running else { return }
        running = true
        failures = 0
        log("[session] 开始，目标 \(macText)")

        while running {
            do {
                try await connectOnce()
                failures = 0
                while running {
                    guard let l = link, l.isConnected else { break }
                    try await refreshOnce()
                    try await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
                }
                if running {
                    publish(status: "连接断了，正在重连…", connected: false, error: "设备断开")
                    log("[session] 连接断开，准备重连")
                }
            } catch {
                failures += 1
                let msg = humanize(error)
                publish(status: "没连上", connected: false, error: msg)
                log("[fail#\(failures)] \(msg)")
                let delay = backoffSeconds(failures)
                log("[session] \(Int(delay)) 秒后重试")
                try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            }
        }
        log("[session] 已停止")
    }

    public func stop() {
        running = false
        link?.disconnect()
        link = nil
        mible = nil
    }

    /// 立刻重连：丢掉当前会话，让外层循环重新开始。
    public func reconnectNow() {
        link?.disconnect()
        link = nil
        mible = nil
    }

    // MARK: - 连接与轮询

    private func connectOnce() async throws {
        publish(status: "正在连接…", connected: false, error: nil)

        let l = CBMibleLink()
        l.onLog = { [weak self] s in Task { await self?.log(s) } }
        l.onDisconnect = { [weak self] why in Task { await self?.log("[session] 断开：\(why)") } }
        link = l
        mible = nil

        try await l.connect(mac: mac)
        let firmware = try? await l.readFirmware()

        let session = try MibleSession(link: l, token: token)
        session.trace = { [weak self] t in Task { await self?.log(t) } }
        l.onNotification = { ch, data in session.handleNotification(ch, data) }
        mible = session

        publish(status: "正在认证…", connected: false, error: nil)
        let started = Date()
        try await session.authenticate()
        let ms = Int(Date().timeIntervalSince(started) * 1000)
        log("[ok] 认证完成 \(ms)ms 固件=\(firmware ?? "?")")

        var snap = snapshot
        snap.connected = true
        snap.status = "已连接"
        snap.error = nil
        snap.firmware = firmware
        snapshot = snap

        try await refreshOnce()
    }

    private func refreshOnce() async throws {
        guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }

        var snap = snapshot
        snap.connected = true
        snap.status = "已连接"
        snap.error = nil

        if let m = try await s.sendCommand(siid: 2, piid: 16, timeout: 4), let v = m.uintValue {
            snap.portSwitch = Int(v & 0x0F)
        }
        if let m = try await s.sendCommand(siid: 2, piid: 5, timeout: 4), let first = m.valueBytes.first {
            snap.sceneMode = Int(first)
        }

        var answered = 0
        for i in 0..<4 {
            var slot = snap.ports[i]
            slot.enabled = (snap.portSwitch & (1 << i)) != 0
            if let m = try await s.sendCommand(siid: 2, piid: UInt16(i + 1), timeout: 2.5),
               let d = PortData.parse(m.valueBytes) {
                slot.apply(d)
                answered += 1
            }
            snap.ports[i] = slot
        }
        snap.at = Date()

        if answered == 0 { throw MibleError.timeout("四个端口都没有应答") }

        snapshot = snap
        onSnapshot?(snap)
    }

    // MARK: - 控制

    /// index: 0=C1 1=C2 2=C3 3=USB-A。读-改-写，不会影响其它口。
    public func setPortEnabled(_ index: Int, _ enabled: Bool) async throws {
        guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }
        guard index >= 0 && index < 4 else { throw MibleError.auth("端口序号只能是 0~3") }

        // 先读基线：读失败绝不能按 0 兜底，否则会把其它口一起关掉
        guard let curMsg = try await s.sendCommand(siid: 2, piid: 16, timeout: 4),
              let cur = curMsg.uintValue else {
            throw MibleError.timeout("读取端口开关失败（设备无响应），为了不误关其它口，本次没有写入")
        }
        let bit = UInt32(1) << UInt32(index)
        let next = enabled ? (cur | bit) : (cur & ~bit)

        guard try await s.sendCommand(siid: 2, piid: 16, value: next, timeout: 6) != nil else {
            throw MibleError.timeout("写入端口开关没有收到确认")
        }
        guard let backMsg = try await s.sendCommand(siid: 2, piid: 16, timeout: 4),
              let back = backMsg.uintValue else {
            throw MibleError.timeout("已写入，但回读失败，实际状态未知")
        }
        snapshot.portSwitch = Int(back & 0x0F)
        if back != next {
            throw MibleError.auth(String(format: "设备只接受了 0x%02X（请求 0x%02X），可能是该口不支持", back, next))
        }
        try? await refreshOnce()
    }

    /// 切换充电模式（SIID=2 PIID=5，取值 1~4）。
    public func setSceneMode(_ mode: Int) async throws {
        guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }
        guard mode >= 1 && mode <= 4 else { throw MibleError.auth("充电模式只有 1~4") }

        guard try await s.sendCommand(siid: 2, piid: 5, value: UInt32(mode), timeout: 6) != nil else {
            throw MibleError.timeout("写入充电模式没有收到确认")
        }
        if let m = try await s.sendCommand(siid: 2, piid: 5, timeout: 4), let first = m.valueBytes.first {
            snapshot.sceneMode = Int(first)
            if Int(first) != mode {
                throw MibleError.auth("设备只接受了模式 \(first)（请求 \(mode)）")
            }
        }
        try? await refreshOnce()
    }

    // MARK: - 内部

    private func publish(status: String, connected: Bool, error: String?) {
        var snap = snapshot
        snap.connected = connected
        snap.status = status
        snap.error = error
        snapshot = snap
        onSnapshot?(snap)
    }

    private func backoffSeconds(_ failures: Int) -> TimeInterval {
        let exp = min(max(failures - 1, 0), 4)
        return min(8.0, pow(2.0, Double(min(exp, 2))) * 2.0)
    }

    private func log(_ s: String) { onLog?(s) }

    private func humanize(_ error: Error) -> String {
        if let e = error as? CBLinkError { return e.description }
        if let e = error as? MibleError { return e.description }
        return error.localizedDescription
    }
}
#endif
