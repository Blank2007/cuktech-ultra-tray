# Mac 版具体方案（v2）

> 状态：**方案定稿，等你说「开始」就动手**。日期 2026-11-24。
> v1 是可行性稿；这一版按你的三条新要求重写：①没有 Mac 设备也要能出成品；②扫码配对必做；③菜单栏文字要系统级、不能是透明窗口。

---

## 0. 三句话

1. **菜单栏那串功率数字是系统原生的 `NSStatusItem`**，由 WindowServer 绘制、菜单栏自己排版，不是我们把一块透明窗口叠上去。Windows 那套 `SetParent` 到任务栏的 hack（还动不动错误 5）在 mac 上根本不需要——所以 mac 版这块反而更省事。
2. **没有 Mac 也能出成品**：编译、打包、签名、连截屏验证都放到 GitHub Actions 的 macOS runner（公开仓库免费）；协议加密层还能在 Linux runner 上跑单元测试，我用从 C# 版导出的「黄金向量」把它钉死。
3. **扫码配对做**，而且这是 mac 上的正路：macOS 拿不到蓝牙 MAC，扫码一次同时拿到 token 和设备 MAC 写进配置，用户不用抄任何东西。代价约 +750 行、+2 天。

---

## 1. 菜单栏文字：系统级怎么做

### 1.1 它是什么

`NSStatusBar.system.statusItem(length:)` 是系统菜单栏的**正式成员**：位置由系统布局在右侧区域、高度 22 pt 固定、像素由 WindowServer 画，我们的程序只提供「字符串 + 颜色 + 点击回调」。它和输入法、电池、时钟在同一个容器里，不是浮在上面的窗口。

真代码（不是伪码），这就是全部：

```swift
let item = NSStatusBar.system.statusItem(withLength: 64)   // 固定宽，防数字变长时抖动
item.autosaveName = "cuktech.watts"                        // 记住用户把它拖到哪儿
item.button?.attributedTitle = NSAttributedString(
    string: "12.3 W",
    attributes: [
        .font: NSFont.monospacedDigitSystemFont(ofSize: 11.5, weight: .semibold),
        .foregroundColor: WattColor.of(12.3),              // 同一套分档：灰/淡蓝/绿/黄/红
    ])
item.button?.target = self
item.button?.action = #selector(statusBarClicked)
item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp]) // 左键弹面板，右键弹菜单
```

### 1.2 和 Windows 版的差别（这张表说明了为什么 mac 更省事）

| | Windows 0.4.5 | macOS（本方案） |
| --- | --- | --- |
| 承载方式 | 无边框子窗口，`SetParent` 挂到 `Shell_TrayWnd`，要求与 explorer 同完整性级别，否则 err=5 | `NSStatusItem`，官方 API |
| 谁在画 | 我们自己用 GDI+ 逐像素画，透明靠逐像素 alpha | 系统画，我们只给字符串与颜色 |
| 位置 | 程序算出「开始按钮左侧 X」，用户可拖动 | 系统排在右侧，用户 ⌘ 拖动，`autosaveName` 记住；程序不能也不该强制位置 |
| 失败模式 | 挂不上就退回普通托盘图标 | 没有「挂不上」这回事 |
| 宽度 | 84 dip 写死 | 64 pt 写死（不用 variableLength，避免 4.0 W → 120.0 W 时右边的电池/时钟跟着跳） |
| 折叠 | 无 | 菜单栏空间不够时系统可能隐藏它（刘海屏 + 项多），用户能拖回来 |

### 1.3 三条必须提前说清的限制

1. **位置不由程序决定**：没有「紧挨苹果菜单」这种特权，只能靠用户拖。这是 mac 的惯例，不是能力不足。
2. **高度写死 22 pt**，字号 11–12 是上限；不能塞两行、不能换背景（要背景只能用自定义 NSView，仍受系统布局约束）。
3. **颜色要自己适配深浅色菜单栏**：`NSColor.labelColor` 会自动跟随，但功率分档的淡蓝/绿在浅色菜单栏上对比不够，我会按「系统浅色时用深一档」处理。这细节在 mac 上比 Windows 更要紧（Windows 我们只对深色任务栏调过色）。

---

## 2. 没有 Mac，编译与验证怎么做（方案的地基）

三层验证，越往下越贵：

| 层 | 跑在哪 | 验证什么 | 耗时 |
| --- | --- | --- | --- |
| L1 协议单测 | `ubuntu-latest`（Swift 也能在 Linux 跑） | HKDF/HMAC/AES-CCM 黄金向量、MiOT 帧编解码、FE95 广播解析、设备列表 JSON 解析 | 1–2 分钟 |
| L2 构建与界面 | `macos-26`（arm64）+ `macos-26-intel`（x86_64） | `swift build -c release` 通过、组装 .app、ad-hoc 签名、启动、**截一张菜单栏真屏图传回 artifact** | 3–5 分钟 |
| L3 真机蓝牙 | **没有** | 连接 / 认证 / 读数 / 断线重连 | 只能靠别人实测 |

**L1 的关键是黄金向量**：我先在 Windows 上跑一次现在的 C# 版，导出

- `HKDF-SHA256(token, 64, salt = randKey‖devRandom, "mible-login-info")` 的 40 字节输出；
- `HMACSHA256(devKey, saltInv)` 的结果；
- AES-CCM 用固定 key / nonce / aad 加密一段 payload 得到的密文 + 4 字节 tag；
- 真机日志里那 40 多帧认证交互的**逐帧 hex 与顺序**（回放测试用）。

存成 `Tests/Resources/golden.json`。Swift 版这三个函数差一位，L1 立刻红。这是「没有真机」时能拿到的最强保证。

**这一步我刚刚已经先做了一遍**（拿 .NET 的 `AesCcm`/`HKDF`/`HMACSHA256` 跑出来的真值），结果存在 `tools/golden-vectors/golden.json`：

| 向量 | 结果（前 16 字节） |
| --- | --- |
| `HKDF-SHA256(token, 64, salt=app_random‖dev_random, "mible-login-info")` | `dbda7471e98fe1af47d8bf0d3fe01d98…` → devKey `dbda7471…` / appKey `ad420e3a…` / devIv `4d355491` / appIv `bb3ba21f` |
| `HMACSHA256(devKey, dev_random‖app_random)` | `b088d60aefc6b25bb249aca259406215…` |
| AES-CCM，key 全 0x00-0x0f，nonce 13 字节，明文 `hello-mible-payload!` | 密文 `14841c2dd77183bdd9352dbc5f6615ed45b23158`，**4 字节 tag `7bd0e208`** |

顺带实测到一件对 Swift 实现很关键的事：**tag 长度只影响 tag，不影响密文**（同一组 key/nonce 下 4/8/16 字节 tag 的密文都是 `14841c2d…`，tag 分别是 `7bd0e208` / `7c44ff1594e68d57` / `050c7348cfc516bb…`）。CCM 的 `B0` 里编码了 t，所以手写实现里「t 参与第一块 MAC」这一步不能省——这是最容易写错的地方，现在有向量盯着。

**L2 的价值比预想大**：runner 上是完整的 macOS（有 WindowServer，Xcode 的 UI 测试都能跑；arm64 runner 只有 3 核 M1 + 7 GB，4,400 行 Swift 干净编译约 2–3 分钟，够用），所以我可以让程序在 CI 里真的启动、用 `screencapture -x` 抓真屏 PNG、当作 artifact 传回来——**我能亲眼看到菜单栏数字和面板渲染成什么样**，不用摸 Mac。这一步能不能成，M0 就会验证，翻车也不亏（大不了退回到离屏渲染 `NSHostingView` 导出 PNG）。

**L3 的缺口要诚实说**：蓝牙链路（认证时序、分包、通知顺序、重连退避）只能真机验。缓解两条：

- 程序内置 `--dump-frames`，把每一帧 hex 与时序打到 stderr；外人跑一次贴日志就能定位。
- 认证状态机写成纯逻辑 + 可注入 transport，用 L1 那份帧序做**回放测试**：把真机那 40 多帧按顺序喂进去，断言最终状态 = Authenticated 且解出的 port_switch / 功率与真机一致。没有真机时，这是最接近真机的一步。

### CI 配置长什么样

文件 `.github/workflows/macos.yml`，三个 job：

```yaml
protocol-tests: runs-on: ubuntu-latest              # swift test --filter CuktechCoreTests（只要几秒）
build-macos:    runs-on: [macos-26, macos-26-intel]  # swift build → 组装 .app → codesign --force -s - → 启动 + screencapture → tar 上传
release:        runs-on: macos-26, if: startsWith(github.ref, 'refs/tags/mac-')  # zip -ry 打包 → softprops/action-gh-release
```

### CI 的三个坑（已核实，2026-11 现状）

1. **`macos-13` 已经不存在、`macos-14` 在 2026-11-02 退役**。arm64 用 `macos-26`（`macos-latest` 也是它，**不是 Intel**），Intel 只剩 `macos-26-intel`。label 写错会一直卡在排队。
2. **`upload-artifact` 的 zip 会丢可执行位**：`.app` 里的二进制变成 644，下载下来双击就是「无法打开」。所以 CI 内部传 `.app` 一律 `tar -czf` + `archive: false`；发 Release 的 zip 必须在 mac 上 `zip -ry` 打（mac 自带 zip 保留权限），不能用 artifact 的 zip 转手。
3. **`codesign --deep` 从 macOS 13 起对签名已废弃**：正确写法 `codesign --force -s - App.app`，有 framework 时从内到外逐个签。arm64 上未签名二进制会被内核直接杀，ad-hoc 是底线。

---

## 3. 扫码配对（必做）怎么落地

### 3.1 整个流程六步，每步都对得上现有 C# 实现

| 步 | 做什么 | C#（src/CuktechLogin/XiaomiClient.cs） | Swift 侧 | 坑 |
| --- | --- | --- | --- | --- |
| 1 | 申请二维码：`GET account.xiaomi.com/longPolling/loginUrl?qs=%3Fsid%3Dxiaomiio%26_json%3Dtrue&callback=…&sid=xiaomiio&_qrsize=480&_locale=en_GB&_dc=<ms>` | `RequestQrAsync` :172 | URLSession + 扁平 `k=v&` 解析 ~60 行 | 响应**不是 JSON** 是 qs 串（`qr`/`lp`/`timeout`）；timeout 可能是毫秒 |
| 2 | 下载二维码图（`qr` 是图片 URL，实测返回 PNG） | `DownloadQrAsync` :250 | `URLSession.data` → `NSImage` | 480 px，向导里按 1:1 显示 |
| 3 | 长轮询 `lp`：单次 40 s、pending 每 1 s、HTTP 非 200 当过期 | `PollUntilScannedAsync` :322 | async/await + `Task.sleep` | 过期要能自动换一张新码 |
| 4 | 跟随 302 到 `sts.api.io.mi.com/sts`，从 Set-Cookie 取 `serviceToken/passToken/userId/cUserId` | `FinishLoginAsync` :426 · `FollowLocationAsync` :485 · `ReadSetCookies` :520 | URLSession 的 cookie 存储 + 手动跨域搬运 | 跨域 cookie 要自己搬，别指望默认行为 |
| 5 | 设备列表 `/v2/home/home_device_list`（要米家签名：RC4-drop1024 + `signed_nonce` + SHA1） | `CallApiAsync` :567 · `GetDevicesAsync` :671 | URLSession + CommonCrypto | 签名串拼错一个字符就 401；区域 cn/de/us/… 要从 resolver 结果里挑 |
| 6 | 取设备的 `beaconKey` token（列表里的 token 是加密的） | `FetchBeaconKeyTokenAsync` :929 | 同上 | 拿不到 token 就过不了 MiBeacon 认证，这是整条链的钥匙 |

代码量：`XiaomiCloud.swift` **约 700–800 行**（C# 是 1676 行，Swift 的 async/await 与 Codable 省掉不少样板）。

### 3.2 向导界面（四屏，跟 Windows 版一个逻辑）

1. **欢迎**：一句话 + 「开始」。前提写清楚：先装米家 App、充电器已配对、手机和电脑能上网。
2. **扫码**：480 px 二维码居中、剩余秒数、过期自动刷新；下方一行小字「用米家 App 右上角扫一扫」。
3. **选设备**：列表（设备名 + MAC），默认选中 productId 0x660E 那台；点「用这台」。
4. **完成**：显示固件版本 + 「开始看功率」；此时 token 与 MAC 都已写进配置。

### 3.3 兜底（必须做，不然有人会卡死）

- 二维码过期 → 自动换新，不弹错。
- 账号里没有这台充电器 → 明确提示「先在米家 App 里把充电器加进去，再回来扫」。
- 不想扫码 → 「高级」里能手填 MAC + token，直接把 Windows 版 `settings.json` 里那两行抄过来。
- 扫码过程中退出 → 不留半截状态，下次进来重新开始。

---

## 4. 工程结构

一个 SwiftPM 包（不用 Xcode 工程，CI 更省事），`.app` 由脚本组装（`Contents/MacOS` + `Info.plist` + `AppIcon.icns`）。

| 路径 | 约行数 | 说明 |
| --- | --- | --- |
| `Package.swift` | 40 | swift-tools 5.9，platforms `.macOS(.v12)` |
| `Sources/CuktechCore/MiCrypto.swift` | 350 | HKDF、HMAC、**手写 AES-CCM**（CryptoKit 只有 GCM）、RC4-drop1024、SHA1 签名 |
| `Sources/CuktechCore/MiFrame.swift` | 250 | MiOT 帧编解码：0x10 0x59 头、TLV、seq、padding、校验 |
| `Sources/CuktechCore/MibleSession.swift` | 350 | 认证状态机（纯逻辑，transport 注入） |
| `Sources/CuktechCore/DeviceState.swift` | 200 | 解析 port_switch / 四路电压电流功率 / scene_mode |
| `Sources/CuktechCore/AdvMatch.swift` | 80 | FE95 广播解析 → productId + MAC 比对 |
| `Sources/CuktechCore/XiaomiCloud.swift` | 780 | 扫码登录 + 设备列表（URLSession 可注入） |
| `Sources/CuktechCore/Settings.swift` | 120 | `~/Library/Application Support/CuktechTray/settings.json`，与 Windows 格式兼容 |
| `Sources/CuktechBle/CBLink.swift` | 350 | CoreBluetooth：扫描匹配 → 连接 → 发现特征 → 收发队列 |
| `Sources/CuktechMenuBar/main.swift` + `AppDelegate.swift` | 200 | NSApplication、单实例、开机自启（`SMAppService`，macOS 13+） |
| `Sources/CuktechMenuBar/MenuBarController.swift` | 300 | NSStatusItem 数字、左键 popover、右键 NSMenu（条目对齐 Windows 版） |
| `Sources/CuktechMenuBar/PanelView…ModeView.swift` | 600 | SwiftUI：320×404 设计稿搬过来，四路读数 / 控制 / 模式三页 |
| `Sources/CuktechMenuBar/WizardView.swift` | 400 | 四屏扫码向导 |
| `Tests/CuktechCoreTests/*` | 450 | 黄金向量、帧、回放、广播解析、设备列表解析 |
| `scripts/make-app.sh` + `Info.plist` | 120 | 组装 .app、写 `NSBluetoothAlwaysUsageDescription`、codesign |
| `.github/workflows/macos.yml` | 90 | 上面那三个 job |

合计约 **4,400 行 Swift**（C# 版三个工程加起来 7,600 行；SwiftUI 替掉了自绘 Win32，所以更短）。

---

## 5. 里程碑（每一步都有你能看到的东西）

| 步 | 内容 | 我能拿给你看的证据 | 估时 |
| --- | --- | --- | --- |
| **M0** | CI 骨架 + 一个只有菜单栏数字的 Hello 程序 | CI 跑绿、.app artifact、**菜单栏真屏截图** | 0.5 天 |
| **M1** | 协议核心 + 黄金向量 + L1 单测全绿 | 单测报告（每个向量一行 OK） | 1 天 |
| **M2** | 认证状态机 + 回放测试（真机帧序喂进去） | 回放断言通过（状态 = 已认证，读数 = 真机那组） | 1.5 天 |
| **M3** | UI：菜单栏数字 + 面板三页 + 右键菜单 | CI 截图（三个页面各一张） | 2 天 |
| **M4** | 扫码向导四屏 | CI 截图 + 真实扫码录屏（让有 Mac 的人跑一次） | 2 天 |
| **M5** | 打包、ad-hoc 签名、Release、README 加 Mac 一节 | 能下载的 zip + Release 页 | 1 天 |

合计约 **8 个工作日**。M0 是唯一有「CI 能不能截图」风险的，先做它就是为了尽早暴露。

---

## 6. 签名与分发（现实版）

- CI 里做 **ad-hoc 签名**（`codesign --force -s - App.app`；`--deep` 从 macOS 13 起对签名已废弃）：Apple Silicon 上不签的二进制会被内核直接杀掉，ad-hoc 是底线。
- 用户下载后仍会撞 Gatekeeper：macOS 15+ 的「右键 → 打开」捷径已失效，要走 **系统设置 → 隐私与安全性 → 仍要打开**，或跑一句 `xattr -d com.apple.quarantine /Applications/CuktechTray.app`。这一步我在 README 里写成图文，不指望用户自己会。
- 想做到双击就开：Apple Developer Program **99 美元/年** → Developer ID 证书 → `codesign --options runtime` → `notarytool submit --wait` → `stapler staple`。这套要在你的 Apple 账号下做，我这边做不了。
- 架构：CI 分别出 arm64 与 x86_64，再用 `lipo` 合成 universal（成品约 4–6 MB）。
- 好消息：Swift 运行时从 macOS 10.14.4 起随系统，用户不用装任何东西。

---

## 7. 风险（按严重程度重排）

1. **蓝牙链路没有真机验证（最硬的一条）**。回放测试能覆盖认证与解析，但「CoreBluetooth 在这台机器上能不能连上、通知顺序有没有差异」只能靠有 Mac 的人跑一次。若最终没人测，我能交付的是「编译通过 + 单元测试全绿 + 界面截图」，这点我会在 README 里如实写，不吹。
2. **无头 CI 能不能截到菜单栏**（中）。M0 就会试；不成的退路是离屏渲染 SwiftUI 导出 PNG（拿不到真菜单栏，但能看面板）。
3. **分发这一环最容易「看着成功其实用不了」**（中）。ad-hoc 未公证 + artifact zip 丢可执行位，两个坑叠起来就是「CI 全绿、下载打不开」。对策已写进 §2 的三个坑：tar 传内部产物、`zip -ry` 发 Release、README 写清 Gatekeeper 那一关怎么过。
4. **小米云接口变动 / 风控**（中）。扫码登录走的是官方 App 同一条路，风险在于参数与签名细节；已有 C# 版可对照，出问题能二分。
5. **AES-CCM 手写错一位就全错**（低）。有黄金向量兜底，CI 会第一时间红。
6. **TCC 权限静默失败**（低）。按你说的，权限一律当已给，不做降级设计；但界面上仍会提示「蓝牙没开 / 没授权」，避免用户以为程序坏了。

---

## 8. 需要你点头的三件事

1. **开工范围**：按 M0→M5 一路做完（约 8 天），还是先做到 M0+M1+M3（能看界面、能连协议但还连不上设备，约 3.5 天）？
2. **99 美元的开发者账号**：办还是先不办？（不办就走 ad-hoc，用户装的时候要多点两下系统设置）
3. **放哪个仓库**：放现在的 `cuktech-ultra-tray` 的 `mac/` 目录 + 独立 workflow（我倾向这个，一份 README 讲两个平台），还是另开一个仓库？

版本号我按「Mac 版自己从 0.1.0 起」规划，跟 Windows 的 0.4.x 不混——两套实现，各有各的 bug 历史。
