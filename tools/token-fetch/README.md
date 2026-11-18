# token-fetch — 小米云扫码登录取 BLE token

从米家账号（Xiaomi Cloud）取设备的 12 字节 BLE token（beaconkey），
目标设备：酷态科 CUKTECH 10 号 Ultra 充电器（productId 0x660E = 26126）。

## 文件

| 文件 | 说明 |
| --- | --- |
| `fetch_token.py` | 单文件主程序，只依赖 Python 标准库 + requests |
| `run.ps1` | 用项目 venv 的 Python 启动主程序（`-Background` 后台模式） |
| `out/qr.png` | 扫码登录二维码（过期自动刷新覆盖本文件） |
| `out/status.txt` | 当前状态：`WAITING_SCAN` / `SCANNED` / `DONE` / `ERROR: ...` |
| `out/poll.log` | 运行日志（含每次 `QR_READY <绝对路径>`） |
| `out/result.json` | 设备清单 + token（`targets[*].token_hex` 即 24 位 hex = 12 字节） |
| `out/session.json` | 登录会话缓存（含 serviceToken，仅存本机）；下次重跑免扫码，失效会自动回退扫码 |

## 运行

Python：`D:dsh projects.toolspyvenvScriptspython.exe`（venv，已装 requests）

本机 PowerShell 执行策略禁止直接运行 .ps1，需绕过：

```powershell
cd "D:\dsh projects\cuktech-ultra-tray\tools\token-fetch"
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 -Background
```

前台模式（Ctrl+C 结束）：去掉 `-Background`。

也可以直接跑：

```powershell
$env:PYTHONIOENCODING="utf-8"
& "D:\dsh projects\.tools\pyvenv\Scripts\python.exe" -u fetch_token.py
```

## 流程

1. `GET https://account.xiaomi.com/longPolling/loginUrl`（`sid=xiaomiio`, `_qrsize=480`）申请二维码
2. 二维码落盘 `out/qr.png`，stdout 打印 `QR_READY <绝对路径>`
3. 长轮询 `lp` URL 等待扫码；二维码过期（默认 300s）自动申请新码并再次打印 `QR_READY`；总等待上限 20 分钟
4. 跟随 `location` 拿 `serviceToken`
5. `/v2/homeroom/gethome` → `/v2/home/home_device_list` 列出账号下全部设备（自动探测可用区域，默认 cn）
6. 设备列表的 `token` 字段即 12 字节 BLE token（mible 认证用）；同时调 `/v2/device/blt_get_beaconkey`
   取 24 字节 beaconkey 作参考（优先酷态科 / BLE 候选设备）
7. 写 `out/result.json`（`token_hex` / `beaconkey_hex` / 接口原始返回 `beaconkey_raw`）

## 实测结论（2026-10-25，本机首次运行）

- 账号 `user_id=2552664078`，区域 `cn`，家庭 `102001287785`，账号下 **1 台** 设备。
- `did=blt.3.1o6nkhu784003` / `model=njcuk.fitting.ad1204` / `name=CUKTECH 10 GaN Charger Ultra` / MAC `AA:BB:CC:DD:EE:FF`。
- **12 字节 BLE token = `00112233445566778899aabb`**，来源是 `home_device_list` 的 `token` 字段。
  已用该 token 跑通 mible 认证握手（设备端 HMAC-SHA256 校验通过，HKDF 派生 `app_key=29e2cc47c089863f45eb3aa7a2f136de`）。
- `blt_get_beaconkey` 返回的 `beaconkey` 是 **24 字节**（`73c7b7e7...d1ae77`），仅供 MiBeacon 广播解密，
  **不是** mible 连接认证用的 token —— 别拿它当 token。
- 有效期：设备 token 是设备绑定密钥，**不会过期**（除非设备解绑 / 重置）；`out/session.json` 里的云会话
  （serviceToken / ssecurity）会过期，失效后程序自动回退到扫码，无需手工清理。

## 实现要点

- 云 API 需 RC4-drop1024 加密参数。参考实现依赖 pycryptodome（`Crypto.Cipher.ARC4`），
  本 venv 没有该包，因此 `_rc4_drop1024()` 用纯 Python 实现（已用标准向量验证：
  key=`Key`, pt=`Plaintext` → `bbf316e8d940af0ad3`；并与朴素 drop 写法对拍一致）。
- 签名：`_generate_enc_signature`（SHA1 over `METHOD&path&k=v...&signed_nonce`）。
- `blt_get_beaconkey` 的字段值可能是 base64 或 hex，统一规范化为小写 hex；字段长度不一（实测
  `beaconkey` 为 24 字节），因此 12 字节 token 以设备列表 `token` 字段为准，接口返回原样记录到
  `beaconkey_raw` / `beaconkey_candidates` 便于核对。
- 只读：仅调用设备列表与 beaconkey 接口，不改账号任何数据，token 不外发。
