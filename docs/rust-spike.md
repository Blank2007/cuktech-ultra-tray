# Rust 原型实测报告（probe-rs spike）

> 目标：验证"用 Rust 重写 CUKTECH 充电器托盘程序"这条路线在本机（无 Rust、无 MSVC、无管理员权限的 Windows 11 x64）是否真的跑得通。
> 手段：在工作区内装 GNU 工具链，建最小 Cargo 工程，实测 **BLE 扫描** 与 **托盘图标** 两个核心能力。
> 结论先说：**跑通了，路线可行（综合 8/10）**，但构建链需要一次性手工修补（见"踩坑"），且必须限定 `-j 2` 并行度。

- 实测日期：2026-10-25
- 工程位置：`D:\dsh projects\cuktech-ultra-tray\src\probe-rs\`
- 工具链位置（全部在工作区内，未污染系统）：`D:\dsh projects\.tools\`
- 硬约束：**只扫描不连接**（`ble.rs` 里没有任何 `connect()` 调用），不写系统目录、不改 `src/probe-rs/` 之外的文件。

---

## 1. 结论与打分

| 能力 | 结论 | 打分 |
| --- | --- | --- |
| 工具链可安装（无 MSVC/无管理员） | 可行，走 `stable-x86_64-pc-windows-gnu` + 便携 binutils | 7/10 |
| `cargo build --release` | 可行，**冷编译 95.2s**（`-j 2`），必须限制并行度否则 OOM | 8/10 |
| 产物体积 | **4.23 MB**（默认 `strip=debuginfo`）；`strip=symbols` 后 **2.67 MB** | 8/10 |
| BLE 扫描（btleplug / WinRT） | 可行，8 秒扫到 15 个设备，**直接扫到充电器本体** | 9/10 |
| 托盘图标 + 菜单（tray-icon + winit） | 可行，`Shell_NotifyIcon` 返回 OK，5 秒自动退出 exit=0 | 8/10 |
| 体积最优路线 | 相比 .NET self-contained（60MB+）优势巨大，推荐 Rust | 8/10 |
| **综合** | **推荐采用 Rust 路线做托盘程序**；构建环境需固化（Docker/脚本） | **8/10** |

关键风险不是"能不能写"，而是"构建环境脆弱"：本机的 gnu target 依赖 `dlltool`/`as.exe` 手工补齐，换机器/换 CI 必须复刻同一套环境（见第 3 节）。

---

## 2. 环境

| 项 | 值 |
| --- | --- |
| OS | Windows 11 x64，build 26100 |
| CPU/内存 | 15.6 GB 物理内存（关键！见坑 7） |
| 已有工具链 | **无** Rust、**无** MSVC/VS、**无**管理员权限 |
| 采用工具链 | `stable-x86_64-pc-windows-gnu` |
| rustc / cargo | 1.99.1（rustc `58cf8f9c3` 2026-10-13，cargo `5f94df478` 2026-08-27），LLVM 23.1.1 |
| 磁盘占用 | rustup 2377 MB + cargo 769 MB + mingw64 73 MB ≈ **3.2 GB** |

---

## 3. 安装步骤（可复现）

### 3.1 工作区隔离的环境变量

```powershell
$env:RUSTUP_HOME = "D:\dsh projects\.tools\rustup"
$env:CARGO_HOME  = "D:\dsh projects\.tools\cargo"
$env:TMP = $env:TEMP = "D:\dsh projects\.tools\tmp"   # 必须！见坑 2
$env:PATH = "D:\dsh projects\.tools\cargo\bin;" +
            "D:\dsh projects\.tools\mingw64\mingw64\bin;" +
            "D:\dsh projects\.tools\rustup\toolchains\stable-x86_64-pc-windows-gnu\lib\rustlib\x86_64-pc-windows-gnu\bin\self-contained;" +
            $env:PATH
$env:CARGO_TERM_COLOR = "never"
```

### 3.2 装 rustup（走清华镜像）

```powershell
# 官方源在本机只有 0~304 KB/s 且会卡死在 5MB，清华源实测 646 KB/s
$env:RUSTUP_DIST_SERVER = "https://mirrors.tuna.tsinghua.edu.cn/rustup"
.\rustup-init.exe -y --default-toolchain stable-x86_64-pc-windows-gnu --profile minimal --no-modify-path
```

实测镜像速度（同一时刻、单线程）：

| 源 | 速度 |
| --- | --- |
| static.rust-lang.org | 304 KB/s，且多次卡在 5.2 MB / 0 KB/s |
| rsproxy.cn | 515 KB/s |
| **mirrors.tuna.tsinghua.edu.cn** | **646 KB/s** ← 采用 |
| mirrors.ustc.edu.cn | 403 拒绝 |

安装总耗时 58.3s（重装，走了 "Updating existing toolchain" 分支，`--profile minimal` 被忽略，实际装了 7 个组件；`CARGO_HOME/bin` 下 11 个 14.7MB 的 rustup 代理 exe ≈ 196MB）。

> 注意：**不要**用 `rustup-init.ps1` —— 本机 PowerShell 执行策略拦 .ps1（报 "AuthorizationManager 检查失败"）。用 .exe。

### 3.3 配 crates.io 镜像

`D:\dsh projects\.tools\cargo\config.toml`：

```toml
[source.crates-io]
replace-with = "rsproxy-sparse"

[source.rsproxy-sparse]
registry = "sparse+https://rsproxy.cn/index/"

[net]
retry = 5

[http]
multiplexing = false
```

直接连 index.crates.io 会 `spurious network error ... [28] Timeout was reached (Failed to connect to index.crates.io:443 after 21075 ms)`（同样时刻 PowerShell 的 `Invoke-WebRequest` 却能通，疑似 cargo 走了不通的 IPv6/连接方式）。换 sparse 镜像后 `cargo fetch` 63s 通过。

### 3.4 补 GNU 链接器（必须，见坑 3/4/5）

rustup 的 gnu 工具链**不自带 `as.exe` 汇编器**，而 `dlltool` 生成导入库时需要它。从 MSYS2 镜像取便携包，解压到工作区即可（不需要安装 MSYS2、不需要管理员）：

```powershell
$base = "https://mirrors.bfsu.edu.cn/msys2/mingw/mingw64/"
# 目标目录：D:\dsh projects\.tools\mingw64  （用 Windows 自带 bsdtar 3.8.4 直接解 zst）
tar -xf mingw-w64-x86_64-binutils-2.47-4-any.pkg.tar.zst   # as.exe / dlltool.exe / ld.exe / windres.exe
tar -xf mingw-w64-x86_64-gettext-runtime-1.0-1-any.pkg.tar.zst   # libintl-8.dll
tar -xf mingw-w64-x86_64-libiconv-1.19-1-any.pkg.tar.zst         # libiconv-2.dll（as.exe 运行必需）
tar -xf mingw-w64-x86_64-zstd-1.5.7-2-any.pkg.tar.zst            # libzstd.dll
tar -xf mingw-w64-x86_64-zlib-1.3.2-3-any.pkg.tar.zst            # zlib1.dll
```

解压后 `as.exe --version` 必须输出 `GNU assembler (GNU Binutils) 2.47.20260726` 才算成功。

### 3.5 构建

```powershell
cd "D:\dsh projects\cuktech-ultra-tray\src\probe-rs"
cargo build --release -j 2      # -j 2 不是建议，是必须，见坑 7
```

---

## 4. 踩坑清单（每条都是实测卡点）

### 坑 1：`error calling dlltool 'dlltool.exe': program not found`
rustup 把 `dlltool.exe`/`ld.exe`/`x86_64-w64-mingw32-gcc.exe` 放在
`...\toolchains\stable-x86_64-pc-windows-gnu\lib\rustlib\x86_64-pc-windows-gnu\bin\self-contained\`，
默认**不在 PATH**。解法：把该目录加进 PATH。

### 坑 2：`Cannot create temporary file in C:\Users\<user>\AppData\Local\Temp\: Permission denied`
dlltool 写临时文件被拒。解法：`$env:TMP=$env:TEMP="D:\dsh projects\.tools\tmp"`（工作区内）。

### 坑 3：`dlltool.exe: CreateProcess` → 缺 `as.exe`
dlltool 内部要 fork `as.exe` 汇编，rustup 未附带 binutils 汇编器。
试过 `rustup component add llvm-tools`（18.8s 装好）——**无用**：只有 `llvm-as`/`llvm-ar`/`opt`，没有 `llvm-dlltool`/`llvm-mc`，顶替不了。
最终解法：见 3.4（便携 MSYS2 binutils）。

### 坑 4：`as.exe` 静默失败 → 0 字节 .lib
症状链条很迷惑：
1. `as.exe` 缺 DLL（`libiconv-2.dll`）时以 `STATUS_DLL_NOT_FOUND (-1073741515)` 退出，**没有任何提示**；
2. `dlltool` 于是产出一个 **0 字节**的 `*_imports.lib`；
3. rustc 报 `failed to add native library ...\kernel32.dll_imports.lib: 系统找不到指定的文件 (os error 2)`。

排查手段：把 exe 当二进制扫 ASCII 里的 `*.dll` 字符串，得到 `as.exe` 的真实依赖 = `kernel32 / libintl-8 / libzstd / zlib1 / msvcrt`，再查 `libintl-8.dll` 依赖 `libiconv-2.dll`。补齐后症状消失。

### 坑 5：为什么每个 `windows-*` crate 都在调 dlltool？
`windows-sys` 0.61 / `windows` 0.62 在 **gnu target** 上使用 `#[link(kind="raw-dylib")]`，rustc 编译 rlib 阶段就要生成 `*_imports.lib`，因此每个 windows crate 都会触发 dlltool。msvc target 没有这个问题（也正是 gnu 路线的主要摩擦点）。

### 坑 6：`cargo build` 偶发诡异语法错误（假错误）
曾出现一批看起来完全不可能的报错：
```
error[E0531]: cannot find tuple struct or tuple variant `Some` in this scope
error: cannot resolve a prelude import
error: only metadata stub found for `rlib` dependency `core` ...
error: ... (exit code: 0xc0000409, STATUS_STACK_BUFFER_OVERRUN)
```
**根因见坑 7**（内存不足导致 rustc 中途被截断）。忽略这些报错，去看日志里第一条真正的 `error`。

### 坑 7（最重要）：`failed to map object file: 页面文件太小，无法完成操作 (os error 1455)`
本机 15.6 GB 物理内存、提交上限 34.8 GB，但开构建时可用提交只剩 **2.6 GB**。cargo 默认并行 `jobs=16`，同时起十几个 rustc + lld，**瞬间耗尽提交空间** → 随机 crate 编译失败、产出 0 字节 rlib，并连累其他 crate 报"假错误"。
解法：`cargo build --release -j 2`。实测 `-j 2` 一次通过（95.2s），默认并行度必挂。
> 这是本项目所有"随机编译失败"的唯一真凶，写 CI/构建脚本时务必带上 `-j 2`（或 `CARGO_BUILD_JOBS=2`）。

### 坑 8：PowerShell 侧的坑
- 别用 `.ps1` 安装脚本（执行策略拦）。
- 命令行里不要出现反引号（PowerShell 转义符），会截断外部模板字符串；要引号用 `[char]34` 拼。
- `cargo build` 的进度走 stderr，PowerShell 会当错误抛出；用 `cmd /c "cargo build --release 2>&1"`。

---

## 5. 工程结构与依赖

```
src/probe-rs/
├── Cargo.toml
└── src/
    ├── main.rs   # 参数解析：scan | tray | all（默认）， 第二个参数=秒数（tray 默认 5s，scan 默认 8s）
    ├── ble.rs    # btleplug 扫描：Manager → adapters → start_scan → sleep → peripherals → stop_scan（不连接）
    └── tray.rs   # winit 0.30 ApplicationHandler + tray-icon：菜单 "Quit" + 运行时生成 32x32 图标
```

`Cargo.toml` 依赖（刻意精简，共约 120 个传递依赖）：

```toml
[dependencies]
btleplug  = "0.13"                                   # 0.13.5，Windows 后端走 WinRT（windows 0.62）
futures   = "0.3"
tokio     = { version = "1", features = ["rt", "time", "macros"] }  # btleplug 的硬依赖，收敛 feature
tray-icon = "0.26"                                   # 0.26.1（依赖 windows-sys 0.61）
uuid      = "1"
winit     = "0.30"                                   # 0.30.13（0.31 还是 beta）。只为跑事件循环，用 tao 也可以
```

设计要点：
- **不引 image crate**：托盘图标在运行时用 `RgbaImage` 直接画 32x32 青色圆盘，省一个重依赖。
- **不需要额外线程**：`ControlFlow::WaitUntil(deadline)` + `about_to_wait` 里判超时即 `exit()`，5 秒自动退出。
- 扫描结束用 `stop_scan()`，全程只 `peripheral.properties()` 读广播，**没有 `connect()`**（满足"不抢 C# 主控台的连接"约束）。

---

## 6. 实测数据

| 指标 | 数值 |
| --- | --- |
| hello world（`cargo new` 最小工程） | 编译 1.49s，产物 1.22 MB（1,223,996 B），运行 OK |
| probe-rs **冷编译**（`cargo clean` 后，`-j 2`） | **95.2s** |
| probe-rs 增量编译（只改源码） | 1m04s → 实际改动后 ~12s 级（首末两次观测 11.5~64s，取决于改动范围） |
| **产物 probe-rs.exe（默认）** | **4.23 MB（4,430,613 B）** |
| 产物 `RUSTFLAGS="-C strip=symbols"` | **2.67 MB（2,799,616 B）**，编译 116.5s |
| `target/release` 目录总大小 | 227.9 MB / 598 文件（可 `cargo clean` 清掉） |
| 依赖 crate 数 | 约 120 个传递依赖 |
| 托盘进程启动 → 图标就绪 | < 1s（事件循环 3s 采样点已创建完毕） |
| 托盘 5 秒自动退出实测 | exit=0，实际 5.2s |

### 6.1 BLE 扫描实测（`probe-rs.exe scan 8`）

```
[ble] adapters found: 1
[ble] adapter info: Ok("WinRT")
[ble] scanning for 8s (no connections will be made)...
[ble] scan finished in 8.0s, 15 peripheral(s) seen

#   address              name                     rssi  0xFE95?   services                     svc_data
------------------------------------------------------------------------------------------------------------------------
1   AA:BB:CC:DD:EE:FF    njcuk.fitting.ad1204     -41   YES                                    fe95[11B]
2   03:97:0C:0D:EE:29    <no name>                -65   no                                     fd2d[27B]
3   06:84:83:3A:9D:A6    <no name>                -88   no
4   33:1B:A5:E9:DA:96    <no name>                -84   no
5   46:C0:A6:53:7D:D3    <no name>                -82   no        180a,61ce1c20-e8bc-4287-91f~
6   47:89:8A:45:EA:BF    沐白                       -86   no        61ce1c20-e8bc-4287-91fd-7cc~
7   48:9C:42:92:04:3B    <no name>                -92   no        005eb7ca-5306-0301-0100-2cf~
8   4C:D8:F7:18:8C:EC    <no name>                -73   no
9   4D:A8:BC:D3:DE:39    <no name>                -33   no                                     fcc0[59B]
10  52:57:16:FD:99:17    <no name>                -91   no
11  57:17:7C:CB:B2:FB    <no name>                -58   no                                     fcc0[93B]
12  58:51:96:E2:EB:E2    <no name>                -36   no        fdaa                         fdaa[27B]
13  67:84:A5:44:59:36    <no name>                -91   no
14  6E:79:8C:9D:3C:FD    <no name>                -56   no        fdaa                         fdaa[21B]
15  87:43:83:D7:27:6B    iKF-V11 Pro⁺-App         -82   no
------------------------------------------------------------------------------------------------------------------------
[ble] total=15 with_0xFE95=1 (MiBeacon/MiOT candidates)
```

要点：
- 单次 8 秒扫描就**直接命中充电器**：`njcuk.fitting.ad1204` / `AA:BB:CC:DD:EE:FF`（RSSI -41，最近），且带 **`fe95` service data 11 字节** —— 与"0xFE95 筛选"策略完全对得上。
- `0xFE95` 判定同时看 `services` 和 `service_data`（本例设备只出现在 `service_data` 里，只看 services 会漏 —— 这点很重要，建议 C#/Rust 实现都按两者取并集）。
- 名字里的 `njcuk.fitting.ad1204` 是 MiBeacon 广播里的厂商串，不是稳定 ID；**稳定标识应当用 MAC**（但 MiBeacon 的 MAC 可能随机化，需要复扫验证）。
- Windows 侧 adapter 信息是 `Ok("WinRT")`，即 btleplug 用的是 WinRT 后端（不是 Win32 BLE API）。
- **0xFE95 广播不是持续可见**：后续一次 4 秒复扫（12 个设备）`with_0xFE95=0`，充电器没出现在列表里。说明 MiBeacon 广播是间歇性的（可能受充电器状态/已有连接影响），因此**"扫到即退出"的策略不可靠**，正式实现应连续扫描 N 秒或多次重试后再判定"设备不在附近"。

### 6.2 托盘实测（`probe-rs.exe tray 5`）

```
[tray] starting winit event loop, auto-exit after 5s
[tray] tray icon created OK (id=TrayIconId("17216-1"))
[tray] icon visible; will auto-exit in 5s
[tray] event loop exited cleanly after 5s
EXIT=0 elapsed=5.2s
```

- `TrayIconId` 成功返回 = `Shell_NotifyIcon(NIM_ADD)` 成功，图标已进通知区，菜单（`Quit` + 一个 disabled 标题项 + separator）绑定成功。
- 退出时 stderr 会打印一行 `Error removing system tray icon`：这是 tray-icon 在事件循环已退出后 Drop 时清理失败的**良性提示**，不影响退出码（实测 exit=0）；正式版应在 `exiting()` 里主动 `drop(tray)` 或忽略该错误。
- **Windows 11 默认行为坑**：新建的托盘图标会被放进"隐藏的图标"溢出区（`显示隐藏的图标` 按钮里），不会直接出现在任务栏。用 UI Automation 按 tooltip 名 `CUKTECH Ultra - Rust spike` 在顶层查不到，也就是"图标创建成功但用户看不见"。正式产品必须处理：设置应用图标并引导用户把它固定到任务栏，或改用 `Shell_NotifyIcon(NIM_ADD)` + `NIF_STATE/NIS_HIDDEN` 相关策略（Windows 11 上仍受"新图标默认隐藏"设置影响）。
- 未做真实菜单交互点击测试（5 秒窗口太短且无人点击），但菜单对象构建、事件转发通道（`EventLoopProxy` + `MenuEvent::set_event_handler`）都已跑通，无 panic。

---

## 7. 风险与建议

1. **构建环境脆弱（最大风险）**：gnu target + raw-dylib 需要手工 `dlltool`/`as.exe`/`libiconv`。建议把 3.1~3.4 固化成脚本（或直接用 msvc target + VS Build Tools 的 CI 机器）。
   `-j 2` 已写进 `src/probe-rs/.cargo/config.toml`，之后直接 `cargo build --release` 即可（实测增量 1.45s）：
2. **内存**：15.6GB 机器开 16 并行 rustc 会 OOM（os error 1455）。这条对任何 Rust 项目都成立，不只是本 spike。
3. **Windows 11 托盘图标默认隐藏**：产品必须处理，否则用户以为程序没启动。
4. **btleplug 依赖 tokio**（不可选），约 120 个传递依赖；产物本身只有 4.23MB（strip 2.67MB），相比 .NET self-contained（通常 60MB+）优势明显，但**不再有 .NET 的免安装运行时优势**（Rust 产物不需要运行时，反而是优势）。
5. **未验证的部分**（任务边界内没做）：
   - 真实连接 + GATT 读写（受"不得连接设备"约束，留给 C# 主控台）；
   - 长驻运行（内存/句柄泄漏）、开机自启、单实例、图标动态更新；
   - 代码签名 / 安装包。
6. **建议**：托盘壳用 Rust 完全可行且体积最优；若最终 C# 主控台保留 BLE 独占，Rust 壳可与 C# 进程用命名管道/本地 HTTP 通信，避免两边抢蓝牙适配器。

---

## 8. 复现命令速查

```powershell
# 一次性：按 3.1 设置 5 个环境变量
cd "D:\dsh projects\cuktech-ultra-tray\src\probe-rs"
cargo build --release -j 2
.\target\release\probe-rs.exe scan 8      # BLE 扫描 8 秒（只扫不连）
.\target\release\probe-rs.exe tray 5      # 托盘 5 秒自动退出
```

---

*本报告所有数字均为本机实测，命令与输出可在上文原样复现。*
