#if os(macOS)
import AppKit
import SwiftUI
import CuktechCore

/// 面板的四个页签。顺序与 Windows 版一致（读数 / 控制 / 模式 / 曲线），
/// 两边的截图摆在一起能对上号。
enum PanelTab: Int, CaseIterable {
    case read = 0
    case control
    case mode
    case chart

    var title: String {
        switch self {
        case .read: return "读数"
        case .control: return "控制"
        case .mode: return "模式"
        case .chart: return "曲线"
        }
    }

    /// --panel-demo 用的名字（CI 截图按这个名字摆页）。
    static func demo(_ name: String) -> PanelTab? {
        switch name {
        case "read": return .read
        case "control": return .control
        case "mode": return .mode
        case "chart", "chart-solo": return .chart
        default: return nil
        }
    }
}

/// 面板的数据源。所有 UI 状态都在主线程上改，蓝牙动作丢回 DeviceSession。
/// 这里不标 @MainActor：main.swift 的顶层代码直接 new，Swift 5 语言模式下标了反而绕。
/// 真正保证主线程的是调用方 —— ticker 跑在 MainActor 上，按钮回调也都在主线程。
final class PanelModel: ObservableObject {
    @Published var snapshot = DeviceSnapshot()
    @Published var busy = false
    /// 面板被钉住。钉住后点外部不收起，相当于一个常驻小窗。
    @Published var pinned = false
    /// 面板「始终在最前」：把 popover 的窗口层级提到 .floating，压在其他 App 窗口之上。
    /// 跟 pinned 不是一回事 —— pinned 只管「点外面收不收」，这个才管「谁在上面」。
    @Published var alwaysOnTop = false
    /// 面板背景用不用毛玻璃质感。跟 AppSettings.glassPanel 同步，
    /// 只影响底色（玻璃底下要留缝），布局和配色常量一概不动。
    /// 0.1.7 起默认关：真值由 TrayController 从 config.json 灌进来，这里先按「关」建窗口，
    /// 免得建窗口那一瞬间先闪一下玻璃底。
    @Published var glassEnabled = false
    /// 蓝牙读数间隔（秒）。config.json 里的 pollInterval，改完立刻生效并落盘。
    @Published var pollInterval: Double = 2.0
    /// 正在采集的端口（1..4），nil = 没在采集。
    @Published var capturePort: Int?
    @Published var captureStats: CaptureAccumulator?
    @Published var captureStartedAt: Date?
    /// 当前页签。0.1.9 起面板从「一页纵向」改成四页签，与 Windows 版同构。
    @Published var tab: PanelTab = .read
    /// 曲线页看多久：60 / 300 / 900 / 1800 秒，点胶囊循环。真值在 AppSettings.chartSeconds。
    @Published var chartSeconds: Int = 60

    var onReconnect: (() -> Void)?
    var onSetPort: ((Int, Bool) -> Void)?
    var onSetScene: ((Int) -> Void)?
    var onQuit: (() -> Void)?
    /// 图钉切换时回调，让 PanelController 切 NSPopover 的 behavior（transient ↔ applicationDefined）。
    var onPinChanged: ((Bool) -> Void)?
    var onShowAbout: (() -> Void)?
    var onShowSettings: (() -> Void)?
    var onPollIntervalChanged: ((Double) -> Void)?
    var onStartCapture: ((Int) -> Void)?
    var onStopCapture: (() -> Void)?
    /// 曲线页换了时长。TrayController 负责写进 config.json。
    var onChartSecondsChanged: ((Int) -> Void)?

    /// 内存里的功率历史（不写盘）。曲线页读它。
    /// 不是 @Published：曲线跟着 snapshot 每 400ms 一起重画，多一个发布属性只会让
    /// 整个面板多刷一遍。
    private var history = PowerHistory()

    func reconnect() { onReconnect?() }

    func setPort(_ index: Int, _ enabled: Bool) {
        busy = true
        onSetPort?(index, enabled)
    }

    func setScene(_ mode: Int) {
        busy = true
        onSetScene?(mode)
    }

    func togglePin() {
        pinned.toggle()
        onPinChanged?(pinned)
    }

    func setPollInterval(_ v: Double) {
        guard v != pollInterval else { return }
        pollInterval = v
        onPollIntervalChanged?(v)
    }

    func startCapture(_ port: Int) {
        guard capturePort == nil else { return }
        onStartCapture?(port)
    }

    /// 刚点下「开始」的一瞬间不要响应停止：按钮图标从「采集」变成「停止」后，
    /// 同一次点击的 mouseUp 有时会落到新图标上，看起来像「开始就立刻停了」。
    func stopCapture() {
        guard let started = captureStartedAt else { return }
        guard Date().timeIntervalSince(started) > 1.0 else { return }
        onStopCapture?()
    }

    // MARK: - 曲线

    /// 记一拍。节流（两拍至少隔 0.4s）在 PowerHistory 里做。
    func record(_ snap: DeviceSnapshot) {
        history.add(snap)
    }

    /// 曲线页要画的点（最近 seconds 秒）。
    func chartPoints(_ seconds: Double) -> [PowerPoint] {
        history.snapshot(seconds)
    }

    /// 演示数据：--panel-demo 截图用，不碰蓝牙。
    func seedHistory(_ h: PowerHistory) {
        history = h
    }

    static let spanChoices = [60, 300, 900, 1800]

    static func spanLabel(_ sec: Int) -> String {
        switch sec {
        case 60: return "最近 1 分钟"
        case 300: return "最近 5 分钟"
        case 900: return "最近 15 分钟"
        case 1800: return "最近 30 分钟"
        default: return "最近 \(sec) 秒"
        }
    }

    /// 点时长胶囊：在四档预设里循环。当前值不在预设里（手改过 config.json）就从最小那档开始。
    func cycleChartSpan() {
        let cur = chartSeconds
        var next = Self.spanChoices[0]
        if let i = Self.spanChoices.firstIndex(of: cur) {
            next = Self.spanChoices[(i + 1) % Self.spanChoices.count]
        }
        setChartSeconds(next)
    }

    func setChartSeconds(_ sec: Int) {
        guard sec >= 10, sec <= 1800, sec != chartSeconds else { return }
        chartSeconds = sec
        onChartSecondsChanged?(sec)
    }

    /// 采集中顶部那行字：已经采了多久、充进去多少。每秒随采样刷新。
    var captureHeadline: String? {
        guard capturePort != nil, let stats = captureStats, let started = captureStartedAt else {
            return nil
        }
        let secs = max(0, Int(Date().timeIntervalSince(started)))
        guard stats.hasAnyData else { return "第 \(secs) 秒 · 等待读数…" }
        var parts = ["\(secs)s", String(format: "%.2fWh", stats.energyWh)]
        if stats.averagePower > 0.05 {
            parts.append(String(format: "平均 %.1fW", stats.averagePower))
        }
        return parts.joined(separator: " · ")
    }
}

struct PanelView: View {
    @ObservedObject var model: PanelModel

    private var snap: DeviceSnapshot { model.snapshot }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            tabBar
                .padding(.top, 8)
            page
                .padding(.top, 8)
            Spacer(minLength: 0)
            footer
                .padding(.top, 8)
        }
        .padding(.horizontal, 16)
        .padding(.top, 12)
        .padding(.bottom, 10)
        .frame(width: 340, height: 424)
        // 玻璃开着时把底色调薄，让宿主里垫的玻璃/毛玻璃透出来；关掉就是原来的实色。
        .background(Theme.background.opacity(GlassSupport.backdropOpacity(glass: model.glassEnabled)))
        .preferredColorScheme(.dark)
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 8) {
            VStack(alignment: .leading, spacing: 3) {
                Text(model.capturePort.map { "采集中 · " + PortData.portName($0) } ?? "酷态科 10 号 Ultra")
                    .font(.system(size: 14, weight: .semibold))
                    .foregroundColor(Theme.fg)
                Text(model.captureHeadline ?? snap.headline)
                    .font(.system(size: 11))
                    .foregroundColor(model.capturePort == nil ? Theme.dim : Theme.ok)
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

            pinButton
        }
    }

    private var pinButton: some View {
        Button {
            model.togglePin()
        } label: {
            Image(systemName: model.pinned ? "pin.fill" : "pin")
                .font(.system(size: 11.5, weight: .semibold))
                .foregroundColor(model.pinned ? Theme.fg : Theme.dim)
                .rotationEffect(.degrees(model.pinned ? 0 : 45))
                .frame(width: 22, height: 20)
                .background(
                    RoundedRectangle(cornerRadius: 6)
                        .fill(model.pinned ? Theme.cardOn : Color.clear)
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(model.pinned ? "取消固定：点面板外面就收起" : "固定面板：切到别的 App 也不收起")
        .padding(.top, 0)
        .offset(y: -1)
    }

    /// 四个页签。选中项给一层淡底 + 亮字；用普通按钮而不是 Picker，
    /// 免得「窗口还没被激活时第一下点击被系统吃掉」那个老毛病。
    private var tabBar: some View {
        HStack(spacing: 4) {
            ForEach(PanelTab.allCases, id: \.self) { t in
                Button {
                    model.tab = t
                } label: {
                    Text(t.title)
                        .font(.system(size: 11, weight: model.tab == t ? .medium : .regular))
                        .foregroundColor(model.tab == t ? Theme.fg : Theme.dim)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 4)
                        .background(
                            RoundedRectangle(cornerRadius: 6)
                                .fill(model.tab == t ? Theme.cardOn : Color.clear)
                        )
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }

    @ViewBuilder
    private var page: some View {
        switch model.tab {
        case .read: readPage
        case .control: controlPage
        case .mode: modePage
        case .chart: ChartPage(model: model, snap: snap)
        }
    }

    // MARK: - 读数页

    private var readPage: some View {
        VStack(spacing: 6) {
            ForEach(0..<4, id: \.self) { i in
                readRow(slot: snap.ports[i])
            }
        }
    }

    private func readRow(slot: PortSlot) -> some View {
        // C3 与 A 共用一个功率计时，搭车那一路没有自己的数字：名字不换、读数写暗色「并入 C3+A」。
        let passenger = snap.isMergedPassenger(slot)
        let bar = passenger
            ? WattStyle.dim
            : (slot.enabled ? (slot.watts > 0.05 ? WattStyle.port(slot.watts) : WattStyle.ok) : WattStyle.dim)
        return HStack(spacing: 8) {
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

            captureButton(for: slot)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.card))
    }

    /// 每口一个采集按钮：点一下开始读这一口，采集中变成红色停止。
    private func captureButton(for slot: PortSlot) -> some View {
        let capturing = model.capturePort == slot.index
        let otherBusy = model.capturePort != nil && !capturing
        return Button {
            if capturing { model.stopCapture() } else { model.startCapture(slot.index) }
        } label: {
            Image(systemName: capturing ? "stop.circle.fill" : "waveform.path.ecg")
                .font(.system(size: 10, weight: .semibold))
                .foregroundColor(capturing ? Theme.bad : Theme.dim.opacity(0.7))
                .frame(width: 19, height: 19)
                .background(
                    RoundedRectangle(cornerRadius: 5)
                        .fill(capturing ? Theme.cardOn : Color.clear)
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!snap.connected || otherBusy || model.busy)
        .help(capturing
              ? "停止采集并看结果"
              : "采集这个口的电量：每秒读一次，连续 30 秒没输出就结束")
    }

    // MARK: - 控制页

    private var controlPage: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("点右边的开关，充电器立刻就断/通，不用再保存")
                .font(.system(size: 10))
                .foregroundColor(Theme.dim)
                .lineLimit(1)

            VStack(spacing: 6) {
                ForEach(0..<4, id: \.self) { i in
                    controlRow(slot: snap.ports[i])
                }
            }
        }
    }

    private func controlRow(slot: PortSlot) -> some View {
        let passenger = snap.isMergedPassenger(slot)
        let bar = passenger
            ? WattStyle.dim
            : (slot.enabled ? WattStyle.ok : WattStyle.dim)
        let stateText: String = passenger
            ? DeviceSnapshot.mergedPassengerText
            : (slot.enabled ? "通电" : "断电")
        return HStack(spacing: 8) {
            RoundedRectangle(cornerRadius: 2)
                .fill(Theme.color(bar))
                .frame(width: 3, height: 26)

            Text(snap.rowName(slot))
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)

            Spacer(minLength: 0)

            Text(stateText)
                .font(.system(size: 10.5))
                .foregroundColor(passenger ? Theme.dim.opacity(0.8) : (slot.enabled ? Theme.ok : Theme.dim))

            Toggle("", isOn: Binding(
                get: { slot.enabled },
                set: { model.setPort(slot.zeroBasedIndex, $0) }
            ))
            .labelsHidden()
            .toggleStyle(PanelSwitchStyle())
            .disabled(!snap.connected || model.busy || passenger)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.card))
    }

    // MARK: - 模式页

    private var modePage: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("点一下就切，充电器立刻换策略，不用再保存")
                .font(.system(size: 10))
                .foregroundColor(Theme.dim)
                .lineLimit(1)

            LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 6) {
                ForEach(0..<SceneMode.all.count, id: \.self) { i in
                    let item = SceneMode.all[i]
                    Button {
                        model.setScene(item.value)
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 4) {
                                Text(item.name)
                                    .font(.system(size: 11.5, weight: .medium))
                                    .foregroundColor(snap.sceneMode == item.value ? Theme.fg : Theme.dim)
                                Spacer(minLength: 0)
                                // 打个勾，明确「这是充电器此刻的档位」，不是刚被点过的按钮。
                                if snap.sceneMode == item.value {
                                    Image(systemName: "checkmark")
                                        .font(.system(size: 8, weight: .bold))
                                        .foregroundColor(Theme.ok)
                                }
                            }
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

    /// 读数间隔。以前只能手改 config.json 里的 pollInterval，面板上没入口，等于没有。
    ///
    /// 位置放在页脚、和「重新连接」一边：这是 App 自己的刷新频率，不是充电器的设置项。
    /// （原来挂在「充电模式」标题右边，读起来就像充电模式每 1 秒在自动切。）
    ///
    /// 这里不用 SwiftUI 的 Menu：窗口没被激活（面板刚弹出时就是这样）时，第一下点击
    /// 不弹菜单，得再点一次——和开关那个「点一下才变」是同一个毛病。自己弹 NSMenu 就没这问题。
    private var pollIntervalMenu: some View {
        Button {
            popUpIntervalMenu()
        } label: {
            HStack(spacing: 3) {
                Image(systemName: "arrow.clockwise")
                    .font(.system(size: 8, weight: .semibold))
                Text("读数 " + Self.intervalLabel(model.pollInterval))
                    .font(.system(size: 10))
            }
            .foregroundColor(Theme.dim)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(RoundedRectangle(cornerRadius: 5).fill(Theme.card))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .help("蓝牙读数间隔：越短越跟手，也越费电（不影响充电器本身）")
    }

    private func popUpIntervalMenu() {
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.appearance = NSAppearance(named: .darkAqua)
        let target = IntervalMenuTarget { [model] v in model.setPollInterval(v) }
        for v in Self.pollChoices {
            let mi = NSMenuItem(title: Self.intervalLabel(v),
                                action: #selector(IntervalMenuTarget.pick(_:)),
                                keyEquivalent: "")
            mi.target = target
            mi.representedObject = v
            mi.state = v == model.pollInterval ? .on : .off
            menu.addItem(mi)
        }
        // popUp 里跑菜单跟踪循环，target 活在栈上就够
        let p = NSEvent.mouseLocation
        menu.popUp(positioning: nil, at: NSPoint(x: p.x - 6, y: p.y - 6), in: nil)
    }

    static let pollChoices: [Double] = [1, 2, 3, 5, 10]

    static func intervalLabel(_ v: Double) -> String {
        v == v.rounded() ? "\(Int(v)) 秒" : String(format: "%.1f 秒", v)
    }

    private var footer: some View {
        HStack(spacing: 12) {
            Button("重新连接") { model.reconnect() }
                .buttonStyle(.plain)
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
            pollIntervalMenu
            if model.alwaysOnTop {
                // 只在开启时占位：面板高度是固定的 340×424，常驻一行字会挤到下面。
                Text("置顶中")
                    .font(.system(size: 9.5))
                    .foregroundColor(Theme.dim.opacity(0.9))
                    .help("面板始终在最前：压在其他 App 窗口之上。右键菜单里可以关掉。")
            }
            Spacer()
            Button("设置") { model.onShowSettings?() }
                .buttonStyle(.plain)
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
            Button("关于") { model.onShowAbout?() }
                .buttonStyle(.plain)
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
            Button("退出") { model.onQuit?() }
                .buttonStyle(.plain)
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
        }
    }
}

/// 曲线页（0.1.9 新增，语义对齐 Windows 版 PanelWindow.PaintChartPage）。
///
/// 三条设计原则，和 Windows 那边是同一套：
/// 1. **哪一路在输出**：副标题直接点名（「在输出：C3」），单口输出时不用回头对图例；
/// 2. **哪条线是哪一路**：图例里没出过力的那几路压暗，每条线末端直接写着口名；
/// 3. **线要断**：拔线 / 没读数的地方不连线，否则「拔了线」看着像「功率平滑下降」。
struct ChartPage: View {
    @ObservedObject var model: PanelModel
    let snap: DeviceSnapshot

    /// 四路各自的线色：蓝 / 绿 / 橙 / 紫，与 Windows 版一致。
    static let lineColors: [Color] = [
        Color(red: 59 / 255, green: 130 / 255, blue: 246 / 255),
        Color(red: 34 / 255, green: 197 / 255, blue: 94 / 255),
        Color(red: 245 / 255, green: 158 / 255, blue: 11 / 255),
        Color(red: 167 / 255, green: 139 / 255, blue: 250 / 255),
    ]

    private var points: [PowerPoint] {
        model.chartPoints(Double(model.chartSeconds))
    }

    /// 每一路在窗口里的峰值：整条都是 0 的那一路不画线（画出来就是贴在 0 轴上的横线）。
    private var peaks: [Double] {
        var p = [Double](repeating: 0, count: 4)
        for pt in points {
            for i in 0..<4 {
                let v = pt.at(i)
                if v > p[i] { p[i] = v }
            }
        }
        return p
    }

    /// 纵轴顶：20W 一档向上取整，最小 20W。
    private var topWatts: Double {
        let m = peaks.max() ?? 0
        return m <= 20 ? 20 : (m / 20).rounded(.up) * 20
    }

    /// 口名：C3 与 A 合并时第三路写 C3+A。
    private func portName(_ i: Int) -> String {
        switch i {
        case 0: return "C1"
        case 1: return "C2"
        case 2: return snap.pairMerged ? "C3+A" : "C3"
        default: return "A"
        }
    }

    private var subtitle: String {
        var live: [String] = []
        for i in 0..<4 where peaks[i] >= 0.5 {
            live.append(portName(i))
        }
        return live.isEmpty ? "每路一条线，点右边换时长" : "在输出：" + live.joined(separator: "、")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                Text(subtitle)
                    .font(.system(size: 10))
                    .foregroundColor(Theme.dim)
                    .lineLimit(1)
                Spacer(minLength: 0)
                Button {
                    model.cycleChartSpan()
                } label: {
                    Text(PanelModel.spanLabel(model.chartSeconds))
                        .font(.system(size: 10))
                        .foregroundColor(Theme.fg)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(Capsule().fill(Theme.card))
                        .overlay(Capsule().stroke(Color.white.opacity(0.08), lineWidth: 1))
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .fixedSize()
                .help("点一下换一档时长：1 / 5 / 15 / 30 分钟")
            }

            Canvas { ctx, size in
                draw(ctx: ctx, size: size)
            }
            .frame(height: 168)

            HStack(spacing: 0) {
                ForEach(0..<4, id: \.self) { i in
                    HStack(spacing: 5) {
                        RoundedRectangle(cornerRadius: 1.5)
                            .fill(peaks[i] >= 0.5 ? Self.lineColors[i] : Theme.dim.opacity(0.35))
                            .frame(width: 8, height: 8)
                        Text(portName(i))
                            .font(.system(size: 9.5))
                            .foregroundColor(peaks[i] >= 0.5 ? Theme.dim : Theme.dim.opacity(0.45))
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }

            if points.count >= 2 {
                HStack {
                    Text("峰值 " + WattStyle.format(totalPeak) + " W")
                    Spacer(minLength: 0)
                    Text("平均 " + WattStyle.format(totalAverage) + " W")
                }
                .font(.system(size: 9.5))
                .foregroundColor(Theme.dim)
            }
        }
    }

    private var totalPeak: Double {
        var peak = 0.0
        for pt in points {
            let w = (0..<4).reduce(0.0) { $0 + max(0, pt.at($1)) }
            if w > peak { peak = w }
        }
        return peak
    }

    private var totalAverage: Double {
        guard !points.isEmpty else { return 0 }
        let sum = points.reduce(0.0) { acc, pt in
            acc + (0..<4).reduce(0.0) { $0 + max(0, pt.at($1)) }
        }
        return sum / Double(points.count)
    }

    private func draw(ctx: GraphicsContext, size: CGSize) {
        let axisW: CGFloat = 32
        let x0: CGFloat = axisW
        let x1: CGFloat = size.width
        let y0: CGFloat = 4
        let y1: CGFloat = size.height - 4
        let top = topWatts

        // 三条水平参考线（0 / 一半 / 顶），左边写刻度
        for k in 0...2 {
            let v = top * Double(k) / 2.0
            let y = y1 - (y1 - y0) * CGFloat(v / top)
            var line = Path()
            line.move(to: CGPoint(x: x0, y: y))
            line.addLine(to: CGPoint(x: x1, y: y))
            ctx.stroke(line,
                       with: .color(Color.white.opacity(k == 0 ? 0.14 : 0.07)),
                       lineWidth: 1)
            if k > 0 {
                ctx.draw(Text(String(Int(v.rounded())) + "W")
                            .font(.system(size: 8.5))
                            .foregroundColor(Theme.dim.opacity(0.85)),
                         at: CGPoint(x: x0 - 5, y: y),
                         anchor: .trailing)
            }
        }
        ctx.draw(Text("0W")
                    .font(.system(size: 8.5))
                    .foregroundColor(Theme.dim.opacity(0.85)),
                 at: CGPoint(x: x0 - 5, y: y1),
                 anchor: .trailing)

        guard points.count >= 2 else {
            ctx.draw(Text("数据还不够，等十几秒再看")
                        .font(.system(size: 9.5))
                        .foregroundColor(Theme.dim.opacity(0.7)),
                     at: CGPoint(x: (x0 + x1) / 2, y: (y0 + y1) / 2),
                     anchor: .center)
            return
        }

        let span = Double(max(10, model.chartSeconds))
        let from = PowerHistory.now - span
        var ends: [(CGPoint, Int)] = []

        for i in 0..<4 {
            guard peaks[i] >= 0.5 else { continue }
            var path = Path()
            var started = false
            var last: CGPoint?
            for pt in points {
                let w = pt.at(i)
                if w < 0 {
                    // 断点：把这一段画完，下一段从新的 move 开始
                    if started {
                        ctx.stroke(path, with: .color(Self.lineColors[i]), lineWidth: 1.6)
                        path = Path()
                        started = false
                    }
                    continue
                }
                let fx = x0 + CGFloat((pt.t - from) / span) * (x1 - x0)
                let x = min(max(fx, x0), x1)
                let y = y1 - CGFloat(min(w, top) / top) * (y1 - y0)
                let gp = CGPoint(x: x, y: y)
                if started {
                    path.addLine(to: gp)
                } else {
                    path.move(to: gp)
                    started = true
                }
                last = gp
            }
            if started {
                ctx.stroke(path, with: .color(Self.lineColors[i]), lineWidth: 1.6)
            }
            if let l = last { ends.append((l, i)) }
        }

        // 线末端标口名：靠右了就写到线左边，两条线挨太近就往下错开一行
        ends.sort { $0.0.y < $1.0.y }
        var prevY: CGFloat = -.infinity
        for (p, i) in ends {
            var ly = p.y - 16
            if ly < y0 { ly = y0 }
            if prevY > -.infinity && ly < prevY + 13 { ly = prevY + 13 }
            if ly + 14 > y1 { ly = y1 - 14 }
            prevY = ly
            let name = portName(i)
            let labelW: CGFloat = name.count >= 4 ? 34 : 20
            var lx = p.x + 5
            if lx + labelW > x1 { lx = p.x - 5 - labelW }
            if lx < x0 { lx = x0 }
            // 标签垫一层半透明底：线正好从字下面穿过时也读得清
            let rect = CGRect(x: lx - 3, y: ly - 1, width: labelW + 6, height: 15)
            ctx.fill(Path(roundedRect: rect, cornerRadius: 4),
                     with: .color(Color(red: 0.086, green: 0.102, blue: 0.125).opacity(0.82)))
            ctx.draw(Text(name)
                        .font(.system(size: 9.5, weight: .medium))
                        .foregroundColor(Self.lineColors[i]),
                     at: CGPoint(x: lx, y: ly),
                     anchor: .topLeading)
        }
    }
}

/// 间隔菜单的回调目标（NSMenuItem 的 target 必须是 Objective-C 对象）。
final class IntervalMenuTarget: NSObject {
    private let handler: (Double) -> Void
    init(_ handler: @escaping (Double) -> Void) { self.handler = handler }
    @objc func pick(_ sender: NSMenuItem) {
        if let v = sender.representedObject as? Double { handler(v) }
    }
}

/// 自绘开关。
///
/// 系统 `Toggle(.switch)` 在「窗口不是 key window」时会画成不活跃的灰绿（面板刚弹出、
/// App 又没被激活时就是这样），点一下面板内部才变蓝 —— 看起来像坏了。
/// 这里颜色自己定，跟窗口激活状态无关。
struct PanelSwitchStyle: ToggleStyle {
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        Button {
            configuration.isOn.toggle()
        } label: {
            Capsule()
                .fill(configuration.isOn ? Theme.switchOn : Theme.switchOff)
                .frame(width: 34, height: 19)
                .overlay(
                    Circle()
                        .fill(Color.white)
                        .shadow(color: .black.opacity(0.3), radius: 1, y: 1)
                        .frame(width: 15, height: 15)
                        .offset(x: configuration.isOn ? 7.5 : -7.5)
                )
                .animation(.easeInOut(duration: 0.15), value: configuration.isOn)
                .opacity(isEnabled ? 1 : 0.45)
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .disabled(!isEnabled)
    }
}

enum Theme {
    static let background = Color(red: 0.106, green: 0.118, blue: 0.137)
    static let fg = Color(red: 0.91, green: 0.918, blue: 0.929)
    static let dim = Color(red: 0.463, green: 0.49, blue: 0.529)
    static let card = Color.white.opacity(0.045)
    static let cardOn = Color.white.opacity(0.10)
    /// 开关打开时的蓝（系统 accent 在暗色下的观感），关掉时是浅灰槽。
    static let switchOn = Color(red: 10 / 255, green: 132 / 255, blue: 1.0)
    static let switchOff = Color.white.opacity(0.18)
    static let ok = Color(red: 52 / 255, green: 211 / 255, blue: 153 / 255)
    static let bad = Color(red: 248 / 255, green: 113 / 255, blue: 113 / 255)

    static func color(_ c: RGB) -> Color {
        Color(red: Double(c.r), green: Double(c.g), blue: Double(c.b))
    }
}

/// 面板窗口：无标题栏的浮动面板。
/// 用 NSPopover 承载，右上角图钉切 behavior：`.transient`（点外面收）↔
/// `.applicationDefined`（常驻）。不过 `.transient` 只在 popover 窗口 resignKey 时
/// 才关，而菜单栏 App 弹面板时自己往往没被激活，点外面等不到 resignKey，
/// 所以另外自己盯鼠标按下事件（见 `installOutsideMonitors`）。
final class PanelController: NSObject, NSPopoverDelegate {
    let model = PanelModel()
    private var popover: NSPopover?
    private var statusButton: NSStatusBarButton?
    /// 点外部自动收起。`.transient` 是靠 popover 窗口 resignKey 触发的，可菜单栏 App
    /// 弹面板时自己通常**没被激活**（键盘焦点还在用户正用着的那个 App 上），
    /// 于是点外面永远等不到 resignKey，面板就赖着不走。所以自己盯鼠标按下事件。
    private var outsideMonitors: [Any] = []
    /// 切到别的 App（Cmd-Tab、点 Dock 图标）时也收起。这类切换没有鼠标事件落在
    /// 我们的可见范围内，光靠监听鼠标不一定收得到。
    private var appSwitchObserver: NSObjectProtocol?
    private var isPinned = false
    /// 「始终在最前」的当前值。窗口是懒创建的（show 之前
    /// `popover.contentViewController.view.window` 还是 nil），所以这里自己留一份，
    /// 每次 show 之后再统一调一次 `applyWindowLevel()`。
    private var alwaysOnTop = false
    /// 垫在面板内容下面那层玻璃/毛玻璃。玻璃开关切换时直接改它的可见性。
    private var backdropView: PanelBackdropView?

    override init() {
        super.init()
        model.onPinChanged = { [weak self] pinned in self?.applyPin(pinned) }
    }

    deinit {
        removeOutsideMonitors()
    }

    func toggle(relativeTo button: NSStatusBarButton?) {
        if let p = popover, p.isShown {
            close()
        } else {
            show(relativeTo: button)
        }
    }

    func show(relativeTo button: NSStatusBarButton?) {
        guard let button else { return }
        statusButton = button
        let pop = popover ?? makePopover()
        popover = pop
        pop.behavior = isPinned ? .applicationDefined : .transient
        if !pop.isShown {
            pop.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        }
        // 窗口是 show 之后才有的：放在这里才拿得到，重新显示也不会丢层级。
        applyWindowLevel()
        if !isPinned { installOutsideMonitors() }
    }

    /// 托盘菜单里的「面板始终在最前」勾选项进来。存进 AppSettings 由 TrayController 负责。
    func setAlwaysOnTop(_ on: Bool) {
        alwaysOnTop = on
        model.alwaysOnTop = on
        applyWindowLevel()
    }

    /// 托盘菜单里的「面板玻璃质感」勾选项进来。只切背景层，布局和配色都不动。
    func setGlassEnabled(_ on: Bool) {
        model.glassEnabled = on
        backdropView?.setEnabled(on)
    }

    /// 把 popover 的窗口层级改成 .floating / .normal。
    /// 注意 NSPopover 不自带 window 属性，得从 contentViewController 摸上去；
    /// 面板没弹出来过时 window 是 nil，直接跳过（show 时会再调一次）。
    private func applyWindowLevel() {
        guard let w = popover?.contentViewController?.view.window else { return }
        w.level = alwaysOnTop ? .floating : .normal
    }

    func close() {
        removeOutsideMonitors()
        popover?.performClose(nil)
    }

    /// 固定 = 常驻：切 NSPopover behavior。
    /// - `.transient`：点 popover 外任意处自动关闭（系统行为，无时序竞争）
    /// - `.applicationDefined`：不自动关，当常驻小窗用
    private func applyPin(_ pinned: Bool) {
        isPinned = pinned
        popover?.behavior = pinned ? .applicationDefined : .transient
        if pinned {
            removeOutsideMonitors()
        } else if let p = popover, p.isShown {
            installOutsideMonitors()
        }
    }

    // MARK: - 点外部收起

    private func installOutsideMonitors() {
        guard outsideMonitors.isEmpty else { return }
        // 全局监听只收「发给别的 App」的点击，本地监听收本 App 的，两个一起才全覆盖。
        let onClick: (NSEvent) -> Void = { [weak self] _ in
            // 事件还在分发途中就关窗口，容易和 AppKit 的鼠标跟踪打架，挪到下一拍再判。
            DispatchQueue.main.async { self?.closeIfClickedOutside() }
        }
        if let g = NSEvent.addGlobalMonitorForEvents(
            matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown],
            handler: onClick) {
            outsideMonitors.append(g)
        }
        if let l = NSEvent.addLocalMonitorForEvents(
            matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown],
            handler: { event in
                onClick(event)
                return event
            }) {
            outsideMonitors.append(l)
        }
        if appSwitchObserver == nil {
            appSwitchObserver = NSWorkspace.shared.notificationCenter.addObserver(
                forName: NSWorkspace.didActivateApplicationNotification,
                object: nil,
                queue: .main
            ) { [weak self] note in
                guard let self,
                      let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                      app.processIdentifier != ProcessInfo.processInfo.processIdentifier
                else { return }
                self.close()
            }
        }
    }

    private func removeOutsideMonitors() {
        outsideMonitors.forEach { NSEvent.removeMonitor($0) }
        outsideMonitors.removeAll()
        if let o = appSwitchObserver {
            NSWorkspace.shared.notificationCenter.removeObserver(o)
            appSwitchObserver = nil
        }
    }

    private func closeIfClickedOutside() {
        guard let pop = popover, pop.isShown else {
            removeOutsideMonitors()
            return
        }
        let point = NSEvent.mouseLocation
        // 点在面板里：正常操作，别管
        if let w = pop.contentViewController?.view.window, w.frame.contains(point) { return }
        // 点在菜单栏图标上：交给 toggle。这里先关掉的话，紧接着 mouseUp 的 toggle
        // 又会把它打开，就是那个「闪一下」。
        if let button = statusButton,
           let bw = button.window,
           bw.convertToScreen(button.convert(button.bounds, to: nil)).contains(point) { return }
        close()
    }

    func popoverDidClose(_ notification: Notification) {
        removeOutsideMonitors()
    }

    private func makePopover() -> NSPopover {
        let pop = NSPopover()
        pop.behavior = .transient
        pop.animates = true
        pop.contentSize = NSSize(width: 340, height: 424)
        // 面板弹出时 App 往往还没被激活（用户正在别的 App 里），这时第一下点击会被
        // 系统拿去「激活 App」，像点了没反应 —— 也让系统控件画成不活跃的灰绿。
        // 用接受首次点击的 hosting view，第一下就落在控件上。
        let host = FirstMouseHostingView(rootView: PanelView(model: model))
        // 背景层 + 内容层装进一个普通容器：popover 的圆角裁剪在外面，
        // 玻璃只要填满这块 340x424 就行，SwiftUI 那边把底色调薄让出来。
        let container = NSView(frame: NSRect(x: 0, y: 0, width: 340, height: 424))
        let backdrop = GlassSupport.makeBackdrop()
        backdrop.frame = container.bounds
        backdrop.autoresizingMask = [.width, .height]
        backdrop.setEnabled(model.glassEnabled)
        container.addSubview(backdrop)
        host.frame = container.bounds
        host.autoresizingMask = [.width, .height]
        container.addSubview(host)
        backdropView = backdrop
        let vc = NSViewController()
        vc.view = container
        pop.contentViewController = vc
        pop.delegate = self
        return pop
    }
}

// MARK: - 文档 / CI 截图用的面板演示

/// --panel-demo 用的假数据：一台已经连上的酷态科 10 号 Ultra。
/// C1 在带载；solo 时只有 C3 在输出，否则 C3+A 合并成一路。
/// 全是构造出来的数，不碰蓝牙、不联网、不落盘。
enum PanelDemoData {
    static func snapshot(soloC3: Bool) -> DeviceSnapshot {
        var s = DeviceSnapshot()
        s.connected = true
        s.status = "已连接"
        s.firmware = "2.1.2_0073"
        s.portSwitch = 0x0f
        s.sceneMode = 3
        s.ports[0].apply(PortData(state: 0x01, code: 2, current: 2.00, voltage: 20.10))
        s.ports[1].apply(PortData(state: 0x00, code: 0, current: 0, voltage: 0))
        if soloC3 {
            s.ports[2].apply(PortData(state: 0x01, code: 2, current: 0.45, voltage: 20.00))
        } else {
            // 0x11 = 固件把 C3 与 A 的功率并成一路报
            s.ports[2].apply(PortData(state: 0x11, code: 2, current: 0.45, voltage: 20.00))
        }
        s.ports[3].apply(PortData(state: 0x00, code: 0, current: 0, voltage: 0))
        return s
    }
}

/// 把某个页签摆进一个普通窗口（不是 NSPopover）：popover 得挂在状态栏按钮上，
/// CI 的截图脚本拿不到那个按钮，普通窗口反而更稳。
final class PanelDemoController {
    static let shared = PanelDemoController()
    private var window: NSWindow?

    func show(page: PanelTab, soloC3: Bool) {
        let model = PanelModel()
        model.snapshot = PanelDemoData.snapshot(soloC3: soloC3)
        model.tab = page
        // 曲线页给足 5 分钟历史，跟真机默认档一致。
        model.chartSeconds = page == .chart ? 300 : 60
        model.seedHistory(PowerHistory.demo(c3Only: soloC3))

        let host = NSHostingView(rootView: PanelView(model: model))
        host.frame = NSRect(x: 0, y: 0, width: 340, height: 424)

        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 340, height: 424),
                         styleMask: [.borderless],
                         backing: .buffered,
                         defer: false)
        w.isOpaque = true
        w.backgroundColor = NSColor(calibratedRed: 0.106, green: 0.118, blue: 0.137, alpha: 1)
        w.level = .floating
        w.contentView = host
        // 离开屏幕边缘一点，免得截图时被顶出可视区。
        w.setFrameOrigin(NSPoint(x: 240, y: 260))
        w.makeKeyAndOrderFront(nil)
        // 菜单栏 app 默认没被激活，不激活的话窗口在后台，screencapture 抓不到。
        NSApp.activate(ignoringOtherApps: true)
        window = w
    }
}
#endif