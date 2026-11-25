#if os(macOS)
import AppKit
import SwiftUI
import CuktechCore

/// 隐私说明的正文。首次启动那道理同意门（`PrivacyGate`）和配对向导里的「看完整的隐私说明」用的是
/// 同一份文字；内容与 Windows 版向导、README 的「九、隐私」一节对齐，别单独改一份。
///
/// 说明：macOS 版正文里的路径是 ~/Library/Application Support/CuktechTray/，
/// Windows 版是 %LOCALAPPDATA%\CuktechTray\，除此之外四段结构、措辞完全一致。
enum PrivacyText {
    static let intro = "这是第三方小工具，不是小米或酷态科官方出的。"

    struct Section {
        let title: String
        let lines: [String]
    }

    static let sections: [Section] = [
        Section(title: "一、扫码时会做什么", lines: [
            "只向小米云要一张登录二维码，你在手机米家 App 里扫码、确认。",
            "账号、密码、验证码都在你手机上，本程序看不见，也拿不到。",
            "小米云只返回这台充电器的 Mac 和 12 字节 token。",
        ]),
        Section(title: "二、数据留在哪里", lines: [
            "只写进本机的 config.json（~/Library/Application Support/CuktechTray/）。",
            "登录会话 session.json 也留在同一目录，只是为了下次重新配置少扫一次码。",
        ]),
        Section(title: "三、不会做的事", lines: [
            "不上传任何数据；日常读功率走电脑和充电器之间的蓝牙，不经过网络。",
            "只有你点「检查更新」时才访问 GitHub 一次。",
        ]),
        Section(title: "四、想清干净", lines: [
            "删掉 CuktechTray 这个配置目录，本机就什么都不剩；蓝牙那头不留任何东西。",
        ]),
    ]

    /// 拼成一段纯文本（README、日志、--selftest 之外的展示用）。
    static var plain: String {
        var out = intro
        for s in sections {
            out += "\n\n" + s.title
            for line in s.lines { out += "\n· " + line }
        }
        return out
    }
}

/// 首次启动的必答门。跟向导同一套样子：深色底、绿色主按钮、暗色次要按钮。
struct PrivacyView: View {
    let onAgree: () -> Void
    let onDecline: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().opacity(0.25).padding(.vertical, 10)
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    ForEach(0..<PrivacyText.sections.count, id: \.self) { i in
                        let section = PrivacyText.sections[i]
                        VStack(alignment: .leading, spacing: 4) {
                            Text(section.title)
                                .font(.system(size: 12, weight: .medium))
                                .foregroundColor(Theme.fg)
                            ForEach(0..<section.lines.count, id: \.self) { j in
                                HStack(alignment: .top, spacing: 6) {
                                    Text("·")
                                        .font(.system(size: 10.5))
                                        .foregroundColor(Theme.ok.opacity(0.85))
                                    Text(section.lines[j])
                                        .font(.system(size: 10.5))
                                        .foregroundColor(Theme.dim)
                                        .fixedSize(horizontal: false, vertical: true)
                                }
                            }
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            Spacer(minLength: 10)
            footer
        }
        .padding(.horizontal, 22)
        .padding(.top, 20)
        .padding(.bottom, 16)
        .frame(width: 480, height: 560)
        .background(Theme.background)
        .preferredColorScheme(.dark)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 8) {
                Image(systemName: "lock.shield")
                    .font(.system(size: 14))
                    .foregroundColor(Theme.ok)
                Text("先看一眼隐私说明")
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundColor(Theme.fg)
            }
            Text("这是第一次启动，请你确认下面四段。这里是纯本地的小工具，不联网读数据。")
                .font(.system(size: 11))
                .foregroundColor(Theme.dim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("点「同意并继续」才会继续启动；点「不同意，退出」本程序直接退出，不写任何文件。")
                .font(.system(size: 9.5))
                .foregroundColor(Theme.dim.opacity(0.9))
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 12) {
                Button(action: onAgree) {
                    Text("同意并继续")
                        .font(.system(size: 11.5, weight: .medium))
                        .foregroundColor(Theme.background)
                        .padding(.horizontal, 13)
                        .padding(.vertical, 6)
                        .background(RoundedRectangle(cornerRadius: 7).fill(Theme.ok))
                }
                .buttonStyle(.plain)
                .keyboardShortcut(.defaultAction)

                Button(action: onDecline) {
                    Text("不同意，退出")
                        .font(.system(size: 11))
                        .foregroundColor(Theme.dim)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 6)
                }
                .buttonStyle(.plain)
                Spacer(minLength: 0)
            }
        }
    }
}

/// 同步弹一个模态窗口，返回用户是否点了「同意并继续」。
/// 只从正式启动路径（main.swift 的 `app.delegate = …` 那条）调用；--version / --selftest /
/// --wizard-demo 这些非交互参数在进到它之前就已经 exit 或走演示分支，所以不会被这道门挡住。
final class PrivacyGate: NSObject, NSWindowDelegate {
    private var window: NSWindow?

    static func requireConsent() -> Bool {
        PrivacyGate().run()
    }

    private func run() -> Bool {
        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 480, height: 560),
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
        win.contentView = NSHostingView(rootView: PrivacyView(
            onAgree: { NSApp.stopModal(withCode: .OK) },
            onDecline: { NSApp.stopModal(withCode: .cancel) }
        ))
        win.center()
        window = win
        win.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        let response = NSApp.runModal(for: win)
        win.orderOut(nil)
        window = nil
        return response == .OK
    }

    /// 关窗口按钮不让点：要退出请走「不同意，退出」，免得留下「既没同意也没退出」的中间态。
    func windowShouldClose(_ sender: NSWindow) -> Bool { false }
}
#endif
