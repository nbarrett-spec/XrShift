using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XrShift.App;

/// <summary>
/// Extra Windows desktops ("virtual monitors") through Parsec's Virtual Display Driver (signed by Parsec,
/// installed with <c>winget install Parsec.ParsecVDD</c>). XrShift talks to the driver directly: it adds and
/// removes displays, and pings the driver about every 50 ms while any exist. The driver takes its displays away
/// by itself when the pings stop, so if XrShift quits or crashes no monitor is ever left behind.
/// Control protocol as documented by nomi-san/parsec-vdd (core/parsec-vdd.h, BSD licence).
/// </summary>
internal static class VirtualMonitors
{
    static readonly Guid AdapterInterface = new("00b41627-04c4-429e-a26e-0265cf50c8fa");
    const uint IoctlAdd = 0x0022e004, IoctlRemove = 0x0022a008, IoctlUpdate = 0x0022a00c, IoctlVersion = 0x0022e010;

    /// <summary>More than this is asking for trouble: every one is a full desktop Windows has to draw.</summary>
    public const int Max = 6;

    static readonly object s_gate = new();
    static SafeFileHandle? s_device;
    static readonly List<int> s_added = [];
    static Thread? s_pinger;
    static volatile bool s_pinging;

    /// <summary>A monitor made by the driver. Parsec's are "ParsecVDA" with the hardware code PSCCDD0.</summary>
    public static bool IsVirtual(DisplayInfo display) =>
        IsVirtualId(display.Id) || display.MonitorNames.Any(n => n.Contains("ParsecVDA", StringComparison.OrdinalIgnoreCase));

    /// <summary>The same test from a device path alone (a monitor that may not be connected now). Also knows the
    /// earlier driver's monitors (#MTT...), so old layouts are recognised.</summary>
    public static bool IsVirtualId(string? id) =>
        id is not null && (id.Contains("#PSCCDD", StringComparison.OrdinalIgnoreCase) || id.Contains("#MTT", StringComparison.OrdinalIgnoreCase));

    /// <summary>How many virtual monitors a layout uses.</summary>
    public static int Needed(XrShift.Scene.Layout layout) =>
        Math.Min(layout.Screens.Where(s => IsVirtualId(s.DisplayName) && s.DisplayName!.Contains("#PSCCDD", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Max);

    /// <summary>The driver is installed and answers.</summary>
    public static bool Installed => DevicePath() is not null;

    /// <summary>How many virtual monitors XrShift has made, or null if the driver is not there.</summary>
    public static int? Count()
    {
        if (!Installed) return null;
        lock (s_gate) return s_added.Count;
    }

    /// <summary>Adds or removes displays until there are this many. Windows takes a moment to show the change.</summary>
    public static bool TrySetCount(int count, out string problem)
    {
        problem = "";
        count = Math.Clamp(count, 0, Max);
        lock (s_gate)
        {
            try
            {
                if (count == s_added.Count) return true;
                if (!Open(out problem)) return count == 0;
                while (s_added.Count < count)
                {
                    int index = Control(IoctlAdd, []);
                    if (index < 0) { problem = "the driver would not add a display"; break; }
                    s_added.Add(index);
                    Control(IoctlUpdate, []);
                }
                while (s_added.Count > count)
                {
                    int index = s_added[^1];
                    s_added.RemoveAt(s_added.Count - 1);
                    // The index goes as a 16-bit number, high byte first.
                    Control(IoctlRemove, [(byte)(index >> 8), (byte)index]);
                    Control(IoctlUpdate, []);
                }
                if (s_added.Count > 0) StartPinging();
                else Close();
                return problem.Length == 0;
            }
            catch (Exception e) when (e is Win32Exception or IOException)
            {
                problem = e.Message;
                return false;
            }
        }
    }

    /// <summary>Custom sizes (ultrawides) need settings this driver keeps elsewhere; not offered yet.</summary>
    public static bool TryAddSize(int width, int height, out string problem)
    {
        problem = $"{width}x{height} is not one of the driver's sizes yet";
        return false;
    }

    /// <summary>Checks the driver answers (reads its version).</summary>
    public static bool Reachable(out string problem)
    {
        lock (s_gate)
        {
            if (!Open(out problem)) return false;
            int version = Control(IoctlVersion, []);
            if (s_added.Count == 0) Close();
            if (version < 0) { problem = "the driver did not answer"; return false; }
            return true;
        }
    }

    // ---- the driver ------------------------------------------------------------------------------------

    static void StartPinging()
    {
        if (s_pinging) return;
        s_pinging = true;
        s_pinger = new Thread(() =>
        {
            while (s_pinging)
            {
                lock (s_gate)
                {
                    if (s_device is null || s_device.IsInvalid) break;
                    Control(IoctlUpdate, []);
                }
                Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = "Virtual monitors keep-alive", Priority = ThreadPriority.AboveNormal };
        s_pinger.Start();
    }

    static bool Open(out string problem)
    {
        problem = "";
        if (s_device is { IsInvalid: false, IsClosed: false }) return true;
        string? path = DevicePath();
        if (path is null) { problem = "the extra-desktop driver is not installed"; return false; }
        s_device = CreateFile(path, 0xC0000000 /* read, write */, 3 /* share read, write */, IntPtr.Zero, 3 /* open existing */,
            0x80 | 0x20000000 | 0x40000000 | 0x80000000 /* normal, no buffering, overlapped, write through */, IntPtr.Zero);
        if (s_device.IsInvalid)
        {
            problem = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            s_device = null;
            return false;
        }
        return true;
    }

    static void Close()
    {
        s_pinging = false;
        s_device?.Dispose();
        s_device = null;
        s_added.Clear();
    }

    /// <summary>One request to the driver: a 32-byte input, a 4-byte answer, at most 5 s. -1 if it failed.</summary>
    static unsafe int Control(uint code, byte[] data)
    {
        if (s_device is null || s_device.IsInvalid) return -1;
        byte* input = stackalloc byte[32];
        for (int i = 0; i < 32; i++) input[i] = i < data.Length ? data[i] : (byte)0;
        int output = 0;
        using var done = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
        if (!DeviceIoControl(s_device, code, input, 32, &output, 4, out _, &overlapped) && Marshal.GetLastWin32Error() != 997 /* pending */)
            return -1;
        if (!GetOverlappedResultEx(s_device, &overlapped, out _, 5000, false)) return -1;
        return output;
    }

    static string? DevicePath()
    {
        Guid guid = AdapterInterface;
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x10 | 0x02 /* device interface, present */);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data)) return null;
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr detail = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, NativeOverlapped* overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern unsafe bool GetOverlappedResultEx(SafeFileHandle device, NativeOverlapped* overlapped, out int transferred, uint milliseconds, bool alertable);
}
