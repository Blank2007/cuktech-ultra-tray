using System.Threading.Channels;

namespace CuktechMible;

/// <summary>MIoT BLE 的五条 GATT 通道。对应服务 0xFE95 下的特征。</summary>
public enum MibleChannel
{
    DeviceInfo, // char 0x001c, handle 0x001f
    AuthCtrl,   // char 0x0010, handle 0x000d
    AuthData,   // char 0x0019, handle 0x0010
    CmdSend,    // char 0x001a, handle 0x0019
    CmdRecv,    // char 0x001b, handle 0x001c
}

/// <summary>
/// 传输层抽象：把 WinRT / btleplug / 测试桩隔离开。
/// 只做「写」和「订阅」，通知由实现方推入 <see cref="MibleSession.OnNotification"/>。
/// </summary>
public interface IMibleLink
{
    Task WriteAsync(MibleChannel channel, byte[] data, CancellationToken ct = default);
    Task SubscribeAsync(MibleChannel channel, CancellationToken ct = default);
    Task UnsubscribeAsync(MibleChannel channel, CancellationToken ct = default);
}

/// <summary>每个通道一个无界通知队列，带超时的异步读取。</summary>
internal sealed class NotifyQueue
{
    private readonly Channel<byte[]> _ch = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    public void Push(byte[] data) => _ch.Writer.TryWrite(data);

    public void Clear()
    {
        while (_ch.Reader.TryRead(out _)) { }
    }

    public async Task<byte[]?> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _ch.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
