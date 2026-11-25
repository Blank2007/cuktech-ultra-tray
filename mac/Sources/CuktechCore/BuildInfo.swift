import Foundation

/// 版本信息。**macOS 版从 0.1.0 起自己编号**，不再跟 Windows 版的 0.4.x 绑在一起：
/// 两个平台的改动节奏不一样（Windows 大改 UI 时 mac 可能只跟着修 bug），
/// 硬绑同一个号会让 changelog 变成两边互不相干的内容混在一条。
/// 产物文件名用 -win / -mac 区分平台，所以重号也不会混。
public enum BuildInfo {
    /// 0.1.1：修 macOS 上连不上设备（MiBeacon 广播解析把 `95 FE` 头当必需，而 CoreBluetooth 给的是裸 payload）。
    /// 0.1.2：C3 与 A 合并成一路功率计时不再把另一路显示成「空载」；首次启动要先同意隐私说明；
    ///        合并 PR #3（端口开关索引错位、BLE 并发命令串行化、断连占线、浅色状态栏）。
    public static let version = "0.1.2"
    public static let channel = "mac"
}
