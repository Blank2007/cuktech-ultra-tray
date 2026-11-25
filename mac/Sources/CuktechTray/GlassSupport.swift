#if os(macOS)
import AppKit

/// 面板背景的「玻璃」底。
///
/// macOS 26（Tahoe）给 AppKit 加了 `NSGlassEffectView`，也就是系统说的 Liquid Glass；
/// 13~15 上没有这套 API，只能退回 `NSVisualEffectView` 的 popover 材质（毛玻璃）。
/// 这个文件只负责**背景**：布局、配色、控件一概不动，SwiftUI 那层仅把底色调薄，
/// 好让后面垫的这层透出来；玻璃关掉时底色保持原样，外观跟改动前完全一致。
///
/// 交互与设计思路参考了 a1287448852/CuktechMac10Ultra 的玻璃面板（MIT），实现是重写的。
enum GlassSupport {
    /// 这台机器有没有真的 Liquid Glass 可用。
    static var hasLiquidGlass: Bool {
        if #available(macOS 26.0, *) { return true }
        return false
    }

    /// SwiftUI 面板自己那层底色该有多不透明。
    /// 玻璃底下必须留缝，否则玻璃一点都看不出来；关掉玻璃时给 1.0，和老版本一模一样。
    static func backdropOpacity(glass: Bool) -> Double {
        guard glass else { return 1.0 }
        // 真玻璃可以透得更狠；毛玻璃那档糊得不够，多留一点底色保证字还是白的。
        return hasLiquidGlass ? 0.55 : 0.82
    }

    /// 垫在面板内容下面的背景层。
    static func makeBackdrop() -> PanelBackdropView {
        PanelBackdropView(frame: .zero)
    }
}

/// 面板宿主视图的背景层：26+ 是 `NSGlassEffectView`，13~15 是 `.popover` 材质的 `NSVisualEffectView`。
/// `setEnabled(false)` 时两层都藏起来，露出 SwiftUI 自己的实色底（= 改动前的样子）。
final class PanelBackdropView: NSView {
    /// 圆角跟 popover 自己的圆角差不多就够——外面那层系统 mask 会把多余的部分裁掉。
    private static let cornerRadius: CGFloat = 12

    /// 只存成 NSView，避免给整个类自找 availability 麻烦（26 以下这个值就是 nil）。
    private let glass: NSView?
    private let frost: NSVisualEffectView?

    override init(frame frameRect: NSRect) {
        if #available(macOS 26.0, *) {
            let g = NSGlassEffectView()
            g.style = .regular
            g.cornerRadius = PanelBackdropView.cornerRadius
            glass = g
            frost = nil
        } else {
            let v = NSVisualEffectView()
            v.material = .popover
            v.blendingMode = .behindWindow
            v.state = .active
            glass = nil
            frost = v
        }
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.cornerRadius = PanelBackdropView.cornerRadius
        layer?.masksToBounds = true
        if let glass { addSubview(glass) }
        if let frost { addSubview(frost) }
    }

    required init?(coder: NSCoder) { fatalError("面板背景不从 nib 加载") }

    override func layout() {
        super.layout()
        glass?.frame = bounds
        frost?.frame = bounds
    }

    /// 背景层不吃点击：面板上的按钮要照常收得到第一下。
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    func setEnabled(_ on: Bool) {
        glass?.isHidden = !on
        frost?.isHidden = !on
    }
}
#endif
