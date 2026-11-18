# Windows 托盘 BLE 功率看板 · 技术选型调研

> 目标产品：一个**常驻系统托盘、占用小、点开即连**的 Windows 小工具，连接酷态科 10 号 Ultra 充电器（BLE GATT），实时显示充/放电功率。
> 本文只做**选型**，不含产品代码。BLE 协议细节见 `docs/protocol-research.md`（另一位队友负责）。
> 调研时间：2026-10。数字标注方式：**[实测]** 有可复现出处（开源项目发布物/官方 issue）｜**[文档]** 官方文档明示｜**[估算]** 本报告按同类项目推算，未在本机实测（本机无 dotnet/rust/go 工具链）。

---

## 1. 结论先行（TL;DR）

| 项 | 结论 |
|---|---|
| **推荐主方案** | **C# / .NET 8 或 9 + WinForms NotifyIcon + WinRT（Windows.Devices.Bluetooth）** |
| 交付体积 | self-contained 单文件 **≈ 52 MB**［实测同类项目］；改用框架依赖（需装 .NET Desktop Runtime）**≈ 1 MB**［估算］ |
| 常驻内存 | **≈ 40–70 MB** 工作集［估算］ |
| 开发成本 | **最低**：WinRT 投影随 SDK 自带，BLE + 托盘各 100 行内可跑通 |
| **备选（体积/内存优先）** | **Rust + btleplug + tray-icon**：**≈ 5–10 MB** 二进制、**≈ 15–25 MB** 内存［估算］，代价是开发迭代慢 2–3 倍 |
| **明确排除** | WinUI 3 / WinAppSDK（重、依赖 WebView2）、Tauri 2（BLE 仍要 Rust，白加 WebView2 内存）、Go（Windows 端 BLE 库不支持连接/通知）、AutoHotkey（无 BLE 能力）、WPF（不可裁剪且更重） |
| Python + bleak | 只适合做「协议验证原型」，不适合作为最终常驻形态（内存 60–120 MB、启动慢）［估算］ |

一句话：**先 C# 打通协议与交互（快），确定要极致瘦身再评估移植 Rust**。两条路在 Windows 上最终都调用 WinRT，协议解析代码可 1:1 翻译。

---

## 2. 约束与判定标准

| 约束 | 权重 | 说明 |
|---|---|---|
| 常驻内存低 | ★★★ | 用户明确要求"占用小"；常驻进程内存比安装包体积更影响体验 |
| 开发/迭代成本低 | ★★★ | 充电器是私有 GATT 协议，需要反复试命令、抓通知，调试循环越快越好 |
| BLE 能力完整 | ★★★（硬门槛） | 必须能：扫描 → 连接 → 读特征 → **订阅通知（notify）** → 写命令 |
| 启动快 | ★★ | 托盘工具应在 1 s 内可用 |
| 体积 | ★★ | 50 MB 可接受但要说明；能用 5–10 MB 更好 |
| Win10/11 兼容 | ★★ | 无需管理员权限、无需额外运行时最佳 |
| UI 表现力 | ★（低） | 托盘 + 一个轻量弹窗即可，不需要复杂前端 |

---

## 3. Windows 上到底怎么读 BLE（关键技术事实）

### 3.1 三条访问路径

| 路径 | 具体 API | 语言可用性 | 需管理员 | 需先配对 | 后台自动重连 | 读加密特征 | 结论 |
|---|---|---|---|---|---|---|---|
| **WinRT**（UWP 投影） | `Windows.Devices.Bluetooth.BluetoothLEDevice`、`GenericAttributeProfile`、`Advertisement.BluetoothLEAdvertisementWatcher`、`GattSession.MaintainConnection` | C#（SDK 自带投影）、C++/WinRT、Rust（windows-rs / btleplug）、Python（PyWinRT / bleak） | 否 | 访问加密特征前需完成配对（可用 `DeviceInformation.Pairing` 触发） | 需自己写重连循环（`MaintainConnection` 只保证会话保持，不保证设备不睡） | 是：Bond 后由系统加密层自动处理 | **事实标准，选它** |
| **Win32 BLE** | `bluetoothleapis.h`：`BluetoothGATTRegisterEvent` / `BluetoothGATTGetServices` | 基本只有 C/C++；无官方 C#/Rust 绑定 | 否 | 同上 | 回调+线程模型，样板代码多 | 能，但需自己处理 | 仅在需要极致小体积的原生程序里考虑 |
| **第三方库** | btleplug（Rust）、bleak（Python）、tinygo-org/bluetooth（Go）〔早期还有 bless 等 Rust 库，仓库已不可访问/少维护，不列入候选〕 | 见 §4 | 否 | 同上 | 库封装重试 | 依库 | **Windows 后端全部是 WinRT**，所以选库 ≈ 选语言 |

### 3.2 三条关键结论

1. **"用什么语言"其实等价于"用什么方式调用 WinRT"**：btleplug 的 Windows 后端就是 WinRT/Win32 封装；bleak 的 Windows 后端文档明写 *"The Windows backend of bleak is written using PyWinRT to provide bindings for the Windows Runtime (WinRT)"*（[bleak 文档](https://bleak.readthedocs.io/en/latest/backends/windows.html)）。协议实现可跨语言移植。
2. **客户端角色不需要管理员权限**，也不需要打包成 UWP/MSIX（Win32 桌面程序可直接调用 WinRT 设备 API；TFM 设 `net8.0-windows10.0.19041.0` 即可拿到投影）。真正需要处理的是**配对/加密特征**：私有协议的功率特征通常是加密的，必须先 bond（配对）才能读，这一点在协议调研里要一并确认。
3. **Go 基本不可用**：`tinygo-org/bluetooth` 的 Windows（WinRT）后端只实现了 **Scanning**，连接/写特征/收通知全是 ✗（[README 平台矩阵](https://github.com/tinygo-org/bluetooth)）。用 Go 等于自己手写 WinRT 绑定，性价比极低。

---

## 4. 技术栈对比（含具体数字与出处）

### 4.1 主对比表

| 方案 | 交付体积 | 常驻内存 | 冷启动 | BLE 能力 | 开发速度 | 判定 |
|---|---|---|---|---|---|---|
| **C# .NET 8/9 WinForms + NotifyIcon**（self-contained 单文件） | **52.0 MB**［实测：开源同类项目 v1.8.0 单文件 exe］ | 40–70 MB［估算］ | 0.3–0.8 s［估算］ | 完整（WinRT 原生） | ★★★ | **推荐** |
| C# WinForms（框架依赖） | ≈1 MB + 需 .NET Desktop Runtime［估算］ | 同上 | 更快 | 完整 | ★★★ | 分发方便时可选 |
| **C# Win32 托盘（Shell_NotifyIcon P/Invoke）+ WinRT + Native AOT** | 7–15 MB［估算］ | 20–40 MB［估算］ | 0.1–0.3 s | 完整 | ★（手写托盘/菜单/DPI 代码） | 瘦身备选，风险中 |
| WPF | **52.8 MB**［实测：同类项目安装包 v1.0.1］ | 60–110 MB［估算］ | 0.6–1.2 s | 完整 | ★★ | 不推荐（比 WinForms 重） |
| WinUI 3 / WinAppSDK | 60–150 MB（含 WinAppSDK + WebView2 依赖）［估算］ | 100–200 MB［估算］ | 1–2 s | 完整 | ★★ | **排除** |
| Rust + btleplug + tray-icon | 5–10 MB［估算］ | 15–25 MB［估算］ | <0.2 s | 完整（见 4.3） | ★ | **备选（体积最优）** |
| Tauri 2（前端 UI + Rust 后端） | 3–10 MB［估算］ | 80–150 MB（WebView2 多进程）［估算］ | 0.5–1.5 s | 依赖 Rust 端（可为 btleplug） | ★★ | **排除**：BLE 依然要写 Rust，却额外背 WebView2 |
| Go + tinygo-org/bluetooth | 8–12 MB | 20–30 MB | <0.2 s | **不完整（Windows 只能扫描）** | ★ | **排除** |
| Python + bleak + pystray（PyInstaller） | 15–40 MB［估算］ | 60–120 MB［估算］ | 1–3 s | 完整（PyWinRT） | ★★★ | 仅原型 |
| AutoHotkey | 1–3 MB | 10–20 MB | 快 | **无 BLE** | — | **排除** |

### 4.2 为什么 C# 的体积是 50 MB 而不是 10 MB（关键证据）

- **WinForms / WPF 目前都不支持裁剪**。官方文档 *Known trimming incompatibilities* 写得很直白：
  - Windows Forms：*"Windows Forms … is heavily reliant on built-in COM marshalling … so **trimming support for Windows Forms apps is disabled in the .NET SDK currently**"*（[Learn](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)）
  - WPF：*"almost no WPF apps are runnable after trimming, so trimming support for WPF is currently disabled in the .NET SDK"*
  - 对应 epic 至今仍是 open：`dotnet/winforms#4649`「Make WinForms trim compatible」(open)、`dotnet/wpf#3811`「WPF is not trim-compatible」(open)［实测 GitHub API state=open］
- 因此 self-contained WinForms 只能**整包携带运行时**：.NET 5 时代实测 *"145 MB (83 MB when using trimming)"*（[Hanselman](https://www.hanselman.com/blog/how-to-make-a-winforms-app-with-net-5-entirely-from-the-command-line-and-publish-as-one-selfcontained-file)）；到 .NET 8/9 打磨后落到 **≈52 MB**（下面的真实项目实测）。
- 想再小只能走 **Native AOT**（`PublishAot=true` + 强制关掉裁剪错误 `_SuppressWinFormsTrimError=true`，属"官方不支持但能跑"）：社区在 `dotnet/winforms#9911` 里实测 **54 MB**，并分析出 PresentationFramework 误带入、资源无法裁剪等问题，理论可压到 **~7 MB**（[issue](https://github.com/dotnet/winforms/issues/9911)）。**这条路不建议产品化**。
- 若要"小 + C#"，正确姿势是**不用 WinForms/WPF**：Win32 `Shell_NotifyIcon` 托盘 + WinRT BLE + Native AOT。WinRT 侧需要 CsWinRT 的 AOT 优化（CsWinRT 2.2.0 起为 WPF/WinForms 场景提供 opt-in 属性，2.3.x 明确"marshalling of non-blittable arrays under Native AOT"，3.0 预览版"with AOT and trimming in mind from the start"，见 [CsWinRT releases](https://github.com/microsoft/CsWinRT/releases)），可行性高但需要自己踩坑。

### 4.3 Rust 侧的能力核对

btleplug 官方平台能力表在 **Windows 列全为 ✓**：适配器启用、设备发现、服务/特征/描述符发现、GATT Connect/Disconnect（含事件）、**Write to Characteristic、Read from Characteristic、Subscribe/Unsubscribe、Get Characteristic Notification Event**、读 MTU/RSSI/连接参数（[README](https://github.com/deviceplug/btleplug)）。也就是说本产品需要的每一件事它都有。附带生态：`tray-icon`（Rust 托盘库）、`windows-rs`（WinRT 直调，可绕过 btleplug 处理配对细节）。

---

## 5. 推荐方案与理由

### 5.1 主推：C# .NET 8/9 + WinForms NotifyIcon + WinRT

理由（按重要性）：

1. **调试循环最短**：充电器是私有协议，需要反复"写命令 → 等 notify → 解析"。C# + WinRT 全程强类型、可断点、`GattCharacteristic.ValueChanged` 事件直接可用；换语言只会拖慢协议摸索。
2. **零额外依赖风险**：WinRT 投影随 .NET SDK/Windows SDK 提供，`<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>` 就能用 `Windows.Devices.Bluetooth`，不需要 NuGet 第三方 BLE 库、不需要运行时组件。
3. **托盘是 WinForms 的舒适区**：`NotifyIcon` + `ApplicationContext` 是"无窗口常驻"最短路径，动态功率图标用 `Graphics` 画 Bitmap 再 `Icon.FromHandle` 即可。
4. **有现成的同架构开源参照**（见 §7），出问题可对照。
5. 体积 52 MB 是缺点但**可控**：可以给两个发布通道——"绿色单文件 52 MB"与"安装包 1 MB + .NET Desktop Runtime"。对一个本地小工具来说，磁盘 52 MB 的痛感远低于内存和稳定性问题。

### 5.2 备选：Rust + btleplug + tray-icon（当"体积/内存"被提到第一优先级）

- 收益：二进制 5–10 MB、内存 15–25 MB、启动 <0.2 s，**没有运行时依赖**。
- 成本：首版工期约 C# 的 1.5–2 倍；WinRT 的配对/重连边角要靠 `windows-rs` 自己补；UI 需要手写（`tray-icon` + 自绘弹窗，或用 `eframe` 拉一个窗口，但那会把依赖和内存拉高）。
- 建议路径：**C# 先验证协议** → 若要长期分发小体积版本，再把协议解析层（纯字节逻辑）搬到 Rust。

### 5.3 不建议

- **WinUI 3 / WinAppSDK**：为托盘工具引入 MSIX/WinAppSDK 自包含部署 + WebView2 依赖，体积与内存双输，且 UWP 式的单实例/自启更麻烦。
- **Tauri 2**：宣传的"小体积"优势被 WebView2 常驻多进程吃掉，且 BLE 必须落到 Rust（btleplug 生态里有 `tauri-plugin-blec`），等于同时吃 Rust 的成本和 WebView 的内存。
- **WPF**：不可裁剪，安装包 52.8 MB［实测］，内存更高，换来的只是更漂亮的弹窗——托盘工具不需要。
- **Go / AutoHotkey**：BLE 能力硬缺口。

---

## 6. 托盘常驻形态的工程细节清单

| 关注点 | 做法 | 备注 |
|---|---|---|
| 开机自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写一项；或计划任务（可延迟启动、可"最高权限"） | HKCU 无需管理员；卸载时要清理该项 |
| 单实例 | 命名 `Mutex`（如 `Global\CuktechTray`）；第二个实例启动时发信号让首实例弹窗 | WinForms 里在 `Main` 最前面判断 |
| 无窗口启动 | 用 `ApplicationContext` 承载 `NotifyIcon`，`Application.Run(ctx)`；不要 `Show()` 主窗体 | 弹窗用 `Form { ShowInTaskbar = false, StartPosition = Manual }` + 失焦自动隐藏 |
| 托盘图标动态显示功率 | 按 DPI 目标尺寸（16/20/24/32）渲染 Bitmap → `Icon.FromHandle` → `NotifyIcon.Icon`；旧 Icon 要 `DestroyIcon` 防 GDI 句柄泄漏 | 数值刷新建议 1–2 s 节流，避免托盘闪烁 |
| 悬停提示 | `NotifyIcon.Text` 受 `NOTIFYICONDATA.szTip` 限制（版本 4 = 127 字符，老版本 63），超长会抛异常 | 详细数据放进弹窗 |
| 通知气泡 | Win10/11 上 `ShowBalloonTip` 会被转成 Toast；纯 WinRT Toast 需要 **AUMID + 开始菜单快捷方式**（未打包应用的通病） | 参考项目 BT-Charge-Indicator 就"首次运行创建开始菜单快捷方式"以便弹原生通知 |
| 右键菜单 | `ContextMenuStrip`；Win11 下系统托盘菜单为圆角新样式 | 需要"退出/重连/复制诊断信息"等项 |
| 日志 | 轻量滚动日志文件（Serilog 或自写），默认 %LOCALAPPDATA% | 协议调试期务必记录原始字节（hex） |
| 崩溃恢复 | 全局 `UnhandledException` + 关键循环 try/catch 重试；可选注册"失败重启" | 不建议上 Windows 服务（托盘交互会变复杂） |
| 设备掉线重连 | `GattSession.MaintainConnection = true` + 事件驱动重连 + 指数退避；广播监听（`BluetoothLEAdvertisementWatcher`）判断设备是否在附近 | Windows 不会替你保证会话永远活着 |
| 兼容性 | 目标 Win10 1809+ / Win11；高 DPI 需 `PerMonitorV2`；Win11 托盘与 Win10 视觉差异用系统 API 自然适配 | 蓝牙可用性应做启动自检并给出可读错误 |
| 权限 | 桌面程序连通告（无 UWP capability 需求） | 仅当读加密特征时需要先配对 |

---

## 7. 可借鉴的开源项目

| 项目 | 语言/框架 | 与本项目的关系 | 值得抄的点 |
|---|---|---|---|
| [L1GHTSHAPER/BT-Charge-Indicator](https://github.com/L1GHTSHAPER/BT-Charge-Indicator) | C# / .NET 9 / WinForms | **最接近的同构参照**：托盘 + 蓝牙电池（含 BLE GATT Battery Service）+ 通知 | 52.0 MB 单文件发布［实测］；多套托盘图标风格＝"动态图标"完整实现；首次运行建开始菜单快捷方式以弹 Toast；"便携 exe 可直接搬走"的设计 |
| [zonnedev/bluetooth-battery-indicator](https://github.com/zonnedev/bluetooth-battery-indicator) | C# / .NET 8 / WPF | 托盘 + 弹窗 + 历史曲线 | 52.8 MB 安装包［实测］；点击托盘弹小面板、设备昵称、7 天历史、低电提醒的 UX 结构 |
| [LAB02-Research/HASS.Agent](https://github.com/LAB02-Research/HASS.Agent) | C# / WinForms | 1.9k★ 常驻托盘的"传感器采集客户端" | 后台周期采样的调度器、托盘状态切换、配置持久化的工程组织方式 |
| [ZGMFX01A/Wireless-Device-Battery-Monitor](https://github.com/ZGMFX01A/Wireless-Device-Battery-Monitor) | Python | 想看"最省事的原型"长什么样 | Python + 托盘的原型结构（仅参考，不作为交付栈） |
| [deviceplug/btleplug](https://github.com/deviceplug/btleplug) | Rust | 走 Rust 路线时的 BLE 层 | `examples/` 里就有扫描/连接/订阅通知的完整样板 |
| Bluetooth LE Explorer（Microsoft Store 工具） | 官方调试工具 | **开发期必装** | 直接看充电器暴露的 Service/Characteristic UUID、手动读写、开通知看原始数据 |

---

## 8. 风险与对策

| 风险 | 影响 | 对策 |
|---|---|---|
| **设备要求配对/加密**才能读功率特征 | 首连体验变差（弹配对框） | 协议调研阶段先确认；产品里显式设计"配对引导"流程 |
| WinRT 连接不稳定 / `BluetoothLEDevice` 对象被系统回收 | 偶发连不上、通知断流 | 保持静态引用 + `MaintainConnection` + 事件重连 + 状态机日志 |
| 单文件 52 MB 的观感 | 用户觉得"怎么这么大" | 双发布通道；或后期转 Rust/AOT 版本 |
| WinForms 单文件首次启动需解压 | 冷启动慢 0.5–1 s | 关闭 `EnableCompressionInSingleFile` 或接受 |
| Native AOT + WinRT 组合未验证 | 走瘦身路线时可能踩坑 | 先用小 demo 验证 `Shell_NotifyIcon` + `BluetoothLEDevice` 能否 AOT 通过，再投入 |
| 私有协议随固件变化 | 数值解析失效 | 解析层做成可配置/可热更新（JSON 映射），并保留原始 hex 日志 |

---

## 9. 数据来源与标注说明

**实测（有可复现出处）**
- 52.0 MB：`BT-Charge-Indicator.exe`，GitHub Release v1.8.0（C# .NET 9 WinForms 托盘蓝牙应用，self-contained 单文件）— [releases](https://github.com/L1GHTSHAPER/BT-Charge-Indicator/releases)
- 52.8 MB：`BluetoothBatteryIndicator_Setup.exe`，GitHub Release v1.0.1（C# .NET 8 WPF）— [releases](https://github.com/zonnedev/bluetooth-battery-indicator/releases)
- WinForms AOT 54 MB / 理论 ~7 MB、WinForms 裁剪仍不可用（epic #4649 open、WPF #3811 open）：[dotnet/winforms#9911](https://github.com/dotnet/winforms/issues/9911)、[#4649](https://github.com/dotnet/winforms/issues/4649)、[dotnet/wpf#3811](https://github.com/dotnet/wpf/issues/3811)
- btleplug Windows 能力表全绿：[README](https://github.com/deviceplug/btleplug#platform-feature-table)
- Go（tinygo-org/bluetooth）Windows 仅扫描可用：[README](https://github.com/tinygo-org/bluetooth)

**官方文档**
- 裁剪不兼容（WinForms/WPF 裁剪被 SDK 关闭）：[learn.microsoft.com …/trimming/incompatibilities](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)
- 自包含 + 裁剪概念：[Trim self-contained applications](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trim-self-contained)
- Native AOT 限制（必须裁剪、体积随泛型实例化膨胀）：[Native AOT overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- WinRT BLE API：[BluetoothLEDevice](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice)、[GattCharacteristic](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristic)、[BluetoothLEAdvertisementWatcher](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcher)、[DeviceInformation.Pairing](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.deviceinformation.pairing)
- Win32 BLE API（bluetoothleapis.h）：[learn.microsoft.com/windows/win32/api/bluetoothleapis](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothleapis/)
- WinAppSDK 未打包部署：[deploy-unpackaged-apps](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
- CsWinRT 的 AOT/裁剪进展（2.2.0 opt-in 属性、2.3.x Native AOT 修复、3.0 以 AOT 为设计目标）：[CsWinRT releases](https://github.com/microsoft/CsWinRT/releases)
- bleak Windows 后端 = PyWinRT：[bleak 文档](https://bleak.readthedocs.io/en/latest/backends/windows.html)
- Tauri 体积优化手段（仅 Rust 编译选项，不含 WebView2 运行时）：[Tauri App Size](https://tauri.app/concept/size/)

**估算（本报告推算，未在本机实测）**
- 各栈内存/启动时间：按同类项目经验区间给出；C# WinForms 40–70 MB、Rust 15–25 MB、Tauri 80–150 MB、Python 60–120 MB。
- Rust/C#-AOT 二进制体积：按 release + `opt-level` 优化、strip、LTO 的典型结果区间。
- 框架依赖版 ≈1 MB：不含运行时，仅应用与依赖程序集。

> 本机环境：Windows 11 Build 26100；未安装 dotnet SDK / Rust / Go，故以上体积与内存均为外部实测引用 + 区间估算，未做本机编译验证。若需要硬数据，建议后续做一个 30 分钟的对照实验：分别用 .NET 8 WinForms、Rust（btleplug+tray-icon）各写一个"托盘 + 连接充电器读一次功率"的最小程序，记录 `私有工作集`、单文件体积、冷启动时间，再定稿。
