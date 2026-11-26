# 0.5.3 —— 修「设备设置」里点错行，以及 macOS 玻璃面板糊成一片

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.5.3.en.md](release-0.5.3.en.md)。

一句话：Windows 端修掉「点『空闲息屏』实际改了『方向锁』」这个点击错位，并把这类 bug 的根子（绘制和命中各写一份坐标）拔掉；
macOS 端修 [issue #6](https://github.com/Blank2007/cuktech-ultra-tray/issues/6)——0.1.6 在 macOS 26/27 上玻璃面板把深色面冲成中灰、次要文字看不清。

## 这次改了什么

### 0.5.3（Windows，本版）

* **修点击错位**：设备设置窗口里，点「空闲息屏」的开关实际改的是下面那行「方向锁」，点「方向锁」则什么都不发生。
  原因在 `src/CuktechTrayAot/DeviceSettingsWindow.cs`：**绘制和命中各写了一份坐标公式**，绘制是对的，
  命中那份从第一行算起、少算了一行（第一行「息屏时间」右边只有胶囊没有开关），于是整份命中表比画面上移了 32 dip
* **顺手拔根**：新增 `ScreenChipRect` / `SwitchRect` / `LangChipRect` / `CountdownChipRect` /
  `ProtocolChipRect` / `ButtonRect` 六个矩形函数，**绘制和命中都只调它们**，落屏统一过 `R()`。
  这个窗口原是全仓唯一「公式写两遍」的地方，现在也只有一个来源
* **`DrawSwitchRow` 改成显式传 `Kind`**：以前按 `rowY` 反推，将来在这几行中间插一行会静默落到 `Kind.UsbSwitch`，
  又变成「点 A 触发 B」
* **新增自检 `--probe-hits`**：按缩放 1 把 25 个命中矩形导成文本，配合 `--render-settings` 出图；改完这两份都做了前后对比，
  命中矩形逐行一致、渲染图 SHA256 完全相同
* 其余八个自绘窗口（面板、控制页、模式页、单口采集、关于、隐私说明、配对向导、右键菜单）也一并核对过：
  绘制与命中共享同一批矩形方法，没有第二处错位
* 版本号 0.5.2 → **0.5.3**

### macOS 0.1.7（同一版发布）

* **修 issue #6**：0.1.6 在 `#available(macOS 26.0, *)` 上用了 `NSGlassEffectView`，这个视图塞进 `NSPopover` 的普通容器里
  **拿不到窗口背景采样**，只剩一层中性半透明玻璃；同时底色被压到 **0.55**，背后一扇浅色窗口就把深色面冲成中灰，
  `.secondary` 的副标题、「空载 / 0.0W」这类字几乎看不见
* **玻璃底统一退回 `NSVisualEffectView`**（`.popover` 材质 + `.behindWindow` 采样）：13~15 一直是这条路，
  26/27 上也一样能正确采样，不再按系统版本分叉
* **底色不再压那么狠**：玻璃开时固定 **0.9**（原 26+ 为 0.55）
* **「面板玻璃质感」默认关**：新装和升级的用户不会再一上来就撞上这个效果；老配置里写了 `true` 的继续生效，
  托盘菜单里可随时开关
* 版本号 0.1.6 → **0.1.7**

## 怎么升级

* **绿色版**：把新的 `CuktechTray.exe` 覆盖旧的即可，`settings.json` 原样接着用
* **装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己
* **macOS**：解压 `CuktechTray-0.1.7-mac.zip`，把 `CuktechTray.app` 拖进「应用程序」覆盖旧的；第一次打开要右键 → 打开。
  升级后玻璃如果还是开着的（老配置里写着 `true`），会看到对比度已经正常；不想要就去托盘菜单把「面板玻璃质感」关掉

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.5.3-win.exe` | **11,073,024 字节（10.56 MiB）** | Windows：绿色版 + 安装器二合一 |
| `CuktechTray-0.1.7-mac.zip` | 由 CI 在本 tag 上重新打出（约 1.55 MiB） | macOS 13+：解压出 `CuktechTray.app`，说明见 [mac/README.md](../mac/README.md) |

## 校验

```
文件   CuktechTray-0.5.3-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   11,073,024 字节
SHA256 9BA1574943DA6F3EB33D87A554A603F8F053E926DB64471156A8CB453693EA96
```

`CuktechTray-0.1.7-mac.zip` 的哈希以本页资产列表里 GitHub 显示的 digest 为准（CI 每次重新打包都会带新的时间戳）。

Windows 用 `Get-FileHash .\CuktechTray-0.5.3-win.exe -Algorithm SHA256`、macOS 用 `shasum -a 256 CuktechTray-0.1.7-mac.zip` 对着看。

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；或 macOS 13 及以上（Intel / Apple Silicon 通用）
* 两边都不需要装运行时：Windows 端是 NativeAOT 单文件，macOS 端是原生 `.app`
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE）

## 已知

* exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* macOS 包**没有公证**（只有 ad-hoc 签名），第一次打开要右键 → 打开
* macOS 的「按窗口截图框不住面板」这条我们没法复现（CI 的 runner 没有蓝牙硬件，只能验证编译与界面启动）。
  按 issue #6 里的 `CGWindowListCopyWindowInfo` 核对方法，进程只有 popover 那一个窗口；去掉 `NSGlassEffectView` 之后
  也不再有额外的玻璃层，麻烦再试一次窗口吸附截图
* 两个平台的版本号**各自编号**（Windows 0.5.3 / macOS 0.1.7）

## 致谢

* [@editwe](https://github.com/editwe)：[issue #6](https://github.com/Blank2007/cuktech-ultra-tray/issues/6)——
  把玻璃面板的问题定位到 `NSGlassEffectView` 拿不到背景采样、底色被压到 0.55，还给了核对窗口列表的方法
* [@KingGxxT](https://github.com/KingGxxT)：[issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1)
* [@a1287448852](https://github.com/a1287448852)：[CuktechMac10Ultra](https://github.com/a1287448852/CuktechMac10Ultra)
* [@kairui1108](https://github.com/kairui1108)：最早把酷态科 10 号 Ultra 的 BLE 协议写下来的两个项目
* 还有本项目所有提 issue、提 PR 的人
