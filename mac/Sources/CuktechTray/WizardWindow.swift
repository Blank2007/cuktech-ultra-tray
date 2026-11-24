#if os(macOS)
import AppKit
import SwiftUI
import CuktechCore
import CuktechBle

/// 配对向导的数据源。四屏：认路 -> 扫码登录小米账号 -> 挑设备 -> 完成。
/// 与 PanelModel 同一风格：不标 @MainActor，靠所有入口都在主线程（按钮回调、Task { @MainActor }）。
final class WizardModel: ObservableObject {
    enum Step { case welcome, login, pick, done }

    @Published var step: Step = .welcome
    @Published var status = "第一次用，先认一下这台充电器"
    @Published var error: String?
    @Published var qrImage: NSImage?
    @Published var devices: [MiDevice] = []
    @Published var nearby: [BleFoundDevice] = []
    @Published var busy = false
    @Published var manualMac = ""
    @Published var manualToken = ""
    @Published var showManual = false
    @Published var doneText = ""
    @Published var selectedDID = ""

    var settings: AppSettings
    var onDone: ((AppSettings) -> Void)?
    var onClose: (() -> Void)?

    private let log = LogFile()
    private var client: XiaomiClient?
    private var scanner: BleScanner?

    init(settings: AppSettings) {
        self.settings = settings
    }

    // MARK: - 动作

    func beginLogin() {
        guard !busy else { return }
        busy = true
        error = nil
        qrImage = nil
        step = .login
        status = "正在向小米要一张二维码…"

        let log = self.log
        let c = XiaomiClient(logger: { line in log.write(line) })
        client = c

        Task { @MainActor in
            do {
                let ticket = try await c.requestQr()
                if let png = try? await c.downloadQrImage(ticket), let image = NSImage(data: png) {
                    self.qrImage = image
                }
                self.status = "等你扫码（这个码 \(Int(ticket.ttl.rounded())) 秒内有效）"
                self.busy = false

                let result = try await c.waitForScan(ticket)
                log.write("[wizard] 登录成功 userId=\(result.userId) region=\(result.region ?? "-")")

                self.status = "登录成功，正在列你账号里的设备…"
                self.busy = true
                let list = try await c.devices()
                self.devices = list
                self.busy = false

                if list.isEmpty {
                    self.status = "这个账号里没找到设备"
                    return
                }
                if let first = list.first(where: { $0.isCuktech && $0.hasToken }) {
                    self.selectedDID = first.did
                } else if let first = list.first(where: { $0.hasToken }) {
                    self.selectedDID = first.did
                }
                self.step = .pick
                self.status = "从下面挑出你这台充电器"
                self.startScan()
            } catch {
                self.error = "\(error)"
                self.busy = false
                self.status = "扫码没走通，可以重新生成一张码"
            }
        }
    }

    /// 顺手扫一下附近，云端没给 mac 的设备可以用广播里的地址补齐。
    func startScan() {
        nearby = []
        let scanner = BleScanner()
        self.scanner = scanner
        Task { @MainActor in
            do {
                let found = try await scanner.scan(timeout: 6) { list in
                    Task { @MainActor in self.nearby = list }
                }
                self.nearby = found
                self.log.write("[wizard] 附近扫到 \(found.count) 台 FE95 设备")
            } catch {
                self.log.write("[wizard] 蓝牙扫描失败：\(error)")
            }
        }
    }

    func rescan() {
        startScan()
    }

    func pickSelected() {
        guard let device = devices.first(where: { $0.did == selectedDID }) else {
            error = "先在列表里点一台设备"
            return
        }
        pick(device)
    }

    func pick(_ device: MiDevice) {
        guard let token = device.tokenHex, token.count == 24 else {
            error = "这台设备没拿到 12 字节 token（\(device.tokenSource.isEmpty ? "云端无" : device.tokenSource)），换一台试，或者退回手动填。"
            return
        }

        var mac = WizardModel.normalizedMac(device.mac)
        if mac == nil, let near = nearby.first(where: { $0.isCuktechCharger }) {
            mac = near.macString
            log.write("[wizard] 云端没给 mac，用附近广播里的 \(near.macString)")
        }
        guard let macText = mac else {
            error = "拿不到这台设备的蓝牙地址（广播也没扫到）。可以先手动填一次 MAC。"
            return
        }
        finish(mac: macText, token: token)
    }

    func submitManual() {
        guard let mac = WizardModel.normalizedMac(manualMac) else {
            error = "MAC 要 6 段十六进制，例如 3C:CD:73:27:61:A0"
            return
        }
        let token = manualToken.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard token.count == 24, token.allSatisfy({ $0.isHexDigit }) else {
            error = "token 是 24 位十六进制（12 字节），Windows 版 settings.json 里的 TokenHex 就是它"
            return
        }
        finish(mac: mac, token: token)
    }

    func close() {
        onClose?()
    }

    // MARK: - 演示数据（CI 截图与文档用，不联网、不碰蓝牙）

    /// 把某一屏直接填成假数据。--wizard-demo=<welcome|login|pick|done> 用。
    func loadDemo(_ name: String) {
        error = nil
        busy = false
        switch name {
        case "login":
            step = .login
            status = "等你扫码（这个码 120 秒内有效）"
            qrImage = WizardModel.demoQRCode()
        case "pick":
            step = .pick
            status = "从下面挑出你这台充电器"
            devices = [
                MiDevice(did: "672601331", name: "酷态科 10 号 Ultra", model: "njcuk.fitting.ad1204",
                         mac: "3C:CD:73:27:61:A0", tokenHex: "00112233445566778899aabb",
                         tokenSource: "云端", isCuktech: true, isOnline: true),
                MiDevice(did: "1043902774", name: "台灯", model: "yeelink.light.lamp1",
                         mac: "", tokenHex: nil, tokenSource: "", isCuktech: false, isOnline: false),
            ]
            selectedDID = "672601331"
            nearby = [
                BleFoundDevice(mac: [0x3C, 0xCD, 0x73, 0x27, 0x61, 0xA0], name: "njcuk.fitting.ad1204",
                               rssi: -42, productId: 0x660E),
            ]
        case "done":
            step = .done
            status = "配好了，剩下的交给菜单栏"
            doneText = "配置存到 \(AppSettings.fileURL.path)"
        default:
            step = .welcome
            status = "第一次用，先认一下这台充电器"
        }
    }

    /// 画一张「假二维码」占位（三个定位角 + 伪随机点），只为让截图里有一张码。
    static func demoQRCode() -> NSImage {
        let n = 25
        let cell = 8
        let size = n * cell
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
                                   bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        let ctx = NSGraphicsContext(bitmapImageRep: rep)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        NSColor.white.setFill()
        NSRect(x: 0, y: 0, width: size, height: size).fill()
        NSColor.black.setFill()
        var seed: UInt64 = 0x9E37_79B9_7F4A_7C15
        for y in 0..<n {
            for x in 0..<n {
                let inFinder = (x < 8 && y < 8) || (x >= n - 8 && y < 8) || (x < 8 && y >= n - 8)
                if inFinder { continue }
                seed = seed &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
                if (seed >> 33) % 3 == 0 {
                    NSRect(x: x * cell, y: y * cell, width: cell, height: cell).fill()
                }
            }
        }
        for (fx, fy) in [(0, 0), (n - 7, 0), (0, n - 7)] {
            NSRect(x: fx * cell, y: fy * cell, width: 7 * cell, height: 7 * cell).fill()
            NSColor.white.setFill()
            NSRect(x: (fx + 1) * cell, y: (fy + 1) * cell, width: 5 * cell, height: 5 * cell).fill()
            NSColor.black.setFill()
            NSRect(x: (fx + 2) * cell, y: (fy + 2) * cell, width: 3 * cell, height: 3 * cell).fill()
        }
        NSGraphicsContext.restoreGraphicsState()
        let img = NSImage(size: NSSize(width: size, height: size))
        img.addRepresentation(rep)
        return img
    }

    // MARK: - 内部

    private func finish(mac: String, token: String) {
        var s = settings
        s.mac = mac
        s.tokenHex = token
        do {
            try s.save()
        } catch {
            self.error = "写配置文件失败：\(error)"
            return
        }
        settings = s
        error = nil
        doneText = "配置存到 \(AppSettings.fileURL.path)"
        step = .done
        status = "配好了，剩下的交给菜单栏"
        log.write("[wizard] 配对完成 mac=\(mac) token=已写入(24 位)")
        onDone?(s)
    }

    static func normalizedMac(_ raw: String) -> String? {
        let clean = raw.uppercased()
            .replacingOccurrences(of: "-", with: "")
            .replacingOccurrences(of: ":", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard clean.count == 12, clean.allSatisfy({ $0.isHexDigit }) else { return nil }
        var parts: [String] = []
        var idx = clean.startIndex
        while idx < clean.endIndex {
            let next = clean.index(idx, offsetBy: 2)
            parts.append(String(clean[idx..<next]))
            idx = next
        }
        return parts.joined(separator: ":")
    }
}

// MARK: - 界面

struct WizardView: View {
    @ObservedObject var model: WizardModel
    @State private var showPrivacy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().opacity(0.25).padding(.vertical, 10)
            content
            Spacer(minLength: 10)
            if let err = model.error {
                Text(err)
                    .font(.system(size: 10.5))
                    .foregroundColor(Theme.bad)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.bottom, 6)
            }
            footer
        }
        .padding(.horizontal, 22)
        .padding(.top, 20)
        .padding(.bottom, 16)
        .frame(width: 480, height: 580)
        .background(Theme.background)
        .preferredColorScheme(.dark)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("配对这台充电器")
                .font(.system(size: 17, weight: .semibold))
                .foregroundColor(Theme.fg)
            Text(model.status)
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    @ViewBuilder
    private var content: some View {
        switch model.step {
        case .welcome: welcome
        case .login: login
        case .pick: pick
        case .done: done
        }
    }

    // 第一屏
    private var welcome: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("充电器的数据只有小米云和它自己知道：蓝牙那头要一个 12 字节的 token 才算登录，而这个 token 只能从你自己账号的设备清单里取。所以第一次要走一遍扫码。")
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
                .fixedSize(horizontal: false, vertical: true)

            stepRow("1", "扫码登录小米账号", "只读你账号里的设备清单。token 存在本机，不上传任何东西，也不碰云端写接口。")
            stepRow("2", "挑出这台充电器", "带「酷态科」的排最前；挑完记下它的蓝牙 MAC 和 token。")
            stepRow("3", "以后不用再扫", "配好就常驻菜单栏，重连、读数、开关都自动。")

            previewCard

            privacyNote

            if model.showManual {
                manualForm
            }
        }
    }

    /// 隐私说明：一句人话 + 点开看全文。内容跟 README 的「隐私」一节、
    /// Windows 版向导里那个弹窗保持一致。
    private var privacyNote: some View {
        HStack(alignment: .top, spacing: 9) {
            Image(systemName: "lock.shield")
                .font(.system(size: 11))
                .foregroundColor(Theme.ok.opacity(0.85))
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 5) {
                Text("不是小米或酷态科官方出的。扫码只向小米云换一次这台充电器的 Mac 和 token，凭据只写在本机的 config.json；日常读功率走蓝牙，不联网。")
                    .font(.system(size: 10.5))
                    .foregroundColor(Theme.dim)
                    .fixedSize(horizontal: false, vertical: true)
                Button("看完整的隐私说明") { showPrivacy = true }
                    .buttonStyle(.plain)
                    .font(.system(size: 10.5, weight: .medium))
                    .foregroundColor(Theme.ok)
            }
            Spacer(minLength: 0)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 9).fill(Theme.card.opacity(0.6)))
        .alert("隐私说明", isPresented: $showPrivacy) {
            Button("知道了", role: .cancel) { }
        } message: {
            Text("""
            这是第三方小工具，不是小米或酷态科官方出的。

            一、扫码时会做什么
            · 只向小米云要一张登录二维码，你在手机米家 App 里扫码、确认。
            · 账号、密码、验证码都在你手机上，本程序看不见，也拿不到。
            · 小米云只返回这台充电器的 Mac 和 12 字节 token。

            二、数据留在哪里
            · 只写进本机的 config.json（~/Library/Application Support/CuktechTray/）。
            · 登录会话 session.json 也留在同一目录，只是为了下次重新配置少扫一次码。

            三、不会做的事
            · 不上传任何数据；日常读功率走电脑和充电器之间的蓝牙，不经过网络。
            · 只有你点「检查更新」时才访问 GitHub 一次。

            四、想清干净
            · 删掉 CuktechTray 这个配置目录，本机就什么都不剩；蓝牙那头不留任何东西。
            """)
        }
    }

    /// 第一屏底部那张示意图：配好之后菜单栏长什么样。纯 SwiftUI 画的，不是截图。
    private var previewCard: some View {
        HStack(spacing: 10) {
            ZStack {
                RoundedRectangle(cornerRadius: 7).fill(Theme.ok.opacity(0.16))
                    .frame(width: 28, height: 28)
                Image(systemName: "bolt.fill")
                    .font(.system(size: 13))
                    .foregroundColor(Theme.ok)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text("配好之后")
                    .font(.system(size: 10))
                    .foregroundColor(Theme.dim)
                Text("菜单栏右上角直接显示总功率，点开是四路读数与开关")
                    .font(.system(size: 11))
                    .foregroundColor(Theme.fg)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            Text("32.2 W")
                .font(.system(size: 12, weight: .semibold, design: .monospaced))
                .foregroundColor(Theme.ok)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
                .background(RoundedRectangle(cornerRadius: 5).fill(Color.black.opacity(0.28)))
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 9).fill(Theme.card))
    }

    private func stepRow(_ index: String, _ title: String, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text(index)
                .font(.system(size: 10, weight: .semibold))
                .foregroundColor(Theme.background)
                .frame(width: 18, height: 18)
                .background(Circle().fill(Theme.ok))
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(Theme.fg)
                Text(text)
                    .font(.system(size: 10.5))
                    .foregroundColor(Theme.dim)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var manualForm: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("手动填（从 Windows 版 settings.json 抄也行）")
                .font(.system(size: 10.5, weight: .medium))
                .foregroundColor(Theme.fg)
            TextField("3C:CD:73:27:61:A0", text: $model.manualMac)
                .textFieldStyle(.plain)
                .font(.system(size: 11, design: .monospaced))
                .foregroundColor(Theme.fg)
                .padding(6)
                .background(RoundedRectangle(cornerRadius: 6).fill(Theme.card))
            TextField("24 位十六进制 token", text: $model.manualToken)
                .textFieldStyle(.plain)
                .font(.system(size: 11, design: .monospaced))
                .foregroundColor(Theme.fg)
                .padding(6)
                .background(RoundedRectangle(cornerRadius: 6).fill(Theme.card))
            Text("token 是设备凭据，别贴给别人。")
                .font(.system(size: 9.5))
                .foregroundColor(Theme.dim)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 8).fill(Theme.card))
    }

    // 第二屏
    private var login: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let img = model.qrImage {
                HStack(alignment: .top, spacing: 16) {
                    Image(nsImage: img)
                        .resizable()
                        .interpolation(.none)
                        .frame(width: 190, height: 190)
                        .padding(8)
                        .background(RoundedRectangle(cornerRadius: 10).fill(Color.white))

                    VStack(alignment: .leading, spacing: 8) {
                        Text("拿手机扫这个码")
                            .font(.system(size: 12, weight: .medium))
                            .foregroundColor(Theme.fg)
                        Text("用「米家」App 右上角的扫一扫。扫完手机上会问要不要登录，确认就行。")
                            .font(.system(size: 10.5))
                            .foregroundColor(Theme.dim)
                            .fixedSize(horizontal: false, vertical: true)
                        if model.busy {
                            HStack(spacing: 6) {
                                ProgressView().controlSize(.small)
                                Text("登录中…")
                                    .font(.system(size: 10.5))
                                    .foregroundColor(Theme.dim)
                            }
                        }
                    }
                    Spacer(minLength: 0)
                }
            } else {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(model.busy ? "正在取二维码…" : "没拿到码，点下面的「重新生成」再试一次")
                        .font(.system(size: 11))
                        .foregroundColor(Theme.dim)
                }
            }
        }
    }

    // 第三屏
    private var pick: some View {
        VStack(alignment: .leading, spacing: 8) {
            ScrollView {
                VStack(spacing: 6) {
                    ForEach(model.devices) { d in
                        deviceRow(d)
                    }
                }
            }
            .frame(maxHeight: 260)

            if model.busy {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("还在补设备信息…").font(.system(size: 10.5)).foregroundColor(Theme.dim)
                }
            }

            if !model.nearby.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    Text("附近蓝牙里扫到的")
                        .font(.system(size: 10))
                        .foregroundColor(Theme.dim)
                    Text(nearbyText)
                        .font(.system(size: 9.5, design: .monospaced))
                        .foregroundColor(Theme.dim)
                        .lineLimit(3)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
    }

    private var nearbyText: String {
        model.nearby.prefix(6).map { d in
            let tag = d.isCuktechCharger ? " 酷态科" : ""
            return d.macString + tag
        }.joined(separator: "  ·  ")
    }

    private func deviceRow(_ d: MiDevice) -> some View {
        let selected = model.selectedDID == d.did
        return Button {
            model.selectedDID = d.did
        } label: {
            HStack(spacing: 10) {
                Circle()
                    .fill(d.hasToken ? Theme.ok : Theme.bad)
                    .frame(width: 6, height: 6)
                VStack(alignment: .leading, spacing: 2) {
                    Text(d.name.isEmpty ? d.model : d.name)
                        .font(.system(size: 12, weight: selected ? .semibold : .regular))
                        .foregroundColor(Theme.fg)
                        .lineLimit(1)
                    Text(d.summary)
                        .font(.system(size: 9.5))
                        .foregroundColor(Theme.dim)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
                if d.isCuktech {
                    Text("酷态科")
                        .font(.system(size: 9))
                        .foregroundColor(Theme.ok)
                        .padding(.horizontal, 5)
                        .padding(.vertical, 2)
                        .background(RoundedRectangle(cornerRadius: 4).fill(Theme.ok.opacity(0.15)))
                }
            }
            .padding(.horizontal, 9)
            .padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 8).fill(selected ? Theme.cardOn : Theme.card))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(selected ? Theme.ok.opacity(0.7) : Color.clear, lineWidth: 1))
        }
        .buttonStyle(.plain)
    }

    // 第四屏
    private var done: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: "checkmark.circle.fill")
                    .font(.system(size: 22))
                    .foregroundColor(Theme.ok)
                Text("配好了")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundColor(Theme.fg)
            }
            Text("菜单栏上应该已经有数字了。点一下就展开四路读数和充电模式；右键有重新连接、开机启动这些。")
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
                .fixedSize(horizontal: false, vertical: true)
            Text(model.doneText)
                .font(.system(size: 10, design: .monospaced))
                .foregroundColor(Theme.dim.opacity(0.9))
                .lineLimit(2)
        }
    }

    // 底部按钮
    private var footer: some View {
        HStack(spacing: 12) {
            switch model.step {
            case .welcome:
                if model.showManual {
                    primary("保存") { model.submitManual() }
                    ghost("还是扫码吧") { model.showManual = false }
                } else {
                    primary("开始扫码登录") { model.beginLogin() }
                    ghost("手动填 MAC 和 token") { model.showManual = true }
                }
            case .login:
                ghost("重新生成二维码") { model.beginLogin() }
            case .pick:
                primary("用这台") { model.pickSelected() }
                ghost("重扫蓝牙") { model.rescan() }
            case .done:
                primary("完成") { model.close() }
            }
            Spacer()
            ghost("关闭") { model.close() }
        }
    }

    private func primary(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 11.5, weight: .medium))
                .foregroundColor(Theme.background)
                .padding(.horizontal, 13)
                .padding(.vertical, 6)
                .background(RoundedRectangle(cornerRadius: 7).fill(Theme.ok))
        }
        .buttonStyle(.plain)
        .disabled(model.busy)
    }

    private func ghost(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
        }
        .buttonStyle(.plain)
    }
}

// MARK: - 窗口

final class WizardController {
    static let shared = WizardController()

    private var window: NSWindow?
    private var model: WizardModel?

    /// 打开向导。已经有窗口就顶到前面，不重复开。
    static func show(settings: AppSettings, demo: String? = nil,
                     onDone: ((AppSettings) -> Void)? = nil) {
        shared.present(settings: settings, demo: demo, onDone: onDone)
    }

    static func closeAll() {
        shared.window?.orderOut(nil)
    }

    private func present(settings: AppSettings, demo: String?, onDone: ((AppSettings) -> Void)?) {
        if let win = window, win.isVisible {
            win.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let m = WizardModel(settings: settings)
        if let demo { m.loadDemo(demo) }
        m.onDone = onDone
        m.onClose = { [weak self] in self?.window?.orderOut(nil) }
        model = m

        let win = window ?? makeWindow()
        window = win
        win.contentView = NSHostingView(rootView: WizardView(model: m))
        win.center()
        win.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func makeWindow() -> NSWindow {
        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 480, height: 580),
                           styleMask: [.titled, .closable, .fullSizeContentView],
                           backing: .buffered,
                           defer: false)
        win.titleVisibility = .hidden
        win.titlebarAppearsTransparent = true
        win.isReleasedWhenClosed = false
        win.isMovableByWindowBackground = true
        win.level = .floating
        win.collectionBehavior = [.moveToActiveSpace]
        return win
    }
}
#endif