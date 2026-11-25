#if os(macOS)
import AppKit
import SwiftUI
import CuktechCore

/// 采集结束后的结果面板。
/// 数据只活在这一次显示里：关掉窗口就没了，不写文件、也不进日志。
struct CaptureResultView: View {
    let result: CaptureResult
    var onClose: () -> Void

    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline) {
                Text("\(result.portName) 采集结果")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundColor(Theme.fg)
                Spacer(minLength: 8)
                Text(result.reason.shortText)
                    .font(.system(size: 9.5))
                    .foregroundColor(reasonColor)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(RoundedRectangle(cornerRadius: 5).fill(Theme.card))
            }

            Text(subtitle)
                .font(.system(size: 10.5))
                .foregroundColor(Theme.dim)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 3)

            Divider().opacity(0.25).padding(.vertical, 11)

            Text("充入电量")
                .font(.system(size: 9.5))
                .foregroundColor(Theme.dim)

            HStack(alignment: .firstTextBaseline, spacing: 5) {
                Text(result.energyText)
                    .font(.system(size: 32, weight: .semibold, design: .monospaced))
                    .foregroundColor(Theme.fg)
                Text("Wh")
                    .font(.system(size: 12, weight: .medium, design: .monospaced))
                    .foregroundColor(Theme.dim)
                Spacer(minLength: 0)
                VStack(alignment: .trailing, spacing: 1) {
                    Text("平均功率")
                        .font(.system(size: 9.5))
                        .foregroundColor(Theme.dim)
                    Text("\(result.averageText) W")
                        .font(.system(size: 15, weight: .semibold, design: .monospaced))
                        .foregroundColor(Theme.color(WattStyle.total(result.stats.averagePower)))
                }
                .padding(.leading, 10)
            }

            Divider().opacity(0.25).padding(.vertical, 11)

            VStack(spacing: 5) {
                row("有效时长", "\(result.activeDurationText)（总 \(result.totalDurationText)）")
                row("峰值功率", "\(result.peakText) W")
                row("电压区间", voltageLine)
                row("采样次数", "\(result.stats.sampleCount) 次")
            }

            Text("采集结果仅供参考 · 仅本次显示，不写入磁盘")
                .font(.system(size: 9.5))
                .foregroundColor(Theme.dim.opacity(0.85))
                .padding(.top, 10)

            HStack(spacing: 8) {
                Spacer(minLength: 0)
                pill(copied ? "已复制" : "复制摘要", prominent: false) {
                    let pb = NSPasteboard.general
                    pb.clearContents()
                    pb.setString(result.summaryText, forType: .string)
                    copied = true
                }
                pill("好", prominent: true) { onClose() }
            }
            .padding(.top, 10)
        }
        .padding(16)
        .frame(width: 320)
        .background(Theme.background)
        .preferredColorScheme(.dark)
    }

    private var reasonColor: Color {
        switch result.reason {
        case .disconnected: return Theme.bad
        case .noData: return Color(red: 251 / 255, green: 191 / 255, blue: 36 / 255)
        case .noLoad, .stopped: return Theme.dim
        }
    }

    private var subtitle: String {
        var parts = ["酷态科 10 号 Ultra"]
        if let fw = result.firmware, !fw.isEmpty { parts.append(fw) }
        parts.append(result.reason.text)
        return parts.joined(separator: " · ")
    }

    /// 电压区间后面补一句换档次数——设备切换档位时是按段积分算的，这里直接告诉用户跳了几次。
    private var voltageLine: String {
        let n = result.stats.voltageChanges
        return n > 0 ? "\(result.voltageText) · 换档 \(n) 次" : result.voltageText
    }

    private func row(_ name: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(name)
                .font(.system(size: 10.5))
                .foregroundColor(Theme.dim)
            Spacer(minLength: 0)
            Text(value)
                .font(.system(size: 10.5, design: .monospaced))
                .foregroundColor(Theme.fg)
        }
    }

    private func pill(_ title: String, prominent: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.system(size: 11, weight: prominent ? .medium : .regular))
                .foregroundColor(prominent ? Theme.fg : Theme.dim)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(
                    RoundedRectangle(cornerRadius: 7)
                        .fill(prominent ? Theme.cardOn : Theme.card)
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

// MARK: - 窗口

final class CaptureResultController {
    static let shared = CaptureResultController()

    private var window: NSWindow?

    func show(_ result: CaptureResult) {
        window?.orderOut(nil)

        let win = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 320, height: 300),
                           styleMask: [.titled, .closable, .fullSizeContentView],
                           backing: .buffered,
                           defer: false)
        win.titleVisibility = .hidden
        win.titlebarAppearsTransparent = true
        win.isReleasedWhenClosed = false
        win.isMovableByWindowBackground = true
        win.level = .floating
        win.collectionBehavior = [.moveToActiveSpace]

        let host = FirstMouseHostingView(rootView: CaptureResultView(result: result) { [weak win] in
            win?.orderOut(nil)
        })
        // 必须先挂到窗口上再量 fittingSize：没进窗口时 SwiftUI 量出来的高度偏小，
        // 窗口会矮一截，内容被压扁/裁掉（第一版就踩了这个）。
        win.contentView = host
        host.layoutSubtreeIfNeeded()
        let fit = host.fittingSize
        if fit.height > 1 {
            win.setContentSize(NSSize(width: max(320, fit.width), height: fit.height))
        }
        win.center()
        NSApp.activate(ignoringOtherApps: true)
        win.makeKeyAndOrderFront(nil)

        window = win
    }
}
#endif
