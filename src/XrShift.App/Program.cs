using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using XrShift.Scene;
using XrShift.Tracking;

namespace XrShift.App;

internal static class Program
{
    const float DegToRad = MathF.PI / 180f;

    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Log.Start();
        if (args.Contains("--help") || args.Contains("-h")) return Usage();

        ApplicationConfiguration.Initialize();
        if (Option(args, "--make-icon") is { } iconPath)
        {
            AppIcon.WriteIco(iconPath);
            Console.WriteLine($"Icon written to {iconPath}");
            return 0;
        }
        if (args.Contains("--list")) return ListDisplays();
        if (args.Contains("--direct-list")) { foreach (string line in DirectDisplay.Describe()) Console.WriteLine(line); return 0; }
        if (args.Contains("--windows")) return ListWindows();
        if (args.Contains("--reset"))
        {
            // The way back to a plain PC: no virtual monitors, whatever state the app was left in.
            bool sent = !VirtualMonitors.Installed || VirtualMonitors.TrySetCount(0, out string _);
            Console.WriteLine(sent ? "Virtual monitors removed. Windows on them move back to your real screens." : "The virtual monitor driver did not answer.");
            MessageBox.Show(sent
                    ? "XrShift's virtual monitors are removed. Any windows on them move back to your screens.\n\nThe glasses, if plugged in, are an ordinary extended screen until you start XrShift again."
                    : "The virtual monitor driver did not answer. Try again in a few seconds.",
                "XrShift", MessageBoxButtons.OK, sent ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return sent ? 0 : 2;
        }
        if (Option(args, "--virtual-count") is { } wantedCount && int.TryParse(wantedCount, out int virtualCount))
        {
            bool sent = VirtualMonitors.TrySetCount(virtualCount, out string why);
            Console.WriteLine(sent ? $"Asked the driver for {Math.Clamp(virtualCount, 0, VirtualMonitors.Max)} virtual monitor(s)." : $"Could not reach the driver: {why}");
            return sent ? 0 : 2;
        }
        if (args.Contains("--virtual")) return VirtualStatus(Option(args, "--virtual"));
        if (Option(args, "--settings-shot") is { } settingsShot) return SettingsShot(settingsShot, Option(args, "--page"), args.Contains("--stationary"));

        string? shot = Option(args, "--shot");
        if (shot is not null) return Shot(shot, args);

        // One copy at a time. Two copies fight over the glasses and the virtual monitors and slow the whole PC;
        // starting it again just brings the running copy's settings window forward.
        // A short test window (--window, --seconds) touches no monitors and may run beside the real thing.
        bool trial = args.Contains("--window") || Number(args, "--seconds", 0) > 0;
        using var only = new Mutex(!trial, trial ? @"Local\XrShift.Trial" : @"Local\XrShift.Live", out bool first);
        if (!first && !trial)
        {
            try { using var wake = EventWaitHandle.OpenExisting(@"Local\XrShift.Show"); wake.Set(); } catch (Exception) { }
            return 0;
        }

        Log.StartFile();
        // An error in any window or timer must come here, to be logged and cleaned up, instead of Windows'
        // "Continue / Quit" box, which leaves the virtual monitors behind.
        // One known harmless case is let through: Windows finishing a cross-thread call for a window of an
        // earlier session that is already gone (it crashed the app in a loop on 2026-11-17 after a replug).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LiveApp.WindowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Log.Line("XrShift stopped because of an error on a background thread:");
            Log.Line(e.ExceptionObject.ToString() ?? "");
            try { if (VirtualMonitors.Installed) VirtualMonitors.TrySetCount(0, out _); } catch (Exception) { }
        };
        try
        {
            return LiveApp.Run(args);
        }
        catch (Exception e)
        {
            // Never die silently: say what happened, keep the details, and take the virtual monitors away so
            // nothing is left on screens nobody can see.
            Log.Line("XrShift stopped because of an error:");
            Log.Line(e.ToString());
            try { if (VirtualMonitors.Installed) VirtualMonitors.TrySetCount(0, out _); } catch (Exception) { }
            MessageBox.Show(
                "XrShift ran into a problem and had to stop.\n\n" + e.Message +
                "\n\nThe details are in last-run.txt in " + XrShift.Tracking.TrackingConfig.Folder + ".",
                "XrShift", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }
    }

    static int Usage()
    {
        Console.WriteLine("""
            xrshift                      float your monitors around you in the glasses; a settings window opens on the PC.
                                         With no glasses plugged in it waits in the tray until they are.
              --window                   use a 1280x720 window on the main screen instead of the glasses
              --monitor N                show on monitor N from --list
              --list                     list the monitors Windows is using
              --windows                  list the programs whose windows could be pinned as screens
              --virtual                  say whether the virtual monitor driver is installed and answering
              --virtual-count N          set how many virtual monitors exist (0 removes them all) and exit
              --reset                    remove all virtual monitors and exit: back to a plain PC
              --keep-monitors            do not add or remove virtual monitors when starting, quitting or switching profile
              --no-capture               stand-in pictures instead of live monitors
              --no-settings              do not open the settings window at start (Ctrl+Alt+S opens it)
              --api                      let other programs on this PC list the scene and switch layouts (pipe ArWorkspace.Api)
              --free-cursor             let the mouse pointer wander onto the glasses' own desktop area
              --keep-modes               leave virtual monitors' resolution and refresh rate alone
              --fov D                    horizontal view angle of the glasses in degrees (default 50.7, or what you last tuned)
              --delay MS                 how long the glasses take to show a frame, in milliseconds (default 16.5, or what you last tuned)
              --lead MS                  experiment: read the head pose this long before each screen refresh (default off; costs missed frames)
              --host H --port N          read head motion from somewhere else (e.g. 'xrprobe serve')
              --seconds S                close by itself after S seconds
              --shot FILE.png            draw one frame of the saved layout to a picture and exit (add --capture for live monitors), with either
                --yaw D --pitch D --roll D     a head pose (left, up and tilt-right are positive), or
                --after S                      the tracked pose after S seconds of head motion
              --settings-shot FILE.png   save a picture of the settings window and exit (--page screen|stationary|follow|tune, --stationary)
            Keys, from any program: Ctrl+Alt+0 (or Home, or Space) recentre, Ctrl+Alt+S settings, Ctrl+Alt+Q quit,
            Ctrl+Alt+1 to 4 profiles, Ctrl+Alt+G game mode (glasses show their own desktop),
            Ctrl+Alt+M bring the mouse pointer to where you are looking,
            Ctrl+Alt+H status line, Ctrl+Alt+[ ] glasses delay, Ctrl+Alt+- = view angle.
            """);
        return 1;
    }

    /// <summary>Says whether the virtual monitor driver is there and answering. Changes nothing.</summary>
    static int VirtualStatus(string? setMode)
    {
        if (!VirtualMonitors.Installed)
        {
            Console.WriteLine("Virtual Display Driver: not found.");
            return 2;
        }
        bool answers = VirtualMonitors.Reachable(out string problem);
        Console.WriteLine($"Virtual Display Driver: installed, set to {VirtualMonitors.Count()?.ToString() ?? "?"} monitor(s), " +
            (answers ? "takes commands on its pipe." : $"does not take commands ({problem})."));
        // "--virtual 1920x1080@120" puts every virtual monitor in that mode.
        DisplayModes.Mode? wanted = null;
        if (setMode is not null)
        {
            string[] parts = setMode.Split('x', '@');
            if (parts.Length == 3 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && int.TryParse(parts[2], out int hz))
                wanted = new DisplayModes.Mode(w, h, hz);
        }

        foreach (DisplayInfo d in Displays.List().Where(VirtualMonitors.IsVirtual))
        {
            Console.WriteLine($"  {d}");
            Console.WriteLine($"    now {DisplayModes.Current(d.DeviceName)}; 1920x1080 offered at {string.Join(", ", DisplayModes.Available(d.DeviceName).Where(m => m is { Width: 1920, Height: 1080 }).Select(m => m.Hz))} Hz");
            if (wanted is { } mode)
                Console.WriteLine(DisplayModes.TrySet(d.DeviceName, mode, out string why) ? $"    set to {mode}" : $"    not changed: {why}");
        }
        return answers ? 0 : 2;
    }

    /// <summary>How many windows could be pinned, by program. Titles are left out on purpose: they can be private.</summary>
    static int ListWindows()
    {
        List<WindowInfo> windows = WindowFinder.List();
        Console.WriteLine($"{windows.Count} windows could be pinned:");
        foreach (IGrouping<string, WindowInfo> group in windows.GroupBy(w => w.Program, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key}: {string.Join(", ", group.Select(w => $"{w.Width}x{w.Height}{(WindowFinder.Minimized(w.Handle) ? " minimised" : "")}"))}");
        return 0;
    }

    static int ListDisplays()
    {
        List<DisplayInfo> displays = Displays.List();
        for (int i = 0; i < displays.Count; i++)
            Console.WriteLine($"{i}: {displays[i]}{(displays[i].IsGlasses ? "  <- glasses" : "")}");
        Console.WriteLine("Drawn by:");
        foreach ((string adapter, string luid, List<string> monitors) in Renderer.AdaptersAndMonitors())
            Console.WriteLine($"  {adapter} ({luid}): {(monitors.Count > 0 ? string.Join(", ", monitors) : "no monitors")}");
        return 0;
    }

    // ---- one frame to a file ------------------------------------------------------------

    static int Shot(string path, string[] args)
    {
        ViewSettings view = ViewSettings.Load();
        view = view with { FovDeg = Number(args, "--fov", view.FovDeg) };
        float after = Number(args, "--after", 0);
        DisplayOptics optics = DisplayOptics.OnePro with { HorizontalFovDeg = view.FovDeg };

        Quaternion head;
        string status;
        if (after > 0)
        {
            using var cts = new CancellationTokenSource();
            HeadTracker tracker = TrackingConfig.CreateTracker();
            GlassesConnection link = NewLink(args);
            link.Sample += s => tracker.Update(s);
            Thread reader = link.Start(cts.Token);

            var wait = Stopwatch.StartNew();
            while (!tracker.HasPose && wait.Elapsed.TotalSeconds < 8) Thread.Sleep(20);
            if (!tracker.HasPose)
            {
                Console.WriteLine("No head-motion data.");
                return 2;
            }
            tracker.GetPose(); // straight ahead = where the head starts
            Thread.Sleep((int)(after * 1000));
            HeadPose pose = tracker.GetPose();
            cts.Cancel();
            reader.Join(3000);

            head = pose.Orientation;
            status = $"tracked   yaw {pose.YawDeg:F1}  pitch {pose.PitchDeg:F1}  roll {pose.RollDeg:F1}";
        }
        else
        {
            float yaw = Number(args, "--yaw", 0), pitch = Number(args, "--pitch", 0), roll = Number(args, "--roll", 0);
            head = Quaternion.CreateFromYawPitchRoll(yaw * DegToRad, pitch * DegToRad, -roll * DegToRad);
            status = $"still frame   yaw {yaw:F1}  pitch {pitch:F1}  roll {roll:F1}";
        }

        List<DisplayInfo> displays = Displays.List();
        IReadOnlyList<DisplayInfo> sources = displays.Where(d => !d.IsGlasses).ToList();
        Layout layout = LayoutStore.WithScreensFor((LayoutStore.Load() ?? new Workspace()).Current.Layout, sources, optics);
        bool live = args.Contains("--capture");

        using var renderer = new Renderer(optics.Width, optics.Height, displays.FirstOrDefault(d => d.IsGlasses)?.DeviceName);
        using var scene = new SpaceScene(renderer, live);
        scene.Apply(layout, sources, live ? WindowFinder.Resolve(layout, new Dictionary<string, IntPtr>(), search: true) : null, optics);
        if (live)
        {
            // The first frame comes as soon as Windows has one; give it a moment.
            var waited = Stopwatch.StartNew();
            while (!scene.Update() && waited.Elapsed.TotalSeconds < 3) Thread.Sleep(20);
            status += scene.Updates > 0 ? $"   {scene.LiveCount} live" : "   no frame from the monitors";
        }
        scene.RulerVisible = view.Ruler || args.Contains("--ruler");
        scene.SetStatus($"{status}   {layout.Screens.Count} screens   view {view.FovDeg:F1}°");

        Quaternion trim = Quaternion.CreateFromYawPitchRoll(0, -view.LevelDeg * DegToRad, view.RollDeg * DegToRad);
        scene.Look(head * trim, false, false, 1f, Number(args, "--easy", view.EasyReading));
        scene.PointToHiddenScreens(head * trim, optics, view.EdgeArrows);
        if (args.Contains("--menu")) scene.SetMenu(["Hide all", "Recentre", "Put here", "Next profile", "Close"], 1);
        renderer.Draw(head * trim, optics);

        using Bitmap picture = renderer.Capture();
        SavePng(picture, path);
        if (scene.LastProblem is not null) Console.WriteLine(scene.LastProblem);
        Console.WriteLine($"{status} -> {path}{(renderer.Software ? " (software renderer)" : "")}");
        return 0;
    }

    /// <summary>Draws the settings window as it would open, for checking its look without a person at the screen.</summary>
    static int SettingsShot(string path, string? page, bool stationary)
    {
        ViewSettings view = ViewSettings.Load();
        DisplayOptics optics = DisplayOptics.OnePro with { HorizontalFovDeg = view.FovDeg };
        IReadOnlyList<DisplayInfo> sources = Displays.List().Where(d => !d.IsGlasses).ToList();
        Workspace workspace = LayoutStore.Load() ?? new Workspace();
        Layout layout = LayoutStore.WithScreensFor(workspace.Current.Layout, sources, optics);

        using var settings = new SettingsWindow(layout, view, sources, "Ctrl+Alt+0") { HideOnClose = false };
        settings.SetProfiles(workspace.Profiles.Select(p => p.Name).ToList(), workspace.Active);
        Rectangle home = (sources.FirstOrDefault(d => d.Primary) ?? sources[0]).Bounds;
        settings.Location = new Point(home.Left + 40, home.Top + 40);
        settings.Show();
        settings.SetGlassesMode(stationary);
        settings.ShowPageNamed(page ?? "screen");
        Application.DoEvents();
        using var picture = new Bitmap(settings.Width, settings.Height);
        settings.DrawToBitmap(picture, new Rectangle(Point.Empty, settings.Size));
        settings.Close();
        SavePng(picture, path);
        Console.WriteLine($"Settings window {picture.Width}x{picture.Height} -> {path}");
        return 0;
    }

    static void SavePng(Bitmap picture, string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null) Directory.CreateDirectory(folder);
        picture.Save(path, ImageFormat.Png);
    }

    // ---- helpers ------------------------------------------------------------------------

    public static GlassesConnection NewLink(string[] args) => new()
    {
        Host = Option(args, "--host") ?? GlassesConnection.DefaultHost,
        Port = int.TryParse(Option(args, "--port"), out int port) ? port : GlassesConnection.ImuPort,
    };

    public static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static float Number(string[] args, string name, float fallback) =>
        float.TryParse(Option(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
}
