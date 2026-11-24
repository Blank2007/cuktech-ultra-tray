# 0.4.6 —— 扫完码报的那句英文，是我们自己的错

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.4.6.en.md](release-0.4.6.en.md)。

一句话：修一个用户实际操作时才会撞到的坑——**扫码配对成功之后，向导上直接弹出一句英文**
`Reflection-based serialization has been disabled for this app`。顺带把隐私说明写进了向导和 README，
macOS 版从这一版起自己编号。

## 这次改了什么

### 0.4.6（本版）

* **修扫码配对失败**：0.4.0 换成 NativeAOT 之后，反射式 JSON 序列化默认被关掉，而扫码链路上有两处用了它——
  `/v2/home/home_device_list` 的请求体（`MiCrypto.JsonToken()` 内部）和 `blt_get_beaconkey`（`serialize(did)`）。
  这两处都改成手写转义 `MiCrypto.JsonQuote()`，行为与原来完全一致
* **为什么之前没测出来**：本地一直用已保存的 `settings.json` 跳过登录，扫码链路从没在 AOT 版上真机跑过一遍。
  这一版把缺口补上：`--selftest` 增加 7 项 JSON 转义断言 + 一次设置文件源生成序列化往返，并打印
  `[aot] json-reflection-enabled=False` 把「AOT 下反射序列化确实是关的」写进日志
* **向导加隐私说明**：第一屏底部一行小字，点开是四段式说明（扫码时做什么 / 数据留在哪 / 不会做什么 / 想清干净删什么）。
  没有勾选框、不挡路
* **README 新增「九、隐私」一节**（中英同步），内容与弹窗一致，用一张表把「什么时候 / 做了什么 / 数据去哪」列清楚
* 版本号 0.4.5 → 0.4.6

### 隐私说明（详情）

* 扫码只向小米云换一次这台充电器的 `mac` 与 12 字节 `token`；账号、密码、验证码全程在手机上，程序看不见
* 凭据只写本机 `settings.json`（安装版 `%LOCALAPPDATA%\CuktechTray\settings.json`）；
  `session.json` 里的登录会话只为「重新配置设备」时少扫一次码
* 日常读功率、断/通端口全走蓝牙，不联网；**只有你主动点「检查更新」才会访问一次 GitHub**
* 不上传读数、不做统计、不装后台服务；卸载后本机不留东西

### macOS 版：0.1.0（从这一版起独立编号）

macOS 版不再跟 Windows 共用版本号，自己从 **0.1.0** 开始，产物名 `CuktechTray-0.1.0-mac.zip`。
两个平台改动节奏不一样，绑同一个号只会让更新日志变成两边不相干的条目混在一起。
这一版 macOS 端也加了隐私说明（欢迎页一段小字 + 「看完整的隐私说明」弹窗），说明见 [mac/README.md](../mac/README.md)。

### 0.4.5

* 宣传素材换成真身：`--render-icons` / `--render-taskbar` 由程序自己渲图，右键菜单从真身抓屏；功能与 0.4.4 相同

### 0.4.4

* 顶栏「已连接」去掉半透明绿底，只留圆点与文字；顶栏整体下移 8 px，面板 320×396 → **320×404**

## 怎么升级

* **绿色版**：把新的 `CuktechTray.exe` 覆盖旧的即可，`settings.json` 原样接着用
* **装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己；也可以直接替换
  `%LOCALAPPDATA%\Programs\CuktechTray\CuktechTray.exe`
* **扫过码但没成功的**：升级后重新走一次向导即可（托盘菜单 →「重新配置设备…」，或 `CuktechTray.exe --setup`）
* **命令行等价物**：`--install [--dir=<路径>] [--autostart] [--desktop] [--silent]`、`--uninstall [--silent]`，全程不弹 UAC

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.4.6-win.exe` | **10,882,048 字节（10.38 MiB）** | Windows：绿色版 + 安装器二合一；比 0.4.5 多 2,560 字节（手写转义 + 自检项） |
| `CuktechTray-0.1.0-mac.zip` | **911,811 字节（890 KiB）** | macOS 13+：解压出 `CuktechTray.app`（universal，Intel / Apple Silicon 都能跑，ad-hoc 签名），说明见 [mac/README.md](../mac/README.md) |

## 校验

```
文件   CuktechTray-0.4.6-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   10,882,048 字节
SHA256 BEF1F61D06DA270871DD4333FA39BEFD04EAE547D0F5DCB656F9F0A835BCF051

文件   CuktechTray-0.1.0-mac.zip   （CI 在 macos-26-intel 上编的 universal 包）
大小   911,811 字节
SHA256 42621A1F1D1E3FF6BE18542F4C15B5E636400119906272E3791AFEE5EDCECA8F
```

Windows 用 `Get-FileHash .\CuktechTray-0.4.6-win.exe -Algorithm SHA256`、macOS 用
`shasum -a 256 CuktechTray-0.1.0-mac.zip` 对着看；GitHub 的资产页也会直接显示 digest。

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；或 macOS 13 及以上（Intel / Apple Silicon 通用）
* 两边都不需要装运行时：Windows 端是 NativeAOT 单文件，macOS 端是原生 .app
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE），认证成功后的运行期只走蓝牙

## 已知

* 这个 exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* 自动更新只认名字以 `CuktechTray-` 开头、以 `.exe` 结尾、且不含 `Setup` 的发布资产，所以 `-win.exe` 天然符合、`-mac.zip` 不会被误选
* macOS 包**没有公证**（没有 Apple 开发者账号，只有 ad-hoc 签名），第一次打开要右键 → 打开，细节见 [mac/README.md](../mac/README.md)
* macOS 版从这一版起版本号独立（0.1.0），升级时别拿 Windows 的 0.4.x 去对
* `--render*` / `--font-probe` / `--update-selftest` 这些是开发自查用的开关，正常使用不用管
