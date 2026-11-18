using System.Runtime.InteropServices;

namespace XrShift.App;

/// <summary>Reads and sets a monitor's resolution and refresh rate. Only ever used on the virtual monitors the app asked for.</summary>
internal static class DisplayModes
{
    public readonly record struct Mode(int Width, int Height, int Hz)
    {
        public override string ToString() => $"{Width}x{Height} at {Hz} Hz";
    }

    /// <summary>The mode a monitor is in now. <paramref name="deviceName"/> is the short Windows name, e.g. <c>\\.\DISPLAY28</c>.</summary>
    public static Mode? Current(string deviceName)
    {
        DEVMODE mode = NewMode();
        return EnumDisplaySettings(deviceName, -1, ref mode) ? new Mode(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency) : null;
    }

    public static List<Mode> Available(string deviceName)
    {
        var modes = new HashSet<Mode>();
        DEVMODE mode = NewMode();
        for (int i = 0; EnumDisplaySettings(deviceName, i, ref mode); i++)
            modes.Add(new Mode(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency));
        return modes.OrderBy(m => m.Width).ThenBy(m => m.Height).ThenBy(m => m.Hz).ToList();
    }

    /// <summary>Switches a monitor to a mode it offers, and has Windows remember it for that monitor.</summary>
    public static bool TrySet(string deviceName, Mode wanted, out string problem)
    {
        problem = "";
        DEVMODE mode = NewMode();
        if (!EnumDisplaySettings(deviceName, -1, ref mode))
        {
            problem = "Windows would not report the monitor's current mode";
            return false;
        }
        if (!Available(deviceName).Contains(wanted))
        {
            problem = $"the monitor does not offer {wanted}";
            return false;
        }

        mode.dmPelsWidth = wanted.Width;
        mode.dmPelsHeight = wanted.Height;
        mode.dmDisplayFrequency = wanted.Hz;
        mode.dmFields = 0x00080000 | 0x00100000 | 0x00400000; // width, height, frequency
        int result = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, 1 /* remember it */, IntPtr.Zero);
        if (result == 0) return true;
        problem = $"Windows refused the change (code {result})";
        return false;
    }

    /// <summary>
    /// The mode a virtual monitor should be in to work well through the glasses, or null to leave it.
    /// 800x600 is the driver's start-up default and useless, so it becomes 1920x1080. The refresh rate
    /// goes to <paramref name="glassesHz"/>, which the app sets to 60: measured on this hardware the
    /// rate makes no difference to delay, and every virtual monitor has to be redrawn by Windows at its
    /// rate whether or not a game is hungry for the same graphics card, so lower is kinder.
    /// </summary>
    /// <param name="wantWidth">With <paramref name="wantHeight"/>: the size the wearer chose for this screen (ultrawide, say). 0 = keep the size.</param>
    public static Mode? Better(Mode current, IReadOnlyCollection<Mode> available, int glassesHz, int wantWidth = 0, int wantHeight = 0)
    {
        (int width, int height) = wantWidth > 0 && wantHeight > 0 ? (wantWidth, wantHeight)
            : current is { Width: 800, Height: 600 } ? (1920, 1080) : (current.Width, current.Height);
        int hz = available.Any(m => m.Width == width && m.Height == height && m.Hz == glassesHz) ? glassesHz : current.Hz;
        var better = new Mode(width, height, hz);
        if (!available.Contains(better))
        {
            // The size is offered at some rate, surely; take the closest to what was there.
            List<Mode> sameSize = available.Where(m => m.Width == width && m.Height == height).ToList();
            if (sameSize.Count == 0) return null;
            better = sameSize.OrderBy(m => Math.Abs(m.Hz - glassesHz)).First();
        }
        return better == current ? null : better;
    }

    static DEVMODE NewMode() => new() { dmDeviceName = "", dmFormName = "", dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    /// <summary>
    /// Moves monitors on the Windows desktop (where the mouse crosses from one to the next) in one go, and has
    /// Windows remember it. Only used on the virtual monitors XrShift made, and on the glasses' own area.
    /// </summary>
    public static bool TryPlace(IReadOnlyList<(string DeviceName, int X, int Y)> places, out string problem)
    {
        problem = "";
        foreach ((string device, int x, int y) in places)
        {
            DEVMODE mode = NewMode();
            if (!EnumDisplaySettings(device, -1, ref mode)) { problem = $"no mode for {device}"; return false; }
            mode.dmPositionX = x;
            mode.dmPositionY = y;
            mode.dmFields = 0x00000020; // position
            int result = ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, 0x01 | 0x10000000 /* remember, apply later */, IntPtr.Zero);
            if (result != 0) { problem = $"Windows refused to move {device} (code {result})"; return false; }
        }
        int applied = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (applied == 0) return true;
        problem = $"Windows refused the new arrangement (code {applied})";
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string? deviceName, IntPtr mode, IntPtr window, int flags, IntPtr parameters);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DEVMODE mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE mode, IntPtr window, int flags, IntPtr parameters);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
}
