using System.Text;

namespace CuktechTray;

/// <summary>无人值守自检：连接 → 认证 → 轮询四口，把结果打到控制台，用于回归验证。</summary>
internal static class SelfTest
{
    public static async Task<bool> RunAsync(Settings settings)
    {
        var ctx = new SynchronizationContext();
        var client = new DeviceClient(settings, ctx);
        var last = new Snapshot();
        client.Updated += s => last = s;
        client.Start();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !(last.Connected && last.Ports.Any(p => p.HasData)))
            await Task.Delay(250);

        var sb = new StringBuilder();
        sb.AppendLine("connected=" + last.Connected + " firmware=" + last.Firmware + " port_switch=0x" + last.PortSwitch.ToString("x2"));
        sb.AppendLine("status=" + last.Status + " error=" + last.Error);
        foreach (var p in last.Ports)
        {
            sb.AppendLine(string.Format("  {0,-2} has={1} idle={2} state=0x{3:x2} {4,5:0.0}V {5,5:0.00}A {6,6:0.0}W",
                p.Name, p.HasData, p.Idle, p.State, p.Volts, p.Amps, p.Watts));
        }
        sb.AppendLine("total=" + last.TotalWatts.ToString("0.0") + "W");
        Console.Write(sb.ToString());

        await client.StopAsync();
        return last.Connected && last.Ports.Any(p => p.HasData);
    }
}
