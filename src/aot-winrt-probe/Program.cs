using System.Text;
using System.Text.Json;
using CuktechMible;
using ProbeCs;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        WinRtMibleLink.Diag = s => Console.WriteLine("[diag] " + s);

        var settingsPath = @"D:\dsh projects\cuktech-ultra-tray\dist\settings.json";
        string mac = "";
        string token = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (doc.RootElement.TryGetProperty("Mac", out var m)) mac = m.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("TokenHex", out var t)) token = t.GetString() ?? "";
        }
        catch (Exception ex)
        {
            Console.WriteLine("read settings failed: " + ex.Message);
        }
        Console.WriteLine("mac=" + mac + " tokenLen=" + token.Length);
        if (mac.Length == 0)
        {
            Console.WriteLine("no mac, abort");
            return 2;
        }

        var parts = mac.Split(':');
        ulong addr = 0;
        for (int i = 0; i < parts.Length; i++)
            addr = (addr << 8) | Convert.ToByte(parts[i], 16);
        Console.WriteLine("addr=0x" + addr.ToString("X12"));

        var link = await WinRtMibleLink.ConnectAsync(addr);
        Console.WriteLine("connected chars=" + link.AllCharacteristics.Count);
        var fw = await link.ReadShortAsync(0x0004);
        if (fw is not null)
        {
            var s = Encoding.UTF8.GetString(fw).TrimEnd('\0');
            Console.WriteLine("firmware=" + s);
        }
        await link.DisposeAsync();
        Console.WriteLine("winrt aot probe ok");
        return 0;
    }
}
