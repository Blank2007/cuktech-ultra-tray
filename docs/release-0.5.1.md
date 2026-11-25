# 0.5.1 —— 采集补上平均和最大，面板能一直浮在最上面

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.5.1.en.md](release-0.5.1.en.md)。

一句话：Windows 的**单口采集**补上了平均 / 最大电压电流（跟 macOS 一个口径），面板多了个**「始终在最前」**，
点开关**少等 1 秒**；macOS 侧并了 [@editwe](https://github.com/editwe) 的 [PR #5](https://github.com/Blank2007/cuktech-ultra-tray/pull/5)，
发 **0.1.6**。两个平台的版本号还是各算各的。

## 这次改了什么

### 0.5.1（Windows，本版）

* **采集补上「平均 / 最大」两列**（`src/CuktechTrayAot/CaptureWindow.cs`）：电压、电流、功率各一行，左边平均、右边最大。
  以前只有平均功率和电压区间，「峰值电流到底冲到多少」只能自己盯
* **峰值电流只认带载样本**：空口那 5V 待机电不会把峰值抬高；空载时这几项显示「—」而不是 `0.00`
* **平均电压 / 平均电流与平均功率同口径**：跨档那一拍按跳变前的值积分，所以「平均电压 × 平均电流」和平均功率对得上，
  不会再算出 12.5V 这种不存在的中间态
* **拔线不再算一次换档**：只有前后两次都在带载、且电压差 ≥ 0.5V 才计数，换档次数不再虚高
* **电压区间只统计带载**：空口待机电、拔线后的 0V 都不进 min/max
* **面板「始终在最前」**（托盘右键 →「面板始终在最前」）：勾上以后面板盖在其它窗口上面。它跟「固定面板到桌面」是两件事——
  后者只是「不自动收起」，切到别的程序仍会被压下去；两个勾可以同时开，配置记在 `settings.json` 的 `PanelAlwaysOnTop`
* **点开关少等 1 秒**：`src/CuktechMible/MibleSession.cs` 收到 SET 的 ACK 就直接返回，以前还要再等满 1 秒。
  本机实测一次写掩码 **1215 ms → 179 ms**；调用方本来就回读比对，判定语义没变
* 版本号 0.5.0 → **0.5.1**

### macOS 0.1.6（同一版发布）

* 并入 PR #5 的 0.1.5 + 0.1.6：只用一个 central、连接前清陈旧连接、扫描时**接管**系统里已连上的那台、
  扫描超时后再试一次；`NSWorkspace.didWakeNotification` 之后**睡醒自动重连**；配置里记住 `peripheralId`
  （字段必须是可选的，否则老 `config.json` 解码失败会把 MAC 和 token 一起冲掉）
* 采集同样补上平均 / 最大电压电流，结果窗口改成两列；`--capture-demo` 可以直接看样例
* 面板「始终在最前」（`NSWindow.level = .floating`，与「固定面板」独立）
* **菜单栏不再抖**：那一格固定 58pt，并且只有读数/颜色真变了才重绘。以前数字变一位宽窄就跳一下，
  `mac/README.md` 里甚至把这事写成了「建议别把字号调太大」，现在是事实描述了；托盘菜单可以关掉
* **面板玻璃质感**：macOS 26 用 `NSGlassEffectView`，13~15 退回毛玻璃，只动面板背景；托盘菜单可关（默认开）
* 版本 0.1.3 → **0.1.6**

### 0.5.0 / macOS 0.1.3

* 设备设置、单口采集、关于窗口（Windows）；mac 并入同一条 PR

## 怎么升级

* **绿色版**：把新的 `CuktechTray.exe` 覆盖旧的即可，`settings.json` 原样接着用
* **装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己
* **macOS**：解压新的 `CuktechTray-0.1.6-mac.zip`，把 `CuktechTray.app` 拖进「应用程序」覆盖旧的；第一次打开要右键 → 打开
* 注意：同一台电脑上**只能有一份程序连充电器**。蓝牙是独占的，托盘里那份在跑的时候，`--selftest` 会报
  「枚举特征失败: AccessDenied」，这是正常的，不是缺陷

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.5.1-win.exe` | **11,068,928 字节（10.56 MiB）** | Windows：绿色版 + 安装器二合一；比 0.5.0 多 4,608 字节 |
| `CuktechTray-0.1.6-mac.zip` | 见下方校验 | macOS 13+：解压出 `CuktechTray.app`（universal，ad-hoc 签名），说明见 [mac/README.md](../mac/README.md) |

## 校验

```
文件   CuktechTray-0.5.1-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   11,068,928 字节
SHA256 1652C5E09FB0B1CA91BE0E2E82F8BCCD359DE90728CB006ACAA3ABB37A17A014
```

Windows 用 `Get-FileHash .\CuktechTray-0.5.1-win.exe -Algorithm SHA256`、macOS 用 `shasum -a 256 CuktechTray-0.1.6-mac.zip` 对着看；GitHub 的资产页也会直接显示 digest。

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；或 macOS 13 及以上（Intel / Apple Silicon 通用）
* 两边都不需要装运行时：Windows 端是 NativeAOT 单文件（10.56 MB），macOS 端是原生 .app
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE）

## 已知

* exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* macOS 包**没有公证**（只有 ad-hoc 签名），第一次打开要右键 → 打开
* 「始终在最前」在 Windows 上是 `WS_EX_TOPMOST`，某些全屏游戏 / 独占全屏切换时系统会让它退到后面，切回来即可
* 玻璃质感是 macOS 26 才有的系统效果，13~15 上看到的是毛玻璃；两个平台都能在托盘菜单里关掉
* 两个平台的版本号**各自编号**（Windows 0.5.1 / macOS 0.1.6）

## 致谢

* [@editwe](https://github.com/editwe)：[PR #5](https://github.com/Blank2007/cuktech-ultra-tray/pull/5)
  ——接管残留连接、睡醒重连、采集中位/最大，他先在 macOS 上做完的；Windows 这一版按同一口径补齐
* [@a1287448852](https://github.com/a1287448852)：[CuktechMac10Ultra](https://github.com/a1287448852/CuktechMac10Ultra)
  ——本项目 macOS 版的衍生项目。这一版 macOS 端的固定宽度菜单栏、睡醒重连、面板玻璃质感的思路参考了它，
  实现是自己重写的（见 [NOTICE](../NOTICE)）
* [@kairui1108](https://github.com/kairui1108)：最早把酷态科 10 号 Ultra 的 BLE 协议写下来的两个项目
* 还有本项目所有提 issue、提 PR 的人
