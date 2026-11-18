#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""一站式获取小米云 token（扫码登录）

流程:
  1. 向 account.xiaomi.com 申请扫码登录二维码 (sid=xiaomiio)
  2. 二维码落盘 out/qr.png, stdout 打印 "QR_READY <绝对路径>"
  3. 长轮询等待扫码 (总时长上限 20 分钟; 二维码过期自动换新码并再次打印 QR_READY)
  4. 换 serviceToken -> 拉取账号下全部设备
  5. 取每台设备的 12 字节 BLE token(米家设备列表的 token 字段)与
     blt_get_beaconkey 返回的 24 字节 beaconkey(参考信息) -> out/result.json

依赖: Python 标准库 + requests。RC4-drop1024 由本文件纯 Python 实现
(参考实现里的 pycryptodome 在此不可用, 也不安装额外包)。
"""

import base64
import hashlib
import json
import os
import random
import string
import sys
import time
import traceback
from datetime import datetime, timezone
from pathlib import Path

import requests

# ---------------------------------------------------------------- 基础设置

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

BASE = Path(__file__).resolve().parent
OUT = BASE / "out"
OUT.mkdir(parents=True, exist_ok=True)
QR_PNG = OUT / "qr.png"
RESULT_JSON = OUT / "result.json"
STATUS_TXT = OUT / "status.txt"
SESSION_JSON = OUT / "session.json"   # 敏感: 含 serviceToken, 仅本机临时复用

MAX_WAIT_SECONDS = 20 * 60          # 总体最长等待扫码时间
TARGET_PRODUCT_ID = 26126           # 0x660E CUKTECH 10 号 Ultra
CUKTECH_HINTS = ("cuktech", "酷态科")

SERVERS = {
    "cn": "https://api.io.mi.com/app",
    "de": "https://de.api.io.mi.com/app",
    "us": "https://us.api.io.mi.com/app",
    "ru": "https://ru.api.io.mi.com/app",
    "tw": "https://tw.api.io.mi.com/app",
    "sg": "https://sg.api.io.mi.com/app",
    "in": "https://in.api.io.mi.com/app",
    "i2": "https://i2.api.io.mi.com/app",
}


def log(msg):
    print("[%s] %s" % (datetime.now().strftime("%H:%M:%S"), msg), flush=True)


def set_status(value):
    """status.txt 原子写入, 避免读到半截内容。"""
    try:
        tmp = STATUS_TXT.with_suffix(".tmp")
        tmp.write_text(value.rstrip("\n") + "\n", encoding="utf-8")
        os.replace(tmp, STATUS_TXT)
    except OSError as exc:  # pragma: no cover
        log("写 status 失败: %s" % exc)


def fail(msg):
    set_status("ERROR: %s" % msg)
    log("ERROR: %s" % msg)


# ---------------------------------------------------------------- 加密工具

def _rc4_drop1024(key, data):
    """RC4 流密码 + 丢弃前 1024 字节密钥流（与 pycryptodome ARC4.new(k) 后 encrypt(1024*0) 等价）。"""
    s = list(range(256))
    j = 0
    klen = len(key)
    if klen == 0:
        raise ValueError("RC4 key 不能为空")
    for i in range(256):
        j = (j + s[i] + key[i % klen]) & 0xFF
        s[i], s[j] = s[j], s[i]
    i = j = 0
    for _ in range(1024):          # 丢弃 1024 字节
        i = (i + 1) & 0xFF
        j = (j + s[i]) & 0xFF
        s[i], s[j] = s[j], s[i]
    out = bytearray(len(data))
    for n in range(len(data)):
        i = (i + 1) & 0xFF
        j = (j + s[i]) & 0xFF
        s[i], s[j] = s[j], s[i]
        out[n] = data[n] ^ s[(s[i] + s[j]) & 0xFF]
    return bytes(out)


def _b64e(raw):
    return base64.b64encode(raw).decode()


def _b64d(text):
    text = text.strip()
    return base64.b64decode(text + "=" * (-len(text) % 4))


def generate_agent():
    agent = "Android-7.1.1-1.0.0-ONEPLUS A3010-136-" + "".join(
        random.choices(string.ascii_uppercase + string.digits, k=11))
    return agent + " MIIO/" + "".join(random.choices(string.ascii_uppercase + string.digits, k=6))


def generate_device_id():
    return "".join(random.choices(string.ascii_lowercase + string.digits, k=16))


def generate_nonce(millis):
    return _b64e(os.urandom(8) + (int(millis / 60000)).to_bytes(4, byteorder="big"))


def signed_nonce(ssecurity, nonce):
    return _b64e(hashlib.sha256(_b64d(ssecurity) + _b64d(nonce)).digest())


def encrypt_rc4(signed_nonce_b64, payload):
    return _b64e(_rc4_drop1024(_b64d(signed_nonce_b64), payload.encode("utf-8")))


def decrypt_rc4(signed_nonce_b64, payload_b64):
    return _rc4_drop1024(_b64d(signed_nonce_b64), _b64d(payload_b64)).decode("utf-8", "replace")


def generate_enc_signature(url, method, sn, params):
    parts = [str(method).upper(), url.split("com")[1].replace("/app/", "/")]
    for k, v in params.items():
        parts.append("%s=%s" % (k, v))
    parts.append(sn)
    return _b64e(hashlib.sha1("&".join(parts).encode("utf-8")).digest())


def generate_enc_params(url, method, sn, nonce, params, ssecurity):
    params["rc4_hash__"] = generate_enc_signature(url, method, sn, params)
    for k, v in params.items():
        params[k] = encrypt_rc4(sn, v)
    params.update({
        "signature": generate_enc_signature(url, method, sn, params),
        "ssecurity": ssecurity,
        "_nonce": nonce,
    })
    return params


def to_json(text):
    try:
        return json.loads(text.replace("&&&START&&&", "").strip())
    except (json.JSONDecodeError, ValueError):
        return {}


def key_to_hex(key):
    """beaconkey 可能是 base64 或 24 位 hex, 统一转成小写 hex。"""
    if not key:
        return None
    s = str(key).strip()
    if len(s) == 24 and all(c in "0123456789abcdefABCDEF" for c in s):
        return s.lower()
    try:
        raw = _b64d(s)
    except Exception:
        return None
    if not raw:
        return None
    return raw.hex()


HEX_DIGITS = set("0123456789abcdefABCDEF")


def parse_fields(inner):
    """把 blt_get_beaconkey 返回 result 里的字符串字段全部规范化为 hex。

    返回 (candidates, best)，candidates = {字段名: {hex, bytes, raw}}，
    best = (字段名, hex)。优先选 12 字节（24 位 hex）的字段，字段名偏好
    key > token > beaconkey（参考实现的两个版本分别取 key / beaconkey，
    实测该接口同时含这两个字段且长度不同，必须以 12 字节为准）。
    """
    cands = {}
    for field, val in inner.items():
        if not isinstance(val, str) or not val.strip():
            continue
        s = val.strip()
        hx = None
        if len(s) == 24 and all(c in HEX_DIGITS for c in s):
            hx = (s.lower(), 12)
        else:
            try:
                raw = _b64d(s)
            except Exception:
                raw = None
            if raw:
                hx = (raw.hex(), len(raw))
        if hx:
            cands[field] = {"hex": hx[0], "bytes": hx[1], "raw": s}
    best = None
    for prefer in ("key", "token", "ble_key", "beaconkey"):
        c = cands.get(prefer)
        if c and c["bytes"] == 12:
            best = (prefer, c["hex"])
            break
    if best is None:
        for field, c in cands.items():
            if c["bytes"] == 12:
                best = (field, c["hex"])
                break
    if best is None and cands:
        field = next(iter(cands))
        best = (field, cands[field]["hex"])
    return cands, best


# ---------------------------------------------------------------- 云 API 客户端

class XiaomiQRLogin:
    LOGIN_URL = "https://account.xiaomi.com/longPolling/loginUrl"

    def __init__(self, server="cn"):
        self.server = server
        self.agent = generate_agent()
        self.device_id = generate_device_id()
        self.session = requests.Session()
        self.session.headers.update({"User-Agent": self.agent})
        self.ssecurity = None
        self.service_token = None
        self.user_id = None
        self.location = None

    # ---- 第一步: 申请二维码 -------------------------------------------
    def request_qr(self):
        params = {
            "_qrsize": "480",
            "qs": "%3Fsid%3Dxiaomiio%26_json%3Dtrue",
            "callback": "https://sts.api.io.mi.com/sts",
            "_hasLogo": "false",
            "sid": "xiaomiio",
            "serviceParam": "",
            "_locale": "en_GB",
            "_dc": str(int(time.time() * 1000)),
        }
        resp = self.session.get(self.LOGIN_URL, params=params, timeout=30)
        if resp.status_code != 200:
            raise RuntimeError("申请二维码失败: HTTP %s" % resp.status_code)
        data = to_json(resp.text)
        if "qr" not in data or "lp" not in data:
            raise RuntimeError("申请二维码失败: 响应缺少 qr/lp: %s" % resp.text[:300])
        timeout = data.get("timeout") or 300
        try:
            timeout = float(timeout)
        except (TypeError, ValueError):
            timeout = 300.0
        if timeout > 1000:           # 毫秒 -> 秒
            timeout /= 1000.0
        timeout = max(30.0, min(timeout, 300.0))
        return {
            "qr_url": data["qr"],
            "lp": data["lp"],
            "login_url": data.get("loginUrl", ""),
            "timeout": timeout,
        }

    def save_qr_png(self, qr_url):
        resp = self.session.get(qr_url, timeout=30)
        resp.raise_for_status()
        blob = resp.content
        if not blob:
            raise RuntimeError("二维码图片为空")
        QR_PNG.write_bytes(blob)
        kind = "png" if blob[:8] == b"\x89PNG\r\n\x1a\n" else (
            "jpeg" if blob[:2] == b"\xff\xd8" else "unknown")
        log("二维码已保存 %s (%d bytes, %s)" % (QR_PNG, len(blob), kind))
        print("QR_READY %s" % QR_PNG, flush=True)
        return kind

    # ---- 第二步: 长轮询等待扫码 ---------------------------------------
    def poll_once(self, lp_url, timeout):
        """返回 ('scanned', data) / ('pending', None) / ('expired', None)。"""
        try:
            resp = self.session.get(lp_url, timeout=(10, 40))
        except requests.exceptions.Timeout:
            return ("pending", None)
        except requests.exceptions.RequestException as exc:
            log("轮询网络错误: %s" % exc)
            time.sleep(2)
            return ("pending", None)

        if resp.status_code != 200:
            log("轮询返回 HTTP %s, 视为二维码过期" % resp.status_code)
            return ("expired", None)

        data = to_json(resp.text)
        if data.get("ssecurity") or data.get("userId"):
            return ("scanned", data)
        if data.get("code"):
            log("轮询响应 code=%s (继续等待)" % data.get("code"))
        time.sleep(1.0)
        return ("pending", None)

    # ---- 第三步: 换 serviceToken --------------------------------------
    def finish_login(self, data):
        self.user_id = data.get("userId")
        self.ssecurity = data.get("ssecurity")
        self.location = data.get("location")
        if not self.ssecurity:
            raise RuntimeError("扫码结果缺少 ssecurity")
        self.service_token = None
        if self.location:
            try:
                resp = self.session.get(
                    self.location,
                    headers={"User-Agent": self.agent,
                             "Content-Type": "application/x-www-form-urlencoded"},
                    allow_redirects=True,
                    timeout=30,
                )
                log("location 跟随完成: HTTP %s" % resp.status_code)
                self.service_token = resp.cookies.get("serviceToken")
            except requests.exceptions.RequestException as exc:
                log("location 跟随失败: %s" % exc)
        else:
            log("扫码结果无 location, 直接使用已有 cookie")
        if not self.service_token:
            self.service_token = self.session.cookies.get("serviceToken")
        if not self.service_token:
            # 少数情况下 serviceToken 落在 yetAnotherServiceToken
            self.service_token = self.session.cookies.get("yetAnotherServiceToken")
        if not self.service_token:
            raise RuntimeError("无法获取 serviceToken (cookie 中不存在)")
        log("已拿到 serviceToken (userId=%s)" % self.user_id)
        return True

    # ---- 云 API 调用 ---------------------------------------------------
    def api_call(self, url, params):
        if not self.ssecurity:
            raise RuntimeError("尚未登录")
        headers = {
            "Accept-Encoding": "identity",
            "User-Agent": self.agent,
            "Content-Type": "application/x-www-form-urlencoded",
            "x-xiaomi-protocal-flag-cli": "PROTOCAL-HTTP2",
            "MIOT-ENCRYPT-ALGORITHM": "ENCRYPT-RC4",
        }
        cookies = {
            "userId": str(self.user_id),
            "yetAnotherServiceToken": str(self.service_token),
            "serviceToken": str(self.service_token),
            "locale": "en_GB",
            "timezone": "GMT+08:00",
            "is_daylight": "0",
            "dst_offset": "0",
            "channel": "MI_APP_STORE",
        }
        millis = round(time.time() * 1000)
        nonce = generate_nonce(millis)
        sn = signed_nonce(self.ssecurity, nonce)
        fields = generate_enc_params(url, "POST", sn, nonce, dict(params), self.ssecurity)
        try:
            resp = self.session.post(url, headers=headers, cookies=cookies,
                                     params=fields, timeout=40)
        except requests.exceptions.RequestException as exc:
            log("API 请求异常 %s: %s" % (url.rsplit("/", 1)[-1], exc))
            return None
        if resp.status_code != 200:
            log("API %s -> HTTP %s" % (url.rsplit("/", 1)[-1], resp.status_code))
            return None
        try:
            decoded = decrypt_rc4(signed_nonce(self.ssecurity, fields["_nonce"]), resp.text)
        except Exception as exc:
            log("API 响应解密失败 %s: %s" % (url.rsplit("/", 1)[-1], exc))
            return None
        try:
            return json.loads(decoded)
        except json.JSONDecodeError:
            log("API %s 返回非 JSON: %s" % (url.rsplit("/", 1)[-1], decoded[:200]))
            return None

    def call(self, path, params, region=None):
        region = region or self.server
        return self.api_call(SERVERS[region] + path, params)


# ---------------------------------------------------------------- 业务逻辑

def is_cuktech(dev):
    hay = " ".join(str(dev.get(k, "")) for k in ("model", "name", "did", "extra")).lower()
    if any(h in hay for h in CUKTECH_HINTS):
        return True
    for k in ("pid", "productId", "product_id"):
        if str(dev.get(k, "")) == str(TARGET_PRODUCT_ID):
            return True
    return False


def looks_ble(dev):
    hay = " ".join(str(dev.get(k, "")) for k in ("model", "name", "extra")).lower()
    return any(t in hay for t in ("blt", "ble", "mesh", "bluetooth"))


def pick_region(client):
    """登录后探测可用区域(默认 cn, 失败时回落到其它区域)。"""
    for region in ["cn"] + [r for r in SERVERS if r != "cn"]:
        try:
            res = client.call("/v2/homeroom/gethome",
                              {"data": json.dumps({"fg": True, "fetch_share": True,
                                                   "fetch_share_dev": True, "limit": 300,
                                                   "app_ver": 7})}, region=region)
        except Exception as exc:
            log("区域 %s 探测异常: %s" % (region, exc))
            continue
        if isinstance(res, dict) and res.get("code") == 0:
            log("使用区域: %s" % region)
            return region, res
        log("区域 %s 不可用: %s" % (region, str(res)[:160]))
    return None, None


def collect_devices(client, region, homes_resp):
    devices = []
    seen = set()
    homelist = (homes_resp.get("result") or {}).get("homelist", []) or []
    log("家庭数量: %d" % len(homelist))
    for home in homelist:
        home_id = home.get("id")
        owner = home.get("uid") or client.user_id
        payload = {"data": json.dumps({
            "home_owner": owner,
            "home_id": home_id,
            "limit": 200,
            "get_split_device": True,
            "support_smart_home": True,
        })}
        res = client.call("/v2/home/home_device_list", payload, region=region)
        if not isinstance(res, dict) or res.get("code") != 0:
            log("home_device_list 失败 home=%s: %s" % (home_id, str(res)[:200]))
            continue
        result = res.get("result") or {}
        dev_list = result.get("device_info") or result.get("list") or []
        log("家庭 %s (%s): %d 台设备" % (home_id, home.get("name", ""), len(dev_list)))
        for dev in dev_list:
            did = str(dev.get("did", ""))
            if did and did in seen:
                continue
            if did:
                seen.add(did)
            dev["_home_id"] = home_id
            devices.append(dev)
    return devices


def fetch_beaconkey(client, region, did):
    """返回 (raw_result, candidates, best, error)。best = (字段名, hex) 或 None。"""
    res = client.call("/v2/device/blt_get_beaconkey",
                      {"data": json.dumps({"did": did, "pdid": 1})}, region=region)
    if not isinstance(res, dict):
        return None, {}, None, "无响应"
    if res.get("code") != 0:
        return None, {}, None, "code=%s %s" % (res.get("code"), res.get("message", ""))
    inner = res.get("result") or {}
    cands, best = parse_fields(inner)
    log("blt_get_beaconkey 原始返回 [%s]: %s" % (did, json.dumps(inner, ensure_ascii=False)))
    if best is None:
        return dict(inner), cands, None, "结果无可解析的 key 字段"
    return dict(inner), cands, best, None


def device_row(dev, raw, cands, best, err):
    """组装单台设备的记录。

    12 字节 BLE token 取米家设备列表的 token 字段(实测该值即为 mible 认证
    所用 token, 握手 HMAC 校验通过); blt_get_beaconkey 返回的 24 字节
    beaconkey 单独保留, 仅供 MiBeacon 广播解密, 与连接认证无关。
    """
    listed = key_to_hex(dev.get("token")) if dev.get("token") else None
    api_best = best[1] if best else None
    token_hex = token_source = None
    if listed and len(listed) == 24:
        token_hex, token_source = listed, "device_list_token"
    elif api_best and len(api_best) == 24:
        token_hex, token_source = api_best, best[0]
    beaconkey_hex = (cands.get("beaconkey") or {}).get("hex") or (
        api_best if api_best and len(api_best) == 48 else None)
    row = {
        "did": str(dev.get("did", "")),
        "model": dev.get("model", ""),
        "name": dev.get("name", ""),
        "is_ble_candidate": bool(looks_ble(dev) or is_cuktech(dev)),
        "cuktech": bool(is_cuktech(dev)),
        "pid": dev.get("pid", ""),
        "mac": dev.get("mac", ""),
        "is_online": dev.get("isOnline", ""),
        "token_hex": token_hex,
        "token_bytes": (len(token_hex) // 2) if token_hex else None,
        "token_source": token_source,
        "device_list_token_hex": listed,
        "beaconkey_hex": beaconkey_hex,
        "beaconkey_bytes": (len(beaconkey_hex) // 2) if beaconkey_hex else None,
        "beaconkey_raw": raw,
        "beaconkey_candidates": cands,
        "beaconkey_error": err,
        "home_id": dev.get("_home_id"),
    }
    return row


def save_session(client):
    """保存登录会话, 便于下次免扫码重跑(仅存本机 out/session.json)。"""
    try:
        SESSION_JSON.write_text(json.dumps({
            "user_id": client.user_id,
            "ssecurity": client.ssecurity,
            "service_token": client.service_token,
            "server": client.server,
            "saved_at": datetime.now(timezone.utc).isoformat(),
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        log("登录会话已保存到 %s (下次可直接复用, 免扫码)" % SESSION_JSON)
    except OSError as exc:
        log("保存会话失败: %s" % exc)


def load_session(client):
    if not SESSION_JSON.exists():
        return False
    try:
        data = json.loads(SESSION_JSON.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        log("读取会话失败: %s" % exc)
        return False
    client.user_id = data.get("user_id")
    client.ssecurity = data.get("ssecurity")
    client.service_token = data.get("service_token")
    ok = bool(client.user_id and client.ssecurity and client.service_token)
    log("复用已保存会话: %s (userId=%s)" % ("是" if ok else "否", client.user_id))
    return ok


def write_result(payload):
    tmp = RESULT_JSON.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(tmp, RESULT_JSON)
    log("已写入 %s" % RESULT_JSON)


def print_table(rows):
    log("账号下共 %d 台设备:" % len(rows))
    header = "%-3s %-30s %-26s %-32s %-26s %-18s %s" % (
        "#", "did", "model", "name", "token_hex", "source", "bytes")
    print(header, flush=True)
    for idx, row in enumerate(rows, 1):
        print("%-3d %-30s %-26s %-32s %-26s %-18s %s" % (
            idx, row["did"][:30], str(row["model"])[:26], str(row["name"])[:32],
            (row["token_hex"] or "-"), (row["token_source"] or "-"),
            row["token_bytes"] if row["token_bytes"] else "-"), flush=True)


# ---------------------------------------------------------------- 主流程

def do_login_and_scan(client):
    set_status("WAITING_SCAN")
    deadline = time.time() + MAX_WAIT_SECONDS
    attempt = 0
    while time.time() < deadline:
        attempt += 1
        try:
            qr = client.request_qr()
        except Exception as exc:
            log("获取二维码异常: %s" % exc)
            time.sleep(5)
            continue
        try:
            client.save_qr_png(qr["qr_url"])
        except Exception as exc:
            log("下载二维码图片失败: %s" % exc)
            time.sleep(3)
            continue
        log("第 %d 个二维码生效中 (有效期 %.0fs), 等待扫码..." % (attempt, qr["timeout"]))
        expire_at = time.time() + qr["timeout"]
        while time.time() < min(expire_at, deadline):
            state, data = client.poll_once(qr["lp"], qr["timeout"])
            if state == "scanned":
                log("扫码成功!")
                set_status("SCANNED")
                return client.finish_login(data)
            if state == "expired":
                break
        else:
            if time.time() >= deadline:
                break
        log("二维码已过期, 重新生成...")
        set_status("WAITING_SCAN")
    raise RuntimeError("等待扫码超时 (%d 分钟)" % (MAX_WAIT_SECONDS // 60))


def run():
    log("=" * 72)
    log("小米云 token 获取工具启动 | Python %s | requests %s" % (
        sys.version.split()[0], requests.__version__))
    log("输出目录: %s" % OUT)

    client = XiaomiQRLogin(server="cn")
    region = homes = None
    if load_session(client):
        region, homes = pick_region(client)
        if region is None:
            log("已保存会话失效, 将重新扫码登录")
            try:
                SESSION_JSON.unlink()
            except OSError:
                pass
            client = XiaomiQRLogin(server="cn")

    if region is None:
        do_login_and_scan(client)
        save_session(client)
        region, homes = pick_region(client)

    if region is None:
        raise RuntimeError("所有区域 gethome 均失败, 无法列出设备")

    devices = collect_devices(client, region, homes)
    log("总计拉取到 %d 台设备" % len(devices))

    # 先处理疑似酷态科设备 + 明显 BLE 设备, 尽快产出目标 token
    prio = [d for d in devices if is_cuktech(d) or looks_ble(d)]
    rest = [d for d in devices if d not in prio]
    log("优先探测 %d 台 (酷态科/BLE 候选), 其余 %d 台随后补全" % (len(prio), len(rest)))

    rows_by_did = {}
    targets = []

    def probe(devs):
        found = 0
        for dev in devs:
            did = str(dev.get("did", ""))
            if not did:
                continue
            raw, cands, best, err = fetch_beaconkey(client, region, did)
            row = device_row(dev, raw, cands, best, err)
            rows_by_did[did] = row
            if row["token_hex"]:
                found += 1
                log("token OK: %s | %s | %s -> %s (来源=%s, %d 字节)" % (
                    row["name"], row["model"], did, row["token_hex"],
                    row["token_source"], row["token_bytes"]))
                if row["cuktech"]:
                    targets.append(row)
            else:
                log("token 无: %s | %s | %s (%s)" % (
                    row["name"], row["model"], did, err))
        return found

    probe(prio)
    if targets:
        write_result({
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "region": region,
            "user_id": client.user_id,
            "targets": targets,
            "devices": list(rows_by_did.values()),
            "note": "targets 为疑似酷态科且取到 token 的设备(阶段快照); 正在补全其余设备",
        })
        print("TARGET_READY %s" % json.dumps(
            [{"did": t["did"], "token_hex": t["token_hex"],
              "token_source": t["token_source"], "token_bytes": t["token_bytes"]}
             for t in targets], ensure_ascii=False), flush=True)
        set_status("DONE")

    probe(rest)

    rows = list(rows_by_did.values())
    # 未成功探测的设备也列出来
    for dev in devices:
        did = str(dev.get("did", ""))
        if did not in rows_by_did:
            rows.append(device_row(dev, None, {}, None, "未探测"))

    rows.sort(key=lambda r: (not r["cuktech"], not bool(r["token_hex"]), r["name"] or ""))
    print_table(rows)

    if not targets:
        targets = [r for r in rows if r["token_hex"] and r["cuktech"]]
    if not targets:
        # 全部 BLE 设备里找一遍, 至少给出可用的 key
        targets = [r for r in rows if r["token_hex"]]

    write_result({
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "region": region,
        "user_id": client.user_id,
        "target_product_id": TARGET_PRODUCT_ID,
        "targets": targets,
        "devices": rows,
        "note": ("12 字节 BLE token 看 token_hex(= device_list_token_hex, "
                 "米家设备列表的 token 字段, 实测 mible 认证握手成功); "
                 "beaconkey_hex 是 blt_get_beaconkey 返回的 24 字节 beaconkey, "
                 "仅供 MiBeacon 广播解密, 与本程序 mible 连接认证无关; "
                 "beaconkey_raw / beaconkey_candidates 为接口原始返回, 便于核对"),
    })

    if targets:
        for t in targets:
            print("TOKEN %s %s %s %s" % (
                t["did"], t["token_hex"], t["token_source"], t["name"]), flush=True)
        set_status("DONE")
        log("完成: %d 个可用 token" % len(targets))
    else:
        set_status("DONE")
        log("完成, 但未取到任何 beaconkey (设备可能不支持 BLE 或未在线)")


def main():
    try:
        run()
        return 0
    except KeyboardInterrupt:
        fail("被用户中断")
        return 130
    except Exception as exc:
        detail = "%s: %s" % (type(exc).__name__, exc)
        fail(detail)
        traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
