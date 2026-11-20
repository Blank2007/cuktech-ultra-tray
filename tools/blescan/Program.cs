using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

internal static class Program
{
    private static readonly ulong Target = 0x3CCD7327_61A0UL; // 3C:CD:73:27:61:A0

    private static async Task<int> Main(string[] args)
    {
        int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 12;
        var seen = new Dictionary<ulong, (string Name, short Rssi)>();
        var gate = new object();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        watcher.Received += (_, e) =>
        {
            string name = e.Advertisement?.LocalName ?? "";
            lock (gate)
            {
                if (!seen.TryGetValue(e.BluetoothAddress, out var old) || (string.IsNullOrEmpty(old.Name) && !string.IsNullOrEmpty(name)))
                    seen[e.BluetoothAddress] = (string.IsNullOrEmpty(name) && old.Name != null ? old.Name : name, e.RawSignalStrengthInDBm);
            }
        };

        Console.WriteLine("watcher status(before)=" + watcher.Status);
        watcher.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        watcher.Stop();
        Console.WriteLine($"watcher status(after)={watcher.Status}  found={seen.Count}");

        foreach (var kv in seen.OrderBy(k => k.Key))
        {
            string mark = kv.Key == Target ? "  <== OUR CHARGER" : "";
            Console.WriteLine($"{kv.Key:X12}  rssi={kv.Value.Rssi,4}  name='{kv.Value.Name}'{mark}");
        }

        try
        {
            var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(Target);
            Console.WriteLine("FromBluetoothAddressAsync(target)=" + (dev == null ? "null" : $"'{dev.Name}' status={dev.ConnectionStatus}"));
            if (dev != null)
            {
                var svc = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                Console.WriteLine($"gatt status={svc.Status} services={svc.Services.Count}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FromBluetoothAddressAsync threw: " + ex.GetType().Name + ": " + ex.Message);
        }

        return 0;
    }
}
