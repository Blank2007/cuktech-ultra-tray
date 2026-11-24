# CUKTECH 10 Ultra · macOS 菜单栏功率表

macOS 版的「酷态科 10 号 Ultra」功率表：一个菜单栏小工具，蓝牙直连充电器，
状态栏上直接显示四口总功率，点开看四路电压/电流/功率和充电模式，还能直接断电。

纯 Swift 写的（AppKit + SwiftUI + CoreBluetooth），不需要 Electron、不需要装运行时。
目标系统 macOS 13 或更新（Apple Silicon 与 Intel 都行）。

---

## 和 Windows 版的关系

同一套协议、同一套取色规则、同样的数字格式，两边都是从一个充电器上读出来的：

| | Windows 版 | macOS 版 |
|---|---|---|
| 语言 / 技术 | C# + NativeAOT，自绘 Win32 界面 | Swift + AppKit/SwiftUI |
| 常驻位置 | 托盘图标 + 任务栏数字 | 状态栏（menu bar）文字 |
| 找设备 | 按 MAC 直连（Windows 缓存里有） | 扫 FE95 广播反解 MAC 再连 |
| 配置 | settings.json | ~/Library/Application Support/CuktechTray/config.json |
| 配对 | 扫码登录小米云 → 取 token | 一样（向导四屏） |
| 体积 | 约 10.4 MB 单文件 exe | 约 1 MB 的 .app |

两者的配置可以互相抄：Windows 版 settings.json 里的 `Mac` 与 `TokenHex` 就是 macOS 版 config.json 里的 `mac` 与 `tokenHex`。

---

## 安装

下载 `CuktechTray-<版本>-mac.zip`，解压出 `CuktechTray.app`，拖进「应用程序」。

这个 .app 是 **ad-hoc 签名**（本仓库没有 Apple 开发者账号，没做公证），第一次打开时
macOS 会拦一下：

1. 右键点 `CuktechTray.app` → 打开，或者双击后在弹窗里点「取消」；
2. 进 **系统设置 → 隐私与安全性**，往下滚到「安全性」，点「仍要打开」；
3. 再双击一次就开了。

如果是从命令行解压的，也可以直接去掉隔离属性：

```bash
xattr -dr com.apple.quarantine /Applications/CuktechTray.app
```

第一次启动会问蓝牙权限，必须给，否则只能显示「未配对」。

---

## 第一次用：扫一次码

菜单栏出现 `未配对` 时会自动弹向导：

1. **认路** —— 说明为什么要扫码：充电器的 token 只在你自己小米账号的设备清单里；
2. **扫码** —— 用「米家」App 扫码登录（只读设备清单，token 存在本机）；
3. **挑设备** —— 列表里带「酷态科」的排最前，选这台 10 号 Ultra；
4. **完成** —— 状态栏立刻开始显示功率。

如果云端没给蓝牙地址（有些账号的 `mac` 字段是空的），向导会顺手扫一遍附近的
FE95 广播，用广播里反解出来的地址补上。实在不行还有「手动填 MAC 和 token」的兜底，
从 Windows 版的 settings.json 抄两个值就行。

---

## 日常

- 菜单栏上那串数字就是四口总功率，颜色按档走：灰 ≈0W、淡蓝 0–20W、绿 20–60W、黄 60–100W、红 100W 以上；
- **点一下**数字：弹出面板（四路读数、每路的开关、四种充电模式）；
- **右键**：显示功率面板 / 重新连接 / 重新配对… / 菜单栏显示功率数字 / 打开日志 / 显示配置文件 / 退出；
- 断开或者走远了会自动重连，重连不上就先显示 `—`。

---

## 命令行参数

```bash
/Applications/CuktechTray.app/Contents/MacOS/CuktechTray --version     # 打印版本号
/Applications/CuktechTray.app/Contents/MacOS/CuktechTray --selftest    # 自检：配置路径、是否已配对
/Applications/CuktechTray.app/Contents/MacOS/CuktechTray --wizard-demo # 只把配对向导第一屏摆出来（截图用）
```

---

## 自己编译

需要 Xcode 命令行工具（或者完整 Xcode）：

```bash
swift build -c release --package-path mac          # 编出 mac/.build/release/CuktechTray
bash mac/scripts/make-app.sh mac/.build/release/CuktechTray dist   # 组装 .app + ad-hoc 签名
dist/CuktechTray.app/Contents/MacOS/CuktechTray --selftest
open dist/CuktechTray.app
```

跑测试：

```bash
swift test --package-path mac
```

协议那部分（AES-CCM、HKDF、MiOT 编解码、小米云客户端）跟界面完全解耦，
在 Linux 上也能跑，所以 CI 里有一条 ubuntu job 专门跑单元测试。

---

## 目录

```
mac/
  Package.swift                SwiftPM 清单（CuktechCore / CuktechBle / CuktechTray）
  Sources/CuktechCore/         协议核心：AES-CCM、HKDF、MiOT 编解码、小米云客户端
  Sources/CuktechBle/          CoreBluetooth：连接、认证、扫描
  Sources/CuktechTray/         菜单栏 App：状态栏、面板、向导
  Tests/CuktechCoreTests/      单元测试（黄金向量、协议、云端解析）
  scripts/make-app.sh          组装 .app 并签名
  Resources/Info.plist         包信息（LSUIElement、蓝牙权限说明）
```

---

## 已知限制

- **没做公证**：只有 ad-hoc 签名，第一次打开要走「隐私与安全性 → 仍要打开」。想彻底顺畅得买 Apple 开发者账号（99 美元/年）做 Developer ID 签名 + 公证。
- **蓝牙这块没有真机回归**：GitHub 的 macOS runner 是没有蓝牙硬件的虚拟机，CI 只能验证「编译过、单测全绿、界面能起来」，连充电器那一步必须自己机器上跑。
- **只在自己机器上验过**：开发过程中没有第二台 Mac，Intel 与 Apple Silicon 都是 CI 编出来的，实际连设备以你手上的为准。
- 菜单栏宽度是 `NSStatusItem.variableLength`，刘海屏机器上建议别把字号调太大。

---

## 许可证

[MIT](../LICENSE)。与小米、酷态科官方没有任何关系，是照公开资料和协议实测自己写的第三方小工具。
