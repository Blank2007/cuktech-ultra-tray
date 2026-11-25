#if os(macOS)
import AppKit
import SwiftUI
import CuktechCore

/// 面板的数据源。所有 UI 状态都在主线程上改，蓝牙动作丢回 DeviceSession。
/// 这里不标 @MainActor：main.swift 的顶层代码直接 new，Swift 5 语言模式下标了反而绕。
/// 真正保证主线程的是调用方 —— ticker 跑在 MainActor 上，按钮回调也都在主线程。
final class PanelModel: ObservableObject {
    @Published var snapshot = DeviceSnapshot()
    @Published var busy = false

    var onReconnect: (() -> Void)?
    var onSetPort: ((Int, Bool) -> Void)?
    var onSetScene: ((Int) -> Void)?
    var onQuit: (() -> Void)?

    func reconnect() { onReconnect?() }

    func setPort(_ index: Int, _ enabled: Bool) {
        busy = true
        onSetPort?(index, enabled)
    }

    func setScene(_ mode: Int) {
        busy = true
        onSetScene?(mode)
    }
}

struct PanelView: View {
    @ObservedObject var model: PanelModel

    private var snap: DeviceSnapshot { model.snapshot }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().opacity(0.25).padding(.vertical, 10)
            ports
            Divider().opacity(0.25).padding(.vertical, 10)
            scenes
            Spacer(minLength: 8)
            footer
        }
        .padding(.horizontal, 16)
        .padding(.top, 14)
        .padding(.bottom, 12)
        .frame(width: 340, height: 486)
        .background(Theme.background)
        .preferredColorScheme(.dark)
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 8) {
            VStack(alignment: .leading, spacing: 3) {
                Text("酷态科 10 号 Ultra")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundColor(Theme.fg)
                Text(snap.headline)
                    .font(.system(size: 11))
                    .foregroundColor(Theme.dim)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            HStack(spacing: 5) {
                Circle()
                    .fill(Theme.color(snap.connected ? WattStyle.ok : WattStyle.bad))
                    .frame(width: 7, height: 7)
                Text(snap.connected ? "已连接" : "未连接")
                    .font(.system(size: 10.5))
                    .foregroundColor(Theme.dim)
            }
            .padding(.top, 2)
        }
    }

    private var ports: some View {
        VStack(spacing: 6) {
            ForEach(0..<4, id: \.self) { i in
                portRow(slot: snap.ports[i])
            }
        }
    }

    private func portRow(slot: PortSlot) -> some View {
        // C3 与 A 共用一个功率计时，搭车那一路没有自己的数字：名字不换、读数写暗色「并入 C3+A」。
        let passenger = snap.isMergedPassenger(slot)
        let bar = passenger
            ? WattStyle.dim
            : (slot.enabled ? (slot.watts > 0.05 ? WattStyle.port(slot.watts) : WattStyle.ok) : WattStyle.dim)
        return HStack(spacing: 10) {
            RoundedRectangle(cornerRadius: 2)
                .fill(Theme.color(bar))
                .frame(width: 3, height: 34)

            VStack(alignment: .leading, spacing: 2) {
                Text(snap.rowName(slot))
                    .font(.system(size: 11))
                    .foregroundColor(Theme.dim)
                Text(snap.rowDetail(slot))
                    .font(.system(size: 10.5, design: .monospaced))
                    .foregroundColor(passenger ? Theme.dim : (slot.enabled ? Theme.fg : Theme.dim))
            }

            Spacer(minLength: 0)

            Text(snap.rowWattsText(slot))
                .font(.system(size: passenger ? 11 : 15,
                              weight: passenger ? .regular : .semibold,
                              design: .monospaced))
                .foregroundColor(Theme.color(passenger
                                             ? WattStyle.dim
                                             : (slot.enabled ? WattStyle.port(slot.watts) : WattStyle.dim)))

            Toggle("", isOn: Binding(
                get: { slot.enabled },
                set: { model.setPort(slot.zeroBasedIndex, $0) }
            ))
            .labelsHidden()
            .toggleStyle(.switch)
            .controlSize(.mini)
            .disabled(!snap.connected || model.busy)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.card))
    }

    private var scenes: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("充电模式")
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
            LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 6) {
                ForEach(0..<SceneMode.all.count, id: \.self) { i in
                    let item = SceneMode.all[i]
                    Button {
                        model.setScene(item.value)
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.name)
                                .font(.system(size: 11.5, weight: .medium))
                                .foregroundColor(snap.sceneMode == item.value ? Theme.fg : Theme.dim)
                            Text(item.hint)
                                .font(.system(size: 9.5))
                                .foregroundColor(Theme.dim.opacity(0.8))
                                .lineLimit(1)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 6)
                        .background(
                            RoundedRectangle(cornerRadius: 8)
                                .fill(snap.sceneMode == item.value ? Theme.cardOn : Theme.card)
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 8)
                                .stroke(snap.sceneMode == item.value ? Theme.ok.opacity(0.7) : Color.clear, lineWidth: 1)
                        )
                    }
                    .buttonStyle(.plain)
                    .disabled(!snap.connected || model.busy)
                }
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 12) {
            Button("重新连接") { model.reconnect() }
                .buttonStyle(.plain)
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
            Spacer()
            Button("退出") { model.onQuit?() }
                .buttonStyle(.plain)
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
        }
    }
}

enum Theme {
    static let background = Color(red: 0.106, green: 0.118, blue: 0.137)
    static let fg = Color(red: 0.91, green: 0.918, blue: 0.929)
    static let dim = Color(red: 0.463, green: 0.49, blue: 0.529)
    static let card = Color.white.opacity(0.045)
    static let cardOn = Color.white.opacity(0.10)
    static let ok = Color(red: 52 / 255, green: 211 / 255, blue: 153 / 255)
    static let bad = Color(red: 248 / 255, green: 113 / 255, blue: 113 / 255)

    static func color(_ c: RGB) -> Color {
        Color(red: Double(c.r), green: Double(c.g), blue: Double(c.b))
    }
}

/// 面板窗口：无标题栏的浮动面板，点菜单里那一项才出现，失焦不消失。
final class PanelController {
    let model = PanelModel()
    private var panel: NSPanel?

    func toggle(relativeTo button: NSStatusBarButton?) {
        if let p = panel, p.isVisible {
            p.orderOut(nil)
            return
        }
        show(relativeTo: button)
    }

    func show(relativeTo button: NSStatusBarButton?) {
        let win = panel ?? makePanel()
        panel = win
        if let b = button, let win2 = b.window {
            let frame = win2.convertToScreen(b.convert(b.bounds, to: nil))
            var origin = NSPoint(x: frame.midX - win.frame.width / 2, y: frame.minY - win.frame.height - 6)
            let screen = win2.screen?.visibleFrame ?? NSScreen.main?.visibleFrame ?? .zero
            origin.x = min(max(screen.minX + 8, origin.x), screen.maxX - win.frame.width - 8)
            win.setFrameOrigin(origin)
        }
        win.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func close() { panel?.orderOut(nil) }

    private func makePanel() -> NSPanel {
        let win = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 340, height: 486),
                          styleMask: [.titled, .fullSizeContentView, .nonactivatingPanel],
                          backing: .buffered,
                          defer: false)
        win.titleVisibility = .hidden
        win.titlebarAppearsTransparent = true
        win.isMovableByWindowBackground = true
        win.isFloatingPanel = true
        win.level = .floating
        win.hidesOnDeactivate = false
        win.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        win.contentView = NSHostingView(rootView: PanelView(model: model))
        return win
    }
}
#endif
