# 0.4.7 —— 两个口合成一路算，第一次打开先点头

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.4.7.en.md](release-0.4.7.en.md)。

一句话：修一个「看着像坏了」的读数问题——**C3 和 A 同时插着时，功率被合并上报，我们却把另一路写成「空闲」**；
另外把隐私说明从「告知」改成真正的「**第一次打开必须点同意**」，不同意就退出、不写任何文件。
macOS 版同步发 **0.1.2**，并合了社区那条 PR。

## 这次改了什么

### 0.4.7（Windows，本版）

* **C3 与 A 合并成一路显示**：这两个口共用同一路功率计，固件合并工作时会在这路上报 `state = 0x11`
  （`0x00` 空闲 / `0x01` 单口 / `0x11` C3+A 合并——这个语义在 `src/CuktechMible/MiotCodec.cs:109` 里记了很久，
  界面一直没用上）
  * 读数页：有数的那行名字变成 **C3+A**，读数照旧；另一行不再写「空闲」，改灰字「并入 C3+A」
  * 控制页「实时读数」列同样处理，两路开关仍然各自独立
  * 总输出只算一次（`Snapshot.TotalWatts` 遇到合并只累加代表行），不会翻倍
  * 没合并时行为一个像素不变
* **首次打开必须同意隐私说明**：独立窗口（`src/CuktechTrayAot/ConsentWindow.cs`，500×546），四段正文
  * 点「同意并继续」→ 写 `settings.json` 的 `PrivacyAccepted: true`，以后不再弹
  * 点「不同意，退出」、按 `Esc`、直接关窗口 → 进程退出，**不写任何文件、不连蓝牙**
  * 0.4.7 之前装的版本升级上来会再看一次（那时没有这个字段），这是有意的
* 新增开发自查开关：`--render-consent`（把同意窗口渲成 PNG）、`--render-merged`（用合并状态假数据渲染读数页）
* README 隐私一节补上这一屏的图与说明，读数页补上合并显示规则（中英同步）
* 版本号 0.4.6 → 0.4.7

### macOS 0.1.2（同一版发布）

* 跟上 Windows 的两条改动：合并功率显示（同一份判据与措辞）、首次启动隐私同意门
  （`PrivacyWindow.swift`，`Esc` 和关窗都关不掉，只能二选一；`--version` / `--selftest` / `--wizard-demo` 不弹）
* **并入社区 PR [#3](https://github.com/Blank2007/cuktech-ultra-tray/pull/3)** 的四条修复：端口开关索引错位
  （USB-A 报「端口序号只能是 0~3」）、Swift actor 在 `await` 处可重入导致轮询与控制并发发命令、
  SET 的 ACK 白等一秒、深色壁纸下状态栏看不见
* 顺带修掉读回被截断：GET 只认 `op = 0x03`、SET 只认 `op = 0x01`，SET ACK 不会再把 GET 结果提前截断
* 版本 0.1.1 → 0.1.2；测试里加了 5 条合并功率用例

### 0.4.6

* 修 AOT 下扫码失败（反射式 JSON 序列化在 AOT 被关掉，改手写 `MiCrypto.JsonQuote`）、`--selftest` 加 AOT 专项自查、
  向导与 README 加隐私说明；macOS 版从 0.1.0 起独立编号

### 0.4.5

* 宣传素材换成真身：`--render-icons` / `--render-taskbar` 由程序自己渲图，右键菜单从真身抓屏；功能与 0.4.4 相同

### 0.4.4

* 顶栏「已连接」去掉半透明绿底，只留圆点与文字；顶栏整体下移 8 px，面板 320×396 → **320×404**

## 怎么升级

* **绿色版**：把新的 `CuktechTray.exe` 覆盖旧的即可，`settings.json` 原样接着用（升级后第一次打开要点一次「同意」）
* **装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己；也可以直接替换
  `%LOCALAPPDATA%\Programs\CuktechTray\CuktechTray.exe`
* **macOS**：解压新的 `CuktechTray-0.1.2-mac.zip`，把 `CuktechTray.app` 拖进「应用程序」覆盖旧的；
  第一次打开要右键 → 打开（ad-hoc 签名、没有公证）
* **命令行等价物**（Windows）：`--install [--dir=<路径>] [--autostart] [--desktop] [--silent]`、`--uninstall [--silent]`，全程不弹 UAC

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.4.7-win.exe` | **10,898,944 字节（10.39 MiB）** | Windows：绿色版 + 安装器二合一；比 0.4.6 多 16,896 字节（同意窗口 + 合并显示） |
| `CuktechTray-0.1.2-mac.zip` | 1,073,719 字节（1.02 MiB） | macOS 13+：解压出 `CuktechTray.app`（universal，Intel / Apple Silicon 都能跑，ad-hoc 签名，自带图标），说明见 [mac/README.md](../mac/README.md) |

## 校验

```
文件   CuktechTray-0.4.7-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   10,898,944 字节
SHA256 60F3E377F3D6548EF22B06831C8F63372C6246748A00B71185B78EEE1794FF25

文件   CuktechTray-0.1.2-mac.zip   （CI 在 macos-26-intel 上编的 universal 包）
大小   1,073,719 字节（1.02 MiB）
SHA256 617B958A7AB153E5CBFF9767948B0655669EA6070847A336EE120E532B6B59CA
```

Windows 用 `Get-FileHash .\CuktechTray-0.4.7-win.exe -Algorithm SHA256`、macOS 用
`shasum -a 256 CuktechTray-0.1.2-mac.zip` 对着看；GitHub 的资产页也会直接显示 digest。

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；或 macOS 13 及以上（Intel / Apple Silicon 通用）
* 两边都不需要装运行时：Windows 端是 NativeAOT 单文件（10.4 MB），macOS 端是原生 .app
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE），认证成功后的运行期只走蓝牙

## 已知

* 这个 exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* macOS 包**没有公证**（没有 Apple 开发者账号，只有 ad-hoc 签名），第一次打开要右键 → 打开，细节见 [mac/README.md](../mac/README.md)
* 自动更新只认名字以 `CuktechTray-` 开头、以 `.exe` 结尾、且不含 `Setup` 的发布资产，所以 `-win.exe` 天然符合、`-mac.zip` 不会被误选
* 两个平台的版本号**各自编号**（Windows 0.4.7 / macOS 0.1.2），别拿 Windows 的号去对 macOS
* 隐私同意门只在「第一次打开」出现：想再看一次，把 `settings.json`（macOS 是 `config.json`）里的
  `PrivacyAccepted` / `privacyAccepted` 删掉或改 `false` 即可
* `--render*` / `--font-probe` / `--update-selftest` 这些是开发自查用的开关，正常使用不用管

## 致谢

* [@KingGxxT](https://github.com/KingGxxT)：[issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1)
  把「C3 和 A 应该合并显示，官方固件也没区分」说得清清楚楚，两个平台的合并显示都是照着这张截图做的
* [@a1287448852](https://github.com/a1287448852)：[PR #3](https://github.com/Blank2007/cuktech-ultra-tray/pull/3)
  修好了 macOS 侧端口开关索引错位、BLE 命令并发、以及深色壁纸下状态栏看不见的问题，
  还配了能跑的测试；0.1.2 里大部分 macOS 修复是他的功劳
