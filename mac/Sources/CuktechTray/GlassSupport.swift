#if os(macOS)
import AppKit

/// 面板背景的「玻璃」底。
///
/// 只负责**背景**：布局、配色、控件一概不动，SwiftUI 那层仅把底色调薄一点，
/// 好让后面垫的这层透出来；玻璃关掉时底色保持原样，外观跟没有这个效果时完全一致。
///
/// 0.1.7 改了实现（issue #6，@editwe 报的）：0.1.6 在 macOS 26+ 用了 `NSGlassEffectView`，
/// 这个视图塞进 `NSPopover` 的普通容器里拿不到窗口背景采样，只剩一层中性半透明玻璃；
/// 再把 SwiftUI 底色压到 0.55，深色底被背后的浅色窗口冲成中灰，`.secondary` 灰字基本看不见了。
/// 现在**统一退回 `NSVisualEffectView`（.popover 材质 + behindWindow）**：13~15 一直是这条路径，
/// 实测对比度正常；26/27 上也一样能正确采样背后的窗口。底色保证不低于 0.9，次要文字才留得住对比度。
///
/// 交互与设计思路参考了 a1287448852/CuktechMac10Ultra 的玻璃面板（MIT），实现是重写的。
enum GlassSupport {
    /// SwiftUI 面板自己那层底色该有多不透明。
    /// 玻璃底下必须留缝，否则玻璃一点都看不出来；但缝隙大了背后的内容就会把深色底冲淡，
    /// 所以 0.1.7 起固定在 0.9（0.1.6 在 26+ 上用的是 0.55，字直接糊掉了）。
    static func backdropOpacity(glass: Bool) -> Double {
        guard glass else { return 1.0 }
        return 0.9
    }

    /// 垫在面板内容下面的背景层。
    static func makeBackdrop() -> PanelBackdropView {
        PanelBackdropView(frame: .zero)
    }
}

/// 面板宿主视图的背景层：一颗 `.popover` 材质 + `behindWindow` 采样的 `NSVisualEffectView`。
/// `setEnabled(false)` 时藏起来，露出 SwiftUI 自己的实色底（= 没有玻璃的样子）。
///
/// 0.1.6 在 macOS 26+ 上换成了 `NSGlassEffectView`，结果在 `NSPopover` 里不采样窗口背景、
/// 把面板冲成中灰（issue #6），0.1.7 起不再用它。
final class PanelBackdropView: NSView {
    /// 圆角跟 popover 自己的圆角差不多就够——外面那层系统 mask 会把多余的部分裁掉。
    private static let cornerRadius: CGFloat = 12

    private let frost: NSVisualEffectView

    override init(frame frameRect: NSRect) {
        let v = NSVisualEffectView()
        v.material = .popover
        v.blendingMode = .behindWindow
        v.state = .active
        frost = v
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.cornerRadius = PanelBackdropView.cornerRadius
        layer?.masksToBounds = true
        addSubview(frost)
    }

    required init?(coder: NSCoder) { fatalError("面板背景不从 nib 加载") }

    override func layout() {
        super.layout()
        frost.frame = bounds
    }

    /// 背景层不吃点击：面板上的按钮要照常收得到第一下。
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    func setEnabled(_ on: Bool) {
        frost.isHidden = !on
    }
}
#endif
