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
    print("[selftest] privacyAccepted=\(settings.privacyAccepted)")
    exit(0)
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var settings: AppSettings
    private var controller: TrayController?

    init(settings: AppSettings) {
        self.settings = settings
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // 首次启动必须先同意隐私说明：不同意就当场退出、不写盘；同意才落 privacyAccepted 并继续启动。
        // --version / --selftest / --wizard-demo 在 main 里已经 exit 或者走了演示分支，进不到这里。
        if !settings.privacyAccepted {
            print("[privacy] 第一次启动，先问一句隐私说明")
            fflush(stdout)
            guard PrivacyGate.requireConsent() else {
                print("[privacy] 用户选择「不同意，退出」，本次不写任何配置")
                fflush(stdout)
                exit(0)
            }
            settings.privacyAccepted = true
            do {
                try settings.save()
                print("[privacy] 用户已同意，privacyAccepted=true 写进 \(AppSettings.fileURL.path)")
            } catch {
                print("[privacy] 用户已同意，但写配置失败：\(error)")
            }
            fflush(stdout)
        }

        let controller = TrayController(settings: settings)
        self.controller = controller
        controller.start()
        print("[tray] 菜单栏项已创建")
        fflush(stdout)
        // 配置不全（第一次跑，或者配置被删了）就直接把配对向导摆出来。
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
            self.controller?.showWizardIfNeeded()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        controller?.shutdown()
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)   // 菜单栏 app：不占 Dock、不进 Cmd-Tab

if let demoArg = args.first(where: { $0 == "--wizard-demo" || $0.hasPrefix("--wizard-demo=") }) {
    // 文档与 CI 截图用：把向导的某一屏摆出来（welcome / login / pick / done），
    // 填的是假数据，不联网、不碰蓝牙。
    let screen = demoArg.contains("=")
        ? String(demoArg.split(separator: "=", maxSplits: 1)[1])
        : "welcome"
    DispatchQueue.main.async {
        WizardController.show(settings: AppSettings(), demo: screen)
        print("[wizard] demo window shown (\(screen))")
        fflush(stdout)
    }
} else {
    // 正式启动路径：这里才可能弹「首次启动隐私说明」那道门（在 applicationDidFinishLaunching 里）。
    app.delegate = AppDelegate(settings: AppSettings.load())
}
app.run()