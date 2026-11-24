import AppKit
import CuktechCore

// M0 骨架：验证「CI 上能编译、能组装 .app、能起来、菜单栏项能出现」。
// 真正的面板/菜单/向导从 M3 开始长。

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var item: NSStatusItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let bar = NSStatusBar.system.statusItem(withLength: 64)
        bar.autosaveName = "cuktech.watts"
        bar.button?.attributedTitle = NSAttributedString(
            string: "0.0 W",
            attributes: [
                .font: NSFont.monospacedDigitSystemFont(ofSize: 11.5, weight: .semibold),
                .foregroundColor: NSColor.labelColor,
            ])
        bar.button?.toolTip = "Cuktech 10 Ultra"
        item = bar

        print("[m0] status item created, visible=\(bar.isVisible), version=\(BuildInfo.version)")
        fflush(stdout)
    }
}

let args = CommandLine.arguments

if args.contains("--version") {
    // 只打印版本号，给 scripts/make-app.sh 往 Info.plist 里填
    print(BuildInfo.version)
    exit(0)
}

if args.contains("--selftest") {
    print("[m0] selftest ok version=\(BuildInfo.version) channel=\(BuildInfo.channel)")
    exit(0)
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)   // 菜单栏 app：不占 Dock、不进 Cmd-Tab
let delegate = AppDelegate()
app.delegate = delegate
app.run()
