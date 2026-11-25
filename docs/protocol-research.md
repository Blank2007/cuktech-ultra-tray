# 酷态科 CUKTECH 10 号 Ultra 充电器 —— BLE / 米家协议调研报告

> 调研目的：为「Windows 托盘小工具直接本地读功率」摸清协议与可抄的开源实现。
> 调研方式：本地解包阅读 `kairui1108/cuktech-ble-ha`（Python）与 `kairui1108/cuktech-ble-esp32`（C）源码 + GitHub 搜索。
> 结论时间：2026-10-25。

---

## 0. 结论速览（TL;DR）

1. **协议 = 小米 MIoT BLE（mible），不是 miBeacon 被动广播，也不是明文私有 GATT。** 充电数据走 `0xFE95` 服务下的私有特征，**AES-CCM 加密会话**，必须先完成 Mi 认证（需要设备的 12 字节 token）。
2. **功率数据来源有两条**：C1/C2 口由固件**主动推送**（notification，实时）；**C3/A 口固件从不推送，只能主动 GET 轮询**（源码实测定为 3 秒一次）。
3. **单口数据 = 4 字节**：`[状态][code][电流×0.1A][电压×0.1V]`。电压/电流各只有 **1 字节、0.1 分辨率**（20.1V / 2.5A），功率由主机相乘得到。这是本项目**精度天花板**。
4. 现成可抄实现两份：Python（bleak，**跨平台，含 Windows**）与 ESP32 C（无依赖）。**Python 那份已在本机 Windows 上跑过**（源码有 win32 分支），是托盘工具的最佳移植基线。

---

## 1. 设备身份

| 项 | 值 | 证据 |
|---|---|---|
| 商品名 | 酷态科 10 号 Ultra / CUKTECH 10 GaN Charger Ultra | kairui1108/cuktech-ble-ha README 标题 |
| 内部型号串 | `njcuk.fitting.ad1204_<芯片名>` | `ble_server/src/cuktech_ble/controller.py:315` |
| 推测型号 | **AD1204U**（`ad1204` 前缀；Lead 独立确认 120W 带屏版） | 同上 |
| MiOT productId | **0x660E = 26126** | `ble_server/src/cuktech_ble/protocol.py:133` |
| BLE Service | **0xFE95**（Xiaomi MiOT） | `protocol.py:144` |
| 广播 | 顶层 service 列表**不含** 0xFE95；真实 MAC 只在 **MiBeacon service-data（0xFE95 payload）** 内，小端，`payload[-6:]` | `ble_server/ble_manager.py:1489-1510` |
| 固件版本 | char `0x0004` 直接 Read，明文 ASCII | `controller.py:318-323` |
| 实测固件 | `2.1.2_0073`（作者真机，2026-10） | `controller.py:940` 注释 |

> ⚠️ **AP01 不是这颗充电器**。AP01 是酷态科另一款带 320×240 屏的设备（`wqytommy666/cuktech-screen-controller`，固件 1.0.2_0031），走 Wi-Fi + 米家云端 FDS ticket + 刷固件，与 10 号 Ultra 的 BLE 路线完全不同，**结论不可混用**。

---

## 2. 协议判定与证据

判定为 **MIoT BLE（mible）加密私有通道**：

1. 服务 UUID 是小米 `0xFE95`，特征短 UUID `0x0010/0x0019/0x001a/0x001b/0x001c/0x0004` —— 与小米 MiOT BLE 规范一致（`protocol.py:143-150`）。
2. 认证与 python-miio / miauth 同构：`HKDF(SHA256, salt=app_random||dev_random, info=b"mible-login-info", L=64).derive(TOKEN)`（`controller.py:491-497`）。
3. 数据通道 **AES-CCM tag=4**，nonce = `iv(4) + 00000000 + it(4 LE)`（`controller.py:683-731`）。
4. 命令是 **MiOT Spec TLV**（siid/piid/type-len），opcode 0x00=SET / 0x02=GET / 0x03=GET结果 / 0x04=推送（`controller.py:838-884`）。
5. 必须 12 字节 **token**；链路有"解密率"指标 —— 非明文。

**未配对不能读功率**（认证是硬门槛）；但**设备型号 + 固件版本不需要认证**（`controller.py:291 read_device_info` 注释「无需认证」）。

---

## 3. GATT 通道

| 用途 | 特征 UUID（0xFE95 下，短 UUID） | 属性 | ATT 句柄（实测） |
|---|---|---|---|
| 设备信息查询 | `0000001c` | Write NoResp / Notify | 0x001f |
| 认证控制 | `00000010` | Write NoResp / Notify | 0x000d |
| 认证数据 | `00000019` | Write NoResp / Notify | 0x0010 |
| 命令发送（加密） | `0000001a` | Write NoResp / Notify | 0x0019 |
| 命令接收（加密） | `0000001b` | Write NoResp / Notify | 0x001c |
| 固件版本 | `00000004` | Read | 0x0008 |

（`ble_server/src/cuktech_ble/protocol.py:135-157`；完整 UUID 形式为 `0000XXXX-0000-1000-8000-00805f9b34fb`）

- **MTU**：bleak 自动协商，源码只读 `client.mtu_size` 做诊断，**不手动请求**；MTU=23 时端口推送帧会被分片（`controller.py:209-219`）。
- **连接参数**：无自定义；连上后 `sleep(2)` 等设备稳定 → 读设备信息 → 认证（`ble_manager.py:1527-1535`）。
- **订阅顺序必须对齐米家**：先 `cmd_recv, cmd_send, dev_info`，再 `auth_data`；`auth_ctrl` 延迟到密钥交换完成后才订阅（`controller.py:229-247, 431-437`）。

---

## 4. 认证（握手）流程

> 依据：miauth 库 + BLE 日志 / Frida 抓包验证（`controller.py:333-341, 543-547`）

**Phase A 设备初始化**
1. 写 `auth_ctrl` = `A4`（1 字节）。
2. 收 `auth_data` 响应，把 `byte[2] += 1` 回写 `auth_data`（04→05，协议协商回传）。
3. 收设备密钥交换数据（本设备实测 240 字节，带 `0xF2` 占位）。
4. 回写等长占位：`00 00 05 01` + `0xF2`×(len-4)。

**Phase B 登录（CMD_LOGIN）**
5. ⚠️ 此时才 `start_notify(auth_ctrl)`。
6. 写 `auth_ctrl` = `24 00 00 00`（CMD_LOGIN）。
7. 写 `auth_data` = `00 00 00 0B 01 00`（CMD_SEND_KEY）→ 等 `00 00 01 01`（RCV_RDY）。
8. 写 `auth_data` = `01 00` + **16 字节随机 app_random** → 等 `00 00 01 00`（RCV_OK）。
9. 从 `auth_data` 收设备 16 字节 **dev_random**，再收 32 字节 **dev_hmac**。
10. 派生：

    derived = HKDF(SHA256, length=64, salt = app_random || dev_random,
                   info = b"mible-login-info").derive(TOKEN)
    dev_key = derived[0:16];  app_key = derived[16:32]
    dev_iv  = derived[32:36]; app_iv  = derived[36:40]

11. 验设备：`HMAC-SHA256(dev_key, salt_inv)`，`salt_inv = dev_random || app_random`。
12. 写 `auth_data` = `00 00 00 0A 01 00`（CMD_SEND_INFO）→ 等 RCV_RDY。
13. 写 `auth_data` = `01 00` + `HMAC-SHA256(app_key, salt)`（32B）。
14. **第二轮挑战-应答**：设备推 `0x0d`(16B challenge) / `0x0c`(32B response)，每次回 keepalive `00 00 03 00`；收到 `0x0c` 后回 ACK `00 00 00 0A 01 00`；随后回写 `01 00 0C` + response[3:]，等 RCV_OK。
15. 读 `auth_ctrl` 通知：**`0x21` = 登录成功**，`0x11` = 激活成功，`0x23` = 登录失败，`0x12` = 激活失败。
16. 成功后：`sent_it=0`、`dev_it_hi=0`、`miot_seq=1`，并 drain 掉设备认证后自动推送的初始端口帧（`controller.py:744-755`）。

**失败重试**：设备状态机可能不同步（收到 <20 字节且 `byte[2]==0x04` 的重复 init 响应），代码最多等 12 秒重收 key exchange；仍失败则整链重连。

---

## 5. 加密与帧格式

### 5.1 AES-CCM（tag 长度 4）
- 发送：`nonce = app_iv(4) + 00000000 + send_it(4 LE)`，`AESCCM(app_key, tag_length=4)`，输出 `it_lo it_hi` + ciphertext。
- 接收：`nonce = dev_iv(4) + 00000000 + dev_it(4 LE)`，`AESCCM(dev_key, tag=4)`。
- **坑**：设备帧里只带 it 的**低 16 位**，超过 65535 后高 16 位非 0；源码靠"低 16 位回绕（差值 > 32768）"自行进位（`controller.py:711-723`）。托盘是短连，基本踩不到。

### 5.2 传输分帧（auth_data / cmd_send / cmd_recv 通用）
- 写：头部 `00 00 00 00 01 00`（声明发 1 帧）→ 等 `00 00 01 01`（RCV_RDY）→ 写 `01 00` + payload → 等 `00 00 01 00`（RCV_OK）。
- 收：`data[2]==0x02` = 内联单帧（payload 在 `data[4:]`，回 ACK `00 00 03 00`）；`data[2]==0x00` = 多帧（`data[4] | data[5]<<8` = 帧数，回 `00 00 01 01`，收 N 个子帧，每帧剥 2 字节帧号后拼接，再回 `00 00 01 00`）。
- **多帧必须整体解密一次**，逐帧解密必然偏移错位（`controller.py:1030-1053`、`ble_manager.py:2261-2266`）。

### 5.3 MiOT TLV 明文
    [len:1B][0x20][seq:1B][0x00][opcode:1B][cnt=0x01][siid:1B][piid:2B LE][tl:2B LE][value:NB]
    tl       = (type_id << 12) | byte_length     # type_id: 1=UINT8, 3=UINT16, 5=UINT32
    opcode   = 0x00 SET / 0x02 GET
    len      = 11 + byte_length

响应（索引基于解密后明文，`controller.py:882-884`）：
- SET ACK: `0B 20 seq 00 01 01 siid piid`
- GET Result: `.. 20 seq 00 03 01 siid piid 00 00 len type value..`
- Push: `.. 20 seq 00 04 01 siid piid 00 00 len type value..`
- `pt[9] | pt[10]` 非零 = 设备错误码，**仅 GET 路径可信**；SET 路径实测恒为 `01 10` 但设备确实执行了，照搬 GET 判定会把每次成功都误报成失败（`controller.py:939-946`）。
- value 在 `pt[13:13+len]`（len≥4 时按 LE u32 读 `pt[13:17]`）。

---

## 6. 功率 / 端口数据（核心）

### 6.1 属性位置
SIID = **2**（充电器服务）；PIID **1/2/3/4 = C1/C2/C3/A 口**。值 = 4 字节 = 解密帧最后 4 字节 `pt[-4:]`。

### 6.2 字节布局（`ble_server/state_protocol_v2.py:91-98`）

    b = payload[-4:]
    status_raw  = b[0]   # 0x00=空闲, 0x01=单口供电, 0x11=C3+A 合并通道
    code        = b[1]   # PDO 档位索引 / 硬件状态码 —— 不是协议号！
    current_raw = b[2]   # 电流 = current_raw / 10.0  (A)  → 分辨率 0.1A
    voltage_raw = b[3]   # 电压 = voltage_raw / 10.0  (V)  → 分辨率 0.1V
    power       = round(voltage * current, 1)
    active      = in_use or voltage > 0 or current > 0

**实测样例**（`ble_server/tests/test_state.py:26-35`）：`...01 0A 19 C9` → in_use=1, code=0x0A, current=25→2.5A, voltage=201→20.1V, power=50.2W。

> ⚠️ **`code` 字节不是协议号**。v2 版曾用它推断 PD/PPS，后被固件反汇编证伪（20V PD 充电时该字节在 0x0B/0x04 跳变），v3 已删除该推断（`state_protocol_v2.py:8-12`）。别重蹈覆辙。

### 6.3 推送 vs 轮询（关键工程结论）
- **C1/C2**：认证后固件主动推送 → 实时（`pt[4]==0x04 && piid in {1,2}`）。
- **C3/A**：**固件永不推送**（与 C3 共用 0x11 合并检测通道），只能 GET 轮询，**间隔 3 秒**（`ble_manager.py:202 _C3A_POLL_INTERVAL_SEC = 3`）。
- C1/C2 停止推送 > **15 秒** → 主动 GET 一次，防止拔线推送丢失导致读数卡在旧值（`_IDLE_VERIFY_SEC`，`ble_manager.py:195`）。
- 协议显示去抖：需连续 **3** 次读数一致（`_PROTO_DEBOUNCE_N = 3`）；"无负载"需持续 15 秒（`NO_LOAD_DEBOUNCE_SEC`）。
- **上电顺序**：认证成功后设备主动推一轮初始端口帧（`ctrl.init_push_frames`），托盘应消费这批，否则首屏要等轮询。

### 6.4 协议类型（PD/PPS/QC/UFCS/SCP）

**权威来源是 PIID 17 / PIID 18**，不是口数据里的 `code`：

    PIID 17 (c1_c2_protocol, u32): [C1_proto][C1_power_W][C2_proto][C2_power_W]
    PIID 18 (c3_a_protocol,  u32): [C3_proto][C3_power_W][A_proto ][A_power_W ]

即 `(v>>24)&0xFF` = 高口协议号，`(v>>16)&0xFF` = 高口协商功率(W)，`(v>>8)&0xFF` = 低口协议号，`v&0xFF` = 低口功率（`ble_server/state.py:256-281 decode_pdo_caps`）。

米家协议号表（`state_protocol_v2.py:23-35`）：`0=idle, 1/2=5V, 3=QC, 4=AFC, 5=FCP, 6=SCP, 7=PD, 8/9=PPS, 10=UFCS`。C3/A 口可能返回 MiOT 原始 code（0x60=USB-A、0x70=QC、0x80=PD），需映射回米家号（`state_protocol_v2.py:142-145`）。

固件在 PD/PPS 协商变化时**主动推送** PIID 17/18（`pt[4]==0x04 && piid in (17,18)`，值在 `pt[12:16]` LE u32），所以协议显示可实时（`ble_manager.py:2238-2259`）。

---

## 7. 控制命令（SIID=2）

| 功能 | PIID | 取值 |
|---|---|---|
| 场景模式 | 5 | 1=AI 2=数码生态 3=单口 4=均衡 |
| 息屏时间 | 6 | 1=5min 2=10min 3=30min 4=常亮 5=1min |
| 协议控制（只写） | 7 | — |
| 倒计时设置 | 8 | 0..1440 |
| 各口倒计时 | 9/10/11/12 = C1/C2/C3/A | 0..1440 分钟 |
| 语言 | 13 | 0=EN 1=中文 |
| 进入界面（只写） | 14 | 1-5 |
| USB-A 小电流 | 15 | 0/1 |
| **端口开关** | **16** | **位掩码 bit0=C1, bit1=C2, bit2=C3, bit3=A** |
| 空闲息屏 | 19 | 0/1 |
| 屏幕方向锁 | 20 | 0/1 |
| **协议开关** | **21** | 32 位位掩码，见下 |

（`protocol.py:162-178`、`state.py:24-49`）

**PIID 16 必须"读-改-写"**（`ble_manager.py:2060-2101`）：先 GET 16 取基线 → `cur | (1<<bit)` / `cur & ~(1<<bit)` → SET。
- 全开/全关用常量 `0x0F` / `0x00`，不需基线。
- **GET 失败时绝不能按 0 兜底**：会把其余供电中的口一起关掉且不可自愈；取不到基线就拒绝写入。
- SET 响应多为 **ACK-only**（无回显值）；也不能"值没变就跳过"，缓存可能滞后（固件倒计时会自行关口）→ 会产生"假成功"。

**PIID 21 协议开关位定义**（`state.py:38-49`）：

    c1Flags = bit0 PD  | bit1 PPS | bit2 UFCS | bit3 保留(固定1)
    c2Flags = bit8 PD  | bit9 PPS | bit10 UFCS | bit11 保留
    c3Flags = bit16 UFCS | bit17 SCP
    aFlags  = bit24 UFCS | bit25 SCP
    value   = aFlags<<24 | c3Flags<<16 | c2Flags<<8 | c1Flags

**可读设置 PIID 全集**：`[5,6,8,9,10,11,12,13,15,16,17,18,19,20,21]`（`protocol.py:181`）。

---

## 8. 工程细节（重连 / 稳定性 / 解密率）

| 项 | 值与做法 | 出处 |
|---|---|---|
| 重连退避 | `base * 2^min(attempt,10)`，上限 `reconnect_max_delay`，±25% 抖动，最小 0.5s | `ble_manager.py:1319-1329` |
| 认证失败退避 | ≥5 次→600s；≥3 次→300s；否则 `min(120*n,180)` s（等设备清 BLE 会话） | `ble_manager.py:1270-1281` |
| 连续认证失败上限 | 15 次 → 重启进程（Linux `os.execv`；**win32 只能干净退出靠外部拉起**，WinRT 事件循环无法在新映像重建） | `ble_manager.py:70, 1288-1317` |
| 解密失败判定 | 连续 **3** 次失败 → 判会话密钥失步 → 主动重连 | `ble_manager.py:76, 2280-2286` |
| 熔断 | 连续 20 次失败 → 冷却 300s | `ble_manager.py:68-69` |
| GET 失败累积 | verify_port 连续 5 次无响应 → 强制重连 | `ble_manager.py:209` |
| 断连前清理 | 必须 `stop_notify` 全部 5 个通道，否则 BlueZ GATT 缓存 CCCD 错乱，快速重连认证必失败 | `controller.py:251-282` |
| 失败断开后等待 | `sleep(3)` 再重连，避免旧会话未释放 | `ble_manager.py:1543-1545` |
| 连接质量 | 评分 0-100（解密率 / 通知响应 / 重连次数）→ 佐证链路会丢包 | README 功能列表 |

---

## 9. token / key 从哪来

- 需要 **12 字节（24 位 hex）token**。来源两条：
  1. **小米云账号**：登录米家账号 → 拉设备列表 → 取 token。现成工具 [PiotrMachowski/Xiaomi-cloud-tokens-extractor](https://github.com/PiotrMachowski/Xiaomi-cloud-tokens-extractor)。
  2. 本项目作者在 Web 配置页内置「**小米云扫码自动获取**」（`ble_server/xiaomi_cloud.py`，19KB），复用米家扫码登录。
- **`CUKTECH_DEVICE_BLE_KEY`（16 字节）虽从环境变量读入，但认证代码只用了 token**（`controller.py:491-497` 只用 `self.token`）→ 需确认是否真的不需要。
- **未配对不能读功率**；但型号 + 固件版本不需要认证。
- **米家 App 会抢连接**：BLE 同一时刻只允许一个中心连接，托盘运行前应让米家 App 断开/退到后台。

---

## 10. Linux 依赖 / Windows 可移植性

**结论：可移植。** 协议层只用 **bleak + cryptography**，无 Linux 专用代码。

- Linux 专用逻辑都在 `ble_manager.py` 的外围自愈里，**非协议必需**：
  - `_find_ble_adapter()` 读 `/sys/class/bluetooth`（`ble_manager.py:1455-1467`）；
  - `_stop_ble_scan()` / `_force_disconnect_bluetooth()` 调 `bluetoothctl scan off / disconnect / power off|on`（`ble_manager.py:1629-1710`），有 `_has_bluetoothctl()` 守卫，非 Linux 直接跳过（注释：「其它平台由 bleak 层处理断连」）。
- 源码已有 **win32 分支**（`ble_manager.py:1291-1293`）→ 作者在 Windows 上跑过。
- Windows 注意点：bleak 走 WinRT；`os.execv` 自愈不可用（需外部看守进程）；Windows 上 MTU 由系统协商，通常 23~247 不定。
- 依赖极轻（`ble_server/requirements.txt` 仅 69 字节）：bleak、cryptography（paho-mqtt / PyYAML 仅服务端用）。

---

## 11. GitHub 项目清单

按对本项目的价值排序。

### 11.1 直接对口（规格书 + 参考实现）

| 项目 | 语言 | 许可证 | 价值 |
|---|---|---|---|
| [kairui1108/cuktech-ble-ha](https://github.com/kairui1108/cuktech-ble-ha) | Python | MIT | ⭐⭐⭐⭐⭐ 同型号「CUKTECH 10 GaN Charger Ultra」接入 Home Assistant。含 BLE Server（bleak + cryptography）、Web UI、MQTT、HA 集成，以及**完整协议逆向结论**。本报告第 3–9 节主要来源。 |
| [kairui1108/cuktech-ble-esp32](https://github.com/kairui1108/cuktech-ble-esp32) | C (ESP-IDF) | 见仓库 | ⭐⭐⭐⭐ 同协议的第二份独立实现（`esp32_ble/main/miot_auth.h`、`ble_manager.c`），可用于交叉验证握手/加解密细节；也是「PC 不常连设备」时的硬件桥方案。 |
| [PiotrMachowski/Xiaomi-cloud-tokens-extractor](https://github.com/PiotrMachowski/Xiaomi-cloud-tokens-extractor) | Python | MIT | ⭐⭐⭐⭐ 登录小米账号批量导出设备 **token / ble key**，是拿 12 字节 token 的标准工具；也可只取其登录流程写进我们的程序。 |

### 11.2 参考但不同协议（易误用，标注清楚）

| 项目 | 说明 | 结论 |
|---|---|---|
| [wqytommy666/cuktech-screen-controller](https://github.com/wqytommy666/cuktech-screen-controller) | 酷态科 **AP01「10 号电能基站」**（320×240 屏）刷机/控屏工具 | ❌ **不是同一款设备**（AP01 走 Wi-Fi + 固件刷写）。仅当设备其实是 AP01 时才相关。 |
| [rytilahti/python-miio](https://github.com/rytilahti/python-miio) | 米家 **Wi-Fi** 设备主力库（miIO 协议） | ⚠️ 对 BLE 支持零散；本项目走 MIoT **BLE**，参考价值有限。 |
| [home-assistant/core](https://github.com/home-assistant/core) 的 `xiaomi_ble` / [Bluetooth-Devices/xiaomi-ble](https://github.com/Bluetooth-Devices/xiaomi-ble) | 解析小米 BLE **被动广播**（温湿度计等） | ❌ 本充电器不发可用功率广播，必须连接 + 认证。 |
| [esphome/esphome](https://github.com/esphome/esphome) `xiaomi_ble` 组件 | 同上，ESP32 上的广播解析 | ❌ 同上。 |

### 11.3 底层通用库（实现时可能直接依赖）

| 库 | 用途 |
|---|---|
| [hbldh/bleak](https://github.com/hbldh/bleak) | Python BLE（Windows 走 WinRT、Linux 走 BlueZ）。仅作原型参考。 |
| [pyca/cryptography](https://github.com/pyca/cryptography) | AES-CCM / HKDF / HMAC。C# 对应 `System.Security.Cryptography`（`AesCcm`、`HKDF` 均为内置）。 |
| [deviceplug/btleplug](https://github.com/deviceplug/btleplug) | Rust BLE（Windows 后端 WinRT），Rust 方案首选。 |
| [tinygo-org/bluetooth](https://github.com/tinygo-org/bluetooth) | Go BLE —— **Windows 仅支持扫描**，连接/通知缺失，Go 方案不可行（已排除）。 |

### 11.4 结论

社区**已经有人把这款设备啃透了**，且给出 Python + C 两份可交叉验证的实现。
本项目不需要继续逆向，要做的是：**把协议移植到 C#/Rust + 做成托盘产品 + 补齐 Windows 稳定性**。

---

## 12. 待确认问题

1. 是否真的需要 `ble_key`（16B）？源码只用 token —— 若不需要，用户少一步。
2. 用户设备固件版本？（本项目在 `2.1.2_0073` 验证；固件升级可能改 PDU 布局）
3. 托盘工具是只读功率，还是也要控制（端口开关 / 协议开关）？
4. 是否接受"首次配置需小米账号扫码取 token"（一次性，之后本地缓存）？

---

## 13. 真机探测：属性全集（2026-11 补测）

为了确认「还能读出什么」，在 macOS 版里临时加了一段扫描（`connectOnce` 之后按标记文件触发），
对 `siid=1..8`、`piid=1..40` 逐条 GET，并打印解密后的原始帧。验完已删掉这段临时代码，只留结论。

### 13.1 siid=2 是唯一有内容的服务

| PIID | 读回示例 | 含义 |
|---|---|---|
| 1–4 | `00 07 00 00` / `01 80 00 33` | C1/C2/C3/A 端口数据（状态/档位码/电流×0.1A/电压×0.1V） |
| 5 | `04` | 充电模式（1=AI 2=数码生态 3=单口 4=均衡） |
| 6 | `03` | 息屏时间（1=5min 2=10min 3=30min 4=常亮 5=1min） |
| 7 | `07` | 协议控制（只写，读回无意义） |
| 8 | `00` | 倒计时（分钟） |
| 9–12 | `00 00` | C1/C2/C3/A 各自的倒计时 |
| 13 | `01` | 语言（0=EN 1=中文） |
| 14 | 报错 | 进入界面（只写） |
| 15 | `00` | USB-A 小电流模式 |
| 16 | `0f` | 端口开关位掩码（bit0..3 = C1/C2/C3/A） |
| 17 | `00 00 00 00` | C1/C2 协议+协商功率（每口 1 字节协议 + 1 字节瓦数） |
| 18 | `00 00 0a 01` | C3/A 协议+协商功率 |
| 19 | `01` | 空闲息屏 |
| 20 | `00` | 屏幕方向锁 |
| 21 | `0b 0b 02 03` | 协议开关位掩码（PD/PPS/UFCS/SCP） |
| 22–40 | `0b 20 .. 00 03 01 02 xx 00 5f f0` | **属性不存在**（11 字节错误响应，末两字节 `5f f0`） |

### 13.2 其它 siid 一律是空的

`siid=1`（设备信息）和 `siid=3..8` 无论 `piid` 取多少，回的都是同一个 13 字节帧：

```
0d 20 <seq> 00 03 01 <siid> <piid> 00 00 00 00 f0
```

`tl = 0x0000`、`type = 0xf0`（非法 type），既不是「值 0」也不是有效数据 —— 即**这些服务不存在**。
所以这台充电器在 BLE 上只暴露了 `siid=2` 的 21 个属性，一个不多。

### 13.3 结论：没有温度

- 协议里**没有温度属性**，也没有风扇/温度保护之类的设置项。设备屏上显示的温度/状态是固件自己
  算给自己看的，没通过 BLE 暴露；`siid=2` 扫到 21 就到底了（22 起全报「属性不存在」）。
- 想拿温度只能另想办法（拆机接 NTC / 红外测温 / 用功率和时长间接估机身发热），都不属于这个协议。
- 但**能玩的东西不少**，上面 13.1 的表里除了已做的端口开关和充电模式，还有：
  息屏时间、语言（中/英）、USB-A 小电流、空闲息屏、屏幕方向锁、四口倒计时、协议开关、
  以及 PIID 17/18 的**实时协议类型与协商功率**（PD/PPS/UFCS/SCP/QC…，PD 变化时固件会主动推送）。

### 13.4 设置面板实测（macOS 0.1.3，固件 2.1.2_0079）

上面这些可读设置已经全部做进 macOS 版的「设备设置」面板（`Sources/CuktechTray/SettingsWindow.swift`），
读写都在 `DeviceSession.readSettings / set*` 里。真机一次完整读取的原始结果：

```
息屏=3  空闲息屏=true  方向锁=false  A小电流=false  语言=1  协议位=0x03020B0B  倒计时=0/0/0/0
PIID 17 → C1 空闲  C2 空闲      PIID 18 → C3 5V 10W  A 空闲
```

`0x03020B0B` 按字节拆：C1 `0x0B` = PD|PPS（bit0/bit1，bit3 是固定为 1 的保留位）、
C2 `0x0B` = PD|PPS、C3 `0x02` = 仅 SCP、A `0x03` = UFCS|SCP。也就是说**出厂状态不是全开**：
C1/C2 的 UFCS 是关的，C3 的 UFCS 也是关的。

写入侧实测结论（每条都 SET → 回读比对过）：

| 操作 | 写入 | 回读 | 备注 |
|---|---|---|---|
| 息屏时间 3→1→3 | `1` / `3` | 相同 | 单字节，strict 校验 |
| 方向锁 0→1→0 | `1` / `0` | 相同 | 同上 |
| C2 定时断电 0→15→0 | `15` / `0` | 相同 | 读回的就是设定值，不是剩余时间，但仍按 `strict:false` 处理更稳 |
| A 口 UFCS 关→开 | `0x02020B0B` / `0x03020B0B` | 相同 | PIID 21 读-改-写，其它位置原样保留 |

一次完整读取 = 13 条 GET（6/19/20/15/13/21、9–12、17、18），全程握着命令通道；
实测约 1.5~2 秒，期间常规轮询在等锁，读完之后读数恢复正常，没有出现回读超时。
`piid=8`（全局倒计时）语义不明（文档只说 0..1440，没写单位与作用），暂时**没有**做进界面。
