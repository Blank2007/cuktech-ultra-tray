using System.Security.Cryptography;
using System.Text;

namespace CuktechMible;

public sealed class MibleAuthException : Exception
{
    public MibleAuthException(string message) : base(message) { }
}

/// <summary>
/// 小米 MIoT BLE (mible) 会话：认证握手 + AES-128-CCM 加密帧 + MiOT TLV 命令。
/// 直接移植自 kairui1108/cuktech-ble-ha 的 controller.py（MIT），并做了工程化整理。
/// 本类不依赖任何 BLE 库，只通过 <see cref="IMibleLink"/> 收发字节。
/// </summary>
public sealed class MibleSession
{
    // ---- 协议常量（来自 BLE 抓包/参考实现）----
    private static readonly byte[] CmdLogin = { 0x24, 0x00, 0x00, 0x00 };
    private static readonly byte[] CmdSendKey = { 0x00, 0x00, 0x00, 0x0B, 0x01, 0x00 };
    private static readonly byte[] CmdSendInfo = { 0x00, 0x00, 0x00, 0x0A, 0x01, 0x00 };
    private static readonly byte[] RcvRdy = { 0x00, 0x00, 0x01, 0x01 };
    private static readonly byte[] RcvOk = { 0x00, 0x00, 0x01, 0x00 };
    private static readonly byte[] KeepAlive = { 0x00, 0x00, 0x03, 0x00 };
    private const int MaxFrames = 100;
    private static readonly byte[] HkdfInfo = Encoding.ASCII.GetBytes("mible-login-info");

    private readonly IMibleLink _link;
    private readonly byte[] _token;
    private readonly Dictionary<MibleChannel, NotifyQueue> _queues = new();

    private byte[] _devKey = Array.Empty<byte>();
    private byte[] _appKey = Array.Empty<byte>();
    private byte[] _devIv = Array.Empty<byte>();
    private byte[] _appIv = Array.Empty<byte>();

    private uint _sendIt;
    private int _devItHi;
    private ushort _lastDevItLo;
    private byte _miotSeq = 1;

    public bool Authenticated { get; private set; }
    /// <summary>分阶段耗时/诊断输出（探针用，生产可为 null）。</summary>
    public Action<string>? Trace { get; set; }
    public IReadOnlyList<byte> AppKey => _appKey;
    public IReadOnlyList<byte> DevIv => _devIv;
    public event Action<MiotMessage>? PushReceived;

    public MibleSession(IMibleLink link, byte[] token12)
    {
        if (token12 is null || token12.Length != 12)
            throw new ArgumentException("token 必须是 12 字节", nameof(token12));
        _link = link;
        _token = token12;
        foreach (MibleChannel ch in Enum.GetValues<MibleChannel>())
            _queues[ch] = new NotifyQueue();
    }

    /// <summary>传输层收到通知后必须调用本方法。</summary>
    public void OnNotification(MibleChannel channel, byte[] data)
    {
        if (_queues.TryGetValue(channel, out var q)) q.Push(data);
    }

    // ===================== 认证 =====================

    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        Authenticated = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void T(string s) => Trace?.Invoke($"[{sw.ElapsedMilliseconds,5}ms] {s}");

        // 订阅顺序必须与参考实现一致：先 cmd_recv / cmd_send / dev_info，再 auth_data；
        // auth_ctrl 要等密钥交换之后再订阅。
        // 少了 cmd_recv / cmd_send 的订阅，认证后的命令通道收不到 RCV_RDY，所有 GET 都会超时。
        await _link.SubscribeAsync(MibleChannel.CmdRecv, ct);
        await _link.SubscribeAsync(MibleChannel.CmdSend, ct);
        await _link.SubscribeAsync(MibleChannel.DeviceInfo, ct);
        await _link.SubscribeAsync(MibleChannel.AuthData, ct);
        _queues[MibleChannel.AuthData].Clear();

        // ---- Phase A：设备初始化 (0xA4) ----
        T("订阅完成，发 0xA4");
        await _link.WriteAsync(MibleChannel.AuthCtrl, new byte[] { 0xA4 }, ct);
        var initResp = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct)
                       ?? throw new MibleAuthException("未收到初始化响应 (0xA4)");

        // 协议协商回传：byte[2] += 1 (04 -> 05)
        var ack = (byte[])initResp.Clone();
        if (ack.Length >= 3) ack[2] = (byte)(ack[2] + 1);
        await _link.WriteAsync(MibleChannel.AuthData, ack, ct);

        // 接收设备密钥交换数据（本设备发 240 字节，含 0xF2 占位）
        var keyData = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(5), ct);
        if (keyData is null) throw new MibleAuthException("未收到密钥交换数据");

        // 若设备状态机不同步，会先回一个过短的重放帧；最多再等 12 秒
        bool desync = keyData.Length < 20 && keyData.Length >= 3 && keyData[2] == 0x04;
        if (desync)
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                var extra = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct);
                if (extra is null) continue;
                if (extra.Length >= 20 && extra[2] == 0x04) { keyData = extra; desync = false; break; }
            }
            if (desync) throw new MibleAuthException("设备状态机不同步（重复 init 响应），放弃本次认证");
        }

        T("密钥交换完成");
        // 回传相同长度的 0xF2 占位
        int padLen = Math.Max(0, keyData.Length - 4);
        var placeholder = new byte[4 + padLen];
        placeholder[0] = 0x00; placeholder[1] = 0x00; placeholder[2] = 0x05; placeholder[3] = 0x01;
        for (int i = 0; i < padLen; i++) placeholder[4 + i] = 0xF2;
        await _link.WriteAsync(MibleChannel.AuthData, placeholder, ct);

        await Task.Delay(300, ct); _queues[MibleChannel.AuthData].Clear();
        await Task.Delay(300, ct); _queues[MibleChannel.AuthData].Clear();

        // ---- Phase B：登录 (CMD_LOGIN=0x24) ----
        await _link.SubscribeAsync(MibleChannel.AuthCtrl, ct);
        await Task.Delay(50, ct);
        T("占位回写完成，发 CMD_LOGIN");
        await _link.WriteAsync(MibleChannel.AuthCtrl, CmdLogin, ct);

        var randKey = RandomNumberGenerator.GetBytes(16);
        await _link.WriteAsync(MibleChannel.AuthData, CmdSendKey, ct);

        bool ready = false;
        for (int i = 0; i < 5 && !ready; i++)
        {
            var d = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct);
            if (d is not null && Seq(d, RcvRdy)) ready = true;
        }
        if (!ready) throw new MibleAuthException("未收到 RCV_RDY（发送随机密钥前）");

        await _link.WriteAsync(MibleChannel.AuthData, Concat(new byte[] { 0x01, 0x00 }, randKey), ct);
        var okFrame = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct);
        if (okFrame is null || !Seq(okFrame, RcvOk))
            throw new MibleAuthException("未收到 RCV_OK（发送随机密钥后）");

        T("随机密钥已发，收设备随机数");
        var devRandom = await RecvAuthResponseAsync(ct) ?? throw new MibleAuthException("设备随机数接收失败");
        if (devRandom.Length < 16) throw new MibleAuthException($"设备随机数长度异常: {devRandom.Length}");
        devRandom = devRandom[..16];

        var devHmacInfo = await RecvAuthResponseAsync(ct) ?? throw new MibleAuthException("设备 HMAC 接收失败");
        if (devHmacInfo.Length < 32) throw new MibleAuthException($"设备 HMAC 长度异常: {devHmacInfo.Length}");
        devHmacInfo = devHmacInfo[..32];

        var salt = Concat(randKey, devRandom);      // app_random || dev_random
        var saltInv = Concat(devRandom, randKey);   // dev_random || app_random

        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, _token, 64, salt, HkdfInfo);
        _devKey = derived[..16];
        _appKey = derived[16..32];
        _devIv = derived[32..36];
        _appIv = derived[36..40];

        var expectedDevHmac = HMACSHA256.HashData(_devKey, saltInv);
        if (!CryptographicOperations.FixedTimeEquals(expectedDevHmac, devHmacInfo))
            throw new MibleAuthException("设备 HMAC 校验失败（token 可能不对）");

        T($"密钥派生+设备 HMAC 校验通过，dev_key={Convert.ToHexString(_devKey).ToLowerInvariant()}");
        var ourHmac = HMACSHA256.HashData(_appKey, salt);

        await _link.WriteAsync(MibleChannel.AuthData, CmdSendInfo, ct);
        var rdy2 = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct);
        if (rdy2 is null || !Seq(rdy2, RcvRdy)) throw new MibleAuthException("未收到 RCV_RDY（发送 HMAC 前）");

        await _link.WriteAsync(MibleChannel.AuthData, Concat(new byte[] { 0x01, 0x00 }, ourHmac), ct);
        await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct); // RCV_OK，忽略

        T("我方 HMAC 已发");
        // ---- 第二轮：挑战-应答 ----
        // 本机实测（固件 2.1.2_0073）设备并不发起第二轮挑战，等 1.2s 没帧就跳过，
        // 否则白烧 8 秒——认证耗时从 12s 降到 ~1.5s 的关键。
        byte[]? challenge = null, response = null;
        byte[]? d0 = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromMilliseconds(1200), ct);
        var deadline2 = DateTime.UtcNow.AddSeconds(8);
        while (d0 is not null && DateTime.UtcNow < deadline2)
        {
            var d = d0;
            d0 = null;
            if (d.Length >= 3 && d[2] == 0x0D)
            {
                challenge = d;
                await _link.WriteAsync(MibleChannel.AuthData, KeepAlive, ct);
            }
            else if (d.Length >= 3 && d[2] == 0x0C)
            {
                response = d;
                await _link.WriteAsync(MibleChannel.AuthData, KeepAlive, ct);
                await _link.WriteAsync(MibleChannel.AuthData, CmdSendInfo, ct);
            }
            else if (Seq(d, RcvRdy) || Seq(d, RcvOk)) break;
            d0 = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct);
        }
        _ = challenge;
        if (response is not null)
        {
            var second = Concat(new byte[] { 0x01, 0x00, 0x0C }, response[3..]);
            await _link.WriteAsync(MibleChannel.AuthData, second, ct);
            await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct);
        }

        T($"挑战应答结束 challenge={challenge?.Length} response={response?.Length}");
        // ---- 等待认证结果（走 auth_ctrl）----
        var result = await ReadAsync(MibleChannel.AuthCtrl, TimeSpan.FromSeconds(5), ct)
                     ?? throw new MibleAuthException("未收到认证结果");
        switch (result[0])
        {
            case 0x21:
            case 0x11:
                T($"认证结果 0x{result[0]:x2}，开始建立命令通道");
                Authenticated = true;
                await SetupCommandChannelAsync(ct);
                T("命令通道就绪");
                return;
            case 0x23: throw new MibleAuthException("登录失败 (0x23)");
            case 0x12: throw new MibleAuthException("激活失败 (0x12)");
            default: throw new MibleAuthException($"未知认证结果: 0x{result[0]:x2}");
        }
    }

    /// <summary>认证响应：内联(0x02) 或 多帧(0x00)，两者都自动 ACK。</summary>
    private async Task<byte[]?> RecvAuthResponseAsync(CancellationToken ct)
    {
        var data = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct);
        if (data is null || data.Length < 4) return null;

        if (data[2] == 0x02)
        {
            await _link.WriteAsync(MibleChannel.AuthData, KeepAlive, ct);
            return data[4..];
        }
        if (data[2] == 0x00 && data.Length >= 6)
        {
            int frameCount = Math.Min(data[4] + (data[5] << 8), MaxFrames);
            await _link.WriteAsync(MibleChannel.AuthData, RcvRdy, ct);
            using var ms = new MemoryStream();
            for (int i = 0; i < frameCount; i++)
            {
                var frame = await ReadAsync(MibleChannel.AuthData, TimeSpan.FromSeconds(3), ct);
                if (frame is null) break;
                ms.Write(frame, 2, frame.Length - 2);
            }
            await _link.WriteAsync(MibleChannel.AuthData, RcvOk, ct);
            return ms.ToArray();
        }
        return null;
    }

    // ===================== 命令通道 =====================

    private async Task SetupCommandChannelAsync(CancellationToken ct)
    {
        _sendIt = 0;
        _devItHi = 0;
        _lastDevItLo = 0;
        _miotSeq = 1;

        await Task.Delay(500, ct);
        await DrainPendingPushesAsync(ct);
        await DrainDevicePushAsync(ct);
    }

    /// <summary>认证成功后设备会自动推一批数据，必须排空，否则后续命令会读到陈旧响应。</summary>
    private async Task DrainDevicePushAsync(CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        int count = 0;
        while (count < 60 && (DateTime.UtcNow - start).TotalSeconds < 6.0)
        {
            var data = await ReadAsync(MibleChannel.CmdRecv, TimeSpan.FromMilliseconds(800), ct);
            if (data is null) break;
            count++;
            if (data.Length >= 6 && data[2] == 0x00)
            {
                int frameCount = Math.Min(data[4] + (data[5] << 8), MaxFrames);
                await _link.WriteAsync(MibleChannel.CmdRecv, RcvRdy, ct);
                for (int i = 0; i < frameCount; i++)
                {
                    if (await ReadAsync(MibleChannel.CmdRecv, TimeSpan.FromSeconds(3), ct) is null) break;
                }
                await _link.WriteAsync(MibleChannel.CmdRecv, RcvOk, ct);
            }
            else if (data.Length >= 4 && data[2] == 0x02)
            {
                await _link.WriteAsync(MibleChannel.CmdRecv, KeepAlive, ct);
                var pt = Decrypt(data[4..]);
                if (pt is not null && MiotCodec.Parse(pt) is { } msg) PushReceived?.Invoke(msg);
            }
        }
    }

    private async Task<int> DrainPendingPushesAsync(CancellationToken ct)
    {
        int drained = 0;
        while (true)
        {
            var data = await ReadAsync(MibleChannel.CmdRecv, TimeSpan.FromMilliseconds(1), ct);
            if (data is null) break;
            if (data.Length >= 4 && data[2] == 0x02)
            {
                await _link.WriteAsync(MibleChannel.CmdRecv, KeepAlive, ct);
                var pt = Decrypt(data[4..]);
                if (pt is not null && MiotCodec.Parse(pt) is { } msg) PushReceived?.Invoke(msg);
                drained++;
            }
            else if (data.Length >= 6 && data[2] == 0x00)
            {
                int frameCount = Math.Min(data[4] + (data[5] << 8), MaxFrames);
                await _link.WriteAsync(MibleChannel.CmdRecv, RcvRdy, ct);
                for (int i = 0; i < frameCount; i++)
                {
                    if (await ReadAsync(MibleChannel.CmdRecv, TimeSpan.FromSeconds(2), ct) is null) break;
                }
                await _link.WriteAsync(MibleChannel.CmdRecv, RcvOk, ct);
                drained++;
            }
        }
        return drained;
    }

    /// <summary>发送一条 MiOT 命令。value=null 表示 GET。</summary>
    public async Task<MiotMessage?> SendCommandAsync(byte siid, ushort piid, uint? value = null, double timeoutSec = 8.0, CancellationToken ct = default)
    {
        if (!Authenticated) throw new InvalidOperationException("尚未认证");

        await DrainPendingPushesAsync(ct);

        byte seq = _miotSeq;
        _miotSeq = (byte)((_miotSeq + 1) & 0xFF);

        var plaintext = MiotCodec.BuildCommand(seq, siid, piid, value);
        if (!await SendEncryptedAsync(plaintext, ct))
            return null;

        return await ReceiveResponseAsync(siid, piid, value is not null, timeoutSec, ct);
    }

    private async Task<MiotMessage?> ReceiveResponseAsync(byte siid, ushort piid, bool isSet, double timeoutSec, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        bool gotAck = false;

        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var data = await ReadAsync(MibleChannel.CmdRecv, remaining < TimeSpan.FromSeconds(3) ? remaining : TimeSpan.FromSeconds(3), ct);
            if (data is null) { if (gotAck) break; continue; }

            var msg = await DecodeInlineAsync(data, ct);
            if (msg is null) continue;

            if (msg.Siid == siid && msg.Piid == (byte)(piid & 0xFF))
            {
                if (msg.IsGetResult) return msg;
                if (msg.IsSetAck)
                {
                    if (isSet) { gotAck = true; deadline = DateTime.UtcNow.AddSeconds(1); continue; }
                    return msg;
                }
            }
            if (msg.IsPush) PushReceived?.Invoke(msg);
        }
        return gotAck ? new MiotMessage
        {
            Raw = Array.Empty<byte>(), Opcode = 0x01, Seq = 0, Siid = siid, Piid = (byte)(piid & 0xFF),
            ValueBytes = Array.Empty<byte>(),
        } : null;
    }

    /// <summary>解密一条 cmd_recv 通知：内联单帧(0x02) 或多帧(0x00)。</summary>
    private async Task<MiotMessage?> DecodeInlineAsync(byte[] data, CancellationToken ct)
    {
        if (data.Length >= 4 && data[2] == 0x02)
        {
            await _link.WriteAsync(MibleChannel.CmdRecv, KeepAlive, ct);
            var pt = Decrypt(data[4..]);
            return pt is null ? null : MiotCodec.Parse(pt);
        }
        if (data.Length >= 6 && data[2] == 0x00)
        {
            int frameCount = Math.Min(data[4] + (data[5] << 8), MaxFrames);
            await _link.WriteAsync(MibleChannel.CmdRecv, RcvRdy, ct);
            using var ms = new MemoryStream();
            for (int i = 0; i < frameCount; i++)
            {
                var frame = await ReadAsync(MibleChannel.CmdRecv, TimeSpan.FromSeconds(3), ct);
                if (frame is null) break;
                ms.Write(frame, 2, frame.Length - 2);
            }
            await _link.WriteAsync(MibleChannel.CmdRecv, RcvOk, ct);
            var payload = ms.ToArray();
            if (payload.Length == 0) return null;
            var pt = Decrypt(payload);
            return pt is null ? null : MiotCodec.Parse(pt);
        }
        return null;
    }

    private async Task<bool> SendEncryptedAsync(byte[] plaintext, CancellationToken ct)
    {
        _queues[MibleChannel.CmdSend].Clear();
        var encrypted = Encrypt(plaintext);

        await _link.WriteAsync(MibleChannel.CmdSend, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 }, ct);
        var d = await ReadAsync(MibleChannel.CmdSend, TimeSpan.FromSeconds(3), ct);
        if (d is null || !Seq(d, RcvRdy)) return false;

        await _link.WriteAsync(MibleChannel.CmdSend, Concat(new byte[] { 0x01, 0x00 }, encrypted), ct);
        d = await ReadAsync(MibleChannel.CmdSend, TimeSpan.FromSeconds(3), ct);
        return d is not null && Seq(d, RcvOk);
    }

    // ===================== 密码学 =====================

    /// <summary>发送方向：app_key + app_iv，输出 it_lo it_hi || ciphertext || tag(4)</summary>
    private byte[] Encrypt(byte[] plaintext)
    {
        uint it = _sendIt;
        var itBytes = BitConverter.GetBytes(it);
        var nonce = Concat(_appIv, new byte[4], itBytes);

        var output = new byte[plaintext.Length + 4];
        using (var ccm = new AesCcm(_appKey))
        {
            ccm.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, 4));
        }
        _sendIt++;
        return Concat(itBytes[..2], output);
    }

    /// <summary>接收方向：dev_key + dev_iv。设备只发低 16 位计数器，需自行处理进位。</summary>
    private byte[]? Decrypt(byte[] data)
    {
        if (data.Length < 6) return null;

        ushort itLo = BitConverter.ToUInt16(data, 0);
        if (itLo < _lastDevItLo && _lastDevItLo - itLo > 32768) _devItHi++;
        _lastDevItLo = itLo;
        uint it = ((uint)_devItHi << 16) | itLo;

        var nonce = Concat(_devIv, new byte[4], BitConverter.GetBytes(it));
        var ct = data[2..];
        var plain = new byte[ct.Length - 4];

        try
        {
            using var ccm = new AesCcm(_devKey);
            ccm.Decrypt(nonce, ct.AsSpan(0, ct.Length - 4), ct.AsSpan(ct.Length - 4, 4), plain);
            return plain;
        }
        catch (CryptographicException)
        {
            return null; // 会话失步
        }
    }

    // ===================== 工具 =====================

    private Task<byte[]?> ReadAsync(MibleChannel ch, TimeSpan timeout, CancellationToken ct)
        => _queues[ch].ReadAsync(timeout, ct);

    private static bool Seq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int off = 0;
        foreach (var p in parts) { Array.Copy(p, 0, result, off, p.Length); off += p.Length; }
        return result;
    }
}
