#if os(macOS)
import AppKit
import CuktechCore
import CuktechBle

/// 菜单栏那一格 + 右键菜单 + 面板的接线员。
/// 与 M0 的 AppDelegate 同一风格：不标 @MainActor，靠所有入口都在主线程
/// （AppKit 回调、菜单动作、MainActor 上的 ticker）。
final class TrayController: NSObject {
    private var settings: AppSettings   // 菜单里的「菜单栏显示功率数字」会就地改它
    private let log: LogFile
    private let statusItem: NSStatusItem
    let panel = PanelController()

    private var session: DeviceSession?
    private var snapshot = DeviceSnapshot()
    private var ticker: Task<Void, Never>?
    private var statusLineItem: NSMenuItem!
    private var wattsItem: NSMenuItem!
    /// 右键菜单里的「面板始终在最前」勾选项。
    private var alwaysOnTopItem: NSMenuItem!
    /// 右键菜单里的「菜单栏固定宽度」与「面板玻璃质感」勾选项。
    private var fixedWidthItem: NSMenuItem!
    private var glassItem: NSMenuItem!
    /// 上次真正画到菜单栏上的内容指纹。读数每 400ms 刷一次，没变就不重画 ——
    /// 每次改 attributedTitle 都会让菜单栏重排，观感上就是那一格在抖。
    private var lastRenderedTitleKey: String?
    /// 系统唤醒重连的限速时间戳：屏幕反复亮灭时会连着发好几条 didWake。
    private var lastWakeReconnect: Date?
    /// 右键菜单。左键不弹它——左键直接开面板（README 一直是这么写的，
    /// 之前把 menu 挂死在 statusItem 上，结果左键点出来的是菜单，面板反而要再点一次）。
    private let statusMenu = NSMenu()

    init(settings: AppSettings) {
        self.settings = settings
        self.log = LogFile()
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        buildMenu()
        wireStatusItemButton()
        wirePanel()
        // 按配置恢复「始终在最前」。此时面板还没弹出来过（window 是 nil），
        // PanelController 会记住这个值，等 show 的时候补上。
        panel.setAlwaysOnTop(settings.alwaysOnTop)
        // 玻璃质感同理：面板还没造出来时只记住开关，等 makePopover 时按当前值装配。
        panel.setGlassEnabled(settings.glassPanel)
        SettingsController.shared.onLog = { [weak self] line in self?.log.write(line) }
        applyStatusItemLength()
        updateTitle(force: true)
    }

    // MARK: - 生命周期

    func start() {
        log.write("[tray] 启动 version=\(BuildInfo.version) config=\(AppSettings.fileURL.path)")
        bootSession()

        // 合盖/睡眠回来后，BLE 连接基本已经废了（GATT 句柄失效、peripheral 还在
        // "connected" 状态但读写全超时），macOS 自己不会帮我们重连。
        // 这里主动重连一次，比让用户盯着不动的读数去手点强。
        NSWorkspace.shared.notificationCenter.addObserver(
            self,
            selector: #selector(didWake),
            name: NSWorkspace.didWakeNotification,
            object: nil)

        ticker = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                if let s = self.session {
                    let snap = await s.snapshot
                    self.apply(snap)
                }
                try? await Task.sleep(nanoseconds: 400_000_000)
            }
        }
    }

    func shutdown() {
        NSWorkspace.shared.notificationCenter.removeObserver(
            self, name: NSWorkspace.didWakeNotification, object: nil)
        ticker?.cancel()
        ticker = nil
        if let s = session {
            Task { await s.stop() }
        }
        log.write("[tray] 退出")
    }

    // MARK: - 会话

    private func bootSession() {
        guard let mac = settings.macBytes, let token = settings.tokenBytes else {
            log.write("[tray] 还没配对：\(AppSettings.fileURL.path) 里缺 mac 或 token")
            statusLineItem.title = "还没配对，右键「重新配对…」走一遍向导"
            SettingsController.shared.attach(session: nil)
            return
        }
        // 上次认出来的设备 identifier：带上它，重连时可跳过扫描直接接管
        // 系统里已连着的那条连接（设备被旧连接占着时根本不广播）。
        let known = settings.peripheralId.flatMap { UUID(uuidString: $0) }
        let s = DeviceSession(mac: mac, token: token, knownPeripheral: known)
        session = s
        SettingsController.shared.attach(session: s)
        let poll = settings.pollInterval
        let logger = log
        Task {
            await s.setPollInterval(poll)
            await s.setLogHandler { line in logger.write(line) }
            // 认出设备真身后记进配置，下次唤醒/重连直接用。
            await s.setPeripheralHandler { [weak self] id in
                DispatchQueue.main.async {
                    guard let self else { return }
                    guard self.settings.peripheralId != id.uuidString else { return }
                    self.settings.peripheralId = id.uuidString
                    do {
                        try self.settings.save()
                    } catch {
                        self.log.write("[tray] 保存设备 identifier 失败：\(error)")
                    }
                }
            }
            await s.start()
        }
    }

    private func apply(_ snap: DeviceSnapshot) {
        let sig = "\(snap.connected)|\(snap.totalText)|\(snap.headline)|\(snap.sceneMode)"
        let old = "\(snapshot.connected)|\(snapshot.totalText)|\(snapshot.headline)|\(snapshot.sceneMode)"
        snapshot = snap
        if sig != old {
            updateTitle()
            refreshMenuStatus()
        }
        panel.model.snapshot = snap
        SettingsController.shared.setConnected(snap.connected)
    }

    // MARK: - 菜单栏标题

    /// 关掉「菜单栏显示功率数字」时显示的那个图标。
    /// 以前这里塞的是「瓦」这个字，挤在菜单栏里像坏掉的标签；换成闪电模板图，
    /// 跟 App 图标上的闪电对应，深浅色菜单栏都会自动反色（isTemplate）。
    private static let boltIcon: NSImage? = {
        let cfg = NSImage.SymbolConfiguration(pointSize: 13, weight: .semibold)
        guard let img = NSImage(systemSymbolName: "bolt.fill", accessibilityDescription: "酷态科功率表")?
            .withSymbolConfiguration(cfg) else { return nil }
        img.isTemplate = true
        return img
    }()

    /// 菜单栏固定在一点宽度时占多少点。读数最长就是 "100.0W" 这一档（见 WattStyle.format），
    /// 12pt 等宽数字大约 45pt，58pt 留了点余量给「未配对」和色块边缘。
    private static let fixedMenuBarLength: CGFloat = 58

    /// 按设置决定菜单栏宽度。
    /// 系统默认的 .variableLength 会跟着文字长度伸缩，读数在 9.9W ↔ 10.0W 之间跳时整格宽度就变，
    /// 右边所有菜单栏图标跟着左右挪 —— 这就是「菜单栏在抖」。固定宽度后格子不动，只有数字变。
    /// 图标模式没有文字，照旧交给系统伸缩。
    private func applyStatusItemLength() {
        let fixed = settings.fixedMenuBarWidth && settings.showWattsInMenuBar
        statusItem.length = fixed ? Self.fixedMenuBarLength : NSStatusItem.variableLength
    }

    /// 把当前 snapshot 画到菜单栏。
    /// force 为真时无条件重画（启动、切菜单项、改宽度之后）；否则先比内容指纹，
    /// 一样就直接返回 —— 读数每 400ms 刷一次，绝大部分时候什么都没变。
    private func updateTitle(force: Bool = false) {
        guard let button = statusItem.button else { return }
        let tip = snapshot.connected ? snapshot.headline : "酷态科 10 号 Ultra · \(snapshot.status)"

        if !settings.showWattsInMenuBar {
            let key = "icon|\(tip)"
            guard force || key != lastRenderedTitleKey else { return }
            lastRenderedTitleKey = key
            button.image = Self.boltIcon
            button.imagePosition = .imageOnly
            button.imageScaling = .scaleProportionallyDown
            button.attributedTitle = NSAttributedString(string: "")
            button.toolTip = tip
            return
        }

        let text: String
        var color: NSColor?

        if snapshot.connected {
            text = snapshot.totalText
            if snapshot.totalWatts > 0.05 {
                let c = snapshot.totalColor
                color = NSColor(srgbRed: c.r, green: c.g, blue: c.b, alpha: 1)
            }
        } else if session != nil {
            text = "—"
        } else {
            text = "未配对"
        }

        // 颜色也要进指纹：功率跨色档时得跟着换色。
        let colorKey = color.map {
            "\($0.redComponent),\($0.greenComponent),\($0.blueComponent)"
        } ?? "auto"
        let key = "\(text)|\(colorKey)|\(statusItem.length)|\(tip)"
        guard force || key != lastRenderedTitleKey else { return }
        lastRenderedTitleKey = key

        button.image = nil
        button.imagePosition = .noImage

        var attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .semibold),
        ]
        // 中性状态不硬编码颜色，由系统菜单栏按壁纸明暗自动适配反白/反黑（避免浅色模式+深色壁纸下隐身）
        if let color {
            attrs[.foregroundColor] = color
        }
        button.attributedTitle = NSAttributedString(string: text, attributes: attrs)
        button.toolTip = tip
    }

    private func refreshMenuStatus() {
        statusLineItem.title = snapshot.connected
            ? snapshot.headline
            : (snapshot.error.map { "没连上：" + $0 } ?? snapshot.status)
    }

    // MARK: - 菜单

    private func buildMenu() {
        let menu = statusMenu
        menu.autoenablesItems = false

        statusLineItem = NSMenuItem(title: "正在启动…", action: nil, keyEquivalent: "")
        statusLineItem.isEnabled = false
        menu.addItem(statusLineItem)
        menu.addItem(.separator())

        menu.addItem(item("显示功率面板", #selector(showPanel)))
        menu.addItem(item("设备设置…", #selector(showSettings)))
        menu.addItem(item("重新连接", #selector(reconnect)))
        menu.addItem(item("重新配对…", #selector(runWizard)))
        menu.addItem(.separator())

        wattsItem = item("菜单栏显示功率数字", #selector(toggleWatts))
        wattsItem.state = settings.showWattsInMenuBar ? .on : .off
        menu.addItem(wattsItem)

        alwaysOnTopItem = item("面板始终在最前", #selector(toggleAlwaysOnTop))
        alwaysOnTopItem.state = settings.alwaysOnTop ? .on : .off
        menu.addItem(alwaysOnTopItem)

        fixedWidthItem = item("菜单栏固定宽度", #selector(toggleFixedMenuBarWidth))
        fixedWidthItem.state = settings.fixedMenuBarWidth ? .on : .off
        menu.addItem(fixedWidthItem)

        glassItem = item("面板玻璃质感", #selector(toggleGlassPanel))
        glassItem.state = settings.glassPanel ? .on : .off
        menu.addItem(glassItem)

        menu.addItem(.separator())
        menu.addItem(item("打开日志", #selector(openLog)))
        menu.addItem(item("显示配置文件", #selector(showConfig)))
        menu.addItem(.separator())
        menu.addItem(item("关于", #selector(showAbout)))
        menu.addItem(.separator())
        menu.addItem(item("退出", #selector(quit)))

    }

    /// 左键开面板、右键弹菜单。不设 `statusItem.menu`，否则左键永远只有菜单。
    private func wireStatusItemButton() {
        guard let button = statusItem.button else { return }
        button.target = self
        button.action = #selector(statusItemClicked)
        button.sendAction(on: [.leftMouseUp, .rightMouseUp])
    }

    @objc private func statusItemClicked() {
        let event = NSApp.currentEvent
        let isSecondary = event?.type == .rightMouseUp
            || (event?.modifierFlags.contains(.control) ?? false)
        if isSecondary {
            // 右键是「要菜单」，面板留着会被菜单压住还挡视线，先收起来
            panel.close()
            // 临时挂上菜单再点一次，让 AppKit 把它弹在正确位置；弹完立刻摘掉
            statusItem.menu = statusMenu
            statusItem.button?.performClick(nil)
            statusItem.menu = nil
        } else {
            panel.toggle(relativeTo: statusItem.button)
        }
    }

    private func item(_ title: String, _ action: Selector) -> NSMenuItem {
        let mi = NSMenuItem(title: title, action: action, keyEquivalent: "")
        mi.target = self
        return mi
    }

    @objc private func showPanel() {
        panel.toggle(relativeTo: statusItem.button)
    }

    /// 菜单栏右键进来的设置面板。先把功率面板收起来，免得两个浮窗叠在一起。
    @objc private func showSettings() {
        panel.close()
        SettingsController.shared.show()
    }

    @objc private func reconnect() {
        guard let s = session else { return }
        Task { await s.reconnectNow() }
    }

    @objc private func toggleWatts() {
        settings.showWattsInMenuBar.toggle()
        wattsItem.state = settings.showWattsInMenuBar ? .on : .off
        try? settings.save()
        // 图标模式和固定宽度是互斥的：切过去要重新按设置定宽度，再无条件重画一次。
        applyStatusItemLength()
        updateTitle(force: true)
    }

    /// 「菜单栏固定宽度」：勾上给菜单栏那一格钉死 58pt，读数变化时整格不再伸缩。
    @objc private func toggleFixedMenuBarWidth() {
        settings.fixedMenuBarWidth.toggle()
        fixedWidthItem.state = settings.fixedMenuBarWidth ? .on : .off
        try? settings.save()
        applyStatusItemLength()
        updateTitle(force: true)
        log.write("[tray] 菜单栏固定宽度 -> \(settings.fixedMenuBarWidth)")
    }

    /// 「面板玻璃质感」：勾上给面板背景垫玻璃（26+ Liquid Glass，13~15 毛玻璃）。
    @objc private func toggleGlassPanel() {
        settings.glassPanel.toggle()
        glassItem.state = settings.glassPanel ? .on : .off
        try? settings.save()
        panel.setGlassEnabled(settings.glassPanel)
        log.write("[panel] 玻璃质感 -> \(settings.glassPanel)")
    }

    /// 「面板始终在最前」：勾上把面板窗口层级提到 .floating，关掉回 .normal。
    /// 跟面板右上角那个图钉不是一回事 —— 图钉只管「点外面不收起」，这个是压别的窗口。
    @objc private func toggleAlwaysOnTop() {
        settings.alwaysOnTop.toggle()
        alwaysOnTopItem.state = settings.alwaysOnTop ? .on : .off
        try? settings.save()
        panel.setAlwaysOnTop(settings.alwaysOnTop)
        log.write("[panel] 始终在最前 -> \(settings.alwaysOnTop)")
    }

    /// 系统唤醒（合盖、睡眠、显示器断开）后重连一次。
    /// 60 秒内只做一次：屏幕反复亮灭会连着来好几条通知，不然就成重连风暴了。
    @objc private func didWake() {
        if let last = lastWakeReconnect, Date().timeIntervalSince(last) < 60 { return }
        lastWakeReconnect = Date()
        guard let s = session else { return }
        log.write("[tray] 系统唤醒，重新建立连接")
        Task { await s.reconnectNow() }
    }

    @objc private func runWizard() {
        showWizard()
    }

    /// 首次运行（配置不全）时由 AppDelegate 调；菜单里点「重新配对…」也走这里。
    func showWizardIfNeeded() {
        guard !settings.isComplete else { return }
        log.write("[tray] 配置不全，先弹向导：\(AppSettings.fileURL.path)")
        showWizard()
    }

    private func showWizard() {
        WizardController.show(settings: settings) { [weak self] newSettings in
            guard let self else { return }
            self.log.write("[tray] 向导写完配置，重建会话")
            if let old = self.session {
                Task { await old.stop() }
            }
            self.session = nil
            self.settings = newSettings
            self.bootSession()
            self.updateTitle()
        }
    }

    @objc private func openLog() {
        let url = AppSettings.logURL
        if !FileManager.default.fileExists(atPath: url.path) {
            FileManager.default.createFile(atPath: url.path, contents: Data())
        }
        NSWorkspace.shared.open(url)
    }

    @objc private func showConfig() {
        NSWorkspace.shared.activateFileViewerSelecting([AppSettings.fileURL])
    }

    @objc private func showAbout() {
        AboutController.shared.show()
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    // MARK: - 面板动作

    private func wirePanel() {
        panel.model.onReconnect = { [weak self] in self?.reconnect() }
        panel.model.onQuit = { NSApp.terminate(nil) }
        panel.model.onSetPort = { [weak self] index, on in self?.setPort(index, on) }
        panel.model.onSetScene = { [weak self] mode in self?.setScene(mode) }
        panel.model.pollInterval = settings.pollInterval

        // 面板底部「关于」：先把面板收起来，否则 transient popover 会被新窗口顶掉，
        // 看起来像闪了一下又没了。
        panel.model.onShowAbout = { [weak self] in
            self?.panel.close()
            AboutController.shared.show()
        }

        panel.model.onShowSettings = { [weak self] in
            self?.panel.close()
            SettingsController.shared.show()
        }

        panel.model.onPollIntervalChanged = { [weak self] v in
            guard let self else { return }
            self.settings.pollInterval = v
            do {
                try self.settings.save()
            } catch {
                self.log.write("[panel] 保存轮询间隔失败：\(error)")
            }
            self.log.write("[panel] 轮询间隔 -> \(v)s")
            if let s = self.session {
                Task { await s.setPollInterval(v) }
            }
        }

        panel.model.onStartCapture = { [weak self] port in self?.startCapture(port) }
        panel.model.onStopCapture = { [weak self] in
            guard let s = self?.session else { return }
            Task { await s.stopCapture() }
        }
    }

    // MARK: - 单口采集

    /// 采集跑在 DeviceSession 里（它握着命令通道和连接状态）。
    /// 这里只负责把每秒的累计值推到面板、结束时弹结果窗。
    private func startCapture(_ port: Int) {
        guard let s = session, panel.model.capturePort == nil else { return }
        guard panel.model.snapshot.connected else {
            panel.model.captureStats = nil
            panel.model.captureStartedAt = nil
            return
        }
        panel.model.capturePort = port
        panel.model.captureStats = CaptureAccumulator()
        panel.model.captureStartedAt = Date()
        log.write("[capture] 开始采集 \(PortData.portName(port))")

        Task { @MainActor in
            await s.startCapture(
                port: port,
                onUpdate: { acc in
                    Task { @MainActor in
                        guard self.panel.model.capturePort == port else { return }
                        self.panel.model.captureStats = acc
                    }
                },
                onFinish: { result in
                    Task { @MainActor in self.finishCapture(result) }
                })
        }
    }

    private func finishCapture(_ result: CaptureResult) {
        panel.model.capturePort = nil
        panel.model.captureStats = nil
        panel.model.captureStartedAt = nil
        panel.close()
        log.write("[capture] 结果 \(result.energyText)Wh / 平均 \(result.averageText)W，"
                  + "有效 \(result.activeDurationText)，\(result.reason.shortText)")
        CaptureResultController.shared.show(result)
    }

    /// - Parameter index: 开关这条路上一律用 0-based（面板传 `PortSlot.zeroBasedIndex`，
    ///   会话的 `setPortEnabled` 也是 0..3）。PR #4 原本在这里又减了一次 1，而 0.1.2
    ///   （PR #3）已经把面板改成传 0-based，两边一叠加就会「点 C1 关 C2、点 A 报序号错」，
    ///   所以这里统一为：面板给 0..3，直接透传，不再换算。
    ///   采集那条路仍是 1..4（`PortSlot.index`），因为 `DeviceSession.startCapture` 按 1..4 干活。
    private func setPort(_ index: Int, _ on: Bool) {
        guard let s = session, (0..<4).contains(index) else {
            panel.model.busy = false
            return
        }
        Task { @MainActor in
            do {
                try await s.setPortEnabled(index, on)
            } catch {
                self.log.write("[panel] 设置端口 \(index + 1) 失败：\(error)")
            }
            self.panel.model.busy = false
        }
    }

    private func setScene(_ mode: Int) {
        guard let s = session else { return }
        Task { @MainActor in
            do {
                try await s.setSceneMode(mode)
            } catch {
                self.log.write("[panel] 切换模式 \(mode) 失败：\(error)")
            }
            self.panel.model.busy = false
        }
    }
}
#endif