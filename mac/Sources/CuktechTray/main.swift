import AppKit
import CuktechCore

// mac 版入口。菜单栏 app：不占 Dock、不进 Cmd-Tab，只活在最上面的状态栏里。

let args = CommandLine.arguments

if args.contains("--version") {
    // 只打印版本号，给 scripts/make-app.sh 往 Info.plist 里填
    print(BuildInfo.version)
    exit(0)
}

if args.contains("--selftest") {
    // CI 里跑：验证二进制能起、配置路径可算、配置解析不炸。
    let settings = AppSettings.load()
    print("[selftest] selftest ok version=\(BuildInfo.version) channel=\(BuildInfo.channel)")
    print("[selftest] config=\(AppSettings.fileURL.path)")
    print("[selftest] paired=\(settings.isComplete)")
    print("[selftest] mac=\(settings.mac.isEmpty ? "(未设置)" : settings.mac)")
    print("[selftest] tokenBytes=\(settings.tokenBytes?.count ?? 0)")
    exit(0)
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let controller: TrayController

    init(controller: TrayController) {
        self.controller = controller
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        controller.start()
        print("[tray] 菜单栏项已创建")
        fflush(stdout)
    }

    func applicationWillTerminate(_ notification: Notification) {
        controller.shutdown()
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)   // 菜单栏 app：不占 Dock、不进 Cmd-Tab
let controller = TrayController(settings: AppSettings.load())
let delegate = AppDelegate(controller: controller)
app.delegate = delegate
app.run()
