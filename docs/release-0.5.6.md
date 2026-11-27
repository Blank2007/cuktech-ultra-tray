# 0.5.6 —— 右键菜单换回系统菜单，子菜单能点了

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.5.6.en.md](release-0.5.6.en.md)。

一句话：**右键菜单里的子菜单点不动（0.5.x 的 bug）修好了——修法是别再自己画菜单，换回 Windows 自己的菜单**；
顺手修掉「配置没配对时把整份 settings.json 忘掉重来」。macOS 这次没改动，仍是 0.1.8。

## 这次改了什么

### Windows：右键菜单换回系统菜单

* **问题**：0.4.0 之后菜单是自绘的，一级菜单每一项都正常，但「充电模式」「任务栏数字」「功率曲线时长」
  这三层子菜单里点任何一项都没反应——点一下菜单就收起来，什么都没发生；连「点菜单外面关掉」也一起失效
* **根因**：自绘菜单靠「根菜单扣住鼠标捕获、再转发给子菜单」来工作，而基类 `WinBase` 在收到
  `WM_LBUTTONUP` 时无条件 `ReleaseCapture()`。松手那一毫秒捕获就没了，之后的鼠标消息按屏幕位置发给子菜单窗口，
  子窗口的第一句是 `if (!IsRoot) return;`——于是永远收不到点击
* **修法**：删掉 439 行自绘菜单，改用 Windows 自己的菜单（`CreatePopupMenu` 拼树 +
  `TrackPopupMenuEx(TPM_RETURNCMD)` 一次取回选中项）。子菜单展开方向、键盘、Esc、点外面关闭、
  高 DPI、无障碍全部归系统，**跟系统里别的托盘程序一模一样**（参照的是 TranslucentTB 那种原生菜单）
* 观感也跟着系统走：浅色主题就是浅色菜单，深色主题就是深色菜单；开关项前面是系统画的勾，
  「任务栏数字」那种单选是圆点；有新版本那一条文字前多一个「● 」
* **顺带修掉另一个 bug**：`Settings.Load()` 以前只在「MAC + token 都齐」时才用文件里的内容，否则从空白配置重来——
  也就是「第一次点过同意、还没配对就关掉程序」的用户，下次启动会被再问一遍隐私说明，
  同时任务栏位置、曲线时长、「启动时自动检查更新」的开关也一起忘掉。现在文件能读出来就留着，只补登录信息
* 截图：README 里的菜单图换成真机抓屏的系统菜单（浅色主题）

### macOS：这次没改动

* macOS 本来就是系统菜单（`NSMenu`），没有这个问题；版本仍是 **0.1.8**（导出 CSV + 启动自查更新）
* 这个 tag 上 CI 会照例重新打一份 `CuktechTray-0.1.8-mac.zip` 传上来，内容与 0.5.5 那次一致

## 怎么升级

* **Windows 绿色版**：把新的 `CuktechTray.exe` 覆盖旧的，`settings.json` 原样接着用
* **Windows 装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己
* **macOS**：不用动，仍是 0.1.8

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.5.6-win.exe` | **11,137,536 字节（10.62 MiB）** | Windows：绿色版 + 安装器二合一 |
| `CuktechTray-0.1.8-mac.zip` | **__MACSIZE__** | macOS：仍是 0.1.8（这次没改动） |

## 校验

```
文件   CuktechTray-0.5.6-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   11,137,536 字节
版本   0.5.6.0
SHA256 C7C58523566CA954BFFB5C6B3566896D3201E39EE9C74EC3AF58422BBC555DEF

文件   CuktechTray-0.1.8-mac.zip     （由 CI 打包，解出来是 CuktechTray.app）
大小   __MACSIZE__
SHA256 __MACHASH__
```
## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；不需要装运行时（NativeAOT 单文件）
* macOS 13 或更新，Apple Silicon 与 Intel 都行（universal）
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE）

## 已知

* Windows 的 exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* macOS 的 .app 是 ad-hoc 签名（没有 Apple 开发者账号），第一次打开要在「隐私与安全性」里点「仍要打开」
* 系统菜单只能表达「勾 / 圆点 / 灰掉」，所以「有新版本」那个提示从行尾小红点改成了文字前的「● 」

## 致谢

* [@KingGxxT](https://github.com/KingGxxT)：[issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1)（C3+A 合并显示）、[issue #7](https://github.com/Blank2007/cuktech-ultra-tray/issues/7)（任务栏数字挪不动）
* [@kairui1108](https://github.com/kairui1108)：最早把酷态科 10 号 Ultra 的 BLE 协议写下来的两个项目
* TranslucentTB：这次换系统菜单时照着它的手感对了一遍
* 还有本项目所有提 issue、提 PR 的人
