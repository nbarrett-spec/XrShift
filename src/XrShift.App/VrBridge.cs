using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using Microsoft.Win32;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>
/// VR mode: the glasses become a seated, rotation-only SteamVR headset. XrShift steps aside (as in plain screen
/// mode) and keeps tracking the head; this class hands the head's direction to XrShift's SteamVR driver through
/// a small block of shared memory, tells the driver where the glasses are, registers the driver with SteamVR
/// and starts SteamVR. The driver itself is src/XrShift.SteamVR (C++, built by its build.cmd).
/// </summary>
internal sealed class VrBridge : IDisposable
{
    // Layout shared with driver.cpp: sequence (odd while writing), magic, w, x, y, z, written (QPC ticks).
    const int Size = 64;
    const int Magic = 0x56535258; // 'XRSV'
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    int _sequence;
    readonly object _gate = new();

    public VrBridge()
    {
        _file = MemoryMappedFile.CreateOrOpen(@"Local\XrShift.VrPose", Size, MemoryMappedFileAccess.ReadWrite);
        _view = _file.CreateViewAccessor(0, Size);
        _view.Write(4, Magic);
    }

    /// <summary>Hands the latest head direction to the driver. Called for every motion-sensor reading.</summary>
    public void Publish(Quaternion q)
    {
        lock (_gate)
        {
            _view.Write(0, ++_sequence); // odd: being written
            Thread.MemoryBarrier();
            _view.Write(8, (double)q.W);
            _view.Write(16, (double)q.X);
            _view.Write(24, (double)q.Y);
            _view.Write(32, (double)q.Z);
            _view.Write(40, Stopwatch.GetTimestamp());
            Thread.MemoryBarrier();
            _view.Write(0, ++_sequence); // even: done
        }
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>Folder holding the driver as SteamVR wants it (driver.vrdrivermanifest, bin\win64\driver_xrshift.dll).</summary>
    public static string DriverFolder => Path.Combine(AppContext.BaseDirectory, "steamvr", "xrshift");

    /// <summary>The driver was built and shipped with this copy of XrShift.</summary>
    public static bool DriverPresent => File.Exists(Path.Combine(DriverFolder, "bin", "win64", "driver_xrshift.dll"));

    /// <summary>Where SteamVR is installed, or null.</summary>
    public static string? SteamVrFolder()
    {
        string? steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is null) return null;
        var libraries = new List<string> { steam };
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
                foreach (string line in File.ReadAllLines(vdf))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("\"path\"", StringComparison.Ordinal)) continue;
                    string[] parts = t.Split('"', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3) libraries.Add(parts[^1].Replace(@"\\", @"\"));
                }
        }
        catch (IOException) { }
        return libraries.Select(l => Path.Combine(l, "steamapps", "common", "SteamVR"))
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "bin", "win64", "vrpathreg.exe")));
    }

    /// <summary>
    /// Writes what the driver needs to know (the glasses' place on the desktop, side-by-side 3D or not, refresh
    /// and view angle), registers the driver with SteamVR and starts SteamVR. Returns a reason if it could not.
    /// </summary>
    public static string? Start(Rectangle glasses, double hz, float fovDeg)
    {
        if (!DriverPresent) return "the VR driver is not built into this copy of XrShift";
        string? steamVr = SteamVrFolder();
        if (steamVr is null) return "SteamVR is not installed (install it from Steam)";

        // The glasses' own 3D mode shows as a desktop twice as wide (3840x1080): one half per eye.
        bool stereo = glasses.Width >= glasses.Height * 3;
        Directory.CreateDirectory(TrackingConfig.Folder);
        File.WriteAllText(Path.Combine(TrackingConfig.Folder, "vr.txt"), string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{glasses.X},{glasses.Y},{glasses.Width},{glasses.Height},{(stereo ? 1 : 0)},{Math.Round(hz)},{fovDeg:F1}"));

        try
        {
            var register = new ProcessStartInfo(Path.Combine(steamVr, "bin", "win64", "vrpathreg.exe")) { UseShellExecute = false, CreateNoWindow = true };
            register.ArgumentList.Add("adddriver");
            register.ArgumentList.Add(DriverFolder);
            using (Process? p = Process.Start(register)) p?.WaitForExit(10000);
            Process.Start(new ProcessStartInfo("steam://rungameid/250820") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "could not start SteamVR: " + e.Message;
        }
        Log.Line($"VR mode: glasses at {glasses}, {(stereo ? "side-by-side 3D" : "flat (switch the glasses to 3D for depth)")}, {hz:F0} Hz; SteamVR starting.");
        return null;
    }
}
