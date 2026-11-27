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

/// `--capture-demo` 用的假数据：一段 9.1V 恒压、1.62~2.24A 抖动的带载，
/// 中间每 211 个样本插一次 2.38A 的浪峰（这样「平均/最大」看得出差别），
/// 尾巴再补 32 个空载样本（state 0x00），模拟「这个口没输出了 → 采集结束」。
/// 只用来截图和肉眼检查结果窗，不碰蓝牙、不联网、不落盘。
func captureDemoResult() -> CaptureResult {
    var acc = CaptureAccumulator()
    // xorshift64：要的是「每次跑都一样」的可复现抖动，不是真随机数。
    var seed: UInt64 = 0x2545_F491_4F6C_DD1D
    func nextUnit() -> Double {
        seed ^= seed << 13
        seed ^= seed >> 7
        seed ^= seed << 17
        return Double(seed % 1000) / 1000.0
    }
    let start = Date().addingTimeInterval(-2692)
    for i in 0..<2661 {
        let current = i % 211 == 0 ? 2.38 : (1.62 + nextUnit() * 0.62)
        acc.add(PortData(state: 0x01, code: 2, current: current, voltage: 9.10),
                at: start.addingTimeInterval(Double(i)))
    }
    for i in 2661..<2693 {
        acc.add(PortData(state: 0x00, code: 0, current: 0, voltage: 0),
                at: start.addingTimeInterval(Double(i)))
    }
    return CaptureResult(portIndex: 1,
                         startedAt: start,
                         endedAt: start.addingTimeInterval(2692),
                         stats: acc,
                         reason: .noLoad,
                         firmware: "2.1.2_0079")
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)   // 菜单栏 app：不占 Dock、不进 Cmd-Tab

if args.contains("--capture-demo") {
    // 文档与 CI 截图用：直接摆一个「已经采集完」的结果窗，样本是造出来的。
    DispatchQueue.main.async {
        CaptureResultController.shared.show(captureDemoResult())
        print("[capture] demo window shown")
        fflush(stdout)
    }
} else if let demoArg = args.first(where: { $0 == "--wizard-demo" || $0.hasPrefix("--wizard-demo=") }) {
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
} else if let panelArg = args.first(where: { $0 == "--panel-demo" || $0.hasPrefix("--panel-demo=") }) {
    // 文档与 CI 截图用：把面板的某一页摆出来（read / control / mode / chart / chart-solo），
    // 数据是造的，不联网、不碰蓝牙。
    let name = panelArg.contains("=")
        ? String(panelArg.split(separator: "=", maxSplits: 1)[1])
        : "read"
    let page = PanelTab.demo(name) ?? .read
    DispatchQueue.main.async {
        PanelDemoController.shared.show(page: page, soloC3: name == "chart-solo")
        print("[panel] demo window shown (\(name))")
        fflush(stdout)
    }
} else {
    // 正式启动路径：这里才可能弹「首次启动隐私说明」那道门（在 applicationDidFinishLaunching 里）。
    app.delegate = AppDelegate(settings: AppSettings.load())
}
app.run()