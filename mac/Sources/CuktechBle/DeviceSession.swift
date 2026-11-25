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
    /// 正在采集的端口（1..4）。采集期间常规轮询让路，免得两路命令抢通道。
    public private(set) var capturePort: Int?
    private var captureTask: Task<Void, Never>?

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
                    // 采集期间这一口由采集任务每秒读，常规轮询先让路：
                    // 两路交错会把设备的命令通道挤乱（回读超时 / 端口无应答）。
                    if capturePort == nil {
                        // 命令串行化统一交给 withTransactionLock（refreshOnce 内部每条命令各自持锁），
                        // 这里不再额外抢通道，否则会和上游 PR #3 的锁形成两套。
                        try await refreshOnce()
                    }
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

    // MARK: - 设备设置（屏幕 / 供电 / 定时）

    /// 最近一次读到的设置。界面上「改完立刻显示」用得到，读不到就是空的。
    public private(set) var deviceSettings = DeviceSettings()

    /// 读一整份设置。逐条 GET，**读不到的留 nil**，不同固件的可答项不一样
    /// （倒计时在有的固件上是 ACK-only，回读直接超时）。
    /// 十来条 GET 连发，整段当一个事务握着命令通道：和轮询交错时设备会把后面几条吞掉，
    /// 表现就变成「这一项不存在」。用 urgent 抢，别让后台轮询插进来。
    public func reloadSettings() async -> DeviceSettings {
        guard mible?.authenticated == true else { return deviceSettings }

        let loaded: DeviceSettings? = try? await withTransactionLock(urgent: true) {
            var out = DeviceSettings()

            func get(_ piid: UInt16) async -> UInt32? {
                guard let s = mible, s.authenticated else { return nil }
                let m = try? await s.sendCommand(siid: 2, piid: piid, timeout: 3)
                guard let m, m.errorCode == 0, let v = m.uintValue else { return nil }
                return v
            }

            if let v = await get(6) { out.screenTimeout = Int(v) }
            if let v = await get(19) { out.idleScreenOff = v != 0 }
            if let v = await get(20) { out.screenLock = v != 0 }
            if let v = await get(15) { out.usbALowCurrent = v != 0 }
            if let v = await get(13) { out.language = Int(v) }
            if let v = await get(21) { out.protocolFlags = v }
            for i in 0..<4 {
                if let v = await get(UInt16(9 + i)) { out.countdowns[i] = Int(v) }
            }
            if let v = await get(17) {
                let p = Negotiation.split(v)
                out.negotiations[0] = p.high
                out.negotiations[1] = p.low
            }
            if let v = await get(18) {
                let p = Negotiation.split(v)
                out.negotiations[2] = p.high
                out.negotiations[3] = p.low
            }
            out.at = Date()
            return out
        }
        guard let out = loaded else { return deviceSettings }

        log("[settings] 读到 \(out.loadedCount) 项：息屏=\(optText(out.screenTimeout)) "
            + "空闲息屏=\(optText(out.idleScreenOff)) 方向锁=\(optText(out.screenLock)) "
            + "A小电流=\(optText(out.usbALowCurrent)) 语言=\(optText(out.language)) "
            + "协议位=\(out.protocolFlags.map { String(format: "0x%08X", $0) } ?? "?") "
            + "倒计时=\(out.countdowns.map { optText($0) }.joined(separator: "/"))")

        deviceSettings = out
        return out
    }

    private func optText<T>(_ v: T?) -> String { v.map { "\($0)" } ?? "?" }

    public func setScreenTimeout(_ value: Int) async throws {
        guard ScreenTimeout(rawValue: value) != nil else {
            throw MibleError.auth("息屏时间只有 1~5 这几个取值")
        }
        try await writeSetting(piid: 6, value: UInt32(value), name: "息屏时间", strict: true)
        deviceSettings.screenTimeout = value
    }

    public func setIdleScreenOff(_ on: Bool) async throws {
        try await writeSetting(piid: 19, value: on ? 1 : 0, name: "空闲息屏", strict: true)
        deviceSettings.idleScreenOff = on
    }

    public func setScreenLock(_ on: Bool) async throws {
        try await writeSetting(piid: 20, value: on ? 1 : 0, name: "方向锁", strict: true)
        deviceSettings.screenLock = on
    }

    public func setUSBLowCurrent(_ on: Bool) async throws {
        try await writeSetting(piid: 15, value: on ? 1 : 0, name: "USB-A 小电流", strict: true)
        deviceSettings.usbALowCurrent = on
    }

    public func setLanguage(_ value: Int) async throws {
        guard value == 0 || value == 1 else { throw MibleError.auth("语言只有 0=EN / 1=中文") }
        try await writeSetting(piid: 13, value: UInt32(value), name: "语言", strict: true)
        deviceSettings.language = value
    }

    /// 关掉某一口的某一种快充协议。PIID 21 是 32 位掩码，必须读-改-写。
    public func setProtocol(_ cap: ProtocolCap, port: Int, on: Bool) async throws {
        guard let bit = cap.bit(port: port) else {
            throw MibleError.auth("\(PortData.portName(port)) 没有 \(cap.label) 这一项")
        }
        urgentControlCount += 1
        defer { urgentControlCount -= 1 }

        try await withTransactionLock(urgent: true) {
            guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }

            // 读不到基线就拒绝写入：按 0 兜底等于把别的协议全关掉
            guard let curMsg = try await s.sendCommand(siid: 2, piid: 21, timeout: 4),
                  curMsg.errorCode == 0, let cur = curMsg.uintValue else {
                throw MibleError.timeout("读取协议开关失败（设备无响应），为了不误改其它协议，本次没有写入")
            }
            let mask = UInt32(1) << bit
            let next = on ? (cur | mask) : (cur & ~mask)

            guard try await s.sendCommand(siid: 2, piid: 21, value: next, timeout: 6) != nil else {
                throw MibleError.timeout("写入协议开关没有收到确认")
            }
            guard let backMsg = try await s.sendCommand(siid: 2, piid: 21, timeout: 4),
                  let back = backMsg.uintValue else {
                throw MibleError.timeout("已写入，但回读失败，实际状态未知")
            }
            deviceSettings.protocolFlags = back
            log(String(format: "[settings] 协议 %@ %@ -> 请求 0x%08X，回读 0x%08X",
                       PortData.portName(port), cap.label, next, back))
            if back != next {
                throw MibleError.auth(String(format: "设备只接受了 0x%08X（请求 0x%08X），可能这一项不支持", back, next))
            }
        }
    }

    /// 某口定时断电（PIID 9~12，分钟，0 = 取消）。
    public func setCountdown(port: Int, minutes: Int) async throws {
        guard (1...4).contains(port) else { throw MibleError.auth("端口序号只能是 1~4") }
        guard (0...1440).contains(minutes) else { throw MibleError.auth("倒计时只能是 0~1440 分钟") }
        try await writeSetting(piid: UInt16(8 + port), value: UInt32(minutes),
                               name: "\(PortData.portName(port)) 定时断电", strict: false)
        deviceSettings.countdowns[port - 1] = minutes
    }

    /// 写单个属性的通用流程：SET → 回读 → 比对。
    /// 倒计时这类属性回读的可能是「剩余时间」而不是设定值，所以 `strict: false`
    /// 只记日志不报错，免得把成功的写入报成失败。
    private func writeSetting(piid: UInt16, value: UInt32, name: String,
                              strict: Bool) async throws {
        urgentControlCount += 1
        defer { urgentControlCount -= 1 }

        try await withTransactionLock(urgent: true) {
            guard let s = mible, s.authenticated else { throw MibleError.notAuthenticated }

            guard try await s.sendCommand(siid: 2, piid: piid, value: value, timeout: 6) != nil else {
                throw MibleError.timeout("写入「\(name)」没有收到确认")
            }
            guard let backMsg = try await s.sendCommand(siid: 2, piid: piid, timeout: 4),
                  let back = backMsg.uintValue else {
                throw MibleError.timeout("「\(name)」已写入，但回读失败，实际状态未知")
            }
            log("[settings] \(name) 写 \(value)，回读 \(back)")
            if strict && back != value {
                throw MibleError.auth("「\(name)」设备只接受了 \(back)（请求 \(value)），可能这一项不支持")
            }
        }
    }

    // MARK: - 单口采集

    /// 每秒读一次指定端口，直到「连续 30 秒没有输出」或「连续 30 秒读不到数据」或断连。
    /// 结果只通过回调交出去（内存里），不写文件、不进日志。
    public func startCapture(port: Int,
                             onUpdate: @escaping @Sendable (CaptureAccumulator) -> Void,
                             onFinish: @escaping @Sendable (CaptureResult) -> Void) {
        guard captureTask == nil, (1...4).contains(port) else { return }
        guard let s = mible, s.authenticated else { return }
        capturePort = port
        log("[capture] 开始读 \(PortData.portName(port))，每秒一次")
        captureTask = Task { [weak self] in
            await self?.runCapture(port: port, onUpdate: onUpdate, onFinish: onFinish)
        }
    }

    public func stopCapture() {
        guard captureTask != nil else { return }
        log("[capture] 手动停止")
        captureTask?.cancel()
    }

    private func runCapture(port: Int,
                            onUpdate: @Sendable (CaptureAccumulator) -> Void,
                            onFinish: @Sendable (CaptureResult) -> Void) async {
        let started = Date()
        var acc = CaptureAccumulator()
        var lastGood = started
        var lastActive = started
        var reason: CaptureEndReason = .noData
        /// 第 n 次采样应该落在 started + n 秒。
        ///
        /// 以前是「采完再睡 1 秒」，但一次 GET 往返要 0.1~0.3 秒，一圈实际 1.2 秒，
        /// 30 秒只采到 25 次（用户看到的就是「30 秒 / 25 次」对不上）。
        /// 改成按绝对时刻排下一拍，命令耗时被吸收进等待里，才是真的每秒一次。
        var nextTick = started

        while !Task.isCancelled {
            // 结束判断放在采样**前**：空载恰好 30 秒时，这一拍不再多采一次，
            // 于是 30 秒 = 30 次，数字对得上。
            if Date().timeIntervalSince(lastGood) >= 30 {
                reason = .noData
                break
            }
            if Date().timeIntervalSince(lastActive) >= 30 {
                reason = .noLoad
                break
            }

            guard let l = link, l.isConnected else {
                reason = .disconnected
                break
            }
            guard let s = mible, s.authenticated else {
                reason = .disconnected
                break
            }

            // 采集按后台优先级排队：用户在采集期间点开关/改设置要能插到前面。
            // （sendCommand 本身返回可选值，这里用 do/catch 而不是 try?，免得套成双重可选。）
            var msg: MiotMessage?
            do {
                msg = try await withTransactionLock(urgent: false) {
                    try await s.sendCommand(siid: 2, piid: UInt16(port), timeout: 2.0)
                }
            } catch {
                msg = nil
            }

            if let d = PortData.parse(msg?.valueBytes) {
                let now = Date()
                acc.add(d, at: now)
                lastGood = now
                if d.state != 0x00 && d.power > 0.05 { lastActive = now }

                // 同步这一口到快照，面板上的读数跟着秒变
                var snap = snapshot
                var slot = snap.ports[port - 1]
                slot.apply(d)
                slot.enabled = (snap.portSwitch & (1 << (port - 1))) != 0
                snap.ports[port - 1] = slot
                snap.at = now
                snapshot = snap
                onSnapshot?(snap)
                onUpdate(acc)
            }

            // 下一拍：按绝对时刻推进，命令往返的时间算在这 1 秒里。
            nextTick = nextTick.addingTimeInterval(1.0)
            if nextTick.timeIntervalSinceNow < 0 {
                // 这一轮严重超时（GET 吃满 2 秒 / 通道被别的命令占着），
                // 不补采追帧，直接对齐到当下，免得连发一串命令把设备挤爆。
                nextTick = Date()
            }
            // 分小段睡：点「停止」最多 0.1 秒就有响应
            var remaining = nextTick.timeIntervalSinceNow
            while remaining > 0 {
                if Task.isCancelled { break }
                try? await Task.sleep(nanoseconds: UInt64(min(0.1, remaining) * 1_000_000_000))
                remaining = nextTick.timeIntervalSinceNow
            }
        }

        if Task.isCancelled { reason = .stopped }
        captureTask = nil
        capturePort = nil
        let result = CaptureResult(portIndex: port,
                                   startedAt: started,
                                   endedAt: Date(),
                                   stats: acc,
                                   reason: reason,
                                   firmware: snapshot.firmware)
        log("[capture] 结束 \(PortData.portName(port))：\(acc.sampleCount) 次采样，"
            + String(format: "%.2fWh，平均 %.1fW，", acc.energyWh, acc.averagePower)
            + reason.shortText)
        onFinish(result)
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
