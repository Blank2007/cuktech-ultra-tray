#if os(macOS)
import AppKit
import CuktechCore
import CuktechBle

/// 菜单栏那一格 + 右键菜单 + 面板的接线员。
/// 与 M0 的 AppDelegate 同一风格：不标 @MainActor，靠所有入口都在主线程
/// （AppKit 回调、菜单动作、MainActor 上的 ticker）。
final class TrayController: NSObject {
    private let settings: AppSettings
    private let log: LogFile
    private let statusItem: NSStatusItem
    let panel = PanelController()

    private var session: DeviceSession?
    private var snapshot = DeviceSnapshot()
    private var ticker: Task<Void, Never>?
    private var statusLineItem: NSMenuItem!
    private var wattsItem: NSMenuItem!

    init(settings: AppSettings) {
        self.settings = settings
        self.log = LogFile()
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        buildMenu()
        wirePanel()
        updateTitle()
    }

    // MARK: - 生命周期

    func start() {
        log.write("[tray] 启动 version=\(BuildInfo.version) config=\(AppSettings.fileURL.path)")
        bootSession()

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
            statusLineItem.title = "还没配对，先用 --pair 弄一份配置"
            return
        }
        let s = DeviceSession(mac: mac, token: token)
        session = s
        let poll = settings.pollInterval
        let logger = log
        Task {
            await s.setPollInterval(poll)
            await s.setLogHandler { line in logger.write(line) }
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
    }

    // MARK: - 菜单栏标题

    private func updateTitle() {
        guard let button = statusItem.button else { return }
        let text: String
        var color = NSColor.secondaryLabelColor

        if !settings.showWattsInMenuBar {
            text = "瓦"
        } else if snapshot.connected {
            text = snapshot.totalText
            let c = snapshot.totalColor
            color = NSColor(srgbRed: c.r, green: c.g, blue: c.b, alpha: 1)
        } else if session != nil {
            text = "—"
        } else {
            text = "未配对"
        }

        button.attributedTitle = NSAttributedString(
            string: text,
            attributes: [
                .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .semibold),
                .foregroundColor: color,
            ])
        button.toolTip = snapshot.connected ? snapshot.headline : "酷态科 10 号 Ultra · \(snapshot.status)"
    }

    private func refreshMenuStatus() {
        statusLineItem.title = snapshot.connected
            ? snapshot.headline
            : (snapshot.error.map { "没连上：" + $0 } ?? snapshot.status)
    }

    // MARK: - 菜单

    private func buildMenu() {
        let menu = NSMenu()
        menu.autoenablesItems = false

        statusLineItem = NSMenuItem(title: "正在启动…", action: nil, keyEquivalent: "")
        statusLineItem.isEnabled = false
        menu.addItem(statusLineItem)
        menu.addItem(.separator())

        menu.addItem(item("显示功率面板", #selector(showPanel)))
        menu.addItem(item("重新连接", #selector(reconnect)))
        menu.addItem(.separator())

        wattsItem = item("菜单栏显示功率数字", #selector(toggleWatts))
        wattsItem.state = settings.showWattsInMenuBar ? .on : .off
        menu.addItem(wattsItem)

        menu.addItem(.separator())
        menu.addItem(item("打开日志", #selector(openLog)))
        menu.addItem(item("显示配置文件", #selector(showConfig)))
        menu.addItem(.separator())
        menu.addItem(item("退出", #selector(quit)))

        statusItem.menu = menu
    }

    private func item(_ title: String, _ action: Selector) -> NSMenuItem {
        let mi = NSMenuItem(title: title, action: action, keyEquivalent: "")
        mi.target = self
        return mi
    }

    @objc private func showPanel() {
        panel.toggle(relativeTo: statusItem.button)
    }

    @objc private func reconnect() {
        guard let s = session else { return }
        Task { await s.reconnectNow() }
    }

    @objc private func toggleWatts() {
        settings.showWattsInMenuBar.toggle()
        wattsItem.state = settings.showWattsInMenuBar ? .on : .off
        try? settings.save()
        updateTitle()
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

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    // MARK: - 面板动作

    private func wirePanel() {
        panel.model.onReconnect = { [weak self] in self?.reconnect() }
        panel.model.onQuit = { NSApp.terminate(nil) }
        panel.model.onSetPort = { [weak self] index, on in self?.setPort(index, on) }
        panel.model.onSetScene = { [weak self] mode in self?.setScene(mode) }
    }

    private func setPort(_ index: Int, _ on: Bool) {
        guard let s = session else { return }
        Task { @MainActor in
            do {
                try await s.setPortEnabled(index, on)
            } catch {
                self.log.write("[panel] 设置端口 \(index) 失败：\(error)")
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
