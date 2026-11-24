import Foundation
import CuktechCore

/// M0 占位。M2 起这里换成 CBLink：CBCentralManager 扫描 → 解 0xFE95 广播匹配 MAC →
/// 连接 → 发现 MiService 特征 → 收发队列，对上 CuktechCore 的 MibleTransport 协议。
public enum BleLayer {
    public static let ready = false
}
