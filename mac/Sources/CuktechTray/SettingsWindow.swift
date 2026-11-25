#if os(macOS)
import AppKit
import Combine
import SwiftUI
import CuktechCore
import CuktechBle

/// 设置面板要发给蓝牙层的动作。界面只管「用户点了什么」，
/// 具体怎么读怎么写全部回到 DeviceSession（它握着命令通道）。
enum SettingsAction {
    case reload
    case screenTimeout(Int)
    case idleScreenOff(Bool)
    case screenLock(Bool)
    case usbALowCurrent(Bool)
    case language(Int)
    case protocolCap(port: Int, cap: ProtocolCap, on: Bool)
    case countdown(port: Int, minutes: Int)

    /// 日志里用的名字
    var name: String {
        switch self {
        case .reload: return "读取设置"
        case .screenTimeout: return "息屏时间"
        case .idleScreenOff: return "空闲息屏"
        case .screenLock: return "方向锁"
        case .usbALowCurrent: return "USB-A 小电流"
        case .language: return "语言"
        case .protocolCap(let p, let c, _): return "\(PortData.portName(p)) \(c.label)"
        case .countdown(let p, _): return "\(PortData.portName(p)) 定时断电"
        }
    }
}

final class SettingsModel: ObservableObject {
    @Published var settings = DeviceSettings()
    @Published var connected = false
    @Published var loading = false
    /// 正在写（蓝牙一次只能串一条命令，写的时候把整块控件置灰最省心）。
    @Published var busy = false
    @Published var error: String?

    var onAction: ((SettingsAction) -> Void)?

    func reload() {
        guard !loading else { return }
        loading = true
        error = nil
        onAction?(.reload)
    }

    // 下面这几个都是「先改本地、再写设备」：面板立刻响应，
    // 真写失败了由 controller 回读一遍把界面拉回真实状态。

    func setScreenTimeout(_ v: Int) {
        settings.screenTimeout = v
        onAction?(.screenTimeout(v))
    }

    func setIdleScreenOff(_ on: Bool) {
        settings.idleScreenOff = on
        onAction?(.idleScreenOff(on))
    }

    func setScreenLock(_ on: Bool) {
        settings.screenLock = on
        onAction?(.screenLock(on))
    }

    func setUSBLowCurrent(_ on: Bool) {
        settings.usbALowCurrent = on
        onAction?(.usbALowCurrent(on))
    }

    func setLanguage(_ v: Int) {
        settings.language = v
        onAction?(.language(v))
    }

    /// 协议是位掩码，本地也要按位改，不然界面会跳回旧值。
    func setProtocol(port: Int, cap: ProtocolCap, on: Bool) {
        if let flags = settings.protocolFlags, let bit = cap.bit(port: port) {
            let mask = UInt32(1) << bit
            settings.protocolFlags = on ? (flags | mask) : (flags & ~mask)
        }
        onAction?(.protocolCap(port: port, cap: cap, on: on))
    }

    func setCountdown(port: Int, minutes: Int) {
        if port >= 1 && port <= 4 { settings.countdowns[port - 1] = minutes }
        onAction?(.countdown(port: port, minutes: minutes))
    }
}

// MARK: - 界面

struct SettingsView: View {
    @ObservedObject var model: SettingsModel

    private var s: DeviceSettings { model.settings }
    private var enabled: Bool { model.connected && !model.busy && !model.loading }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().opacity(0.25).padding(.vertical, 10)

            VStack(alignment: .leading, spacing: 12) {
                screenSection
                powerSection
                timerSection
                miscSection
            }

            statusLine
                .padding(.top, 10)
        }
        .padding(16)
        .frame(width: 396)
        .background(Theme.background)
        .preferredColorScheme(.dark)
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 8) {
            VStack(alignment: .leading, spacing: 3) {
                Text("设备设置")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundColor(Theme.fg)
                Text("直接写进充电器，米家 App 那边也会看到")
                    .font(.system(size: 10.5))
                    .foregroundColor(Theme.dim)
            }
            Spacer(minLength: 0)
            HStack(spacing: 5) {
                Circle()
                    .fill(Theme.color(model.connected ? WattStyle.ok : WattStyle.bad))
                    .frame(width: 7, height: 7)
                Text(model.connected ? "已连接" : "未连接")
                    .font(.system(size: 10.5))
                    .foregroundColor(Theme.dim)
            }
            .padding(.top, 3)
            reloadButton
        }
    }

    private var reloadButton: some View {
        Button { model.reload() } label: {
            Image(systemName: "arrow.clockwise")
                .font(.system(size: 10, weight: .semibold))
                .foregroundColor(Theme.dim)
                .frame(width: 22, height: 20)
                .background(RoundedRectangle(cornerRadius: 6).fill(Theme.card))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!model.connected || model.loading || model.busy)
        .help("重新从设备读一遍设置")
        .padding(.top, 1)
    }

    // MARK: - 分块

    /// - Parameter subtitle: 放在标题下面的一句总说明，省得每一行都重复一遍。
    private func section<C: View>(_ title: String, subtitle: String? = nil,
                                  @ViewBuilder content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundColor(Theme.dim.opacity(0.85))
                if let subtitle {
                    Text(subtitle)
                        .font(.system(size: 9))
                        .foregroundColor(Theme.dim.opacity(0.6))
                }
            }
            VStack(alignment: .leading, spacing: 9) {
                content()
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 9)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 9).fill(Theme.card))
        }
    }

    private var screenSection: some View {
        section("屏幕") {
            // 五个选项横排 + 标题放同一行会超出卡片宽度（面板只有 396 宽），
            // 标题单独一行、选项独占一行才不会被右边框裁掉。
            VStack(alignment: .leading, spacing: 5) {
                Text("息屏时间")
                    .font(.system(size: 11))
                    .foregroundColor(s.screenTimeout == nil ? Theme.dim : Theme.fg)
                HStack(spacing: 5) {
                    ForEach(ScreenTimeout.allCases, id: \.rawValue) { item in
                        chip(item.label,
                             on: s.screenTimeout == item.rawValue,
                             enabled: enabled && s.screenTimeout != nil) {
                            model.setScreenTimeout(item.rawValue)
                        }
                    }
                    Spacer(minLength: 0)
                }
            }
            switchRow("空闲息屏", hint: "没插设备时自动灭屏",
                      value: s.idleScreenOff, enabled: enabled && s.idleScreenOff != nil) {
                model.setIdleScreenOff($0)
            }
            switchRow("屏幕方向锁", hint: "锁住当前朝向，转动机身不翻转",
                      value: s.screenLock, enabled: enabled && s.screenLock != nil) {
                model.setScreenLock($0)
            }
        }
    }

    private var powerSection: some View {
        section("供电") {
            switchRow("USB-A 小电流", hint: "给耳机、手表这类要小电流的设备供电",
                      value: s.usbALowCurrent, enabled: enabled && s.usbALowCurrent != nil) {
                model.setUSBLowCurrent($0)
            }
            Divider().opacity(0.12)
            // 每口支持哪些协议在协议开关里是固定的（C1/C2 有 PD/PPS，C3/A 只有 UFCS/SCP），
            // 所以这里按口画几颗芯片，不画「不支持」的空按钮。
            // 中间那列是「现在实际谈成的协议」，只读，用来解释右边这些开关到底在管什么。
            ForEach(1...4, id: \.self) { port in
                HStack(spacing: 5) {
                    Text(PortData.portName(port))
                        .font(.system(size: 11))
                        .foregroundColor(Theme.fg)
                    Text(s.negotiations[port - 1]?.text ?? "—")
                        .font(.system(size: 9.5, design: .monospaced))
                        .foregroundColor(Theme.dim.opacity(0.8))
                        .lineLimit(1)
                    Spacer(minLength: 4)
                    ForEach(ProtocolCap.available(onPort: port), id: \.rawValue) { cap in
                        let on = s.protocolOn(cap, port: port)
                        chip(cap.label, on: on == true,
                             enabled: enabled && on != nil) {
                            model.setProtocol(port: port, cap: cap, on: !(on ?? false))
                        }
                    }
                }
            }
        }
    }

    private var timerSection: some View {
        section("定时断电", subtitle: "到时由充电器自己断开这一口，退出本程序也算数") {
            ForEach(1...4, id: \.self) { port in
                HStack(spacing: 6) {
                    Text(PortData.portName(port))
                        .font(.system(size: 11))
                        .foregroundColor(Theme.fg)
                        .frame(width: 22, alignment: .leading)
                    Text(s.countdowns[port - 1].map { $0 > 0 ? "\($0) 分钟后断开" : "已关闭" } ?? "设备没答这一项")
                        .font(.system(size: 9.5))
                        .foregroundColor(Theme.dim.opacity(0.8))
                    Spacer(minLength: 4)
                    countdownButton(port: port, current: s.countdowns[port - 1])
                }
            }
        }
    }

    private var miscSection: some View {
        section("其他") {
            HStack(spacing: 6) {
                Text("屏幕语言")
                    .font(.system(size: 11))
                    .foregroundColor(Theme.fg)
                Spacer(minLength: 4)
                chip("中文", on: s.language == 1, enabled: enabled && s.language != nil) {
                    model.setLanguage(1)
                }
                chip("English", on: s.language == 0, enabled: enabled && s.language != nil) {
                    model.setLanguage(0)
                }
            }
        }
    }

    // MARK: - 控件

    private func switchRow(_ title: String, hint: String,
                           value: Bool?, enabled: Bool,
                           set: @escaping (Bool) -> Void) -> some View {
        HStack(spacing: 6) {
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.system(size: 11))
                    .foregroundColor(value == nil ? Theme.dim : Theme.fg)
                Text(value == nil ? "设备没答这一项" : hint)
                    .font(.system(size: 9))
                    .foregroundColor(Theme.dim.opacity(0.75))
            }
            Spacer(minLength: 4)
            Toggle("", isOn: Binding(get: { value ?? false }, set: { set($0) }))
                .labelsHidden()
                .toggleStyle(PanelSwitchStyle())
                .disabled(!enabled)
        }
    }

    private func chip(_ title: String, on: Bool, enabled: Bool,
                      action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 10, weight: on ? .semibold : .regular))
                .foregroundColor(on ? Theme.fg : Theme.dim)
                .lineLimit(1)
                .fixedSize(horizontal: true, vertical: false)
                .padding(.horizontal, 6)
                .padding(.vertical, 3)
                .background(
                    RoundedRectangle(cornerRadius: 6)
                        .fill(on ? Theme.cardOn : Color.white.opacity(0.03))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 6)
                        .stroke(on ? Theme.ok.opacity(0.55) : Color.clear, lineWidth: 1)
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled || on ? 1 : 0.4)
    }

    private func countdownButton(port: Int, current: Int?) -> some View {
        Button {
            popUpCountdown(port: port, current: current)
        } label: {
            HStack(spacing: 3) {
                Text(current.map(CountdownOption.label) ?? "—")
                    .font(.system(size: 10, weight: (current ?? 0) > 0 ? .semibold : .regular))
                Image(systemName: "chevron.down")
                    .font(.system(size: 7, weight: .semibold))
            }
            .foregroundColor((current ?? 0) > 0 ? Theme.fg : Theme.dim)
            .padding(.horizontal, 7)
            .padding(.vertical, 3)
            .background(
                RoundedRectangle(cornerRadius: 6)
                    .fill((current ?? 0) > 0 ? Theme.cardOn : Color.white.opacity(0.03))
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!enabled || current == nil)
        .opacity(current == nil ? 0.4 : 1)
        .help("设定后到点自动断开这一口（0 = 关闭）")
    }

    /// 自己弹 NSMenu：SwiftUI 的 `Menu` 在窗口还没成为 key window 时第一下点不开，
    /// 和主面板那个轮询间隔菜单是同一个坑。
    private func popUpCountdown(port: Int, current: Int?) {
        let menu = NSMenu()
        menu.autoenablesItems = false
        // 面板本身是暗色，系统菜单默认跟外观走会白得刺眼（App 没被激活时更明显）
        menu.appearance = NSAppearance(named: .darkAqua)
        let target = PickIntTarget { [model] v in model.setCountdown(port: port, minutes: v) }
        for v in CountdownOption.all {
            let mi = NSMenuItem(title: CountdownOption.label(v),
                                action: #selector(PickIntTarget.pick(_:)),
                                keyEquivalent: "")
            mi.target = target
            mi.representedObject = v
            mi.state = v == (current ?? 0) ? .on : .off
            menu.addItem(mi)
        }
        let p = NSEvent.mouseLocation
        menu.popUp(positioning: nil, at: NSPoint(x: p.x - 6, y: p.y - 6), in: nil)
    }

    @ViewBuilder
    private var statusLine: some View {
        if let err = model.error {
            Text(err)
                .font(.system(size: 10))
                .foregroundColor(Theme.bad)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
        } else if !model.connected {
            Text("连上充电器才能改设置")
                .font(.system(size: 10))
                .foregroundColor(Theme.dim)
        } else if model.loading {
            HStack(spacing: 5) {
                ProgressView().controlSize(.mini)
                Text("正在读设备…").font(.system(size: 10)).foregroundColor(Theme.dim)
            }
        } else if s.loadedCount == 0 {
            Text("这一版固件没有开放设置项")
                .font(.system(size: 10))
                .foregroundColor(Theme.dim)
        } else {
            Text("共读到 \(s.loadedCount) 项 · 息屏与定时由充电器自己执行")
                .font(.system(size: 10))
                .foregroundColor(Theme.dim)
        }
    }
}

/// NSMenuItem 的 target 必须是 Objective-C 对象，闭包里带回选中的整数。
final class PickIntTarget: NSObject {
    private let handler: (Int) -> Void
    init(_ handler: @escaping (Int) -> Void) { self.handler = handler }
    @objc func pick(_ sender: NSMenuItem) {
        if let v = sender.representedObject as? Int { handler(v) }
    }
}

// MARK: - 窗口

final class SettingsController: NSObject, NSWindowDelegate {
    static let shared = SettingsController()

    let model = SettingsModel()
    /// 会话会随「重新配对」重建，所以不是 let。
    private weak var session: DeviceSession?
    private var window: NSWindow?
    private var hosting: NSHostingView<SettingsView>?
    var onLog: ((String) -> Void)?

    override init() {
        super.init()
        model.onAction = { [weak self] action in self?.perform(action) }
    }

    func attach(session: DeviceSession?) {
        self.session = session
    }

    func setConnected(_ connected: Bool) {
        if model.connected != connected { model.connected = connected }
    }

    func show() {
        let firstTime = window == nil
        let win = window ?? makeWindow()
        window = win
        if firstTime { win.center() }
        win.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        fitWindow()
        onLog?("[settings] 窗口 \(Int(win.frame.width))x\(Int(win.frame.height)) "
               + "内容 \(Int(hosting?.fittingSize.width ?? 0))x\(Int(hosting?.fittingSize.height ?? 0))")
        model.reload()
    }

    private func makeWindow() -> NSWindow {
        let host = FirstMouseHostingView(rootView: SettingsView(model: model))
        hosting = host
        host.layoutSubtreeIfNeeded()
        let fit = host.fittingSize
        let size = NSSize(width: max(fit.width, 396), height: max(fit.height, 1))

        let win = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                           styleMask: [.titled, .closable, .fullSizeContentView],
                           backing: .buffered,
                           defer: false)
        win.titleVisibility = .hidden
        win.titlebarAppearsTransparent = true
        win.isReleasedWhenClosed = false
        win.isMovableByWindowBackground = true
        win.level = .floating
        win.collectionBehavior = [.moveToActiveSpace]
        win.delegate = self
        win.contentView = host
        return win
    }

    /// 顶边不动地贴合内容高度（和关于窗口同一套算法，避免展开时往下坠）。
    private func fitWindow() {
        guard let win = window, let host = hosting else { return }
        host.layoutSubtreeIfNeeded()
        let needed = host.fittingSize.height
        guard needed > 1, abs(needed - win.frame.height) > 0.5 else { return }
        let anchor = NSPoint(x: win.frame.minX, y: win.frame.maxY)
        win.setFrame(NSRect(x: anchor.x, y: anchor.y - needed, width: 396, height: needed),
                     display: true, animate: false)
    }

    // MARK: - 动作

    private func perform(_ action: SettingsAction) {
        guard let session else {
            model.loading = false
            model.busy = false
            model.error = "还没连上充电器"
            return
        }
        if case .reload = action {
            Task { @MainActor in
                let s = await session.reloadSettings()
                self.model.settings = s
                self.model.loading = false
                self.fitWindow()
            }
            return
        }

        model.busy = true
        model.error = nil
        Task { @MainActor in
            do {
                switch action {
                case .reload:
                    break
                case .screenTimeout(let v):
                    try await session.setScreenTimeout(v)
                case .idleScreenOff(let on):
                    try await session.setIdleScreenOff(on)
                case .screenLock(let on):
                    try await session.setScreenLock(on)
                case .usbALowCurrent(let on):
                    try await session.setUSBLowCurrent(on)
                case .language(let v):
                    try await session.setLanguage(v)
                case .protocolCap(let port, let cap, let on):
                    try await session.setProtocol(cap, port: port, on: on)
                case .countdown(let port, let minutes):
                    try await session.setCountdown(port: port, minutes: minutes)
                }
                self.onLog?("[settings] \(action.name) 写入成功")
            } catch {
                self.onLog?("[settings] \(action.name) 失败：\(error)")
                self.model.error = "\(action.name)：\(error)"
            }
            // 无论成功失败都回读一次：失败时别把界面留在「假成功」上，
            // 成功时也能看到固件顺手改掉的别的位（倒计时会被重置）。
            self.model.settings = await session.reloadSettings()
            self.model.busy = false
            self.fitWindow()
        }
    }
}
#endif
