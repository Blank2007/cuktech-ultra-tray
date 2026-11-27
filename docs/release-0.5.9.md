# 0.5.9 —— 更新完文件名跟着走，采的数据落在自己目录里

> 这份是发 GitHub Release 用的正文草稿（中文），复制粘贴即可。英文见 [release-0.5.9.en.md](release-0.5.9.en.md)。

一句话：**自更新不再把文件名的版本号留在旧值上**（`CuktechTray-0.5.8-win.exe` 更新完就是 `CuktechTray-0.5.9-win.exe`，
快捷方式 / 开机自启 / 卸载项一起改指过去），**单口采集导出的 CSV 默认落进安装目录**，不再一律写到「文档」里。
macOS 侧这次没有代码改动，仍是 0.1.9。

## 这次改了什么

### 更新之后，文件名里的版本号会跟着变

旧的自动更新是把新文件**覆盖到同名路径**上，于是文件永远停在第一次下载时那一版的名字：
点了几次「检查更新」，程序里版本已经 0.5.x，文件名还写着 `0.4.6`，光看名字分辨不出新旧。

* 替换前先看当前文件名里有没有 `x.y.z`，有就用**新 exe 的版本号**拼一个新名字，替换到新名字、删掉旧名字
* 改名之后把**记得旧文件名的四个地方**一起改指过去：桌面快捷方式、开始菜单快捷方式、
  `HKCU\...\Run` 里的开机自启值、卸载项里的 DisplayIcon / UninstallString / InstallLocation / DisplayVersion
  （只在该条目确实指向旧文件时才动，不动别人的东西）
* 安装到本机的那份固定叫 `CuktechTray.exe`，本来就不带版本号，名字保持不变

### 产生的东西尽量留在程序自己的目录

* **单口采集导出的 CSV**：装过本机的写 `%LOCALAPPDATA%\Programs\CuktechTray\captures\`，
  绿色版写 exe 旁边的 `captures\`；那个目录写不进去（装在只读位置之类）才退回 `文档\CuktechTray\`，日志里会说明为什么退
* `settings.json` 与 `logs\app.log` 本来就跟着 exe 走（安装版在安装目录、绿色版在 exe 旁边），这次没有变化
* 更新过程中的临时文件仍在 `%TEMP%` —— 被替换的 exe 可能正被映射着，同一目录下动不了它，这一处没法改

### 验证

* `--update-selftest` 加了两条断言，现在输出
  `[update-selftest] ok 替换=True 未误伤=True 固定名不动=True 带版本号改名=True 自身版本=0.5.9`
* 端到端验过：拿 `CuktechTray-0.5.7-win.exe` 当目标跑一次 `--apply-update`，事后目录里只剩
  `CuktechTray-0.5.8-win.exe`，旧名字与 `.old` 备份都不见了

## 怎么升级

* **Windows 装过本机的**：托盘右键 →「检查更新」，它自己下载、校验 SHA256、替换自己，几秒后回来。
  这次更新**就是这条链路本身在改**，所以从 0.5.9 往后才会看到文件名跟着变；从 0.5.8 升上来那一次，
  文件名由旧代码决定（不动）
* **Windows 绿色版**：把新的 `CuktechTray.exe` 覆盖旧的，`settings.json` 原样接着用
* **macOS**：这次没有 mac 代码改动，0.1.9 可以不动

## 文件

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `CuktechTray-0.5.9-win.exe` | **10.7 MB**（11,197,440 字节） | Windows：绿色版 + 安装器二合一 |
| `CuktechTray-0.1.9-mac.zip` | **PENDING** | macOS：与 0.5.8 那版相同（0.1.9 源码未改） |

## 校验

```
文件   CuktechTray-0.5.9-win.exe   （本地 build 出来叫 CuktechTray.exe，同一个文件）
大小   11,197,440 字节
版本   0.5.9.0
SHA256 6F94829BE572CC7DE88FBFE458C8757D17C94D7DE9F8930CB6786C31449DB60C

文件   CuktechTray-0.1.9-mac.zip     （由 CI 打包，解出来是 CuktechTray.app）
大小   PENDING
SHA256 PENDING
```

## 系统要求

* Windows 10 2004（内部版本 19041）及以上，x64；不需要装运行时（NativeAOT 单文件）
* macOS 13 或更新，Apple Silicon 与 Intel 都行（universal）
* 硬件还是「酷态科 10 号 Ultra」充电器 + 蓝牙（BLE）

## 已知

* Windows 的 exe **没有代码签名**，首次运行会弹蓝色的「Windows 已保护你的电脑」，点「更多信息 → 仍要运行」即可
* macOS 的 .app 是 ad-hoc 签名，第一次打开要在「隐私与安全性」里点「仍要打开」
* 从 0.5.8 升到 0.5.9 的这一次，文件名还是旧代码决定的（改名逻辑本身要等 0.5.9 跑起来才生效）；
  0.5.9 → 0.6.x 的更新就会看到文件名跟着跳

## 致谢

* [@kairui1108](https://github.com/kairui1108)：最早把酷态科 10 号 Ultra 的 BLE 协议写下来的两个项目
* [@editwe](https://github.com/editwe)、[@a1287448852](https://github.com/a1287448852) 与所有提 issue、提 PR 的人
