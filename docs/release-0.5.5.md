# 0.5.5 —— 隐私说明跟行为对齐，mac 那边多了导出 CSV

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.5.5.en.md](release-0.5.5.en.md)。

一句话：Windows 这边只改了一句隐私说明——0.5.4 起程序会自己查更新，原文却还写着「只有你点「检查更新」才联网」；
macOS 升到 **0.1.8**：采集结果能**导出 CSV**、启动时**自己查一遍更新**并在菜单栏点个红点。

## 这次改了什么

### Windows：隐私说明跟实际行为对齐

* 0.5.4 加了「启动 8 秒后自己查一次更新」，但「第一次打开必须同意」的那份隐私说明第三节还写着
  「只有你点「检查更新」时，才访问 GitHub 一次」——这句话从 0.5.4 起就不成立，而用户同意的是那份文本
* 现在第三节改成「**启动时会在后台查一次更新（默认开，右键菜单里能关），只拉公开的发布列表**」
* 改的是 `src/CuktechTrayAot/ConsentWindow.cs`（首次启动那道门）与向导里的「隐私说明」弹框（`WizardWindow.cs`）
* 除了这一句，Windows 0.5.5 与 0.5.4 是同一个程序：已经用上 0.5.4 的不升也没关系

### macOS 0.1.8：采集结果导出 CSV + 启动自查更新

* 采集结果窗口多一个「**导出 CSV**」：每行一秒（时间 / 电压 / 电流 / 功率 / 是否带载），
  自己挑存哪儿，默认 `cuktech-capture-<口名>-<yyyyMMdd-HHmmss>.csv`，UTF-8 带 BOM（Excel 打开不乱码）
* **启动 8 秒后自己在后台查一次更新**（`checkUpdatesOnStart`，菜单里能关）：查到新版就在菜单栏功率数字后面
  点一个小红点、右键菜单那行变成「检查更新…（有 X）」并带行首红点，**不弹框**
* 只有自己点「检查更新…」时才弹「有新版本 / 去下载 / 以后再说」或「已经是最新的」
* 关掉菜单栏数字只用图标时不点点（模板图会被系统一起染色）；还没配对时查完更新也不会把状态行写成「连上了」
* 新增测试 `testCsvExport` / `testSampleCap`（表头逐字、CRLF、小数位、空载行尾 0、7200 条封顶）

## 怎么升级

* **Windows 绿色版**：把新的 `CuktechTray.exe` 覆盖旧的，`settings.json` 原样接着用
* **Windows 装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、重启换掉自己
* **macOS**：下载 zip 解压出 `CuktechTray.app`，拖进「应用程序」覆盖旧的。仍然是 ad-hoc 签名，
  第一次可能要在「系统设置 → 隐私与安全性」里点一次「仍要打开」

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.5.5-win.exe` | **11,152,384 字节（10.64 MiB）** | Windows：绿色版 + 安装器二合一 |
| `CuktechTray-0.1.8-mac.zip` | **1,623,255 字节（1.55 MiB）** | macOS：导出 CSV + 启动自查更新（解出来是 `CuktechTray.app`） |

## 校验

```
文件   CuktechTray-0.5.5-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   11,152,384 字节
版本   0.5.5.0
SHA256 CB814463EF42D0C240C5348E8FD0AE5B34EE02BA7390267541B16209DE71D575

文件   CuktechTray-0.1.8-mac.zip     （由 CI 打包，解出来是 CuktechTray.app）
大小   1,623,255 字节
SHA256 4CB3BA64C7421E530E4CD4C58FAD8DD08A40D32CF9F00941BA2CE0C57F3EA1AA
```

Windows 上用 `Get-FileHash .\CuktechTray-0.5.5-win.exe -Algorithm SHA256` 对着看。

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；不需要装运行时（NativeAOT 单文件）
* macOS 13 或更新，Apple Silicon 与 Intel 都行（universal）
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE）

## 已知

* Windows 的 exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* macOS 的 .app 是 ad-hoc 签名（没有 Apple 开发者账号），第一次打开要在「隐私与安全性」里点「仍要打开」
* 曲线页、任务栏数字、托盘红角标这些是 Windows 专有的；macOS 的更新提示在菜单栏文字后面点红点

## 致谢

* [@KingGxxT](https://github.com/KingGxxT)：[issue #1](https://github.com/Blank2007/cuktech-ultra-tray/issues/1)（C3+A 合并显示）、[issue #7](https://github.com/Blank2007/cuktech-ultra-tray/issues/7)（任务栏数字挪不动）
* [@kairui1108](https://github.com/kairui1108)：最早把酷态科 10 号 Ultra 的 BLE 协议写下来的两个项目
* 还有本项目所有提 issue、提 PR 的人
