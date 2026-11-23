# AGENTS.md —— 给未来的 AI / 协作者

这份文件写给后续接手这个仓库的编码代理（以及人类协作者）。它不是给人看的文档，
给人看的在 README.md 和 开发日志.md。

## 这是什么

一个 Windows 托盘小程序，通过蓝牙 BLE 直连「酷态科 10 号 Ultra」充电器，
在功率面板、任务栏组件、控制页上显示 / 控制四路端口的功率；托盘图标本身只是一枚状态徽标
（0.4.0 起不再画功率数字，功率交给任务栏那一小块常显）。

## 硬约束（别破坏）

1. **单文件自包含发布**：`build.ps1 -Publish` 必须产出可在没装 .NET 的机器上直接跑的 exe。
   不要引入需要额外 native 依赖或需要 runtime 安装器的包。
2. **不写系统目录、不弹 UAC**：程序只写当前用户（HKCU、%LOCALAPPDATA%、exe 同级目录）。
   安装程序默认装到 `%LOCALAPPDATA%\Programs\CuktechTray`。
3. **配置向后兼容**：`Settings` 加字段要给默认值，`ConfigVersion` 负责迁移，别让老用户的 settings.json 失效。
4. **网络只用在两处**：首次扫码登录（小米云，换设备时才走）和右键菜单「检查更新」（GitHub Releases，点了才走）。
   认证成功后的运行期只走蓝牙，读数和控制一律不联网。
5. **凭据不进仓库**：`settings.json`、`session.json`、`logs/` 都在 .gitignore 里；
   文档里写示例一律用占位值（`AA:BB:CC:DD:EE:FF`、`00112233445566778899aabb`）。
6. **不改系统外观**：深色菜单、深色面板都是本程序自绘的，不要去改系统主题或注册表外观项。
7. **先方案、后动手；推送前必须问**：加功能或改行为之前，先说清楚要做什么、怎么做、影响哪些文件，等用户点头再写代码。
   打安装包、推 GitHub、发 Release、往用户机器目录（`D:\KTK`）部署，每一步都要先问一句「要发吗」，得到明确同意才执行。
   未经同意只允许在本机做只读检查、编译和自测。

## 构建

需要 .NET 8 SDK。

```powershell
.\build.ps1              # WinForms 版（0.3.3）Release 构建
.\build.ps1 -Aot         # NativeAOT 版（0.4.0）-> src\CuktechTrayAot\out\CuktechTray.exe（约 9.2 MB）
.\build.ps1 -Aot -Publish # 再拷成 dist\CuktechTray.exe（覆盖绿色版，发布前必须先问用户）
.\build.ps1 -Publish     # 单文件自包含 exe -> dist\CuktechTray.exe（约 72 MB）
.\build-setup.ps1        # 把 exe 压缩嵌进安装程序 -> dist\CuktechTray-Setup.exe（约 127 MB）
.\run.ps1                # 启动
.\run.ps1 -Panel         # 启动并直接弹面板
.\run.ps1 -Aot -Panel    # 启动 AOT 版并直接弹面板
```

`build-setup.ps1` 会先生成 `setup/Payload/payload.bin` 与 `setup/Payload/PayloadInfo.g.cs`
（66 MB 的内嵌载荷，不进仓库）。发布版本号改在两个 csproj 的 `<Version>`。

**改 .ps1 必须保留 UTF-8 BOM**：`powershell.exe` 对没有 BOM 的脚本按 ANSI 解码，
中文会变成乱码甚至把引号吃掉、报「字符串缺少终止符」。用编辑器存成「UTF-8 带 BOM」，别存成「UTF-8」。

## NativeAOT 版（0.4.0）

0.4.0 起多了一个不带运行时依赖的原生版本，源码在 `src/CuktechTrayAot/`。它自己只用
`.NET 8 + System.Drawing.Common` 加一层薄薄的 Win32 壳，界面全部自绘（没有 WinForms / WPF），
协议层、数据层、设置、自测这些**共用 WinForms 版的那份源码**（`CuktechTrayAot.csproj` 里用
`<Compile Include="..\CuktechTray\XXX.cs" Link="Shared\XXX.cs" />` 引进来，改一处两边一起变）。

* 产物 `src\CuktechTrayAot\out\CuktechTray.exe`：9,633,792 字节（9.19 MB），单文件，目标机不装 .NET 也能跑。
  其中 273,408 字节是内置的安装/卸载与自动更新（见下），换句话说：**这一个 exe 既是绿色版、也是安装包，还能自己升级自己**。
* 构建用 `.\build.ps1 -Aot`。AOT 要 C 链接器，本机没有 MSVC/vcvarsall，脚本自己用
  `.tools\lld\lld-link.exe` + `.tools\xwin` 的 crt/ucrt/um 库，设 `LIB` 并传
  `-p:IlcUseEnvironmentalTools=true -p:CppLinker -p:CppLibCreator`；别绕开脚本手敲 `dotnet publish`。
* 已知噪声（不影响产物）：还原时 `NU1903`（Microsoft.DotNet.ILCompiler 8.0.32 有已知漏洞）；链接期 `LNK4099`（缺 MSVC 自己的 pdb）。
* 自测同 WinForms 版：`src\CuktechTrayAot\out\CuktechTray.exe --selftest`。
* 界面截图不需要真屏：`--render` 直接把读数/控制/模式三页画成 400x450 的 PNG，锁屏也能出图；加
  `--render-demo` 再塞一份假的「已连接」快照；`--render-pinned` 渲染成图钉已固定；`--render-scale=<倍数>`
  指定缩放（和 0.3.3 的 800x900 真屏截图比对时用 1.25，**不能拿 2.5 倍当基准**：`S()` 在 2.5 倍下累加取整，
  位置会差 3 px，2.5 倍图只能看观感）。**`--render` 必须一起给**：只给 `--render-demo` 会掉进正常流程，
  变成常驻托盘程序。另有 `--font-probe`（`GetTextFaceW` 读回真实字体名）与 `--alpha-probe`（同底色上画几档
  alpha，同时用屏幕兼容 DDB 与 GDI+ 位图报 RGB）两个诊断开关。
* **真机上那份正在跑时，诊断模式什么都不出**：它占着单实例互斥体 `Local\CuktechTray.SingleInstance` 和
  命名事件 `Local\CuktechTray.ShowPanel`，新进程会走「让位退出」分支——日志只有一行 `[start]`、零 PNG。
  加 `--standalone` 跳过单实例检查与事件创建（低权限进程连那个事件都打不开，会直接抛
  `UnauthorizedAccessException` 在 `Program.cs` 的 `new EventWaitHandle(...)`）。**这个开关只给诊断用，
  正常启动绝不要加**：两个实例会同时抢蓝牙。
* **改渲染代码的三条硬规矩**（都是踩过的坑，`src\CuktechTrayAot\PanelWindow.cs` / `Surface.cs` / `GdiFont.cs`）：
  ① 坐标换算一律 `(int)(v * Scale)` 截断，别 `Math.Round`——旧版就是截断，四舍五入会让页签间距累加错 2 px；
  ② 字号量化一律向上取整（`MathF.Ceiling`）——旧版 GDI+ 拿到 13×1.25 会算成 17 px；
  ③ `Surface` 里别设 `CompositingQuality.HighQuality`——GDI+ 会按 gamma 校正混合，`alpha 34` 看起来像 83，
  旧版 WinForms 是在 sRGB 空间混的。另外表头这类中文文字必须直接用 `Microsoft YaHei UI`，别靠 `Segoe UI`
  的字体链接回退（回退字体的 ascent 不同，`DrawText` 垂直居中会整体偏 2 px）。
* **安装/卸载就是这一个 exe 自己干的**（`Installer.cs`，不需要单独的 Setup）：托盘菜单「安装到本机…」把
  正在跑的这份复制到 `%LOCALAPPDATA%\Programs\CuktechTray`，建开始菜单快捷方式，写 HKCU 卸载项与
  可选的自启项；菜单变成「从这台电脑卸载…」后可以从头拆干净。命令行等价物：
  `--install [--dir=<路径>] [--autostart] [--desktop] [--silent]`、`--uninstall [--silent]`、
  `--uninstall-clean <目录>`（扫尾用，别手敲）、`--shortcut-test <lnk 路径>`（只验 .lnk 生成与回读）。
  快捷方式用 `CoCreateInstance` + 裸 vtable 调 `IShellLinkW`/`IPersistFile`，**刻意没用 `[ComImport]`**。
* **自动更新也是这一个 exe 自己干的**（`Updater.cs`）：托盘菜单「检查更新」查到新版后，从 Release assets 里挑
  `CuktechTray-<版本>.exe`（跳过 `-Setup-`，选股逻辑在 `UpdateCheck.PickAsset`），下到 exe 同目录的
  `CuktechTray.exe.download`，边下边算 SHA256，字节数与 API 的 `digest` 都对上才允许替换。
  **运行中的 exe 换不掉**（映像被映射住，连改名都会被拒绝），所以先把自身复制到
  `%TEMP%\CuktechTray-update-<pid>.exe`，由它等主进程退出后动手：备份 `.old` → 覆盖 → 启动新版
  （`--tray-only`）→ 删备份 → `MOVEFILE_DELAY_UNTIL_REBOOT` 删自己；覆盖失败用 `.old` 还原，
  最多重试 60 次 × 500 ms（等杀软放句柄）。下载/校验在 `Updater.DownloadAsync`，替换在 `Replace` 与 `ApplyUpdate`。
  **别手敲 `--apply-update`**（它会把你指到的 exe 换成 payload）；调试用 `--update-selftest`（纯文件）和
  `--update-download-test --url= --sha= --size=`（真下载一次），两者都不碰正在运行的文件。

修改原则：0.4.0 之后的界面/交互改动只动 `src/CuktechTrayAot/`，`src/CuktechTray/` 那份
WinForms 壳当作 0.3.3 的归档，不再跟进（共性代码除外）。

## 回归测试

```powershell
.\dist\CuktechTray.exe --selftest      # 连接 -> 认证 -> 读四口，退出码 0/1，结果写 logs\app.log
```

真机才能跑。日志里认这几行：`[ok] 认证完成 …固件=…`、`[cfg] port_switch=0x0f`。

**坑**：连续快速重启进程会让 BLE 连接来不及释放，认证失败（`AccessDenied` 或
`未收到 RCV_RDY`）。关掉程序等 20 秒以上再启动。

## 代码地图

| 想改什么 | 去哪儿 |
| --- | --- |
| 托盘图标长什么样 | `src/CuktechTray/WattIcon.cs`、`WattStyle.cs` |
| 功率面板排版 | `src/CuktechTray/PanelForm.cs`、`UiTheme.cs` |
| 面板的控制页签（四路开关） | `src/CuktechTray/PanelForm.cs`（0.3.0 起与读数页同一个窗口，`Tab.Control`） |
| 面板的模式页签（充电模式四档） | `src/CuktechTray/PanelForm.cs`（`Tab.Mode`）、`DeviceClient.SceneModes` / `SetSceneModeAsync` |
| 右键菜单皮肤（圆角 / 亚克力） | `src/CuktechTray/MenuEffect.cs`（`Apply` / `IsBlurred`）；间距与绿勾在 `UiTheme.cs` 的 `Spacing` / `DrawCheck` |
| 任务栏那一小块 | `src/CuktechTray/TaskbarWidget.cs`（挂载、拖拽、alpha 绘制都在里面） |
| 协议 / 认证 | `src/CuktechMible/` |
| 扫码登录 | `src/CuktechLogin/` |
| 安装程序 | `setup/` |
| 配置项 | `src/CuktechTray/Settings.cs` |
| AOT 版：托盘图标（画） | `src/CuktechTrayAot/WattIcon.cs`（只有「渐变底 + 白闪电」一种样子，按已连接/未连接换色） |
| AOT 版：托盘图标（挂） | `src/CuktechTrayAot/TrayIcon.cs`（`Shell_NotifyIcon` 封装） |
| AOT 版：扫码配对向导（欢迎 / 扫码 / 选设备三页，自绘） | `src/CuktechTrayAot/WizardWindow.cs` |
| AOT 版：面板 / 任务栏 / 菜单 / 外壳 | `src/CuktechTrayAot/PanelWindow.cs`、`TaskbarWindow.cs`（含末尾的 `TaskbarPaint`）、`MenuWindow.cs`、`WinBase.cs`、`GdiText.cs` |
| AOT 版：Win32 互操作（P/Invoke 集中地） | `src/CuktechTrayAot/Native.cs` |
| AOT 版：入口与命令行开关（`--selftest` / `--render` / `--render --render-demo` / `--render-wizard` / `--setup` / `--wizard-demo` / `--panel` / `--check-update` / `--install` / `--uninstall`；更新的几条在 `Installer.RunCli`） | `src/CuktechTrayAot/Program.cs` |
| AOT 版：安装 / 卸载 / 快捷方式 / 注册表 | `src/CuktechTrayAot/Installer.cs`（含 `Shortcut` 类） |
| AOT 版：自动更新（下载 / 校验 / 替换 / 助手） | `src/CuktechTrayAot/Updater.cs` |
| 查最新版本、挑哪个资产、走代理还是直连 | `src/CuktechTray/UpdateCheck.cs`（`RunAsync` / `PickAsset` / `Proxy`） |
| 「检查更新」点下去之后的三条岔路 | `src/CuktechTrayAot/TrayApp.cs`（`AfterUpdateCheck` / `StartUpdate` / `AfterUpdateDownloaded`） |

## 验证界面时的注意事项（都是真踩过的）

* 抓窗口截图要用**真屏 BitBlt**（`tools/shot-screen.ps1`），别用 PrintWindow：
  从 PowerShell 启动的进程 DPI 会被虚拟化，PrintWindow 只画出窗口的一部分，
  看起来像「界面被裁了」，其实是抓图工具的问题。
* 任务栏组件在**低完整性级别**进程里挂不上（`SetParent` 返回错误 5），
  从沙箱/服务/计划任务里启动时属正常现象，不是 bug。
* 面板失焦会自动收起，抓图用 `--panel`（居中且不自动收起）。
* **装/卸在 DSH 沙箱里跑不完整流程**：沙箱把本工作区编译出来的 exe 关进受限容器，写
  `%APPDATA%\…\Start Menu`、桌面、`%TEMP%` 和 HKCU 注册表都会报「拒绝访问」（`hr=0x80070005`）。
  `--install --dir=<工作区内的目录>` 能验证「复制 exe + 搬 settings.json」，卸载能验证「删目录」；
  快捷方式与注册表这两步只能靠日志确认「失败了但不致命」。要到真机上让用户自己在沙箱外跑一次。
* 测卸载前务必先 `reg export` 备份 HKCU 的 `…\Uninstall\CuktechTray`，测完 `reg import` 还原；
  本机 `D:\KTK` 是用户正经装的 0.3.1，别让它被测试顺手改成 0.4.0 的路径。
* **取 exe 的退出码不要直接 `& $exe ...`**：它是 GUI 子系统程序，PowerShell 不等待，`$LASTEXITCODE` 是空的，
  后面的检查会跑在文件生成之前。用
  `cmd.exe /c ('start /wait "" "' + $exe + '" <args> & echo RC=%ERRORLEVEL%')`，
  实测 `--update-selftest` 141 ms、`--selftest` 6.1 s 都能拿到 RC。
* **测自动更新别拿正式产物当助手**：`--apply-update` 跑完会 `MOVEFILE_DELAY_UNTIL_REBOOT` 删掉自己，
  直接把 `out\CuktechTray.exe` 当助手跑，产物就废了。做法：把 exe 复制成 `e2e\CuktechTray.exe`（目标）、
  `e2e\payload.exe`（末尾 `Add-Content` 追加 4 字节，PE 照样能跑，好比对）、`e2e\helper.exe`（助手），
  用 WMI 起目标 → 起助手 → `Stop-Process` 杀掉目标（模拟用户点确定后退出）→ 助手替换并拉起新版。
  实测日志：`[update] 原进程已退出` → `旧版已备份` → `已替换` → `新版已拉起` → `更新完成，删掉旧版备份`。

## 文档风格

* 中文正文用全角标点，技术名词保留原文（BLE、GATT、TLV、PIID）
* 数字要给具体值（字节数、毫秒、SHA256），不要写「更快了」这种
* 踩过的坑写进 开发日志.md，不要只留在 commit message 里
