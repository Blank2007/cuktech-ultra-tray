# 把充电器实时总功率显示在 Windows 11 任务栏上

> 调研文档。范围：只做技术调研与方案评估，不含源码改动。
> 环境基准：Windows 11 家庭版中文 24H2，build 26100，任务栏为 XAML/Composition 实现，系统缩放 125%。
> 相关项目：D:\dsh projects\cuktech-ultra-tray（C# / .NET 8 / WinForms，托盘常驻，已有 PanelForm / ControlForm）。

---

## 0. 结论速览

| 问题 | 结论 |
| --- | --- |
| AFmediaBar 是什么 | GitHub 项目 `Fervent-Tempo/AF-Media-Bar`（约 1.1k star，C#/WPF），任务栏上的媒体控制条 |
| 它怎么贴到任务栏 | `SetParent` 把窗口挂成 `Shell_TrayWnd` 的子窗口（`WS_CHILD`），铺满整个任务栏，再用 `SetWindowRgn` 把可见/可点区域裁成媒体条那一块。**不用 AppBar，不用 DeskBand，不用 XAML island** |
| 第二参考 | **TrafficMonitor**（46.9k star，C++/MFC）同样用 `SetParent` 挂到任务栏；Win11 分支直接挂 `Shell_TrayWnd`，并把 `TrayNotifyWnd` 的矩形当作定位锚点，**1 秒轮询**重排 |
| 推荐方案 | **方案 A：小块无边框窗口 `SetParent` 到 `Shell_TrayWnd`**，位置锚在通知区（`TrayNotifyWnd`）左侧，500ms 轮询自愈 |
| 最大风险 | **Explorer 重启 / 任务栏重建后子窗口失效**（AF-Media-Bar 为此写了 8 次重试 + 稳定样本策略）；其次是第三方任务栏美化工具改写任务栏层次导致挂不上 |
| 预期工作量 | 新增 1 个文件约 **200 行**（含 P/Invoke 与自绘），TrayApp 里 3 处接入，Settings 加 1 个开关 |

---

## 1. 名称澄清：AFmediaBar = AF-Media-Bar

GitHub 上没有名为 `AFmediaBar` 的仓库（搜索 API 只回网页噪音）。真实项目：

| 项 | 值 |
| --- | --- |
| 仓库 | https://github.com/Fervent-Tempo/AF-Media-Bar |
| 描述 | Windows 10/11 桌面上的媒体控制与轻度美化软件 |
| 语言 / 框架 | **C# / WPF**（`.xaml` 视图），运行时依赖 **WebView2 Runtime**，发布包自带 .NET 运行时 |
| star / 最近提交 | 约 1.1k star；最近提交 2026-11-17（活跃） |
| 版本 | v1.7.1（2026-11-12） |
| 系统要求 | Windows 10 1809+ x64（README 第 37 行附近） |
| 配置 | %LOCALAPPDATA%\AFMediaBar\settings.json |

**本机实证：这个程序此刻就运行在当前这台机器上，并且已经挂在任务栏里。**
枚举 `Shell_TrayWnd` 的子窗口，第一个就是它的宿主窗口：

```text
cls = HwndWrapper[AFMediaBar;;d0a227a8-50ed-41dd-b151-54c9f5795f56]
style   = 0x56080000   -> WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN（无 WS_POPUP）
exstyle = 0x08080000   -> WS_EX_NOACTIVATE | WS_EX_LAYERED
parent  = Shell_TrayWnd（GetParent 校验通过）
rect    = 0,1232 - 2048,1280   (2048 x 48)   <- 与任务栏 1:1 重合
GetWindowRgn = COMPLEXREGION
  rgnrect 16,4 - 468,48  (452 x 44)          <- 只有这一小块可见可点，其余裁掉
```

这组数据同时说明两件事：**它的宿主窗口铺满整个任务栏**，但**用窗口区域（region）把绘制/命中范围裁成左侧 452×44** —— 与读源码得到的结论完全一致。

---

## 2. AF-Media-Bar 的停靠实现（源码级证据）

### 2.1 三步走

1. `FindWindow("Shell_TrayWnd")` 找主任务栏 HWND；多屏时再找 `Shell_SecondaryTrayWnd`。
2. 把 WPF 窗口的 `WS_POPUP` 换成 `WS_CHILD`，然后 `SetParent(hwnd, taskbar)` 变成任务栏的子窗口。
3. 子窗口尺寸 = 任务栏客户区整块（`(0,0,width,height)`），内容按需摆放，最后 `SetWindowRgn` 把「媒体条」之外的区域全裁掉 —— 既让任务栏其余部分照常显示，也让点击穿透过去。

### 2.2 关键文件与行号

路径均相对于仓库根 `AF-Media-Bar-main/`：

| 文件:行 | 内容 |
| --- | --- |
| `src/AFMediaBar/Classes/Services/Taskbar/TaskbarDockService.cs:72` | `var mainHwnd = FindWindow("Shell_TrayWnd", null);` |
| `...TaskbarDockService.cs:88` | `FindWindow("Shell_SecondaryTrayWnd", null)` 取副屏任务栏 |
| `...TaskbarDockService.cs:178-184` | `IsTaskbarWindow`：类名必须是 `Shell_TrayWnd` / `Shell_SecondaryTrayWnd`；注释说明句柄可能被回收给别的窗口，不能只信缓存 |
| `...TaskbarDockService.cs:206-217` | `DockWindow`：`style = (style & ~WS_POPUP) \| WS_CHILD; SetWindowLong(...); SetParent(windowHandle, taskbarHandle);` |
| `...TaskbarDockService.cs:223-235` | `UndockWindow`：先 `SetWindowPos(SWP_HIDEWINDOW)`，再 `SetParent(hwnd, IntPtr.Zero)`，最后恢复 `WS_POPUP` |
| `...TaskbarDockService.cs:243-254` | `SetWindowPosition`：固定写 `(0,0,w,h)` + `SWP_NOZORDER\|SWP_NOACTIVATE`，注释理由「宿主覆盖整个父窗口，始终用客户区原点，由父 HWND 带动」 |
| `...TaskbarDockService.cs:156-161` | `GetDpiForWindow(taskbarHandle) / 96.0` 取任务栏 DPI |
| `...TaskbarDockService.cs:260-298` | `ApplyInputRegion`：`CreateRectRgn` + `CombineRgn(RGN_OR)` 合并多块，`SetWindowRgn(hwnd, rgn, redraw)` |
| `src/AFMediaBar/Views/Windows/TaskbarWindow.xaml.cs:27-31` | 类注释：透明子窗口覆盖整个任务栏，媒体栏由 Canvas 摆放并 `SetWindowRgn` 裁剪，so the rest of the taskbar remains visible and click-through |
| `...TaskbarWindow.xaml.cs:795 / :845-846 / :899-904` | `DockWindow` -> `SetWindowPosition(taskbarWidth, taskbarHeight)` -> `PositionBar` -> `ApplyBarInputRegion` |
| `...TaskbarWindow.xaml.cs:786-790` | 当前父 HWND 不是目标任务栏时，走 `RequestTaskbarHostReload` 重建宿主 |
| `...TaskbarWindow.xaml.cs:584` | 注释：Explorer 会在 `TaskbarCreated` 到达主窗口**之前**就销毁任务栏子 HWND |
| `...TaskbarWindow.xaml.cs:113-118` | `_staleSurfaceSamples`：「可见却不可命中」时自愈重建宿主 |
| `src/AFMediaBar/Classes/Interop/NativeMethods.cs:12-17, 268-369, 500-540` | 全部 P/Invoke（`[LibraryImport]` 源生成）：GWL/WS 常量、`FindWindowW`、`FindWindowExW`、`SetParent`、`GetWindowRect`、`SetWindowLongW`、`SetWinEventHook`、`GetDpiForWindow`、`SetWindowRgn`、gdi32 `CreateRectRgn`/`CombineRgn` |
| `src/AFMediaBar/Classes/Services/Taskbar/TaskbarRecoveryPolicy.cs:9-15` | 恢复策略：`MaximumAttempts=8`、`InitialDelayMilliseconds=900`、`RetryDelayMilliseconds=600`、`RequiredStableSamples=2` |
| `...TaskbarOccupiedAreaProbe.cs:191-192`（526 行） | 用 **UIA** 在专用 MTA 后台线程枚举任务栏子元素占位，Shell 回退用 `FindWindowEx` 找 `TrayNotifyWnd` / taskband，算出「空闲区间」避免压住图标 |
| `...TaskbarBarPlacementCalculator.cs:16-39` | 位置计算：把起点夹到 `[rangeStart, rangeEnd - primarySize]`，横轴居中 + DPI 偏移 |
| `docs/design/taskbar.md:26-31` | 自动隐藏/运动规则：任务栏矩形变化时**冻结**位置/尺寸/采样/输入；运动中不 `SetWindowPos`、不 `SetWindowRgn`；**稳定收起时自己显式隐藏宿主**，不能只靠父子联动 |
| `docs/design/taskbar.md:38` | 避让规则：「收缩立即避让，扩大用至少两份、跨 200 ms 的成功样本确认」 |
| `docs/design/taskbar.md:12` | 退出顺序：菜单/Popup -> 恢复任务 -> Raw Input -> 托盘 -> 媒体来源 -> Host；Dispose 必须幂等 |

### 2.3 Win10 / Win11 行为差异与已知问题

- 仓库里 **grep `SHAppBarMessage` 结果为空**，两种系统走同一套 `SetParent` 停靠逻辑；差异主要在「任务栏是否居中对齐」和「任务栏外观是否被第三方工具改写」。
- 与任务栏重建相关的 issue：
  - [#218（open）重启后任务栏中不显示任何部件](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/218)
  - [#137（closed）fix: reposition the media bar after taskbar icons are removed](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/137)
  - [#169（closed）feat(taskbar): 支持任务栏对齐与左右内容排布](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/169)
- 兼容性类：
  - [#106（open）关于对播放器、美化工具的兼容性问题](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/106) —— 第三方任务栏美化（StartAllBack / ExplorerPatcher / Windhawk 一类）会改写任务栏层次，最容易让子窗口「挂不上去」。
  - [#279（open）建议用 Rust 重构，内存占用太大](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/279) —— WPF + WebView2 的体量问题，与本方案无关（我们用 WinForms 自绘）。
- 结论性经验：**它把「停靠」当成一个有状态、会失败、需要重试与自愈的过程**，而不是一次 `SetParent` 就完事。这一点比技术选型本身更值得抄。

---

## 3. 第二参考：TrafficMonitor（最经典的「任务栏小窗」）

https://github.com/zhongyang219/TrafficMonitor —— 46.9k star，C++/MFC，最近提交 2026-09-27。

| 文件:行 | 内容 |
| --- | --- |
| `TrafficMonitor/TaskBarDlg.cpp:1017` | `m_connot_insert_to_task_bar = !(::SetParent(this->m_hWnd, GetParentHwnd()));` —— 同样是 `SetParent`，并且**把失败当成正常分支处理** |
| `TrafficMonitor/TaskBarDlg.cpp:1006` | `ModifyStyleEx(0, WS_EX_TOOLWINDOW);`（不进 Alt-Tab） |
| `TrafficMonitor/TaskBarDlg.cpp:1010-1011` | `FindTaskbarHandle` + `GetWindowRect(m_hTaskbar, m_rcTaskbar)` |
| `TrafficMonitor/TaskBarDlg.cpp:460-495` | `AdjustWindowPos`：先刷新任务栏矩形再算位置；**`SetParent` 失败时用屏幕绝对坐标补正**（`:485-491`） |
| `TrafficMonitor/TaskBarDlg.cpp:605-631` | `FindTaskbarHandle`：副屏走 `GetAllSecondaryDisplayTaskbar`，主屏 `FindWindow("Shell_TrayWnd")` |
| `TrafficMonitor/Win11TaskbarDlg.cpp:5-94` | Win11 专用定位：锚点是 `TrayNotifyWnd`（通知区）与 `Start`（开始按钮）的 `GetWindowRect`；`InitTaskbarWnd` 里 `FindWindowEx(m_hTaskbar, 0, L"TrayNotifyWnd", NULL)`；`GetParentHwnd()` 直接返回 `m_hTaskbar` |
| `TrafficMonitor/Win11TaskbarDlg.cpp:33-41` | **副屏没有通知区窗口**，退回固定 88 px（系统时间区宽度） |
| `TrafficMonitor/ClassicalTaskbarDlg.cpp:72-77` | Win10（经典任务栏）才走 `ReBarWindow32` / `MSTaskSwWClass` 容器 |
| `TrafficMonitor/TrafficMonitor.cpp:1305-1313` | 判断是否 Win11 任务栏：`FindWindowExW(hTaskbar, 0, L"Windows.UI.Composition.DesktopWindowContentBridge", NULL) != NULL` |
| `TrafficMonitor/TrafficMonitorDlg.cpp:31, 108` | `RegisterWindowMessage("TaskbarCreated")` + `ON_REGISTERED_MESSAGE` —— 任务栏重启后重建窗口 |
| `TrafficMonitor/TrafficMonitorDlg.cpp:1180, 2022` | **100ms 定时器**轮询：`m_tBarDlg->AdjustWindowPos();`（含每 1s 一次的 DPI 检查） |

**要点：两个成熟项目都不用 AppBar、都不用 DeskBand、都不是「悬浮窗硬挤进任务栏」，全部是「`SetParent` 子窗口 + 定时轮询重排」。**

---

## 4. 本机实测：24H2 任务栏结构与几何（build 26100，125%）

```text
Shell_TrayWnd              0x1012A   rect (0,1232)-(2048,1280)  = 2048x48   dpi=120 (125%)
子窗口（EnumChildWindows 实测，含 class 与 rect）：
  HwndWrapper[AFMediaBar;...]                       2048x48 @ 0,1232      <- AF-Media-Bar 宿主
  Chrome_WidgetWin_0                                    0x0 @ 0,1232
  Windows.UI.Composition.DesktopWindowContentBridge  2048x48 @ 0,1232      <- XAML 任务栏主体
  Windows.UI.Input.InputSite.WindowClass                0x0 @ 0,1232
  Windows.UI.Core.CoreWindow                    52428x52428 @ 0,1232      <- 虚拟化巨型窗口
  Start                                               46x48 @ 561,1232
  TrayDummySearchControl                                0x0 @ 0,1232
  TrayNotifyWnd                                      263x48 @ 1785,1232     <- 定位锚点
  ReBarWindow32                                      704x48 @ 606,1232
  MSTaskSwWClass                                      702x40 @ 608,1232
  MSTaskListWClass                                    702x40 @ 608,1232
Shell_SecondaryTrayWnd     0x20AE2   （副屏任务栏）
  子窗口里有 bridge / InputSite / Start / WorkerW / MSTaskListWClass —— 没有 TrayNotifyWnd
```

由此得到几条硬结论：

1. **任务栏高度 48 px（125% 缩放下 38.4 dip）**，所有坐标按物理像素算，DPI 用 `GetDpiForWindow(Shell_TrayWnd)/96` 取（实测 120）。
2. **`ReBarWindow32` / `MSTaskSwWClass` 仍然存在**（与「Win11 已无 ReBarWindow32」的常见说法不同）。但它们只是任务按钮区的容器；Win11 的 Shell **不再加载 DeskBand**（无公开注册途径，社区共识），所以 DeskBand 方案依然不可用 —— 置信度：中（本机 HWND 实测 + 无可用注册 API，未做 DeskBand 注册实验）。
3. **通知区 `TrayNotifyWnd` 是唯一稳定的横向锚点**（左沿 1785）。图标区右沿在 1310，所以 **1310–1785 这 475 px 是空白带**，正好放一个小功率窗。
4. **副屏没有 `TrayNotifyWnd`**（本机实测 + TrafficMonitor 源码注释互相印证）-> 第一版只做主任务栏。
5. `Windows.UI.Composition.DesktopWindowContentBridge` 在本机是**直接子窗口**，可用作「是否 Win11 XAML 任务栏」的判断（与 TrafficMonitor 判据一致）。

---

## 5. 三个方案

统一前提：目标窗口只要 **约 76 dip × 任务栏高** 的一小块；不改任务栏布局；不抢焦点；可点击（打开现有面板）。

### 方案 A：`SetParent` 成 `Shell_TrayWnd` 的子窗口（AF-Media-Bar / TrafficMonitor 的做法）

**原理一句话**：把自己的 HWND 的 `WS_POPUP` 换成 `WS_CHILD` 并 `SetParent` 到任务栏 HWND，从此「任务栏怎么动，我就怎么动」，坐标用父客户区像素。

P/Invoke（可直接抄）：

```csharp
using System.Runtime.InteropServices;
using System.Text;

internal static class TaskbarNative
{
    internal const int GWL_STYLE   = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_CHILD         = 0x40000000;
    internal const int WS_POPUP         = unchecked((int)0x80000000);
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint SWP_NOMOVE      = 0x0002;
    internal const uint SWP_NOSIZE      = 0x0001;
    internal const uint SWP_NOZORDER    = 0x0004;
    internal const uint SWP_NOACTIVATE  = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const int  SW_SHOWNA = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);
}
```

挂载 + 定位核心（约 120 行可跑）：

```csharp
private bool Dock()
{
    var tray = TaskbarNative.FindWindow("Shell_TrayWnd", null);
    if (tray == IntPtr.Zero) return false;

    if (TaskbarNative.GetParent(Handle) != tray)
    {
        // 1) WS_POPUP -> WS_CHILD；2) 换父；3) 让样式变更立即生效
        int style = TaskbarNative.GetWindowLong(Handle, TaskbarNative.GWL_STYLE);
        TaskbarNative.SetWindowLong(Handle, TaskbarNative.GWL_STYLE,
            (style & ~TaskbarNative.WS_POPUP) | TaskbarNative.WS_CHILD);
        int ex = TaskbarNative.GetWindowLong(Handle, TaskbarNative.GWL_EXSTYLE);
        TaskbarNative.SetWindowLong(Handle, TaskbarNative.GWL_EXSTYLE,
            ex | TaskbarNative.WS_EX_NOACTIVATE | TaskbarNative.WS_EX_TOOLWINDOW);

        TaskbarNative.SetParent(Handle, tray);
        TaskbarNative.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            TaskbarNative.SWP_NOMOVE | TaskbarNative.SWP_NOSIZE |
            TaskbarNative.SWP_NOZORDER | TaskbarNative.SWP_NOACTIVATE |
            TaskbarNative.SWP_FRAMECHANGED);

        if (TaskbarNative.GetParent(Handle) != tray) { _parent = IntPtr.Zero; return false; }
        _parent = tray;
        TaskbarNative.ShowWindow(Handle, TaskbarNative.SW_SHOWNA); // 不要用 Form.Show()
    }
    return Reposition();
}

private bool Reposition()
{
    if (_parent == IntPtr.Zero || !TaskbarNative.IsWindow(_parent)) return false;
    if (!TaskbarNative.GetWindowRect(_parent, out var tb)) return false;

    int tbW = tb.Right - tb.Left, tbH = tb.Bottom - tb.Top;
    if (tbW <= tbH) return false;  // 竖向任务栏：第一版不支持，直接不显示

    double scale = Math.Max(1.0, TaskbarNative.GetDpiForWindow(_parent) / 96.0);
    int w = (int)Math.Round(DesignWidthDip * scale);   // 例如 76 dip
    int h = tbH;                                      // 与任务栏等高

    // 锚点：通知区左沿；拿不到就退到右侧 88 dip（系统时间区宽度）
    var notify = TaskbarNative.FindWindowEx(_parent, IntPtr.Zero, "TrayNotifyWnd", null);
    int rightEdge;
    if (notify != IntPtr.Zero
        && TaskbarNative.GetWindowRect(notify, out var nr)
        && nr.Left > tb.Left && nr.Left < tb.Right)
        rightEdge = nr.Left - tb.Left - (int)Math.Round(GapDip * scale);
    else
        rightEdge = tbW - (int)Math.Round(88 * scale);

    int x = Math.Max(0, rightEdge - w);
    // 父窗口没有非客户区，客户区原点 = 任务栏左上角
    TaskbarNative.MoveWindow(Handle, x, 0, w, h, true);
    return true;
}
```

自愈（500ms 轮询，成本可忽略）：

```csharp
private void Tick()
{
    // 父窗口被 Explorer 重建 / 被第三方工具摘掉 / 窗口被重排 —— 一律重新挂载
    if (_parent == IntPtr.Zero
        || !TaskbarNative.IsWindow(_parent)
        || TaskbarNative.GetParent(Handle) != _parent)
    {
        Dock();
        return;
    }
    Reposition();     // 任务栏宽度/对齐/图标数量变化都体现在 TrayNotifyWnd 的位置上
    Invalidate();     // 让功率文本刷新
}
```

- **优点**：任务栏移动、自动隐藏、缩放变化天然跟随（子是父的一部分）；不需要 `SetWinEventHook`；不会被别的窗口盖住；点击直接由我们处理；两个成熟项目 + 本机在跑的 AF-Media-Bar 三重验证。
- **风险清单**：
  - Explorer 重启会销毁任务栏（连同子窗口），必须重建（AF-Media-Bar 用 8 次 × 900ms 重试 + 2 次稳定样本；本方案用轮询兜底）。
  - 子窗口 z 序是「后挂的在上」，若被任务栏自己的 `DesktopWindowContentBridge` 压住，需要 `SetWindowPos(hwnd, HWND_TOP, ...)` 顶一下（AF-Media-Bar 因为铺满全栏必然要处理，我们只是小窗，风险低但需实测）。
  - 任务栏图标数量/对齐方式变化时可能与图标重叠（AF-Media-Bar 为此写了 UIA 占用区探测）。我们锚在通知区左侧，稳定得多，但仍需在「任务栏左对齐 + 图标塞满」时核一次。
  - 第三方任务栏美化（StartAllBack / ExplorerPatcher / Windhawk）会改任务栏层次 -> 可能挂不上（对应 issue #106），需降级。
  - WinForms 的 `Form` 被 `SetParent` 后，**不要**再调 `Show()`/`Hide()`（WinForms 会按顶层窗口给你重新摆位）；用 `ShowWindow`。
  - 副任务栏（`Shell_SecondaryTrayWnd`）没有通知区锚点，第一版不覆盖。
- **代码量**：约 **180–220 行**（含自绘与主题）。

### 方案 B：`SHAppBarMessage` 注册 AppBar（占一条边）

**原理一句话**：向 Shell 注册一个 AppBar，「预订」屏幕某条边的一段，Shell 会让别的 AppBar（包括任务栏）和最大化窗口给它让位。

```csharp
[StructLayout(LayoutKind.Sequential)]
private struct APPBARDATA
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public TaskbarNative.RECT rc;
    public IntPtr lParam;
}

private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETTASKBARPOS = 5;
private const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

[DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

// 典型流程（窗口创建后）：
// 1) ABM_NEW        注册（之后 Shell 把位置变化以 ABN_POSCHANGED 发到 uCallbackMessage）
// 2) ABM_QUERYPOS   让 Shell 给出可用矩形
// 3) 修正 rc（比如只取 200px 宽）
// 4) ABM_SETPOS     提交占位
// 5) 退出时 ABM_REMOVE
```

- **表现**：AppBar 占的是**屏幕边缘的一条**，不是「任务栏内部」。要么把任务栏挤走，要么让最大化窗口的工作区少掉这一条。
- **自动隐藏任务栏时**：注册 `ABM_SETAUTOHIDEBAR` 会与任务栏自己的自动隐藏争抢同一条边；`ABM_GETAUTOHIDEBAR` 已被弃用，行为在各版本不一致。
- **优点**：微软文档里的正统接口，不会被「覆盖」，多屏 DPI 提示齐全。
- **风险清单**：把任务栏挤开 / 抢整条边；工作区变小，影响所有最大化窗口；做不出「塞在任务栏里面」的视觉效果（与 AFmediaBar 的观感相差很远）；托盘区、时钟区位置被改会引发观感问题；RDP/多显示器切换时常见需要重新 `ABM_NEW`。
- **代码量**：约 150 行，但「让 Shell 接受一个不是贴边的条」成本高、收益差。**不推荐**。

### 方案 C：无边框悬浮窗跟随任务栏

**原理一句话**：做一个 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 的置顶无边框窗口，轮询（或 `SetWinEventHook`）拿 `Shell_TrayWnd` 与 `TrayNotifyWnd` 的矩形，把自己摆到通知区左侧。

```csharp
// 窗口样式（在 CreateParams 或 OnHandleCreated 里）
protected override CreateParams CreateParams
{
    get
    {
        var cp = base.CreateParams;
        cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE  不抢焦点
        cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW  不进 Alt-Tab
        cp.ExStyle |= 0x00000008;  // WS_EX_TOPMOST
        return cp;
    }
}

// 可选：监听任务栏位置变化（EVENT_OBJECT_LOCATIONCHANGE）
private delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

[DllImport("user32.dll", SetLastError = true)]
private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
    WinEventDelegate proc, uint idProcess, uint idThread, uint flags);

[DllImport("user32.dll")]
private static extern bool UnhookWinEvent(IntPtr hook);

private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
private const uint WINEVENT_OUTOFCONTEXT     = 0x0000;
private const uint WINEVENT_SKIPOWNPROCESS   = 0x0002;

// 探测任务栏是否自动隐藏（0x2C = SPI_GETAUTOHIDEBAREX，uEdge=ABE_BOTTOM）
[DllImport("user32.dll", SetLastError = true)]
private static extern bool SystemParametersInfo(uint action, uint param, ref APPBARDATA data, uint winIni);
```

- **优点**：完全不碰任务栏内部结构，兼容性最好；Explorer 重启对它没影响（只是位置要重算）；可以做得比任务栏更高（浮出一点）。
- **风险清单**：z 序上置顶窗口与任务栏同级争夺，**全屏应用、开始菜单、通知中心、任务视图展开时容易被盖住或盖住别人**；任务栏自动隐藏时它不会跟着滑走，必须自己判断 `SPI_GETAUTOHIDEBAREX` 或比较矩形越界后 `Hide()`；`SetWinEventHook` 只能拿到「有变化」拿不到「变成什么」，仍要 `GetWindowRect` 复核，且回调在别的线程；DPI 变化、多屏、任务栏换边都要重算；「看起来在任务栏里」和「其实浮在任务栏上」在阴影/圆角/透明边缘处很显眼。
- **代码量**：约 160–200 行，且**每个边界情况都要自己写**。

### 对比

| 维度 | A. SetParent 子窗口 | B. AppBar | C. 悬浮跟随 |
| --- | --- | --- | --- |
| 视觉上「在任务栏里」 | 是（真在里边） | 否（占一条边） | 像，但边缘易穿帮 |
| 任务栏自动隐藏 | 自动跟随（父带动子） | 与任务栏争同一条边 | 要自己判断并隐藏 |
| Explorer 重启 | 失效 -> 需重建（有成熟套路） | 需重新 `ABM_NEW` | 无影响，只需重算位置 |
| 对系统的影响面 | 只在自己的 HWND 内 | **改变全局工作区** | 置顶层，可能干扰全屏 |
| 参考实现 | AF-Media-Bar、TrafficMonitor | 少见（grep 两个仓库都是 0） | 各类桌面挂件 |
| 代码量 | ~200 行 | ~150 行 | ~180 行 |
| 结论 | **推荐** | 不推荐 | 作为 A 的降级兜底 |
---

## 6. 推荐方案：A（小块子窗口 + 轮询自愈）

### 6.1 为什么是 A

1. **两个成熟项目 + 本机在跑的 AF-Media-Bar 用的是同一套机制**，风险与坑都有现成答案可抄。
2. 我们只需要「任务栏里的一小块」，比 AF-Media-Bar（铺满全栏 + `SetWindowRgn` 裁剪 + UIA 避让）简单一个数量级：**不需要 `SetWindowRgn`**，一个小矩形子窗口就够。
3. 任务栏自动隐藏、换边、缩放变化，都由「父带动子」免费解决。
4. 失败可降级：`SetParent` 失败（第三方美化工具）时，按 TrafficMonitor `TaskBarDlg.cpp:485-491` 的做法用屏幕绝对坐标补正 —— 即自动退化成方案 C 的观感。

### 6.2 落点几何（本机实测值算出来的）

- 设计尺寸 **76 dip × 任务栏全高**；125% -> 95 × 48 px。
- 锚点 `TrayNotifyWnd.Left = 1785`，留 `GapDip = 8` -> 10 px 间距 -> **窗口落在 x = 1680..1775**，正好在图标区右沿（1310）与通知区（1785）之间的空白带里。
- y 方向 `0`，高 `48`（贴着任务栏上下边，文字垂直居中）。

### 6.3 新增文件：`src\CuktechTray\TaskbarWidget.cs`

结构与命名风格与 `PanelForm.cs` 一致（全部 internal）：

```csharp
namespace CuktechTray;

/// <summary>任务栏里的功率小窗：SetParent 成 Shell_TrayWnd 的子窗口，锚在通知区左侧。</summary>
internal sealed class TaskbarWidget : Form
{
    private const float DesignWidthDip = 76f;   // 76 dip 宽
    private const float GapDip = 8f;            // 与通知区的间距

    private readonly Settings _settings;
    private readonly System.Windows.Forms.Timer _timer;
    private IntPtr _parent = IntPtr.Zero;
    private Snapshot _snap = new();
    private Font _font = null!;
    private Color _fg = Color.FromArgb(232, 234, 237);
    private Color _bg = Color.FromArgb(32, 32, 32);

    public TaskbarWidget(Settings settings) { /* 见 6.4 */ }

    public void Apply(Snapshot snap) { _snap = snap; if (Visible) Invalidate(); }
    public void Stop() { /* _timer.Stop(); 解父 + 恢复 WS_POPUP + Dispose */ }
    private bool Dock() { /* 见方案 A 代码 */ }
    private bool Reposition() { /* 见方案 A 代码 */ }
    private void Tick() { /* 见方案 A 代码 */ }
    protected override void OnPaint(PaintEventArgs e) { /* 见 6.5 */ }
}
```

窗口标题设成 `CuktechWatts`（`Text = "CuktechWatts"`），方便验证脚本按标题找到它。

### 6.4 构造函数要点

```csharp
public TaskbarWidget(Settings settings)
{
    _settings = settings;
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    TopMost = false;                     // 与 PanelForm 一致：本程序任何地方都不把 TopMost 设成 true
    StartPosition = FormStartPosition.Manual;
    AutoScaleMode = AutoScaleMode.None;
    DoubleBuffered = true;
    SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
             ControlStyles.UserPaint, true);
    Text = "CuktechWatts";
    BackColor = _bg;

    _ = Handle;                          // 先建句柄，才能改样式/换父
    ApplyTheme();
    Dock();

    _timer = new System.Windows.Forms.Timer { Interval = 500 };  // 轮询自愈
    _timer.Tick += (_, _) => Tick();
    _timer.Start();
}
```

### 6.5 绘制与文本

```csharp
protected override void OnPaint(PaintEventArgs e)
{
    var g = e.Graphics;
    g.Clear(_bg);
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

    double watts = _snap.TotalWatts;                 // Models.cs:35
    bool on = _snap.Connected;
    string text = on ? watts.ToString(_settings.ShowTenthsOfWatt ? "0.0" : "0") + " W" : "—";

    using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    using var brush = new SolidBrush(on ? _fg : Color.FromArgb(154, 160, 166));
    g.DrawString(text, _font, brush, ClientRectangle, fmt);
}
```

- 字体：`Segoe UI` 或 `Microsoft YaHei UI`，字号 `11f * scale`，Bold；`scale = GetDpiForWindow(_parent) / 96`（每次 `Dock()` 后重算；125% 下 48 px 高的任务栏里约 13.75 px，视觉合适）。
- 主题：读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme`（本机实测 `0` = 深色）。深色 -> 底色 `(32,32,32)` / 字色 `(232,234,237)`；浅色 -> 底色 `(243,243,243)` / 字色 `(26,26,26)`。ControlForm 里改主题时广播一次刷新即可。
- 进阶（可选，v2）：`WS_EX_LAYERED` + `SetLayeredWindowAttributes(hwnd, colorkey, 0, LWA_COLORKEY)` 让底色真正透明（只留数字），代价是文字边缘有色边；AF-Media-Bar 用 WPF 透明 + region 达到了同样效果。

### 6.6 TrayApp 接线（3 处）

| 位置 | 改动 |
| --- | --- |
| `TrayApp.cs:45` 附近（`_panel = new PanelForm();` 之后） | `_widget = new Settings.TaskbarWidget ? new TaskbarWidget(_settings) : null;` + 托盘菜单加一项「在任务栏显示功率」（开关 `Settings.TaskbarWidget`，默认关） |
| `TrayApp.cs:313`（现有 `_panel.Apply(snap);` 旁边） | `_widget?.Apply(snap);` —— 数据源就是 `DeviceClient.Updated`（`DeviceClient.cs:29`）流出的 `Snapshot`，取 `Snapshot.TotalWatts`（`Models.cs:35`） |
| `TrayApp` 的 `Dispose` | `_widget?.Stop();`（先 `SetParent(hwnd, IntPtr.Zero)` 再释放，保持幂等，顺序抄 AF-Media-Bar `UndockWindow`） |

`Settings.cs` 新增 `public bool TaskbarWidget { get; set; }`。注意现有 `CurrentConfigVersion = 2` 与 `Load()` 里的迁移分支（`Settings.cs:79-105`）—— 加字段**不需要**升版本号（`IsComplete` 只看 Mac/TokenHex），默认 `false` 即「老用户不会突然多出东西」。

---

## 7. 验证方法

### 7.1 几何探针（贴没贴住、贴在哪）

```powershell
# 保存为 tools\probe-taskbar.ps1，按需运行
$ErrorActionPreference = 'Stop'
Add-Type @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public class T {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, Proc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  public delegate bool Proc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static List<string> Scan() {
    var o = new List<string>(); var tray = FindWindow("Shell_TrayWnd", null);
    RECT tb; GetWindowRect(tray, out tb);
    o.Add("taskbar " + tb.Left + "," + tb.Top + "-" + tb.Right + "," + tb.Bottom);
    EnumChildWindows(tray, (h, l) => {
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
      if (cls.ToString().Contains("Cuktech") || txt.ToString().Contains("Cuktech")) {
        RECT r; GetWindowRect(h, out r);
        o.Add("FOUND cls=" + cls + " title=" + txt + " rect=" + r.Left + "," + r.Top + "-" + r.Right + "," + r.Bottom
              + " childOfTray=" + (GetParent(h) == tray));
      }
      return true; }, IntPtr.Zero);
    return o; }
}
"@
[T]::Scan() | ForEach-Object { $_ }
```

判据：`childOfTray=True`，窗口 `Top/Bottom` 与任务栏一致，`Right` 小于 `TrayNotifyWnd.Left`（1785）。

### 7.2 截图取证

**重要实测结论：`Graphics.CopyFromScreen` 抓不到 XAML/Composition 任务栏的内容。**
本机对 `(0,1220)-(2048,1280)` 做 `CopyFromScreen`，得到的是**桌面壁纸**（任务栏是空的）；改用 `PrintWindow(hwnd, hdc, 2)`（`PW_RENDERFULLCONTENT`）抓 `Shell_TrayWnd`，立刻拿到完整的 2048×48 任务栏（开始按钮、图标、AF-Media-Bar 的封面/文字/波形、通知区都在）。

```powershell
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class P {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$tb = [P]::FindWindow("Shell_TrayWnd", $null)
$r = New-Object P+RECT; [void][P]::GetWindowRect($tb, [ref]$r)
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
[void][P]::PrintWindow($tb, $hdc, 2)      # 2 = PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save("$PSScriptRoot\..\docs\shot-taskbar-widget.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
```

> 现有 `tools\screenshot.ps1` / `tools\shot-window.ps1` / `tools\focus-shot.ps1` 都用 `CopyFromScreen`，**抓任务栏会失败**（得到壁纸），验证任务栏组件必须走 `PrintWindow(PW_RENDERFULLCONTENT)`。

### 7.3 行为验收清单

- [ ] 首次启动：小窗出现在通知区（时钟/托盘图标）左侧，不遮挡图标。
- [ ] 点击小窗：打开现有面板（或按设计触发），**不抢焦点、不进 Alt-Tab**。
- [ ] 任务栏宽度变化（插拔显示器 / 缩放 100%<->125%<->150%）：小窗 500ms 内回到通知区左侧，字号同步。
- [ ] 任务栏自动隐藏：滑走时小窗一起滑走，滑回来时一起回来。
- [ ] 关掉「在任务栏显示功率」：窗口隐藏且 `SetParent` 已解除（探针脚本找不到它）。
- [ ] `taskkill /f /im explorer.exe` 再起：小窗 1–2 秒内重新出现。
- [ ] 锁屏 -> 解锁：仍在，无异常置顶。
- [ ] 托盘「退出」：窗口消失，不留残影、不留孤儿 HWND。

---

## 8. 风险登记

| 严重度 | 风险 | 触发条件 | 缓解 |
| --- | --- | --- | --- |
| 高 | 子窗口在任务栏重建后消失 | Explorer 重启、任务栏崩溃 | 500ms 轮询 `GetParent != _parent` 即重挂；可选再加 `RegisterWindowMessage("TaskbarCreated")` 提前触发；AF-Media-Bar 用 8 次 × 900ms 重试 |
| 高 | 第三方任务栏美化工具导致挂不上 | StartAllBack / ExplorerPatcher / Windhawk | `SetParent` 后校验 `GetParent`，失败降级为屏幕绝对坐标定位（TrafficMonitor `TaskBarDlg.cpp:485-491` 的做法）并写日志 |
| 中 | 被任务栏自身的 Composition 层盖住 | 后续系统更新调整子窗口顺序 | 挂载后 `SetWindowPos(hwnd, HWND_TOP, ...)`；日志记录「可见但被遮」 |
| 中 | WinForms `Form` 被重新当顶层窗口摆位 | 误调 `Show()`/`Hide()`/`Visible` | 只用 `ShowWindow` + `MoveWindow`；不复用 `PanelForm` 的显示逻辑 |
| 中 | 与任务图标重叠 | 任务栏左对齐 + 图标塞满 | 锚点是通知区左沿；再加一层保护：`Start` / `MSTaskListWClass` 的右沿若与小窗重叠则整体右移或隐藏 |
| 低 | 竖向任务栏（改注册表实现）不支持 | `tbW <= tbH` | 直接不显示 + 日志 |
| 低 | 副任务栏不显示 | 多显示器「所有显示器显示任务栏」 | 第一版只做 `Shell_TrayWnd`；后续按 AF-Media-Bar `TaskbarDockService.cs:86-97` 扩展 |
| 低 | 运行时开销 | 500ms 轮询 + 每秒 1 次 `Invalidate` | 实测可忽略（3 次 P/Invoke + 一次小矩形重绘），不必用 `SetWinEventHook` |

---

## 9. 参考链接

- AF-Media-Bar：https://github.com/Fervent-Tempo/AF-Media-Bar
  - issue [#218 重启后任务栏中不显示任何部件](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/218)
  - issue [#137 reposition after taskbar icons are removed](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/137)
  - issue [#106 播放器/美化工具兼容性](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/106)
  - issue [#169 任务栏对齐与左右内容排布](https://github.com/Fervent-Tempo/AF-Media-Bar/issues/169)
- TrafficMonitor：https://github.com/zhongyang219/TrafficMonitor
- MS Learn：
  - [SetParent](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setparent)
  - [SetWindowRgn](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowrgn)
  - [GetDpiForWindow](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getdpiforwindow)
  - [SetWinEventHook](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwineventhook)
  - [SHAppBarMessage](https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shappbar_message)
  - [Taskbar Extensions（DeskBand 已不适用于 Win11）](https://learn.microsoft.com/windows/win32/shell/taskbar-extensions)
- 本机取证产物：`D:\dsh projects\.tools\research\shots\taskbar-printwindow.png`（PrintWindow 抓到的真实任务栏，含 AF-Media-Bar 的媒体条）

---

## 10. 推荐方案 + 落地清单

**推荐方案 A：把一个小巧的无边框窗口 `SetParent` 到 `Shell_TrayWnd`，锚在 `TrayNotifyWnd` 左侧，500ms 轮询自愈，`SetParent` 失败时降级为屏幕绝对定位。**

1. **新增 `src\CuktechTray\TaskbarWidget.cs`（约 200 行）**：`Form` 子类 + `TaskbarNative` P/Invoke + `Dock()`/`Reposition()`/`Tick()`/`OnPaint()`（抄第 5 节方案 A 的代码），窗口标题设成 `CuktechWatts` 以便探针识别。
2. **`TrayApp.cs` 接线 3 处**：`:45` 之后创建（受 `Settings.TaskbarWidget` 控制）；`:313` 旁加 `_widget?.Apply(snap);`；`Dispose` 里 `_widget?.Stop();`（先解父再释放，幂等）。
3. **`Settings.cs` 加 `public bool TaskbarWidget { get; set; }`（默认 `false`，不升 `ConfigVersion`）**，托盘右键菜单加可勾选项「在任务栏显示功率」，勾选状态持久化。
4. **主题与格式**：读 `SystemUsesLightTheme` 决定底色/字色；文本复用 `Settings.ShowTenthsOfWatt`，数据取 `Snapshot.TotalWatts`，断连时显示 `—`。
5. **验证**：走项目根 `build.ps1` 发布 -> 运行 -> `tools\probe-taskbar.ps1`（7.1 节）核几何 -> `PrintWindow(PW_RENDERFULLCONTENT)`（7.2 节）截图存档到 `docs\shot-taskbar-widget.png` -> 逐条过 7.3 清单（重点：Explorer 重启、125%/150% 缩放、任务栏自动隐藏）。

**如果第 1 步在 WinForms 上遇到「窗口被重新当顶层窗口摆位」的死结**，把 `TaskbarWidget` 从 `Form` 换成 `NativeWindow` + `CreateWindowEx(WS_CHILD, ...)` 直接建子窗口，对外 API 不变，`TrayApp` 无需改动。

---

## 11. 后续修订（第 10 轮）：位置挪到开始按钮左边 + 底色整片抠掉

前 10 节的结论是「锚在 `TrayNotifyWnd` 左侧、实底采样成任务栏同色」。上线后用户提了两条：**不要压在托盘图标旁边，要最左边、挨着开始按钮**；**不要图标、也不要那块底色**。本节记录改动与实测。

### 11.1 新锚点：开始按钮（`Start`）

```csharp
// TaskbarWidget.Reposition()
int gap = (int)Math.Round(GapDip * scale);
var start = TaskbarNative.FindWindowEx(_parent, IntPtr.Zero, "Start", null);
int x = gap;                                            // 兜底：贴任务栏最左
if (start != IntPtr.Zero && GetWindowRect(start, out var sr) && sr.Left >= tb.Left && sr.Left < tb.Right)
{
    int leftOfStart = sr.Left - tb.Left - gap - w;       // 开始按钮左边放得下
    x = leftOfStart >= gap ? leftOfStart : Math.Min(tbW - w, sr.Right - tb.Left + gap);
}
x = Math.Max(gap, Math.Min(x, tbW - w));
```

- Win11 的 `Start` 是 `Shell_TrayWnd` 的**直接子窗口**（class 字面量就是 `Start`，标题「开始」），`IsWindowVisible` 返回 **False**（真正的按钮由 XAML 画），但 `GetWindowRect` 的几何是准的，拿它当锚点没问题。
- 本机实测（125% 缩放，物理像素）：`Shell_TrayWnd` = 0,1540 - 2560,1600（高 60）；`Start` = 729,1540 - 785,1600；算出来 widget 落在 **624..719**，正好紧挨开始按钮左边、不压任何任务按钮。
- 回退顺序：开始按钮左边放不下（任务栏左对齐、开始按钮就贴边）→ 放它右边；连 `Start` 都找不到（竖向任务栏 / 第三方任务栏）→ 贴任务栏最左 `gap`。
- 坑：PowerShell 调 `FindWindowEx` 时把 `$null` 传给 `lpWindowName` 会被转成空串，于是只匹配「标题为空」的窗口，`Start` 找不到。原型脚本里改成在 C# 侧包一层 `FindChild(parent, cls)` 传真正的 `null` 才行；C# 主程序直接用 `null` 没这问题。

### 11.2 底色：`LWA_COLORKEY` 色键透明

```csharp
// CreateParams: cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
// DockToTaskbar() 里、ShowWindow 之前：
_useColorKey = TaskbarNative.TryColorKey(Handle, KeyColor);   // KeyColor = RGB(255,0,255)

// SetLayeredWindowAttributes(hwnd, crKey = 0x00FF00FF, 0, LWA_COLORKEY)
// OnPaint：g.Clear(_useColorKey ? KeyColor : BackColor);
```

- 选品红当键色：任务栏颜色来自壁纸取色，永远不会是纯品红。
- **必须关掉文字抗锯齿**：`TextRenderingHint.SingleBitPerPixelGridFit`。用 `AntiAliasGridFit` / ClearType 时，字形边缘与品红背景混出的中间色不等于键色、抠不掉，屏幕上会出现一圈品红彩边（已实测复现）。硬边字在 125% 缩放下仍然清晰。
- 兜底：`SetLayeredWindowAttributes` 失败时 `_useColorKey = false`，退回原来的「采样任务栏颜色当实底」，任何情况下都不会露出品红。
- 副作用（正面）：色键透明区域不接收鼠标，点空白处等于点在任务栏上；只有压在数字上的点击才会打开面板。

### 11.3 取证工具与踩坑

| 工具 | 用途 | 注意 |
| --- | --- | --- |
| `tools\taskbar-demo.ps1` | PowerShell 原型，复刻 widget（含色键透明） | 必须先 `SetProcessDPIAware()`，否则非 DPI 感知进程下 `MoveWindow` 坐标被系统按 125% 反向缩放，摆出来偏一半 |
| `tools\shot-screen.ps1` | `GetDC(0)` + `BitBlt` 抓真实屏幕区域 | **唯一能证明透明效果的抓法**；`Graphics.CopyFromScreen` 在受限会话里会报「句柄无效」 |
| `tools\shot-hwnd-crop.ps1` | `PrintWindow(PW_RENDERFULLCONTENT)` 抓指定窗口再裁剪放大 | 对**分层窗口**会画出假的品红彩边，**不能**用来判断透明效果好坏 |

### 11.4 已知限制

- 竖排任务栏、副屏任务栏（`Shell_SecondaryTrayWnd`）仍然不显示。
- 任务栏自动隐藏时 widget 会跟着任务栏一起藏（它是任务栏的子窗口，这点比置顶窗口方案省心）。
- 从 DSH 会话里启动 exe 时 `SetParent` 会返回 `ERROR_ACCESS_DENIED`（错误 5），任务栏上不显示——这是本会话的进程完整性级别问题，正常双击启动没有这个现象。

## 12. 第 12/13 轮的返工：逐像素 alpha + 可拖动 + 权限真相

第 11 节的方案（GDI 往分层窗口上画 + 色键抠透明）在实际机器上露出了两个问题，这一节记录怎么修的和为什么。

### 12.1 问题一：数字"两层叠加"

现象：任务栏上的数字看起来像两份叠在一起，边缘发虚、有重影；换个总功率（比如 32.2W → 4.0W）之后旧数字的残影还留着。

原因：`SetLayeredWindowAttributes(hwnd, colorkey, 0, LWA_COLORKEY)` 只做"整片颜色变透明"这一件事，
窗口内容还是普通的 GDI 画布。子窗口被父窗口（任务栏）重绘、以及 DWM 合成层刷新时，走的是两条不完全同步的路径，
上一帧的文字会在合成层里残留一帧，和本帧错开一两个像素 —— 就是那层"重影"。
抗锯齿的边缘像素本来就和底色混过色，抠色键抠不干净，所以又多出一圈毛边。

修法：**不再用色键，改用 `UpdateLayeredWindow`（ULW）+ 逐像素 alpha**。

- 窗口 exstyle 加 `WS_EX_LAYERED`（`TaskbarWidget.cs:84` 附近），这是 ULW 的前提。
- 每帧不是增量画，而是**整张全画**：新建一张 32 位 ARGB 位图，`g.Clear(Color.Transparent)` 之后只把字画上去，
  背景 alpha 全 0（`TaskbarWidget.cs:235` `Redraw()`，绘制 lambda 在 :257-270）。
- 再把这张位图整体提交给系统（`TaskbarNative.PaintLayered`，`TaskbarWidget.cs:552`；
  `BLENDFUNCTION{ BlendOp=AC_SRC_OVER, SourceConstantAlpha=255, AlphaFormat=AC_SRC_ALPHA }` + `ULW_ALPHA`，见 :597-600）。
- `WM_PAINT` 被故意做成空实现（`TaskbarWidget.cs:144` `protected override void OnPaint(PaintEventArgs e) { }`），
  窗口内容完全由 `Redraw()` 提交，系统不会再用自己的重绘插一脚。

因为每次提交的是完整一帧、且带 alpha，DWM 直接拿它做合成，不存在"上一帧残留在下面"的路径 —— 重影消失，
字体边缘也是 ULW 自己算的抗锯齿，不再有彩边。

一句话对比：

| 方案 | 透明怎么来 | 重影 | 抗锯齿边缘 |
| --- | --- | --- | --- |
| 色键（第 11 节） | `LWA_COLORKEY`，整片同色抠掉 | 有 | 有彩边 |
| ULW（现在） | 位图 alpha 通道，逐像素 | 无 | 干净 |

### 12.2 坐标：ULW 在子窗口里用的是父客户区坐标

这个坑值得单记一条。`PaintLayered` 的 `pptDst` 参数：对**顶层窗口**是屏幕坐标，
但对 `SetParent` 过来的**子窗口**是**父窗口客户区坐标**。所以 `Redraw()` 里要自己换算
（`TaskbarWidget.cs:244-257`）：取父窗口 rect 和自身 rect，传 `me.Left - parentX` / `me.Top - parentY`。
直接传屏幕坐标会让文字整块跑到屏幕左上角去。

### 12.3 问题二：位置要能自己摆

用户要的是"紧挨开始菜单"，但开始按钮的宽度、任务栏缩放、第三方任务栏美化工具都会变，写死一个 x 迟早不合适。
所以做成**可拖动 + 自动摆位**：

- 鼠标：`OnMouseDown` 记下光标 x 和当前窗口 x（`TaskbarWidget.cs:148-156`）；
  `OnMouseMove` 里位移超过 `DragThreshold = 4` 像素才算拖（:38、:158-167），光标换成 `Cursors.SizeWE`，
  `DragTo()` 把窗口挪过去并夹在任务栏范围内（:196-210）；
  `OnMouseUp`：拖动过就`SaveManualPosition()` 存盘，没拖动过就是单击 → 打开功率面板（:169-184）。
- 存盘：`SaveManualPosition()` 把**任务栏客户区坐标 x 换算成 dip**（除以 `_scale`）写进 `settings.json`
  的 `TaskbarLeftDip`（`TaskbarWidget.cs:212-218`，字段定义在 `Settings.cs:35`，默认 `-1` 表示"自动"）。
  存 dip 而不是像素，是为了 125% / 150% 缩放来回切时位置不跑偏。
- 自动摆位：`Reposition()` 里 `TaskbarLeftDip >= 0 ? TaskbarLeftDip * _scale : AutoX(...)`（`TaskbarWidget.cs:316-346`）。
  `AutoX()`（:349-362）找子窗口 class 为 `Start` 的开始按钮：`sr.Left - tb.Left - gap - w`，
  左边放不下就挪到开始按钮右边，连开始按钮都找不到就贴任务栏最左。
- 每 500ms 的 `Tick()`（:365-404）会重新算一遍：任务栏换宽度 / 改缩放 / 开始按钮挪了都会自动纠正；
  拖动过程中跳过（`if (!_dragging) Reposition()`，:398）。
- 托盘菜单新增「重置任务栏数字位置」（`TrayApp.cs:101-102`，处理函数 `ResetTaskbarPosition()` 在 `TrayApp.cs:411`）：
  把 `TaskbarLeftDip` 置回 `-1` 存盘，有 widget 就调 `TaskbarWidget.ResetPosition()`（`TaskbarWidget.cs:105`）。
  菜单打开时根据当前状态改文案：「重置任务栏数字位置（现在是拖动后的位置）」/「（现在是自动）」（`TrayApp.cs:118-121`）。

实测（本机 125% 缩放，任务栏 0,1232-2048,1280，开始按钮 583-628）：
自动摆位算出 `w = round(84*1.25) = 105`、`gap = 10`、`x = 583 - 10 - 105 = 468`（任务栏客户区坐标），
屏幕绝对左边 468，紧挨开始按钮、不压任何图标。抓图见 `docs/taskbar-ulw.png`。

### 12.4 顺带查清：为什么从 DSH 会话启动时 `SetParent` 永远错误 5

第 11 节末尾留的疑点（"从 DSH 会话里启动 exe 时 SetParent 返回 ERROR_ACCESS_DENIED"）这一轮查到底了，
结论是**环境问题，不是代码问题**：

| 谁启动 | 进程完整性级别 | `SetParent` 到 Shell_TrayWnd |
| --- | --- | --- |
| 用户双击 / Explorer | Medium（0x2000） | 成功，任务栏上正常显示 |
| DSH 的 pwsh 工具起 .NET exe | **Low（0x1000）** | `err=5`，被 UIPI 拒绝 |
| 同一个 pwsh 工具里的 powershell.exe 自身 | Medium（0x2000） | 成功（原型脚本就是这么验证出来的） |

取证用的是独立探针 `tools\dockprobe\`（WinExe，复刻生产的 `SetWindowLong → SetParent → 重试` 序列，结果写文件）：
`self IL=other(0x1000)`、`tray IL=openProcess err=5`（连 explorer 的进程句柄都打不开）、
`setparent before=0x1000C ret=0x0 err=5 after=0x1000C ok=False`，show 之后重试仍然 `err=5`。
对照组 `tools\probe-setparent.ps1`（PowerShell 内建同一套窗口，4 种变体）全部 `err=0 parent=0x50134`。

绕路也试过，都不通，别再重复：

- `Start-Process explorer.exe "exe"` → 进程根本没起。
- `Shell.Application.ShellExecute` → DSH 的 pwsh 工具被挂成后台 job，exe 没起。
- `schtasks /Create ... /RL LIMITED` + `/Run` → 两个命令都返回 SUCCESS，但 `Last Result: -2147024703`，进程没起、日志为空。

所以产品日志里那句提示是准确的（`TaskbarWidget.cs:390-392`）：错误 5 时写
"系统拒绝了 SetParent（错误 5 / 拒绝访问）。多半是启动本程序的进程权限比 explorer 低，正常双击启动就好了。"
真出现这句，先看是不是被任务管理器 / 计划任务以低权限起起来的。

### 12.5 怎么用截图证明透明是"真透明"

`PrintWindow` 抓分层窗口会画出假的品红彩边，**不能**用来判断透明效果。
唯一可信的是抓真实屏幕：`tools\shot-screen.ps1`（`GetDC(0)` + `BitBlt`）。
这一轮的做法是让 `tools\ulw-demo.ps1`（PowerShell 版 widget，参数与生产一致：84 dip 宽、11 pt 粗体、
绿色 RGB(52,211,153)、文字 "32.2W"）以后台任务跑 40 秒，然后
`shot-screen.ps1 -X 560 -Y 1536 -W 280 -H 64 -Out docs\taskbar-ulw.png -Scale 4` 抓真屏。

脚本回执 `tray=0x50134 tbH=60 scale=1.25 w=105 x=641 parent=0x50134 painted=True`
（x=641 是因为那天的任务栏几何与现在差一点），图里只看得到绿色数字、看不到任何底色方块。
注意 `tools\taskbar-demo.ps1`（色键版原型）只适合验证"能不能挂上去"，透明效果要看 `ulw-demo.ps1`。

### 12.6 工具表（第 12 节新增）

| 工具 | 用途 | 注意 |
| --- | --- | --- |
| `tools\ulw-demo.ps1` | ULW 版 widget 原型（与生产同参数：84 dip / 11 pt / 绿色） | 用 `run_in_background` 跑，让它自己定时退出；别在 pwsh 里 `Start-Process` 等 GUI 程序，DSH 会把工具调用挂成后台 job |
| `tools\dockprobe\` | 进程完整性级别 + `SetParent` 结果探针（结果写 `bin\Release\...\dockprobe.txt`） | 拿它区分"代码错"和"权限不够"：`self IL` 是 Low 就一定是环境问题 |
| `tools\probe-setparent.ps1` | PowerShell 内建 5 种窗口姿势试 `SetParent` | 对照组，证明 Medium IL 下全部成功 |
| `tools\list-widgets.ps1` | 列出任务栏下我们残留的子窗口 | 演示脚本跑完用它确认没留垃圾 |

### 12.7 已知限制（更新）

- 竖排任务栏（`tbW <= tbH`）直接不显示（`TaskbarWidget.cs:323`）。
- 副屏任务栏 `Shell_SecondaryTrayWnd` 不显示。
- 拖动只支持水平方向，且会被夹在任务栏客户区内（不会拖出任务栏）。
- 任务栏自动隐藏时 widget 跟着一起藏（子窗口特性，反而省心）。
- 从低完整性级别进程（DSH 会话、部分沙箱）启动时挂不上任务栏，见 12.4。

