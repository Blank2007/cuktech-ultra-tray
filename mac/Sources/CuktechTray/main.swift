import AppKit
import CuktechCore

// mac 版入口。菜单栏 app：不占 Dock、不进 Cmd-Tab，只活在最上面的状态栏里。

let args = CommandLine.arguments

/// 文档截图用：造一份「44 分 53 秒、2689 次采样」的采集结果，不走蓝牙、不碰设备。
///
/// 数字是照着一次真实采集（C1 充手机，13.01Wh / 平均 17.6W / 峰值 21.8W）凑的：
/// 每 1 秒一个样本，电流在 1.62~2.24A 之间抖（平均 ≈1.93A），每 211 秒来一次 2.38A 的尖峰，
/// 最后 32 秒空载，这样结束原因正好是「这个口 30 秒没有输出」。
func captureDemoResult() -> CaptureResult {
    var acc = CaptureAccumulator()
    let start = Date().addingTimeInterval(-2693)
    var seed: UInt64 = 0x2545_F491_4F6C_DD1D
    func rnd() -> Double {
        seed ^= seed << 13
        seed ^= seed >> 7
        seed ^= seed << 17
        return Double(seed % 1000) / 1000.0
    }
    for i in 0..<2661 {
        var amps = 1.62 + 0.62 * rnd()
        if i % 211 == 0 { amps = 2.38 }
        acc.add(PortData(state: 0x01, code: 0, current: amps, voltage: 9.10),
                at: start.addingTimeInterval(Double(i)))
    }
    for i in 2661..<2693 {
        acc.add(PortData(state: 0x00, code: 0, current: 0, voltage: 0),
                at: start.addingTimeInterval(Double(i)))
    }
    return CaptureResult(portIndex: 1, startedAt: start,
                         endedAt: start.addingTimeInterval(2693),
                         stats: acc, reason: .noLoad, firmware: "2.1.2_0079")
}


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
        // 配置不全（第一次跑，或者配置被删了）就直接把配对向导摆出来。
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
            self.controller.showWizardIfNeeded()
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        controller.shutdown()
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)   // 菜单栏 app：不占 Dock、不进 Cmd-Tab
let controller = TrayController(settings: AppSettings.load())
let delegate = AppDelegate(controller: controller)

if args.contains("--capture-demo") {
    // 文档与 CI 截图用：摆出采集结果窗（假数据，不联网、不碰蓝牙）。
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
} else {
    app.delegate = delegate
}
app.run()