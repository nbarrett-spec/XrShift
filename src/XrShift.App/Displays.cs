using System.Runtime.InteropServices;

namespace XrShift.App;

/// <summary>One Windows desktop area and the monitor(s) showing it. Two names means the picture is duplicated.</summary>
/// <param name="Id">
/// Stays the same for a given monitor across restarts and re-plugging (its device path). Windows hands
/// out the short <paramref name="DeviceName"/> afresh whenever monitors come and go, so layouts remember this instead.
/// </param>
internal sealed record DisplayInfo(string Id, string DeviceName, string[] MonitorNames, Rectangle Bounds, bool Primary, double RefreshHz)
{
    /// <summary>What to call it in a list.</summary>
    public string Label =>
        $"{(MonitorNames.Length > 0 ? MonitorNames[0] : DeviceName.TrimStart('\\', '.'))}  ({Bounds.Width} x {Bounds.Height})";

    public bool IsGlasses => MonitorNames.Any(n =>
        n.Contains("XREAL", StringComparison.OrdinalIgnoreCase) || n.Contains("NREAL", StringComparison.OrdinalIgnoreCase));

    /// <summary>The glasses show the same picture as another monitor (Windows "Duplicate"), not their own.</summary>
    public bool Mirrored => MonitorNames.Length > 1;

    public override string ToString() =>
        $"{DeviceName}  {Bounds.Width}x{Bounds.Height} at {Bounds.X},{Bounds.Y}  {RefreshHz:F0} Hz  " +
        $"{(MonitorNames.Length > 0 ? string.Join(" + ", MonitorNames) : "unnamed")}{(Primary ? "  (primary)" : "")}";
}

/// <summary>Finds the monitors Windows is using and what they call themselves.</summary>
internal static class Displays
{
    public static List<DisplayInfo> List()
    {
        Dictionary<string, (List<string> Names, double Hz, string Path)> targets = QueryTargets();
        var result = new List<DisplayInfo>();
        foreach (Screen screen in Screen.AllScreens)
        {
            targets.TryGetValue(screen.DeviceName, out (List<string> Names, double Hz, string Path) target);
            string id = string.IsNullOrEmpty(target.Path) ? screen.DeviceName : target.Path;
            result.Add(new DisplayInfo(id, screen.DeviceName, target.Names?.ToArray() ?? [], screen.Bounds, screen.Primary, target.Hz));
        }
        return result;
    }

    /// <summary>GDI device name ("\\.\DISPLAY1") to the friendly names of the monitors attached to it.</summary>
    static Dictionary<string, (List<string> Names, double Hz, string Path)> QueryTargets()
    {
        var map = new Dictionary<string, (List<string>, double, string)>(StringComparer.OrdinalIgnoreCase);
        const uint onlyActivePaths = 2;
        if (GetDisplayConfigBufferSizes(onlyActivePaths, out uint pathCount, out uint modeCount) != 0) return map;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(onlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return map;

        for (int i = 0; i < pathCount; i++)
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = 1; // get source name
            source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            source.header.adapterId = paths[i].sourceInfo.adapterId;
            source.header.id = paths[i].sourceInfo.id;
            if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = 2; // get target name
            target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            target.header.adapterId = paths[i].targetInfo.adapterId;
            target.header.id = paths[i].targetInfo.id;
            bool known = DisplayConfigGetDeviceInfo(ref target) == 0;
            string name = known ? target.monitorFriendlyDeviceName ?? "" : "";
            string path = known ? target.monitorDevicePath ?? "" : "";

            DISPLAYCONFIG_RATIONAL rate = paths[i].targetInfo.refreshRate;
            double hz = rate.Denominator != 0 ? (double)rate.Numerator / rate.Denominator : 0;

            if (!map.TryGetValue(source.viewGdiDeviceName, out (List<string> Names, double Hz, string Path) entry))
                entry = ([], hz, path);
            if (name.Length > 0) entry.Names.Add(name);
            map[source.viewGdiDeviceName] = (entry.Names, Math.Max(entry.Hz, hz), entry.Path.Length > 0 ? entry.Path : path);
        }
        return map;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>Only its size matters here; the contents are not read.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }
}
