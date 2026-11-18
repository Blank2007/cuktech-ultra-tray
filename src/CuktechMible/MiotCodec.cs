namespace CuktechMible;

/// <summary>解析后的 MiOT/Spec 响应帧。</summary>
public sealed class MiotMessage
{
    public required byte[] Raw { get; init; }
    /// <summary>pt[4]: 0x01=SET ACK, 0x03=GET 结果, 0x04=主动推送</summary>
    public required byte Opcode { get; init; }
    public required byte Seq { get; init; }
    public required byte Siid { get; init; }
    public required byte Piid { get; init; }
    public int ErrorCode { get; init; }
    public int ValueLength { get; init; }
    public byte TypeId { get; init; }
    public required byte[] ValueBytes { get; init; }

    public bool IsPush => Opcode == 0x04;
    public bool IsSetAck => Opcode == 0x01;
    public bool IsGetResult => Opcode == 0x03;

    /// <summary>len >= 4 时按 LE u32 取值，否则取单字节。</summary>
    public uint? UIntValue => ValueBytes.Length switch
    {
        >= 4 => BitConverter.ToUInt32(ValueBytes, 0),
        1 => ValueBytes[0],
        2 => BitConverter.ToUInt16(ValueBytes, 0),
        0 => null,
        _ => ValueBytes[^1],
    };
}

/// <summary>
/// MiOT TLV 编解码。帧格式（对齐米家 SpecWriteChannelManager）：
///   [total_len][0x20][seq][0x00][opcode][0x01][siid][piid_lo][piid_hi][tl_lo][tl_hi][value...]
///   total_len = 11 + value.Length，tl = (type_id &lt;&lt; 12) | value.Length
///   type_id: 1=UINT8, 3=UINT16, 5=UINT32
/// </summary>
public static class MiotCodec
{
    public const byte OpSet = 0x00;
    public const byte OpGet = 0x02;

    public static byte[] BuildCommand(byte seq, byte siid, ushort piid, uint? value)
    {
        byte opcode;
        byte typeId;
        byte[] valueBytes;

        if (value is uint v)
        {
            opcode = OpSet;
            if (v <= 0xFF) { typeId = 1; valueBytes = new[] { (byte)(v & 0xFF) }; }
            else { typeId = 5; valueBytes = BitConverter.GetBytes(v); }
        }
        else
        {
            opcode = OpGet;
            typeId = 1;
            valueBytes = new byte[] { 0x00 };
        }

        int byteLen = valueBytes.Length;
        int tl = (typeId << 12) | byteLen;
        int totalLen = 11 + byteLen;

        var frame = new byte[11 + byteLen];
        frame[0] = (byte)totalLen;
        frame[1] = 0x20;
        frame[2] = seq;
        frame[3] = 0x00;
        frame[4] = opcode;
        frame[5] = 0x01;
        frame[6] = siid;
        frame[7] = (byte)(piid & 0xFF);
        frame[8] = (byte)((piid >> 8) & 0xFF);
        frame[9] = (byte)(tl & 0xFF);
        frame[10] = (byte)((tl >> 8) & 0xFF);
        Array.Copy(valueBytes, 0, frame, 11, byteLen);
        return frame;
    }

    public static MiotMessage? Parse(byte[] pt)
    {
        if (pt.Length < 8) return null;
        int valueLen = pt.Length > 11 ? pt[11] : 1;
        int valueStart = 13;
        int available = Math.Max(0, pt.Length - valueStart);
        int len = Math.Min(valueLen, available);
        var val = new byte[len];
        if (len > 0) Array.Copy(pt, valueStart, val, 0, len);

        return new MiotMessage
        {
            Raw = pt,
            Opcode = pt[4],
            Seq = pt[2],
            Siid = pt[6],
            Piid = pt[7],
            ErrorCode = (pt.Length > 10) ? (pt[9] << 8 | pt[10]) : 0,
            ValueLength = valueLen,
            TypeId = pt.Length > 12 ? pt[12] : (byte)0,
            ValueBytes = val,
        };
    }
}

/// <summary>
/// 端口数据（SIID=2, PIID 1..4 = C1/C2/C3/A）四字节布局：
///   [0]=状态 0x00空闲/0x01单口/0x11 C3+A合并, [1]=code(PDO 档位索引/硬件状态码),
///   [2]=电流 ×0.1A, [3]=电压 ×0.1V
/// 例："01 0A 19 C9" → 2.5A / 20.1V / 50.2W
/// </summary>
public readonly struct PortData
{
    public byte State { get; init; }
    public byte Code { get; init; }
    public double Current { get; init; }   // A
    public double Voltage { get; init; }   // V
    public double Power => Math.Round(Voltage * Current, 1);
    public bool IsIdle => State == 0x00;

    public static PortData? TryParse(byte[]? valueBytes)
    {
        if (valueBytes is null || valueBytes.Length < 4) return null;
        var b = valueBytes;
        return new PortData
        {
            State = b[0],
            Code = b[1],
            Current = b[2] / 10.0,
            Voltage = b[3] / 10.0,
        };
    }

    public override string ToString() =>
        IsIdle ? $"空闲 (state=0x{State:x2})" : $"{Voltage:F1}V {Current:F1}A {Power:F1}W (state=0x{State:x2} code=0x{Code:x2})";
}
