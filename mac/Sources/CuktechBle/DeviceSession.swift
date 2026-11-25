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

    /// 串行化命令事务并让用户操作优先抢占后台轮询，防止 Swift actor 重入导致 BLE 通道交错。
    private var transactionBusy = false
    private var urgentControlCount = 0
    private var urgentWaiters: [CheckedContinuation<Void, Never>] = []
    private var backgroundWaiters: [CheckedContinuation<Void, Never>] = []

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
                teardownLink()
                if running {
                    publish(status: "连接断了，正在重连…", connected: false, error: "设备断开")
                    log("[session] 连接断开，准备重连")
                    try? await Task.sleep(nanoseconds: 800_000_000)
                }
            } catch {
                // 异常退出前必须主动断开旧连接，否则充电器被旧连接占住会停止广播 FE95，后续重连一直扫不到
                teardownLink()
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
        teardownLink()
    }

    /// 立刻重连：丢掉当前会话，让外层循环重新开始。
    public func reconnectNow() {
        teardownLink()
    }

    // MARK: - 连接与轮询

    private func teardownLink() {
        let old = link
        link = nil
        mible = nil
        old?.onNotification = nil
        old?.onDisconnect = nil
        old?.onLog = nil
        old?.disconnect()
    }

    private func connectOnce() async throws {
        teardownLink()
        publish(status: "正在连接…", connected: false, error: nil)

        let l = CBMibleLink()
        l.onLog = { [weak self, weak l] s in
            Task { await self?.handleLinkLog(l, s) }
        }
        l.onDisconnect = { [weak self, weak l] why in
            Task { await self?.handleDisconnect(l, why) }
        }
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

    private func handleLinkLog(_ source: CBMibleLink?, _ msg: String) {
        guard let source, link === source else { return }
        log(msg)
    }

    private func handleDisconnect(_ source: CBMibleLink?, _ why: String) {
        guard let source, link === source else { return }
        log("[session] 断开：\(why)")
    }

    private func refreshOnce() async throws {
        guard mible?.authenticated == true else { throw MibleError.notAuthenticated }

        var snap = snapshot
        snap.connected = true
        snap.status = "已连接"
        snap.error = nil

        if urgentControlCount > 0 { return }
        if let m = try await withTransactionLock(urgent: false, {
            guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }
            return try await s.sendCommand(siid: 2, piid: 16, timeout: 4)
        }), let v = m.uintValue {
            snap.portSwitch = Int(v & 0x0F)
        }

        if urgentControlCount > 0 { return }
        if let m = try await withTransactionLock(urgent: false, {
            guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }
            return try await s.sendCommand(siid: 2, piid: 5, timeout: 4)
        }), let first = m.valueBytes.first {
            snap.sceneMode = Int(first)
        }

        var answered = 0
        for i in 0..<4 {
            if urgentControlCount > 0 { return }
            var slot = snap.ports[i]
            slot.enabled = (snap.portSwitch & (1 << i)) != 0
            if let m = try await withTransactionLock(urgent: false, {
                guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }
                return try await s.sendCommand(siid: 2, piid: UInt16(i + 1), timeout: 2.5)
            }), let d = PortData.parse(m.valueBytes) {
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
        guard index >= 0 && index < 4 else { throw MibleError.auth("端口序号只能是 0~3") }

        urgentControlCount += 1
        defer { urgentControlCount -= 1 }

        try await withTransactionLock(urgent: true) {
            guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }

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
            // 物理开关硬件生效约需 150~250ms，留出稳定时间避免紧接着 GET 读到切换前旧值
            try? await Task.sleep(nanoseconds: 250_000_000)

            var backMsg = try await s.sendCommand(siid: 2, piid: 16, timeout: 4)
            if let v = backMsg?.uintValue, v != next {
                try? await Task.sleep(nanoseconds: 250_000_000)
                if let retryMsg = try await s.sendCommand(siid: 2, piid: 16, timeout: 4) {
                    backMsg = retryMsg
                }
            }
            guard let back = backMsg?.uintValue else {
                throw MibleError.timeout("已写入，但回读失败，实际状态未知")
            }
            snapshot.portSwitch = Int(back & 0x0F)
            if index < snapshot.ports.count {
                snapshot.ports[index].enabled = (snapshot.portSwitch & (1 << index)) != 0
            }
            onSnapshot?(snapshot)
            if back != next {
                throw MibleError.auth(String(format: "设备只接受了 0x%02X（请求 0x%02X），可能是该口不支持", back, next))
            }
        }
    }

    /// 切换充电模式（SIID=2 PIID=5，取值 1~4）。
    public func setSceneMode(_ mode: Int) async throws {
        guard mode >= 1 && mode <= 4 else { throw MibleError.auth("充电模式只有 1~4") }

        urgentControlCount += 1
        defer { urgentControlCount -= 1 }

        try await withTransactionLock(urgent: true) {
            guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }

            guard try await s.sendCommand(siid: 2, piid: 5, value: UInt32(mode), timeout: 6) != nil else {
                throw MibleError.timeout("写入充电模式没有收到确认")
            }
            if let m = try await s.sendCommand(siid: 2, piid: 5, timeout: 4), let first = m.valueBytes.first {
                snapshot.sceneMode = Int(first)
                onSnapshot?(snapshot)
                if Int(first) != mode {
                    throw MibleError.auth("设备只接受了模式 \(first)（请求 \(mode)）")
                }
            }
        }
    }

    // MARK: - 内部

    private func withTransactionLock<T>(urgent: Bool, _ body: () async throws -> T) async throws -> T {
        while transactionBusy {
            await withCheckedContinuation { cont in
                if urgent {
                    urgentWaiters.append(cont)
                } else {
                    backgroundWaiters.append(cont)
                }
            }
        }
        transactionBusy = true
        defer {
            transactionBusy = false
            if !urgentWaiters.isEmpty {
                let next = urgentWaiters.removeFirst()
                next.resume()
            } else if !backgroundWaiters.isEmpty {
                let next = backgroundWaiters.removeFirst()
                next.resume()
            }
        }
        return try await body()
    }

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
