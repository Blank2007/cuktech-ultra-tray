# 0.4.5 —— 界面一模一样，改的是「拿什么当证据」

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.4.5.en.md](release-0.4.5.en.md)。

一句话：程序和 0.4.4 是同一个程序（功能和界面都没动），这一版把文档里那几张「拿旧截图凑」的图换成了真身——
托盘图标由程序自己渲、右键菜单从真身抓屏、任务栏那串数字按真机字体和配色渲染后叠到真任务栏上。

## 这次改了什么

### 0.4.5（本版）

* **任务栏配图重做**：README 里原来那张任务栏图是 0.2.2 的演示抓图，连颜色都不对——按现在的分档，6.0 W 该是**淡蓝**，
  旧图却画成了绿。现在新增 `--render-taskbar`，用运行时同一套字体（Segoe UI Bold 11 pt × 缩放）和取色规则
  （`WattStyle.Total`）把那一小块渲成透明 PNG，再叠到真任务栏背景上，出图即 `docs/shot-taskbar.png`
* **README「任务栏上的功率」换图换说法**：补一张颜色分档表（灰 ≈0 W / 淡蓝 0–20 W / 绿 20–60 W / 黄 60–100 W /
  红 100 W 以上），并说明图上那个 `6.0W` 为什么是淡蓝；中英同步
* **托盘图标改成程序自己渲**：`WattIcon` 的绘制拆出 `public static Bitmap CreateBitmap(int size, Color accent)`，
  新增 `--render-icons`，直接出 608×256 的透明 PNG（`docs/shot-tray-icons.png`），不再靠 125% 缩放的截图
* **右键菜单从真身抓屏**：`--menu-demo` 弹菜单前先写一次「已连接 · 总 6.0 W」，抓出来的菜单抬头不再是「正在连接…」，
  新图 `docs/shot-menu-aot.png` 替换掉 0.3.3 的 WinForms 截图
* 版本号 0.4.4 → 0.4.5；**功能与界面无变化**，从 0.4.4 升上来不会看到任何不同（exe 比 0.4.4 大 4,608 字节，只多了几个自查开关）

### 0.4.4

* 顶栏的「已连接」胶囊去掉半透明绿底，只留圆点和文字；顶栏元素之间留白，面板 320×396 → **320×404**

### 0.4.2

* **四角黑块修掉**：圆角以前只是「拿圆角路径画了一块背景」，窗口本身还是矩形；现在用 `SetWindowRgn` + `CreateRoundRectRgn`
  把窗口本身裁成圆角（半径取同一个整数值），`--render` 导出的图也把四角抠成透明
* **排版松一档**：面板 320x360 → 320x396，四行读数行距 42 → 48，模式页卡片 44 → 50 高、间距 52 → 58

## 怎么升级

* **绿色版**：把新的 `CuktechTray.exe` 覆盖旧的就行，`settings.json` 原样接着用，配置不会重置
* **装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己；也可以直接替换
  `%LOCALAPPDATA%\Programs\CuktechTray\CuktechTray.exe`
* **命令行等价物**：`--install [--dir=<路径>] [--autostart] [--desktop] [--silent]` 装到本机，
  `--uninstall [--silent]` 从头拆干净（快捷方式、HKCU 卸载项、目录），全程不弹 UAC
* **要重新配对设备**：`--setup` 直接叫出扫码向导（和托盘菜单「重新配置设备…」是同一条路径）

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.4.5-win-x64.zip` | **5,038,055 字节（4.80 MiB）** | `CuktechTray.exe` + `README.md` + `README.en.md` + `LICENSE` + `CHANGELOG.md`，全部在 zip 根目录，没有外层文件夹 |
| `CuktechTray.exe` | **10,879,488 字节（10.38 MiB）** | 绿色版，同时也是安装器；比 0.4.4 的 10,874,880 字节多 4,608 字节（只多了 `--render-icons` / `--render-taskbar` 这类自查开关） |
| `CuktechTray-0.4.5.exe` | **10,879,488 字节（同字节）** | 跟上面那个是同一个文件，只是按「检查更新」认的名字放了一份，传 Release 资产时直接用这个 |

## 校验

```
文件   CuktechTray.exe
大小   10,879,488 字节
SHA256 7A14EFE8E71B9BB2696D00ECFAD4418B526F428D0675E7DAC508229B4A310EEA

文件   CuktechTray-0.4.5.exe         （同一个文件，只改了名字）
大小   10,879,488 字节
SHA256 7A14EFE8E71B9BB2696D00ECFAD4418B526F428D0675E7DAC508229B4A310EEA

文件   CuktechTray-0.4.5-win-x64.zip
大小   5,038,055 字节
SHA256 FEF376E511E667D14CBC0ED766428AFFF5DBA7E3D638E9A358A3DCD3A32688B4
```

同目录的 `SHA256SUMS.txt` 就是这三条（大写 SHA256 + 两个空格 + 文件名，LF 换行），
装了 git for windows 或 WSL 可以直接 `sha256sum -c SHA256SUMS.txt`；纯 Windows 用
`Get-FileHash .\CuktechTray-0.4.5-win-x64.zip -Algorithm SHA256` 对着看。

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64
* 不需要装 .NET 运行时：NativeAOT 单文件，目标机什么都不用配
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE），认证成功后的运行期只走蓝牙

## 已知

* 这个 exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，
  点「更多信息 → 仍要运行」即可；之后不会再问
* 自动更新只认名字形如 `CuktechTray-<版本>.exe` 的发布资产（含 `Setup` 的会被跳过），
  所以 `dist\CuktechTray-0.4.5.exe` 是照这个名字另存的一份，传 Release 资产请用它，别传 `CuktechTray.exe`
* `--render` / `--render-icons` / `--render-taskbar` / `--font-probe` / `--update-selftest` 这些是开发自查用的开关，
  正常使用不用管；`--apply-update` 别手敲（它会把指到的 exe 换成载荷）
