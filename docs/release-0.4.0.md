# 0.4.0 —— 9.19 MB，一个 exe 管安装、管升级

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见下方 [English](#english)。

## 体积

| | 0.3.3 | 0.4.0 |
| --- | --- | --- |
| 绿色版 | 72,715,094 字节（69.3 MiB） | **9,633,792 字节（9.19 MiB）** |
| 安装包 | 132,813,041 字节（126.7 MiB） | 不需要了 |

缩小 7.55 倍，功能一条没少。目标机照样不用装 .NET。

## 怎么用这一版

下这一个 `CuktechTray-0.4.0.exe`，双击就跑（绿色版：配置和日志写在 exe 旁边）。

想装到这台电脑：托盘右键 → **「安装到本机…」**。它把自己复制到 `%LOCALAPPDATA%\Programs\CuktechTray`，
建开始菜单快捷方式，写 HKCU 的卸载入口，可选开机自启 —— 全程不弹 UAC。
卸载就在托盘右键 →「从这台电脑卸载…」，或者设置 → 应用里点卸载，快捷方式、注册表、目录一起清干净。

## 从 0.3.x 升级要手动换一次

0.3.3 及更早的版本没有自动更新能力，把旧的 exe / 安装目录换成这一个就行（`settings.json` 直接接着用）。
**换上 0.4.0 之后，以后的升级都走托盘「检查更新」**：自己查 GitHub、自己下载、自己校验 SHA256、自己重启换掉自己，不用再下文件。

## 这一版改了什么

* **NativeAOT 重写**：`src/CuktechTrayAot/`，13 个文件 4,967 行，纯 Win32 + GDI+ 自绘（不再带 WinForms），
  协议层 / 设置 / 自测这些和 WinForms 版共用同一份源码
* **安装、卸载搬进主程序**：不再需要单独的 `CuktechTray-Setup.exe`
* **一键自动更新**：查 API → 挑 `CuktechTray-<版本>.exe` 资产 → 下到同目录 → 按发布里的 `digest` 校验 SHA256
  → 备份旧版 → 助手进程等主进程退出后替换 → 换失败自动用 `.old` 还原
* **托盘图标变成纯状态徽标**：只有已连接（绿）/ 未连接（灰）两种样子，不再画功率数字；
  功率交给任务栏最左边那串数字和悬停提示
* **界面打磨**：图钉按钮改成常显淡底（原来点得到看不见，字体名写错导致字形回退）、与「已连接」胶囊对齐；
  「当前」胶囊加深；三页顶部说明文字基线统一；已断电/空载行提亮；键盘 1/2/3 直接切三页
* **逐像素对齐 0.3.3**：把 0.3.3 的真屏截图当基准、每页 72 万像素逐点比，修掉三处真偏差 ——
  字号现在向上取整（旧版 GDI+ 把 13×1.25 算成 17 px，之前取成 16，标题整圈小一档）、半透明混合不再走
  gamma 校正（页签底色曾比旧版亮一档）、坐标换算改回截断（页签间距累加错 2 px）；读数页与旧版的差异
  降到 772 个像素（0.1%）
* 新增开发用参数：`--render [--render-demo] [--render-pinned] [--render-scale=N]`（无屏幕也能出三页 PNG）、
  `--font-probe` / `--alpha-probe`（字体与混合诊断）、`--shortcut-test`、`--update-selftest`、`--update-download-test`

## 校验

```
文件   CuktechTray-0.4.0.exe
大小   9,633,792 字节
SHA256 2CF503E0B741103BA0AD4F83ADBFA65E4009118C0EEEF89754033EF180680FB4
```

## 已知

* 这个 exe 没有代码签名，首次运行可能弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」
* 0.4.0 没有内置扫码向导（向导还留在归档的 WinForms 外壳里）：第一次配置设备的办法是
  下 0.3.3 的 `CuktechTray-0.3.3.exe` 跑一次向导，把生成的 `settings.json` 拷到 0.4.0 旁边；
  或者用仓库里的 `tools/token-fetch` 拿 token 手填
* 构建期的两条警告（`NU1903` ILCompiler 8.0.32 的已知漏洞、`LNK4099` 缺 MSVC 自己的 pdb）不影响产物

---

## English

0.4.0 — 9.19 MB, one exe that installs, uninstalls and updates itself.

| | 0.3.3 | 0.4.0 |
| --- | --- | --- |
| Portable | 72,715,094 bytes (69.3 MiB) | **9,633,792 bytes (9.19 MiB)** |
| Installer | 132,813,041 bytes (126.7 MiB) | not needed any more |

Download `CuktechTray-0.4.0.exe` and double-click it. To install: tray menu → "Install on this PC…"
(copies itself into `%LOCALAPPDATA%\Programs\CuktechTray`, Start menu shortcut, HKCU uninstall entry, optional start with Windows — no UAC).

Upgrading from 0.3.x means replacing the old exe or install folder by hand once; from 0.4.0 on,
"Check for updates" downloads, verifies the SHA256 and swaps the exe in place.

Highlights: NativeAOT rewrite (13 files, 4,967 lines, plain Win32 + GDI+), installer folded into the main program,
one-click updater with SHA256 verification against the release `digest`, tray icon reduced to an on/off badge,
and a round of UI fixes (the pin button that was clickable but invisible had a bogus font name).
Every page was then diffed pixel by pixel against the 0.3.3 on-screen captures and three real mismatches were fixed:
font sizes round up like GDI+ did, translucent fills blend in sRGB again instead of gamma-corrected, and coordinates
truncate rather than round — the read page now differs from 0.3.3 by 772 pixels out of 720,000.
The 0.4.0 build has no QR-code wizard yet — use the archived 0.3.3 WinForms build once, or `tools/token-fetch`.
