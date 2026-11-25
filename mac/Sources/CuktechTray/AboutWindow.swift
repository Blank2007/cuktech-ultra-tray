#if os(macOS)
import AppKit
import Combine
import SwiftUI
import CuktechCore

// MARK: - 更新检查

/// 从 GitHub Releases 里找**最新一个带 mac 资产的版本**。
///
/// 为什么不看 `tag_name`：这个仓库的 tag 跟的是 Windows 版的号（v0.4.6），
/// 而 mac 版自己编号（0.1.0），两者并行发布 —— 直接比 tag 会把每次启动都判成「有新版本」。
/// 真正的 mac 版本号只出现在资产名里：`CuktechTray-0.1.0-mac.zip`。
enum UpdateChecker {
    struct Release {
        var version: String      // 从资产名里抠出来的 mac 版本号
        var pageURL: URL?        // release 页面
        var assetURL: URL?       // 直接下 zip 的地址
        var publishedAt: String?
    }

    private static let releasesAPI = URL(string: "https://api.github.com/repos/Blank2007/cuktech-ultra-tray/releases?per_page=20")!

    /// 用自定义枚举而不是 `Result<_, String>`：Result 的失败类型必须实现 Error。
    enum Outcome {
        case found(Release)
        case failed(String)
    }

    static func latestMacRelease() async -> Outcome {
        var req = URLRequest(url: releasesAPI)
        req.timeoutInterval = 15
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("CuktechTray-mac/\(BuildInfo.version)", forHTTPHeaderField: "User-Agent")

        let data: Data
        let resp: URLResponse
        do {
            (data, resp) = try await URLSession.shared.data(for: req)
        } catch {
            return .failed("连不上 GitHub（\(error.localizedDescription)）")
        }
        guard let http = resp as? HTTPURLResponse else { return .failed("GitHub 返回了看不懂的东西") }
        if http.statusCode == 403 {
            // 未登录的 API 每小时 60 次，按 IP 算；把恢复时间直接告诉用户，别让人干等
            if let raw = http.value(forHTTPHeaderField: "x-ratelimit-reset"),
               let epoch = Double(raw), epoch > 0 {
                let f = DateFormatter()
                f.dateFormat = "HH:mm"
                let at = f.string(from: Date(timeIntervalSince1970: epoch))
                return .failed("GitHub 限流了（未登录每小时 60 次），\(at) 之后能再查")
            }
            return .failed("GitHub 限流了（每小时 60 次），过一会儿再试")
        }
        guard http.statusCode == 200 else { return .failed("GitHub 返回 \(http.statusCode)") }
        guard let list = (try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]] else {
            return .failed("解析 release 列表失败")
        }

        for rel in list {
            if (rel["draft"] as? Bool) == true { continue }
            guard let assets = rel["assets"] as? [[String: Any]] else { continue }
            for a in assets {
                guard let name = a["name"] as? String,
                      let ver = macVersion(inAssetName: name) else { continue }
                return .found(Release(
                    version: ver,
                    pageURL: (rel["html_url"] as? String).flatMap(URL.init(string:)),
                    assetURL: (a["browser_download_url"] as? String).flatMap(URL.init(string:)),
                    publishedAt: rel["published_at"] as? String
                ))
            }
        }
        return .failed("还没有发过 mac 版")
    }

    /// `CuktechTray-0.1.0-mac.zip` → `0.1.0`
    static func macVersion(inAssetName name: String) -> String? {
        guard name.hasSuffix("-mac.zip") else { return nil }
        let prefix = "CuktechTray-"
        guard let r = name.range(of: prefix), r.lowerBound == name.startIndex else { return nil }
        let rest = name[name.index(name.startIndex, offsetBy: prefix.count)...]
        let ver = rest.replacingOccurrences(of: "-mac.zip", with: "")
        return ver.first?.isNumber == true ? ver : nil
    }

    /// `remote` 是否比 `local` 新。
    static func isNewer(_ remote: String, than local: String) -> Bool {
        let a = parts(remote), b = parts(local)
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    private static func parts(_ v: String) -> [Int] {
        v.split(separator: ".").map { Int($0.prefix(while: \.isNumber)) ?? 0 }
    }
}

// MARK: - 数据源

final class AboutModel: ObservableObject {
    enum State {
        case idle
        case checking
        case upToDate(String)
        case newer(UpdateChecker.Release)
        case failed(String)
    }

    @Published var state: State = .idle

    /// 每次打开窗口都回到初始态：窗口是「点了才展开」的，留着上次的结果会一开窗就是大窗。
    func reset() {
        if case .checking = state { return }   // 正在查就别打断，查完照常显示
        state = .idle
    }

    /// 只有点「检查更新」按钮才走这里。打开窗口本身不发请求。
    func check() {
        if case .checking = state { return }
        state = .checking
        Task {
            let result = await UpdateChecker.latestMacRelease()
            await MainActor.run {
                switch result {
                case .found(let rel):
                    if UpdateChecker.isNewer(rel.version, than: BuildInfo.version) {
                        self.state = .newer(rel)
                    } else {
                        // 相等 → 已是最新；本地更大 → 自己编的开发版
                        self.state = .upToDate(rel.version)
                    }
                case .failed(let msg):
                    self.state = .failed(msg)
                }
            }
        }
    }
}

// MARK: - 界面

struct AboutView: View {
    @ObservedObject var model: AboutModel

    private var version: String { BuildInfo.version }

    /// 检查之前窗口只放标题和按钮；结果出来后才把状态行撑出来（窗口随之长高）。
    private var showsStatus: Bool {
        if case .idle = model.state { return false }
        return true
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 12) {
                Image(nsImage: NSApp.applicationIconImage ?? NSImage())
                    .resizable()
                    .frame(width: 52, height: 52)
                VStack(alignment: .leading, spacing: 3) {
                    Text("酷态科功率表")
                        .font(.system(size: 15, weight: .semibold))
                        .foregroundColor(Theme.fg)
                    Text("mac 版 \(version)")
                        .font(.system(size: 11, design: .monospaced))
                        .foregroundColor(Theme.dim)
                    Text("菜单栏里看四路功率，不用开米家")
                        .font(.system(size: 11))
                        .foregroundColor(Theme.dim)
                }
                Spacer(minLength: 0)
            }

            Divider().opacity(0.25).padding(.vertical, 12)

            if showsStatus {
                statusRow
                    .padding(.bottom, 12)
            }

            HStack(spacing: 8) {
                pill("GitHub 仓库", prominent: false) {
                    if let u = URL(string: "https://github.com/Blank2007/cuktech-ultra-tray") {
                        NSWorkspace.shared.open(u)
                    }
                }
                pill(checkingTitle, prominent: false) { model.check() }
                    .disabled(isChecking)
                Spacer()
            }
        }
        // 不给固定高度：高度由内容决定，AboutController 会跟着窗口内容大小重设 frame。
        .padding(18)
        .frame(width: 380)
        .background(Theme.background)
        .preferredColorScheme(.dark)
    }

    private var isChecking: Bool {
        if case .checking = model.state { return true }
        return false
    }

    private var checkingTitle: String {
        isChecking ? "检查中…" : "检查更新"
    }

    @ViewBuilder
    private var statusRow: some View {
        switch model.state {
        case .idle, .checking:
            // .idle 不会走到这里；.checking 显示转圈
            HStack(spacing: 6) {
                ProgressView().controlSize(.mini)
                Text("正在问 GitHub…")
                    .font(.system(size: 10.5))
                    .foregroundColor(Theme.dim)
            }
        case .upToDate(let remote):
            if remote == version {
                label("已是最新（\(remote)）", color: Theme.ok)
            } else {
                label("线上最新 mac 版是 \(remote)，你这份 \(version) 是开发版", color: Theme.ok)
            }
        case .newer(let rel):
            HStack(spacing: 8) {
                label("有新版本 \(rel.version)", color: Theme.ok)
                pill("下载", prominent: true) {
                    if let u = rel.assetURL ?? rel.pageURL { NSWorkspace.shared.open(u) }
                }
                if rel.assetURL != nil, let page = rel.pageURL {
                    pill("看说明", prominent: false) { NSWorkspace.shared.open(page) }
                }
                Spacer()
            }
        case .failed(let msg):
            label(msg, color: Theme.bad)
        }
    }

    private func label(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.system(size: 10.5))
            .foregroundColor(color)
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
    }

    private func pill(_ title: String, prominent: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 11, weight: prominent ? .semibold : .regular))
                .foregroundColor(prominent ? Theme.fg : Theme.dim)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(
                    RoundedRectangle(cornerRadius: 7)
                        .fill(prominent ? Theme.cardOn : Theme.card)
                )
        }
        .buttonStyle(.plain)
    }
}

// MARK: - 窗口

/// 菜单栏 App 的窗口：窗口弹出来时 App 可能还没激活（用户正忙着别的 App），
/// 默认「第一次点击只激活窗口」会让按钮像点了没反应，所以放行首次点击。
final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

final class AboutController: NSObject, NSWindowDelegate {
    static let shared = AboutController()

    private var window: NSWindow?
    private var hosting: NSHostingView<AboutView>?
    private let model = AboutModel()
    private var stateObserver: AnyCancellable?

    func show() {
        let firstTime = window == nil
        let win = window ?? makeWindow()
        window = win
        model.reset()
        // 第一次居中；之后沿用用户拖到的位置，别再把人窗口拽回去
        if firstTime { win.center() }
        win.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        // 打开窗口**不**联网：GitHub 未登录的 API 每小时只有 60 次（按 IP 算），
        // 自动查会让「只是看一眼版本号」也吃配额，还容易撞上限流。
        // 想查就点「检查更新」。
        fitWindow(animated: false)
    }

    private func makeWindow() -> NSWindow {
        let host = FirstMouseHostingView(rootView: AboutView(model: model))
        hosting = host

        // 先按内容算一次，省得先弹一个大空窗再缩回去。
        host.layoutSubtreeIfNeeded()
        let fit = host.fittingSize
        let size = NSSize(width: max(fit.width, 380), height: max(fit.height, 1))

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

        // 状态一变（开始查 → 出结果）就把窗口高度改成刚好装下内容。
        stateObserver = model.$state
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in
                // 再等一拍：SwiftUI 先把新内容排好版，fittingSize 才是新高度
                DispatchQueue.main.async { self?.fitWindow(animated: true) }
            }

        return win
    }

    /// 让窗口高度贴合内容，且**顶边不动**（向上长高，而不是往下坠）。
    ///
    /// 用绝对高度而不是增量：`fullSizeContentView` 下 frame 高度就是内容高度，
    /// 直接对齐 `fittingSize` 不会有累积误差；再手动把左上角放回原处。
    private func fitWindow(animated: Bool) {
        guard let win = window, let host = hosting else { return }
        host.layoutSubtreeIfNeeded()
        let needed = host.fittingSize.height
        guard needed > 1 else { return }
        guard abs(needed - win.frame.height) > 0.5 else { return }

        let anchor = NSPoint(x: win.frame.minX, y: win.frame.maxY)
        win.setFrame(NSRect(x: anchor.x, y: anchor.y - needed, width: 380, height: needed),
                     display: true, animate: animated)
    }
}
#endif
