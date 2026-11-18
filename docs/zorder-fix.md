# 面板 z-order 修复记录（"锁屏回来还压在最上面"）

> 需求原话（第 7 轮）："现在还是有问题 锁定桌面后还是有几率在最顶上的窗口"
> 第 6 轮的需求是："面板常驻桌面但是你做成了始终置顶窗口 改掉"

## 现象

常驻面板被用户理解成"待在桌面上、别人能盖住它"。但实际会出现两种情况：

1. 常驻面板自己压在别的窗口上面；
2. 更隐蔽的一种 —— 临时浮层（点托盘弹出来的那个）在锁屏（Win+L）、切用户、别的程序抢前台之后，
   没收到失焦事件，于是带着 `WS_EX_TOPMOST` 一直挂着；解锁回来就看到它压在最上面。

## 根因

旧实现里"置顶"散落在四个地方，没有任何单一出口：

| 位置 | 旧代码 | 问题 |
| --- | --- | --- |
| `TrayApp` 构造 | `_panel.TopMost = settings.PanelTopMost` | 配置里残留的 `true` 直接生效 |
| `TrayApp.ShowPanel` 常驻分支 | `_panel.TopMost = _settings.PanelTopMost` | 同上，且没有回收路径 |
| `TrayApp.ShowPanel` 浮层分支 | `_panel.TopMost = true` | 只靠 `OnDeactivate` 收；锁屏时收不到该事件 |
| `TrayApp` 截图分支 | `_panel.TopMost = true` | 开发用，漏复位 |
| `PanelForm.OnDeactivate` | 只 `Hide()` | **从不复位 TopMost**，窗口再显示就还是置顶层 |

关键点：`OnDeactivate` 是"收起"的唯一触发，而锁屏 / 抢前台不一定能可靠送达 —— 这就是"有几率"的来源。

## 修复

### 1. 置顶只有一个出口

`PanelForm` 新增 `AllowTopMost`（"我允许自己置顶吗"）与唯一写入口 `SyncTopMost()`：

```csharp
public void SyncTopMost()
{
    bool want = AllowTopMost;
    if (TopMost != want) TopMost = want;
}
```

全工程不再有第二处给 `TopMost` 赋值的地方。谁想改层级，只能改 `AllowTopMost` 再 `SyncTopMost()`。

### 2. 收起与复位绑在一起

`HideNow(string why)`：先 `TopMost = false`，再 `Hide()`，并记日志 `[panel] hide (why)`。
所有收起路径（菜单关闭、Esc、切换固定、失焦、锁屏、看门狗、TTL）统一走它。

### 3. 看门狗（350ms）

`GuardTick()` 每拍做两件事：

- `SyncTopMost()` —— 不管哪条分支漏了，这里都会把"不该置顶的"按下去；
- 浮层若已显示、且连续两拍（~700ms）前台都不是本进程的窗口 → `HideNow("guard")`。
  连续两拍是为了避开刚 Show 出来那一瞬间的前台竞态，不让面板闪一下就没。

### 4. 锁屏 / 解锁事件

`TrayApp` 订阅 `SystemEvents.SessionSwitch`（经 `Post()` 回 UI 线程）：

- `SessionLock` / `ConsoleDisconnect` / `RemoteDisconnect` → `OnSessionLocked()`：非常驻就收起并联复位；
- `SessionUnlock` / `ConsoleConnect` / `RemoteConnect` → `OnSessionUnlocked()`：只按策略摆正，不主动弹面板。

### 5. 浮层最长寿命

`PanelForm.AutoHideAfterMs`（0 = 不限）：

- 开机自启那种"开场浮层"设 20 秒，到点自己收；
- 用户主动从托盘点开的浮层不限时，失焦即收；
- 常驻面板恒为 0 —— 用户自己决定它留多久。

### 6. 配置版本 v1 → v2

`Settings.CurrentConfigVersion = 2`。读到旧配置时执行一次迁移：`PanelTopMost = false` 并回写。
理由：历史配置里那个 `true` 是程序自己写的，不是用户选的，不能让它继续生效。
真想要置顶，在面板右键菜单里重新勾一次"总在最前"即可（默认就是关的）。

## 取证（真机 exstyle）

用 `tools\window-flags.ps1`（EnumWindows + `GetWindowLong(h, -20)`）读扩展样式，
`WS_EX_TOPMOST = 0x8`，所以 `0x10000` = 普通窗口，`0x10008` = 置顶窗口。

| 场景 | 期望 | 实测 |
| --- | --- | --- |
| 旧配置残留 `PanelTopMost=true` + `--pin` | 迁移清掉，不置顶 | `topmost=False exstyle=0x10000`，配置变成 `ConfigVersion=2 / PanelTopMost=false` |
| 启动浮层 +2s | 短暂置顶 | `topmost=True exstyle=0x10008` |
| 启动浮层 +15s | 还在 | `topmost=True exstyle=0x10008` |
| 启动浮层 +24s（TTL 20s 到点） | 自己收起 | `none`（进程仍活着，蹲在托盘） |
| 浮层挂着时被记事本抢走前台 | 700ms 内收起 | 抢前台后立刻 `none` |

复现命令（需要先设 `DOTNET_ROOT`）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\verify-zorder.ps1   # 常驻迁移 + 三种模式
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\verify-zorder3.ps1  # TTL
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\verify-zorder4.ps1  # 抢前台兜底
```

> 注意：`verify-zorder3/4` 故意把 MAC 改成一个不存在的邻居地址，只验证窗口层级，不抢充电器的 BLE 连接。
> 跑完记得把 `settings.json` 里的 MAC 改回真实值。

## 一个测试坑（浪费了两轮）

程序是单实例的（`Local\CuktechTray.SingleInstance`）。如果托盘里已经有一个实例在跑，
新起的 `CuktechTray.exe` 会**立刻静默退出**：不写日志、不出窗口、`window-flags` 查到 `none`。
排查时第一件事就是 `Get-Process -Name CuktechTray`，先全杀掉再测。
