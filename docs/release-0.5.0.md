# 0.5.0 —— 别人的功能，搬过来用

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.5.0.en.md](release-0.5.0.en.md)。

一句话：社区 [@editwe](https://github.com/editwe) 的 [PR #4](https://github.com/Blank2007/cuktech-ultra-tray/pull/4)
给 macOS 版写了**设备设置**、**单口采集**、**面板固定**和**关于窗口**，这一版把同一套东西按同一份协议搬到了 Windows 版；
macOS 侧同时把那条 PR 合了，发 **0.1.3**。两个平台的版本号还是各算各的。

## 这次改了什么

### 0.5.0（Windows，本版）

* **设备设置**（新窗口，`src/CuktechTrayAot/DeviceSettingsWindow.cs`），从托盘右键 →「设备设置」进
  * 屏幕：息屏时间（1 分 / 5 分 / 10 分 / 30 分 / 常亮）、空闲息屏、屏幕方向锁、屏幕语言（中文 / English）
  * 端口：C1 / C2 / C3 / A 各自的定时断电（关闭 / 15 分 / 30 分 / 1 小时 / 2 小时 / 4 小时，点一下循环切）、USB-A 小电流
  * 快充协议：每口允许握手哪些协议——C1 与 C2 是 PD / PPS / UFCS，C3 与 A 是 UFCS / SCP
  * 每次写完都**回读比对**，对不上就直说「设备只接受了 0x…」，不假装成功
  * 协议位里有两个固件写死的保留位，所以一律**先读基线再改目标位**；基线读不到就拒绝写入
  * 读不到的项（刚断连、固件不支持）全部压暗点不动，状态行写「充电器还没连上」——不把「没读到」当成「关着」
* **单口采集**（新窗口，`src/CuktechTrayAot/CaptureWindow.cs`），从托盘右键 →「单口采集」进
  * 挑一路每秒采样，停止后给出 **Wh / 平均功率 / 峰值 / 电压区间 / 换档次数 / 结束原因**，摘要可一键复制
  * 积分规则保守：同一档位内用梯形；5V→20V 这种跨档，那一秒按**跳变前**的功率算（直接平均会得出 12.5V 这种不存在的中间态，能量多算一倍）
  * 读数间隔超过 4 秒的那一段不积分；平均功率的分母是**真正在输出的秒数**
  * 连续 30 秒没输出 / 读不到数会自动收尾，四种结束原因都会写清楚
* **关于**（新窗口，`src/CuktechTrayAot/AboutWindow.cs`）：版本、项目主页、实测固件、这版是谁帮着修的；
  打开窗口**不发任何网络请求**，点「检查更新」才去问 GitHub
* 托盘菜单新增「设备设置」「单口采集」「关于」三项；三个自绘窗共用一把模态闸，免得叠起来按 Esc 关错窗口
* 顺手查清一个假故障：0.4.8 的 `--selftest` 只打一行 `[start]` 就退出，不是崩了，是单实例互斥体被另一份实例占着，
  程序按设计「让位退出」。自检类参数现在都要求配 `--standalone`
* 版本号 0.4.8 → **0.5.0**

### macOS 0.1.3（同一版发布）

* 并入 PR #4 的全部内容：设备设置面板、单口电量采集、面板可固定、关于窗口与更新检查、读数间隔挪到页脚、
  端口开关序号错位修复
* 合并时把对方那套命令排队并进了 0.1.2 的 `withTransactionLock(urgent:)`，收掉了他与 0.1.2 冲突的端口序号换算
* 版本 0.1.2 → **0.1.3**；`swift test` 72 项全过，CI 出 universal 包

### 0.4.7

* C3 与 A 合并成一路显示、首次打开必须点同意的隐私说明

## 怎么升级

* **绿色版**：把新的 `CuktechTray.exe` 覆盖旧的即可，`settings.json` 原样接着用
* **装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己
* **macOS**：解压新的 `CuktechTray-0.1.3-mac.zip`，把 `CuktechTray.app` 拖进「应用程序」覆盖旧的；第一次打开要右键 → 打开
* 注意：同一台电脑上**只能有一份程序连充电器**。蓝牙是独占的，托盘里那份在跑的时候，`--selftest` 会报
  「枚举特征失败: AccessDenied」，这是正常的，不是缺陷

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.5.0-win.exe` | **11,064,320 字节（10.55 MiB）** | Windows：绿色版 + 安装器二合一；比 0.4.7 多 165,376 字节（三个新窗口 + 协议读写） |
| `CuktechTray-0.1.3-mac.zip` | 见 Release 资产页 | macOS 13+：解压出 `CuktechTray.app`（universal，ad-hoc 签名），说明见 [mac/README.md](../mac/README.md) |

## 校验

```
文件   CuktechTray-0.5.0-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   11,064,320 字节
SHA256 7DC069BDB4266003915D66A6F98A625C65392DA4E35AC33BB64FD97C43CDE4CE
```

Windows 用 `Get-FileHash .\CuktechTray-0.5.0-win.exe -Algorithm SHA256` 对着看；GitHub 的资产页也会直接显示 digest。

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；或 macOS 13 及以上（Intel / Apple Silicon 通用）
* 两边都不需要装运行时：Windows 端是 NativeAOT 单文件（10.55 MB），macOS 端是原生 .app
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE）

## 已知

* exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* macOS 包**没有公证**（只有 ad-hoc 签名），第一次打开要右键 → 打开
* 自动更新只认名字以 `CuktechTray-` 开头、以 `.exe` 结尾、且不含 `Setup` 的发布资产，所以 `-win.exe` 天然符合、`-mac.zip` 不会被误选
* 两个平台的版本号**各自编号**（Windows 0.5.0 / macOS 0.1.3）
* 定时断电的回读可能是「剩余时间」而不是设定值，所以这一项只记日志、不判失败

## 致谢

* [@editwe](https://github.com/editwe)：[PR #4](https://github.com/Blank2007/cuktech-ultra-tray/pull/4)
  ——设备设置、单口采集、面板固定、关于与更新检查，全是他在 macOS 上先做出来的；这一版 Windows 侧照着同一份协议实现
* [@KingGxxT](https://github.com/KingGxxT)：[issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1)
  ——C3 与 A 合并显示
* [@a1287448852](https://github.com/a1287448852)：[PR #3](https://github.com/Blank2007/cuktech-ultra-tray/pull/3)
  ——macOS 侧端口开关索引、BLE 并发命令、深色壁纸下的状态栏