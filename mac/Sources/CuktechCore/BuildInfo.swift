import Foundation

/// 版本信息。与 Windows 版共用同一个版本号（0.4.5 → 0.4.6 → …），
/// 产物文件名用 -win / -mac 区分平台。
public enum BuildInfo {
    public static let version = "0.4.5"
    public static let channel = "mac"
}
