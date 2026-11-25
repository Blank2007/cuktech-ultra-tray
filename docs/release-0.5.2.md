# 0.5.2 —— 面板记住的位置跑到屏幕外就自己收回来

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.5.2.en.md](release-0.5.2.en.md)。

一句话：修一个**我上几版自己留下的坑**——面板「记住的位置」如果落在屏幕外，启动时又原样搬回去，面板就再也看不见了。
这一版显示前先把它收回屏幕里，拖动也不再允许拖出屏幕，菜单里另加一键「重置面板位置」。

## 这次改了什么

### 0.5.2（Windows，本版）

* **面板位置显示前先在屏幕里检查**（`src/CuktechTrayAot/PanelWindow.cs` 的 `ShowAtClamped` / `ClampKeepVisible`）：
  用 `MonitorFromPoint` 找最近的显示器，按它的工作区把坐标收回来；被收回来时日志写一行
  `[panel] 记住的位置在屏幕外 -495,1072 → 挪到 0,1035`，并把改正后的坐标写回 `settings.json`
* **拖动也收着**：以前拖动是拿绝对坐标直接 `SetWindowPos`，能一路拖到屏幕外面去（这次就是这么坏的）；
  现在拖到边缘会停住，至少留 60 dip 在屏幕里
* **托盘菜单新增「重置面板位置（收回右下角）」**：一键把记下的坐标清掉，面板回到右下角
* 触发条件也说明白：`settings.json` 里 `PanelPinned` 为真、而 `PanelX`/`PanelY` 是屏幕外的旧值时，**每次启动都会消失**，
  不是「偶尔看不见」；0.5.1 新加的「始终在最前」只改 Z 序、不动位置，跟这次无关
* 版本号 0.5.1 → **0.5.2**

### macOS 0.1.6（同一版发布）

macOS 端这一版没有改代码，仍随本 Release 提供 **0.1.6** 的 `.app`（universal，ad-hoc 签名）。
两个平台的版本号还是各算各的。

## 怎么升级

* **绿色版**：把新的 `CuktechTray.exe` 覆盖旧的即可，`settings.json` 原样接着用（坏掉的坐标读出来会被自动改对）
* **装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己
* 如果面板在更新后仍然不出现，右键托盘图标 →「重置面板位置（收回右下角）」，再打开面板看看
* **macOS**：解压 `CuktechTray-0.1.6-mac.zip`，把 `CuktechTray.app` 拖进「应用程序」覆盖旧的；第一次打开要右键 → 打开

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.5.2-win.exe` | **11,070,464 字节（10.56 MiB）** | Windows：绿色版 + 安装器二合一 |
| `CuktechTray-0.1.6-mac.zip` | 由 CI 在本 tag 上重新打出（约 1.55 MiB） | macOS 13+：解压出 `CuktechTray.app`，说明见 [mac/README.md](../mac/README.md) |

## 校验

```
文件   CuktechTray-0.5.2-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   11,070,464 字节
SHA256 1904B3A79FC1D2580D4E01C7D946BDC8F5253F3AEEF0E70F07F2854E32B1991B
```

`CuktechTray-0.1.6-mac.zip` 的哈希以本页资产列表里 GitHub 显示的 digest 为准（CI 每次重新打包都会带新的时间戳）。

Windows 用 `Get-FileHash .\CuktechTray-0.5.2-win.exe -Algorithm SHA256`、macOS 用 `shasum -a 256 CuktechTray-0.1.6-mac.zip` 对着看。

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；或 macOS 13 及以上（Intel / Apple Silicon 通用）
* 两边都不需要装运行时：Windows 端是 NativeAOT 单文件（10.56 MB），macOS 端是原生 .app
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE）

## 已知

* exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* macOS 包**没有公证**（只有 ad-hoc 签名），第一次打开要右键 → 打开
* 多显示器切换后如果面板落到另一个屏上，位置会被收回最近那块屏幕的可见区域，但不会帮你搬到当前屏的正中——
  需要的话用菜单里的「重置面板位置」
* 两个平台的版本号**各自编号**（Windows 0.5.2 / macOS 0.1.6）

## 致谢

* [@KingGxxT](https://github.com/KingGxxT)：[issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1)
  ——C3 与 A 合并输出那一栏的读数问题，0.4.7 起合成一路显示
* [@editwe](https://github.com/editwe)：[PR #5](https://github.com/Blank2007/cuktech-ultra-tray/pull/5)
* [@a1287448852](https://github.com/a1287448852)：[CuktechMac10Ultra](https://github.com/a1287448852/CuktechMac10Ultra)
* [@kairui1108](https://github.com/kairui1108)：最早把酷态科 10 号 Ultra 的 BLE 协议写下来的两个项目
* 还有本项目所有提 issue、提 PR 的人
