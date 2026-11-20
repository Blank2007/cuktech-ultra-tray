using System.Collections.Concurrent;
using CuktechMible;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace ProbeCs;

/// <summary>把 WinRT GATT 包成 MibleSession 需要的字节通道。</summary>
public sealed class WinRtMibleLink : IMibleLink, IAsyncDisposable
{
    public static readonly Guid MiService = new("0000fe95-0000-1000-8000-00805f9b34fb");

    private static readonly Dictionary<MibleChannel, ushort> ShortUuids = new()
    {
        [MibleChannel.DeviceInfo] = 0x001C,
        [MibleChannel.AuthCtrl]   = 0x0010,
        [MibleChannel.AuthData]   = 0x0019,
        [MibleChannel.CmdSend]    = 0x001A,
        [MibleChannel.CmdRecv]    = 0x001B,
    };

    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly Dictionary<MibleChannel, GattCharacteristic> _chars = new();
    private readonly Dictionary<ushort, GattCharacteristic> _all = new();
    private readonly ConcurrentDictionary<MibleChannel, TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>> _handlers = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public event Action<MibleChannel, byte[]>? BytesReceived;
    public BluetoothLEDevice Device => _device;

    /// <summary>诊断输出。控制台版接到 Console，托盘版接到日志文件。</summary>
    public static Action<string>? Diag;

    /// <summary>系统层面掉线（设备关机 / 走远 / 主动踢开）时触发。</summary>
    public event Action? Disconnected;

    private static void Log(string s) => Diag?.Invoke(s);

    private WinRtMibleLink(BluetoothLEDevice device, GattDeviceService service)
    {
        _device = device;
        _service = service;
    }

    /// <summary>
    /// Windows 的 BLE 设备缓存会过期（重启蓝牙、隔天、设备断电一段时间、换适配器之后都会清空）。
    /// 缓存空的时候 FromBluetoothAddressAsync 一律返回 null，跟设备在不在旁边无关——
    /// 这时候必须先用广播扫描把设备“扫”进缓存，再取设备对象。
    /// </summary>
    private static async Task<bool> ScanUntilSeenAsync(ulong address, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            if (e.BluetoothAddress == address)
                tcs.TrySetResult(true);
        };
        watcher.Stopped += (_, _) => tcs.TrySetResult(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var reg = timeout.Token.Register(() => tcs.TrySetResult(false));

        Log("[link] 系统缓存里没有这台设备，开始扫广播（最多 8 秒）…");
        watcher.Start();
        try
        {
            bool seen = await tcs.Task.ConfigureAwait(false);
            Log(seen ? "[link] 扫到设备广播，继续连接" : "[link] 8 秒内没扫到设备广播");
            return seen;
        }
        finally
        {
            try { watcher.Stop(); } catch { /* 已停 */ }
        }
    }

    public static async Task<WinRtMibleLink> ConnectAsync(ulong address, CancellationToken ct = default)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct);
        if (device is null)
        {
            Log("[link] FromBluetoothAddressAsync 返回 null（设备还没进系统缓存），先扫一轮广播");
            if (!await ScanUntilSeenAsync(address, ct).ConfigureAwait(false))
                throw new InvalidOperationException("扫不到这台设备：确认充电器已通电、在蓝牙范围内，手机上也没占着它的连接");

            for (int i = 0; i < 5 && device is null; i++)
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct);
            }
            if (device is null)
                throw new InvalidOperationException("BluetoothLEDevice.FromBluetoothAddressAsync 返回 null（广播扫到了，系统仍不给设备对象）");
        }

        WinRtMibleLink? link = null;
        device.ConnectionStatusChanged += (d, _) =>
        {
            Log($"[link] ConnectionStatus -> {d.ConnectionStatus}");
            if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                link?.Disconnected?.Invoke();
        };

        var svcResult = await device.GetGattServicesForUuidAsync(MiService, BluetoothCacheMode.Uncached).AsTask(ct);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            throw new InvalidOperationException($"找不到小米服务 0xFE95: {svcResult.Status}");
        var service = svcResult.Services[0];

        link = new WinRtMibleLink(device, service);
        var charResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (charResult.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"枚举特征失败: {charResult.Status}");

        foreach (var ch in charResult.Characteristics)
        {
            var shortId = ShortUuid(ch.Uuid);
            if (shortId is null) continue;
            link._all[shortId.Value] = ch;
            foreach (var kv in ShortUuids)
            {
                if (kv.Value == shortId.Value)
                {
                    link._chars[kv.Key] = ch;
                    Log($"[link] {kv.Key} <- 0x{shortId.Value:x4} props={ch.CharacteristicProperties}");
                }
            }
        }

        foreach (var kv in ShortUuids)
        {
            if (!link._chars.ContainsKey(kv.Key))
                throw new InvalidOperationException($"缺少特征 0x{kv.Value:x4} ({kv.Key})");
        }
        return link;
    }

    public async Task SubscribeAsync(MibleChannel channel, CancellationToken ct = default)
    {
        var ch = Get(channel);
        if (_handlers.ContainsKey(channel)) return;

        var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"订阅 {channel} 失败: {status}");

        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler = (_, e) =>
        {
            var reader = Windows.Storage.Streams.DataReader.FromBuffer(e.CharacteristicValue);
            var buf = new byte[e.CharacteristicValue.Length];
            reader.ReadBytes(buf);
            BytesReceived?.Invoke(channel, buf);
        };
        _handlers[channel] = handler;
        ch.ValueChanged += handler;
    }

    public async Task UnsubscribeAsync(MibleChannel channel, CancellationToken ct = default)
    {
        if (!_handlers.TryRemove(channel, out var handler)) return;
        var ch = Get(channel);
        ch.ValueChanged -= handler;
        await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(ct);
    }

    public async Task WriteAsync(MibleChannel channel, byte[] data, CancellationToken ct = default)
    {
        var ch = Get(channel);
        await _writeLock.WaitAsync(ct);
        try
        {
            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(data);
            var buffer = writer.DetachBuffer();
            var result = await ch.WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithoutResponse).AsTask(ct);
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException($"写入 {channel} ({data.Length}B) 失败: {result.Status} {result.ProtocolError}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<byte[]?> ReadAsync(MibleChannel channel, CancellationToken ct = default)
    {
        var ch = Get(channel);
        var result = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success) return null;
        var reader = Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
        var buf = new byte[result.Value.Length];
        reader.ReadBytes(buf);
        return buf;
    }

    /// <summary>按短 UUID 直接读特征（如 0x0004 固件版本）。</summary>
    public async Task<byte[]?> ReadShortAsync(ushort shortUuid, CancellationToken ct = default)
    {
        if (!_all.TryGetValue(shortUuid, out var ch)) return null;
        var result = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success) return null;
        var reader = Windows.Storage.Streams.DataReader.FromBuffer(result.Value);
        var buf = new byte[result.Value.Length];
        reader.ReadBytes(buf);
        return buf;
    }

    public IReadOnlyDictionary<ushort, GattCharacteristic> AllCharacteristics => _all;

    private GattCharacteristic Get(MibleChannel ch) =>
        _chars.TryGetValue(ch, out var c) ? c : throw new InvalidOperationException($"未映射通道 {ch}");

    public static ushort? ShortUuid(Guid g)
    {
        var s = g.ToString("N");
        if (s.Length != 32 || !s.EndsWith("00001000800000805f9b34fb", StringComparison.Ordinal)) return null;
        return Convert.ToUInt16(s.Substring(4, 4), 16);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var ch in _handlers.Keys.ToArray())
        {
            try { await UnsubscribeAsync(ch); } catch { }
        }
        // 特征的 Service 与 _service 是同一 COM 实例，重复 Dispose 会抛 ObjectDisposedException
        try { _service.Dispose(); } catch { }
        _device.Dispose();
    }
}
