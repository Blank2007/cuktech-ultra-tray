namespace CuktechLogin;

/// <summary>
/// 一个扫码登录二维码（一次性）。`Ttl` 到期或 <see cref="XiaomiClient.PollUntilScannedAsync"/>
/// 返回 false 后，需要重新调用 <see cref="XiaomiClient.RequestQrAsync"/> 申请新码。
/// </summary>
public sealed class QrTicket
{
    /// <summary>二维码 PNG 的 URL（给用户扫的图）。</summary>
    public string QrImageUrl { get; init; } = "";

    /// <summary>长轮询 URL（对应小米接口的 lp 字段）。</summary>
    public string PollUrl { get; init; } = "";

    /// <summary>手机上点确认后跳转用的登录 URL（对应 loginUrl 字段），仅诊断/兜底用。</summary>
    public string LoginUrl { get; init; } = "";

    /// <summary>二维码有效期（服务端 timeout 字段，已按 30..300 秒夹取）。</summary>
    public TimeSpan Ttl { get; init; }

    /// <summary>不打日志泄露 token：只报有效期与 lp 长度。</summary>
    public override string ToString()
        => $"QrTicket(ttl={Ttl.TotalSeconds:F0}s, qr={QrImageUrl.Length} chars, lp={PollUrl.Length} chars)";
}

/// <summary>米家账号下的一台设备（只保留向导需要的字段）。</summary>
public sealed class MiDevice
{
    /// <summary>设备 did，例如 <c>blt.3.1o6nkhu784003</c>。</summary>
    public string Did { get; init; } = "";

    /// <summary>设备名，例如 <c>CUKTECH 10 GaN Charger Ultra</c>。</summary>
    public string Name { get; init; } = "";

    /// <summary>设备 model，例如 <c>njcuk.fitting.ad1204</c>。</summary>
    public string Model { get; init; } = "";

    /// <summary>蓝牙 MAC，形如 <c>AA:BB:CC:DD:EE:FF</c>，可能为空。</summary>
    public string Mac { get; init; } = "";

    /// <summary>
    /// 12 字节 BLE token 的 24 位小写 hex（mible 连接认证用），可能为空。
    /// 注意：这是米家设备列表的 <c>token</c> 字段；<c>blt_get_beaconkey</c> 返回的
    /// 24 字节 beaconkey 不是它（仅 MiBeacon 广播解密用）。
    /// </summary>
    public string TokenHex { get; init; } = "";

    /// <summary>疑似酷态科设备：model/name 含 cuktech/酷态科，或 pid == 26126（0x660E 10 号 Ultra）。</summary>
    public bool IsCuktech { get; init; }

    /// <summary>米家上的在线状态（isOnline 字段）。</summary>
    public bool IsOnline { get; init; }

    /// <summary>token 来源：<c>device_list_token</c> / <c>blt_get_beaconkey</c> / 空。</summary>
    public string TokenSource { get; init; } = "";

    /// <summary>不打日志泄露 token：只报有无。</summary>
    public override string ToString()
        => $"MiDevice({Name} | {Model} | did={Did} | token={(TokenHex.Length == 24 ? "有" : "无")} | cuktech={IsCuktech} | online={IsOnline})";
}

/// <summary>登录/网络层的可预期失败（申请二维码、下载二维码等）。调用方捕获后展示给用户即可。</summary>
public sealed class XiaomiLoginException : Exception
{
    public XiaomiLoginException(string message) : base(message)
    {
    }

    public XiaomiLoginException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
