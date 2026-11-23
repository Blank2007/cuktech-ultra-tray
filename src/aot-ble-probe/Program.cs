using System;
using System.Runtime.InteropServices;
using System.Text;

internal static class Probe
{
    private static Guid GattServiceIf =
        new Guid("6e3bb679-4372-40c8-9eaa-4509df260cd8");

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid ifClass, int index, ref SpDevInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDevInfoData data, IntPtr detail, int detailSize, out int required, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiOpenDeviceInterfaceW(IntPtr set, string path, int flags, IntPtr data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevInfoData data, int property, out int regType, IntPtr buffer, int bufferSize, out int required);


    // ---- 已知蓝牙设备枚举 ----
    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothAddress { public ulong Value; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BluetoothDeviceInfo
    {
        public int dwSize;
        public BluetoothAddress Address;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public SystemTime stLastSeen;
        public SystemTime stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
    {
        public int dwSize;
        public int fReturnAuthenticated;
        public int fReturnRemembered;
        public int fReturnUnknown;
        public int fReturnConnected;
        public int fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothRadioInfo
    {
        public uint dwSize;
        public BluetoothAddress Address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
    }

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BluetoothRadioInfo radio, out IntPtr hRadio);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParams p, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BluetoothDeviceInfo info);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    private static void DumpDevices()
    {
        Console.WriteLine("=== known bluetooth devices ===");
        var radio = new BluetoothRadioInfo();
        radio.dwSize = (uint)Marshal.SizeOf<BluetoothRadioInfo>();
        IntPtr hRadio;
        IntPtr hFindRadio = BluetoothFindFirstRadio(ref radio, out hRadio);
        Console.WriteLine("radio find=" + hFindRadio + " hRadio=" + hRadio +
            " err=" + Marshal.GetLastWin32Error() + " name=" + radio.szName);

        var sp = new BluetoothDeviceSearchParams();
        sp.dwSize = Marshal.SizeOf<BluetoothDeviceSearchParams>();
        sp.fReturnAuthenticated = 1;
        sp.fReturnRemembered = 1;
        sp.fReturnUnknown = 1;
        sp.fReturnConnected = 1;
        sp.fIssueInquiry = 0;
        sp.cTimeoutMultiplier = 2;
        sp.hRadio = IntPtr.Zero;

        var info = new BluetoothDeviceInfo();
        info.dwSize = Marshal.SizeOf<BluetoothDeviceInfo>();
        IntPtr hFind = BluetoothFindFirstDevice(ref sp, ref info);
        if (hFind == IntPtr.Zero)
        {
            Console.WriteLine("no devices, err=" + Marshal.GetLastWin32Error());
            return;
        }
        int n = 0;
        do
        {
            ulong addr = info.Address.Value;
            string mac = string.Join(":", new[] {
                ((addr) & 0xFF).ToString("X2"),
                ((addr >> 8) & 0xFF).ToString("X2"),
                ((addr >> 16) & 0xFF).ToString("X2"),
                ((addr >> 24) & 0xFF).ToString("X2"),
                ((addr >> 32) & 0xFF).ToString("X2"),
                ((addr >> 40) & 0xFF).ToString("X2") });
            Console.WriteLine("dev[" + n + "] " + mac + " name='" + info.szName + "' connected=" + info.fConnected +
                " remembered=" + info.fRemembered + " authenticated=" + info.fAuthenticated);
            n++;
            info.dwSize = Marshal.SizeOf<BluetoothDeviceInfo>();
        } while (BluetoothFindNextDevice(hFind, ref info));
        BluetoothFindDeviceClose(hFind);
        Console.WriteLine("devices=" + n);
    }

    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        DumpDevices();
        int found = 0;
        IntPtr set = SetupDiGetClassDevsW(ref GattServiceIf, IntPtr.Zero, IntPtr.Zero, 0x12);
        Console.WriteLine("set=" + set);
        if (set == new IntPtr(-1)) { Console.WriteLine("GetClassDevs failed " + Marshal.GetLastWin32Error()); return; }

        for (int i = 0; ; i++)
        {
            var did = new SpDevInfoData();
            did.cbSize = Marshal.SizeOf<SpDevInfoData>();
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GattServiceIf, i, ref did)) break;

            SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out int need, IntPtr.Zero);
            IntPtr buf = Marshal.AllocHGlobal(need);
            try
            {
                Marshal.WriteInt32(buf, 8);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, buf, need, out _, IntPtr.Zero))
                {
                    Console.WriteLine("detail failed " + Marshal.GetLastWin32Error());
                    continue;
                }
                string path = Marshal.PtrToStringUni(buf + 4);
                string friendly = "";
                IntPtr ifSet = SetupDiOpenDeviceInterfaceW(set, path, 0, IntPtr.Zero);
                if (ifSet != IntPtr.Zero && ifSet != new IntPtr(-1))
                {
                    var info = new SpDevInfoData();
                    info.cbSize = Marshal.SizeOf<SpDevInfoData>();
                    SetupDiGetClassDevsW(ref GattServiceIf, IntPtr.Zero, IntPtr.Zero, 0x12);
                    IntPtr tmp = Marshal.AllocHGlobal(1024);
                    try
                    {
                        if (SetupDiGetDeviceRegistryPropertyW(ifSet, ref info, 0xC, out _, tmp, 1024, out _))
                            friendly = Marshal.PtrToStringUni(tmp);
                    }
                    finally { Marshal.FreeHGlobal(tmp); }
                }
                Console.WriteLine("[" + i + "] " + path + "  | friendly=" + friendly);
                found++;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        SetupDiDestroyDeviceInfoList(set);
        Console.WriteLine("total=" + found);
    }
}
