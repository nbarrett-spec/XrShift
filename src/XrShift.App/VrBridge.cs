using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Text;
using Microsoft.Win32;
using XrShift.Scene;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>
/// VR mode: the glasses become a seated, rotation-only SteamVR headset. XrShift steps aside (as in plain screen
/// mode) and keeps tracking the head; this class hands the head's direction, turn rate and the glasses' place on
/// the desktop to XrShift's SteamVR driver through one block of shared memory (<see cref="VrWire"/>, docs/VR.md),
/// and finds, registers and starts SteamVR. The driver itself is src/XrShift.SteamVR (C++).
/// </summary>
internal sealed class VrBridge : IDisposable
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly object _gate = new();
    readonly byte[] _block = new byte[VrWire.Size];
    uint _sequence;
    VrWire.Display _display;
    VrWire.Command _command = VrWire.Command.QuitSteamVr; // nothing for a driver to show until Begin
    long _ticks;
    Quaternion _rotation = Quaternion.Identity;
    Vector3 _rate;

    public VrBridge()
    {
        _file = MemoryMappedFile.CreateOrOpen(VrWire.Name, VrWire.Size, MemoryMappedFileAccess.ReadWrite);
        _view = _file.CreateViewAccessor(0, VrWire.Size);
        // A block left by an earlier session of this XrShift (the driver keeps it open) carries on its numbering.
        _sequence = _view.ReadUInt32(VrWire.SequenceAt) & ~1u;
        lock (_gate) WriteLocked();
    }

    /// <summary>VR mode starts: the driver may add the glasses to SteamVR, drawing into this display.</summary>
    public void Begin(VrWire.Display display)
    {
        lock (_gate)
        {
            _display = display;
            _command = VrWire.Command.Run;
            WriteLocked();
        }
    }

    /// <summary>VR mode ends: the driver asks SteamVR to close, which gives the glasses' screen back.</summary>
    public void End()
    {
        lock (_gate)
        {
            _command = VrWire.Command.QuitSteamVr;
            WriteLocked();
        }
    }

    /// <summary>The head now. Called for every motion-sensor reading, on the reading thread.</summary>
    public void Publish(Quaternion rotation, Vector3 angularVelocity)
    {
        lock (_gate)
        {
            _ticks = Stopwatch.GetTimestamp(); // the same clock as the driver's QueryPerformanceCounter
            _rotation = rotation;
            _rate = angularVelocity;
            WriteLocked();
        }
    }

    /// <summary>The driver read the block within the last two seconds, i.e. SteamVR is running with the glasses.</summary>
    public bool DriverActive
    {
        get
        {
            long seen = _view.ReadInt64(VrWire.DriverTicksAt);
            return seen > 0 && Stopwatch.GetTimestamp() - seen < 2 * Stopwatch.Frequency;
        }
    }

    // A sequence lock: odd while the rest is being written, so the driver never takes half of one write.
    void WriteLocked()
    {
        VrWire.Write(_block, _ticks, _rotation, _rate, _display, _command);
        _view.Write(VrWire.SequenceAt, ++_sequence);
        Thread.MemoryBarrier();
        _view.WriteArray(VrWire.MagicAt, _block, VrWire.MagicAt, VrWire.DriverTicksAt - VrWire.MagicAt);
        Thread.MemoryBarrier();
        _view.Write(VrWire.SequenceAt, ++_sequence);
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }

    // ---- what VR mode needs, and the parts XrShift can do --------------------------------------------------

    /// <summary>Folder holding the driver as SteamVR wants it (driver.vrdrivermanifest, bin\win64\driver_xrshift.dll).</summary>
    public static string DriverFolder => Path.Combine(AppContext.BaseDirectory, "steamvr", "xrshift");

    /// <summary>The driver was built and shipped with this copy of XrShift.</summary>
    public static bool DriverPresent => File.Exists(Path.Combine(DriverFolder, "bin", "win64", "driver_xrshift.dll"));

    /// <summary>SteamVR's list of extra drivers, per Windows user.</summary>
    public static string VrPathsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");

    /// <summary>Where SteamVR is installed, in any of Steam's library folders, or null.</summary>
    public static string? SteamVrFolder()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) return null;
        string? vdf = null;
        try { vdf = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return SteamVrSetup.FindSteamVr(SteamVrSetup.Libraries(steam, vdf), File.Exists);
    }

    public static bool SteamVrRunning() => Process.GetProcessesByName("vrserver").Length > 0;

    public static SteamVrSetup.Registration Registration(out string? problem)
    {
        string? text = ReadVrPaths(out problem);
        return problem is not null ? SteamVrSetup.Registration.Unreadable : SteamVrSetup.Check(text, DriverFolder);
    }

    static string? ReadVrPaths(out string? problem)
    {
        problem = null;
        try { return File.Exists(VrPathsFile) ? File.ReadAllText(VrPathsFile) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = e.Message; return null; }
    }

    /// <summary>
    /// Adds this copy's driver to SteamVR's list for this Windows user (or takes every XrShift driver out),
    /// keeping a copy of the file first. Returns a reason when nothing was changed; null when done or already so.
    /// </summary>
    public static string? ChangeRegistration(bool register)
    {
        string path = VrPathsFile;
        string? text = ReadVrPaths(out string? problem);
        if (problem is not null) return $"Could not read {path}: {problem}";
        if (!register && text is null) return null;
        string? changed = register ? SteamVrSetup.Register(text, DriverFolder, out string why) : SteamVrSetup.Unregister(text, out why);
        if (changed is null) return why;
        if (changed == text) return null;
        try
        {
            string first = path + ".bak-before-xrshift";
            File.Copy(path, File.Exists(first) ? $"{path}.bak-xrshift-{DateTime.Now:yyyyMMdd-HHmmss}" : first);
            // Written beside it and then swapped in, so a failure half way never leaves SteamVR a broken file.
            string fresh = path + ".xrshift-new";
            File.WriteAllText(fresh, changed, new UTF8Encoding(false));
            File.Move(fresh, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not change {path}: {e.Message}. It is as it was.";
        }
        Log.Line(register ? $"VR: XrShift's driver registered with SteamVR ({DriverFolder})." : "VR: XrShift's driver taken out of SteamVR's list.");
        return null;
    }

    /// <summary>
    /// Everything VR mode needs that XrShift can check, in order, with the first thing missing in plain words;
    /// null when VR mode can start. Registers the driver if that is all that is missing.
    /// </summary>
    public static string? Ready(bool steamVrHasOurDriver)
    {
        if (!DriverPresent)
            return "This copy of XrShift has no VR driver yet (it is built on GitHub for releases). Install a release that includes it.";
        if (SteamVrFolder() is null)
            return "SteamVR is not installed. Install it from Steam (search the store for SteamVR; it is free), run it once, close it, then try again.";
        switch (Registration(out string? problem))
        {
            case SteamVrSetup.Registration.NoFile:
                return "SteamVR has not been run on this PC yet. Start it once from Steam, close it, then try again.";
            case SteamVrSetup.Registration.Unreadable:
                return $"SteamVR's driver list ({VrPathsFile}) could not be read, so XrShift left it alone: {problem ?? "it is not plain JSON."}";
            case SteamVrSetup.Registration.None or SteamVrSetup.Registration.Other:
                if (ChangeRegistration(register: true) is { } why) return "Could not register XrShift's driver with SteamVR: " + why;
                break;
        }
        // SteamVR loads drivers when it starts, and ours only adds the glasses while VR mode is on.
        if (SteamVrRunning() && !steamVrHasOurDriver)
            return "SteamVR is already running without the glasses. Close SteamVR, then start VR again.";
        return null;
    }

    public static string? LaunchSteamVr()
    {
        if (SteamVrRunning()) return null;
        try { Process.Start(new ProcessStartInfo("steam://rungameid/250820") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "Could not start SteamVR: " + e.Message;
        }
        return null;
    }
}
