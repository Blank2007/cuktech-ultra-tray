import Foundation

/// 版本信息。**macOS 版从 0.1.0 起自己编号**，不再跟 Windows 版的 0.4.x 绑在一起：
/// 两个平台的改动节奏不一样（Windows 大改 UI 时 mac 可能只跟着修 bug），
/// 硬绑同一个号会让 changelog 变成两边互不相干的内容混在一条。
/// 产物文件名用 -win / -mac 区分平台，所以重号也不会混。
public enum BuildInfo {
    /// 0.1.1：修 macOS 上连不上设备（MiBeacon 广播解析把 `95 FE` 头当必需，而 CoreBluetooth 给的是裸 payload）。
    /// 0.1.2：面板可固定（不钉时点外面收起）、菜单栏图标模式、关于窗口 + 更新检查。
    /// 0.1.3：设备设置面板（屏幕/供电/定时）、单口采集改成严格 1 Hz。
    /// 0.1.4：读数间隔从「充电模式」标题右侧挪到页脚（原来挤在一行，像充电模式每秒自动切）。
    /// 0.1.5：修「断一次就再也连不上」——复用单个 CBCentralManager，并接管系统里的残留连接。
    /// 0.1.6：采集结果补平均/最大电压与电流（按时间加权，跨档与能量同口径），可对照电流表。
    public static let version = "0.1.6"
    public static let channel = "mac"
}
