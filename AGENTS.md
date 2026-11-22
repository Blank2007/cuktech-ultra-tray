# AGENTS.md —— 给未来的 AI / 协作者

这份文件写给后续接手这个仓库的编码代理（以及人类协作者）。它不是给人看的文档，
给人看的在 README.md 和 开发日志.md。

## 这是什么

一个 Windows 托盘小程序，通过蓝牙 BLE 直连「酷态科 10 号 Ultra」充电器，
在托盘图标、功率面板、任务栏组件、控制页上显示 / 控制四路端口的功率。

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
.\build.ps1              # Release 构建
.\build.ps1 -Publish     # 单文件自包含 exe -> dist\CuktechTray.exe（约 72 MB）
.\build-setup.ps1        # 把 exe 压缩嵌进安装程序 -> dist\CuktechTray-Setup.exe（约 127 MB）
.\run.ps1                # 启动
.\run.ps1 -Panel         # 启动并直接弹面板
```

`build-setup.ps1` 会先生成 `setup/Payload/payload.bin` 与 `setup/Payload/PayloadInfo.g.cs`
（66 MB 的内嵌载荷，不进仓库）。发布版本号改在两个 csproj 的 `<Version>`。

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

## 验证界面时的注意事项（都是真踩过的）

* 抓窗口截图要用**真屏 BitBlt**（`tools/shot-screen.ps1`），别用 PrintWindow：
  从 PowerShell 启动的进程 DPI 会被虚拟化，PrintWindow 只画出窗口的一部分，
  看起来像「界面被裁了」，其实是抓图工具的问题。
* 任务栏组件在**低完整性级别**进程里挂不上（`SetParent` 返回错误 5），
  从沙箱/服务/计划任务里启动时属正常现象，不是 bug。
* 面板失焦会自动收起，抓图用 `--panel`（居中且不自动收起）。

## 文档风格

* 中文正文用全角标点，技术名词保留原文（BLE、GATT、TLV、PIID）
* 数字要给具体值（字节数、毫秒、SHA256），不要写「更快了」这种
* 踩过的坑写进 开发日志.md，不要只留在 commit message 里
