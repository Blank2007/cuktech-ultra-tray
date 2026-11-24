// swift-tools-version:5.9
import PackageDescription

// Mac 版工程：纯 SwiftPM，不依赖 Xcode 工程文件（CI 上 `swift build` 直接出二进制，
// .app 由 scripts/make-app.sh 组装）。
//
// 分层：
//   CuktechCore  —— 纯逻辑（加密、帧、认证状态机、扫码登录），不 import AppKit/CoreBluetooth，
//                   所以在 Linux runner 上也能 swift test。
//   CuktechBle   —— CoreBluetooth 适配层，把 CBPeripheral 包成 CuktechCore 需要的 transport。
//   CuktechTray  —— 可执行 target：NSStatusItem + SwiftUI 面板 + 向导。
let package = Package(
    name: "CuktechTrayMac",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "CuktechTray", targets: ["CuktechTray"]),
        .library(name: "CuktechCore", targets: ["CuktechCore"]),
    ],
    targets: [
        .target(name: "CuktechCore", path: "Sources/CuktechCore"),
        .target(name: "CuktechBle", dependencies: ["CuktechCore"], path: "Sources/CuktechBle"),
        .executableTarget(name: "CuktechTray", dependencies: ["CuktechCore", "CuktechBle"], path: "Sources/CuktechTray"),
        .testTarget(name: "CuktechCoreTests", dependencies: ["CuktechCore"], path: "Tests/CuktechCoreTests"),
    ]
)
