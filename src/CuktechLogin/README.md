# CuktechLogin —— C# 版小米云扫码登录

给「首次使用向导」拿酷态科 10 号 Ultra 的 12 字节 BLE token。逐行移植自 `tools/token-fetch/fetch_token.py`
（741 行 Python 实现，已在本机跑通，产物见 `tools/token-fetch/out/result.json`）。

- 目标框架：`net8.0` 纯类库，零第三方依赖（RC4 / base64 / SHA1 / SHA256 全部用 BCL）
- 命名空间：`CuktechLogin`
- 程序集：`CuktechLogin.dll`

## 文件

| 文件 | 作用 |
| --- | --- |
| `Models.cs` | `QrTicket` / `MiDevice` / `XiaomiLoginException` |
| `MiCrypto.cs` | 小米云 API 加密原语（RC4-drop1024、base64、两次签名等） |
| `XiaomiClient.cs` | 登录流程 + 云 API 调用 + 设备列表 + 会话持久化 |
| `Properties/AssemblyInfo.cs` | 对 DevCheck 暴露 internal 加密函数（用于对拍） |
| `DevCheck/` | 独立控制台自检程序（不属于类库，见下） |

## 快速上手

~~~csharp
using var client = new CuktechLogin.XiaomiClient("cn");
client.Log += line => logBox.AppendText(line + Environment.NewLine);   // 向导进度框
client.LoadSession(sessionPath);                                      // 有旧会话就免扫码
if (!client.IsLoggedIn)
{
    var qr = await client.RequestQrAsync(ct);
    var png = await client.DownloadQrAsync(qr, ct);                   // 显示在向导窗口
    if (!await client.PollUntilScannedAsync(qr, ct)) return;          // 过期/超时 -> 重新申请二维码
    client.SaveSession(sessionPath);                                  // 可选: 下次免扫码
}

var devices = await client.GetDevicesAsync(ct);                       // 探测区域 + 全部家庭
var target = devices.FirstOrDefault(d => d.IsCuktech && d.TokenHex.Length == 24);
if (target is not null)
{
    var token = Convert.FromHexString(target.TokenHex);               // 12 字节, 交给 mible 认证
}
~~~

`GetDevicesAsync` 的返回值已按「酷态科且有 token → 酷态科 → 有 token → 名字」排序，向导里直接取第一台即可。
单实例只应由一个线程顺序调用（内部保存登录态）。

## 行为对照（与 Python 参考实现）

| Python（fetch_token.py） | C# | 说明 |
| --- | --- | --- |
| `request_qr()`（246-276 行） | `RequestQrAsync` / `BuildQrRequestUrl` | 参数顺序与双编码一致；`timeout` 归一到 30..300 秒 |
| `poll_once()`（292-313 行） | `PollOnceAsync` | 40s 超时 → pending；非 200 → 过期；`ssecurity`/`userId` 出现 → 成功；pending 间隔 1s、网络错误 2s |
| `finish_login()`（316-346 行） | `FinishLoginAsync` / `FollowLocationAsync` | 手工逐跳跟随 location 以拿到每一跳的 Set-Cookie；回退 yetAnotherServiceToken |
| `_rc4_drop1024 / signed_nonce / generate_enc_params`（82-158 行） | `MiCrypto` | 逐行等价，已用固定向量与 Python 对拍（见"验证状态"） |
| `api_call()`（349-395 行） | `CallApiAsync` | header/cookie 全集照抄，含拼写错误的头 `x-xiaomi-protocal-flag-cli` |
| `pick_region()`（415-430 行） | `PickRegionAsync` | cn → de → us → ru → tw → sg → in → i2，判定 `code == 0` |
| `collect_devices()`（433-463 行） | `CollectDevicesAsync` | gethome → homelist → home_device_list（device_info 或 list），按 did 去重 |
| `blt_get_beaconkey`（466-479 行） | `FetchBeaconKeyTokenAsync` | 只认 12 字节字段；beaconkey 本身 24 字节，仅广播解密用，**不是**认证 token |
| `key_to_hex()`（168-182 行） | `MiCrypto.KeyToHex` | 24 位 hex 直通，否则按 base64 解码再转 hex；只接受 24 位十六进制 |
| `save_session / load_session` | `SaveSession` / `LoadSession` | JSON 字段同名：`user_id, ssecurity, service_token, server, saved_at` |

超时一览：申请二维码 30s、下载图片 30s、单次长轮询 40s、云 API 40s、location 跟随 30s（最多 10 跳）。
所有网络失败都转成 `LastError` + `Log`，`PollUntilScannedAsync` / `GetDevicesAsync` 不抛网络异常；
只有 `RequestQrAsync` / `DownloadQrAsync` 抛 `XiaomiLoginException`（向导需要区分"申请失败"），调用方 catch 后展示即可。

## 验证状态

| 项 | 结果 |
| --- | --- |
| 类库编译 | ✅ 0 warning / 0 error（net8.0, Release） |
| 申请二维码 + 下载 PNG（DevCheck 默认模式） | ✅ 线上实测：有效期 300s，`out/qr.png` 14991 字节，480×480，PNG 魔数正确 |
| 加密原语与 Python 对拍（`--crypto`） | ✅ rc4 / signed_nonce / enc_params（data, rc4_hash__, signature）/ encrypt / decrypt / key_to_hex 全部逐字节一致 |
| 轮询失败路径（`--poll-negative`） | ✅ 空 lp、HTTP 404、连接失败 + Ttl 超时 均按预期返回 false，未抛未捕获异常 |
| **真机扫码 → serviceToken → 区域探测 → 设备 token** | ⚠️ **未经真机验证**（需要真人用米家 App 扫码，本机无法自助完成） |

未验证部分对应的代码路径：`PollOnceAsync` 的 scanned 分支、`FinishLoginAsync` / `FollowLocationAsync`
（从 Set-Cookie 取 serviceToken）、`CallApiAsync`（真实密文响应解密）、`PickRegionAsync`、
`CollectDevicesAsync`、`FetchBeaconKeyTokenAsync`。

**怎么验**（有手机时，一次搞定）：

~~~powershell
dotnet run --project src\CuktechLogin\DevCheck\DevCheck.csproj -c Release -- --scan
~~~

它会申请二维码 → 存 `out\qr.png` → 等你用米家 App 扫 → 换 serviceToken → 存 `out\session.json`
→ 探测区域 → 打印每台设备与 token 有无。登录过一次后再跑同一命令会直接复用 session（免扫码）。
酷态科 10 号 Ultra 的预期结果见 `tools/token-fetch/out/result.json`：
`njcuk.fitting.ad1204` / `CUKTECH 10 GaN Charger Ultra` / mac `AA:BB:CC:DD:EE:FF` / token 为 24 位 hex。

## 安全注意

- `session.json` 里的 `service_token` 等价于小米账号云接口凭证，**属于敏感文件**：向导保存时不要打印、不要上传、不要随日志发送。
- `MiDevice.TokenHex` 是设备认证密钥，同样不要写日志（本类库的 `Log` 只报"有/无"与来源）。
- 二维码 `qr` / `lp` URL 是当次登录票据，一次性，过期即失效。

## 与 Python 版的有意差异

1. location 用手工逐跳跟随（`AllowAutoRedirect = false`），因为 .NET 的自动重定向不暴露中间跳的 Set-Cookie，而 Python 的 session 会累积。
2. 日志不打印 token / ssecurity / serviceToken 明文（Python 版会打印 token）；排查时看 `MiDevice.TokenSource` 与长度。
3. 额外公开诊断成员：`LastError`、`LastQrResponseFields`、`BuildQrRequestUrl()`、`Server`、`DeviceId`、`MiDevice.TokenSource`（原 API 全部保留）。
4. `PollUntilScannedAsync` 在拿到 serviceToken 失败时返回 false 并写 `LastError`（Python 版抛异常终止流程），便于向导重试。
