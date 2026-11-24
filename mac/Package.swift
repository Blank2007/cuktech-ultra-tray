// swift-tools-version:5.9
import PackageDescription

// Mac 版工程：纯 SwiftPM，不依赖 Xcode 工程文件（CI 上 `swift build` 直接出二进制，
// .app 由 scripts/make-app.sh 组装）。
//
// 分层：
//   CuktechCore  —— 纯逻辑（AES/CCM、帧、认证状态机、扫码登录），不 import AppKit/CoreBluetooth，
//                   所以 Linux runner 上也能 swift test（免费的 ubuntu job 就是干这个的）。
//   CuktechBle   —— CoreBluetooth 适配层，把 CBPeripheral 包成 CuktechCore 需要的 transport。
//   CuktechTray  —— 可执行 target：NSStatusItem + SwiftUI 面板 + 向导。
//
// 注意：CuktechBle / CuktechTray 只在 macOS 上声明。否则 ubuntu job 里 `swift test`
// 会连可执行 target 一起编译，然后在 `import AppKit` 上炸掉（Linux 没有 AppKit）。
//
// 加密用 swift-crypto（Apple 维护的开源实现，API 与 CryptoKit 同形）而不是 CryptoKit：
// CryptoKit 只在 Apple 平台存在，Linux 上没有。
var products: [Product] = [
    .library(name: "CuktechCore", targets: ["CuktechCore"]),
]
var targets: [Target] = [
    .target(
        name: "CuktechCore",
        dependencies: [.product(name: "Crypto", package: "swift-crypto")],
        path: "Sources/CuktechCore"
    ),
    .testTarget(name: "CuktechCoreTests", dependencies: ["CuktechCore"], path: "Tests/CuktechCoreTests"),
]

#if os(macOS)
products.append(.executable(name: "CuktechTray", targets: ["CuktechTray"]))
targets.append(.target(name: "CuktechBle", dependencies: ["CuktechCore"], path: "Sources/CuktechBle"))
targets.append(.executableTarget(name: "CuktechTray", dependencies: ["CuktechCore", "CuktechBle"], path: "Sources/CuktechTray"))
#endif

let package = Package(
    name: "CuktechTrayMac",
    platforms: [.macOS(.v13)],
    products: products,
    dependencies: [
        .package(url: "https://github.com/apple/swift-crypto.git", .upToNextMajor(from: "3.0.0")),
    ],
    targets: targets
)
