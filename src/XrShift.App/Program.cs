using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
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
        if (args.Contains("--bot-hook")) return BotHook.Run(); // before anything that could print or open a window
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
        if (Option(args, "--settings-shot") is { } settingsShot) return SettingsShot(settingsShot, Option(args, "--page"), args.Contains("--stationary"), args);

        string? shot = Option(args, "--shot");
        if (shot is not null) return Shot(shot, args);
        if (Option(args, "--scenario") is { } scenario) return Scenario(scenario, Option(args, "--out") ?? "shots", args.Contains("--software"));
        if (Option(args, "--replay-capture") is { } imu) return ReplayCapture(imu, args);

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
              --no-keys                  take no keys and never the keyboard: for a trial driven by a script (--api) while someone types
              --api                      let other programs on this PC list the scene, drive bots and switch layouts (pipe ArWorkspace.Api);
                                         the tray menu remembers this choice for every start
              --stereo                   start in 3D: a picture per eye, side by side, for the glasses' 3D mode (Ctrl+Alt+D switches)
              --bot-hook                for a Claude Code hook: pass the hook event on standard input to the running app and exit
              --free-cursor            let the mouse pointer wander onto the glasses' own desktop area
              --keep-modes               leave virtual monitors' resolution and refresh rate alone
              --fov D                    horizontal view angle of the glasses in degrees (default 50.7, or what you last tuned)
              --delay MS                 how long the glasses take to show a frame, in milliseconds (default 16.5, or what you last tuned)
              --lead MS                  experiment: read the head pose this long before each screen refresh (default off; costs missed frames)
              --host H --port N          read head motion from somewhere else (e.g. 'xrprobe serve')
              --seconds S                close by itself after S seconds
              --shot FILE.png            draw one frame of the saved layout to a picture and exit (add --capture for live monitors), with either
                --yaw D --pitch D --roll D     a head pose (left, up and tilt-right are positive), or
                --after S                      the tracked pose after S seconds of head motion
                --layout FILE.json             a layout file instead of the saved layout and this PC's monitors
                --deck FILE.json               bots: [{"id","kind","label","state","pc","note","offline","yaw","pitch"}]
                --notice "text"                the status line says this
                --stereo                       3D side by side
                --time S                       the moment the bots' pulsing is drawn at (default 0)
                --software                     draw with Windows' software renderer
              --scenario FILE.json --out DIR   draw every frame a scenario lists to DIR\<name>.png, plus DIR\index.json
              --replay-capture IMU --out DIR   run the head tracker over a recording ('xrprobe dump' or 'synth') and save the
                --seconds S --every MS         frame the wearer sees every MS ms for S seconds, with DIR\poses.csv
                --predict MS --layout F --deck F   (prediction default 25 ms; layout default one test screen ahead)
              --settings-shot FILE.png   save a picture of the settings window and exit (--page home|welcome|screen|follow|stationary|hands|games|bots|voice|pcs|settings|setup|advanced|keys, --stationary, --sample-bots)
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

    /// <summary>Exit code when not even Windows' software renderer could be started: picture tests skip on it.</summary>
    const int NoGraphics = 4;

    static int Shot(string path, string[] args)
    {
        string? layoutFile = Option(args, "--layout");
        // From a layout file, nothing about this PC (its monitors, its tuned settings) goes into the picture.
        ViewSettings view = layoutFile is null ? ViewSettings.Load() : ViewSettings.Default;
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
            head = HeadAt(yaw, pitch, roll);
            status = StillStatus(yaw, pitch, roll);
        }

        Layout layout;
        IReadOnlyList<DisplayInfo> sources;
        string? glasses = null;
        string? why = null;
        if (layoutFile is not null)
        {
            if (!TryReadLayout(layoutFile, out layout, out why)) { Console.WriteLine(why); return 2; }
            sources = [];
        }
        else
        {
            List<DisplayInfo> displays = Displays.List();
            sources = displays.Where(d => !d.IsGlasses).ToList();
            layout = LayoutStore.WithScreensFor((LayoutStore.Load() ?? new Workspace()).Current.Layout, sources, optics, out IReadOnlyList<string> hidden);
            if (hidden.Count > 0) Console.WriteLine($"No room within reach; added hidden: {string.Join(", ", hidden)}.");
            glasses = displays.FirstOrDefault(d => d.IsGlasses)?.DeviceName;
        }
        Deck deck = new();
        if (Option(args, "--deck") is { } deckFile && !TryReadDeck(deckFile, out deck, out why)) { Console.WriteLine(why); return 2; }
        bool live = args.Contains("--capture");

        if (!TryRenderer(optics, glasses, args.Contains("--software"), out Renderer? made)) return NoGraphics;
        using Renderer renderer = made!;
        using var scene = new SpaceScene(renderer, live);
        scene.Apply(layout, sources, live ? WindowFinder.Resolve(layout, new Dictionary<string, IntPtr>(), search: true) : null, optics);
        scene.Apply(deck);
        if (live)
        {
            // The first frame comes as soon as Windows has one; give it a moment.
            var waited = Stopwatch.StartNew();
            while (!scene.Update() && waited.Elapsed.TotalSeconds < 3) Thread.Sleep(20);
            status += scene.Updates > 0 ? $"   {scene.LiveCount} live" : "   no frame from the monitors";
        }

        var frame = new ShotFrame
        {
            Notice = Option(args, "--notice"),
            Stereo = args.Contains("--stereo"),
            Time = Number(args, "--time", 0),
            Menu = args.Contains("--menu"),
            Ruler = view.Ruler || args.Contains("--ruler"),
        };
        view = view with { EasyReading = Number(args, "--easy", view.EasyReading) };
        using Bitmap picture = DrawFrame(renderer, scene, head, optics, view, frame, $"{status}   {layout.Screens.Count} screens   view {view.FovDeg:F1}°");
        SavePng(picture, path);
        if (scene.LastProblem is not null) Console.WriteLine(scene.LastProblem);
        Console.WriteLine($"{status} -> {path}{(renderer.Software ? " (software renderer)" : "")}");
        return 0;
    }

    /// <summary>
    /// One frame of a scenario, or the extras of a single shot. Everything that changes the picture is given here,
    /// so the same frame always gives the same pixels. Paths are relative to the scenario file.
    /// </summary>
    sealed record ShotFrame
    {
        public string Name { get; init; } = "";
        public string? Layout { get; init; }
        public string? Deck { get; init; }
        public float Yaw { get; init; }
        public float Pitch { get; init; }
        public float Roll { get; init; }
        /// <summary>What the status line says instead of describing the frame.</summary>
        public string? Notice { get; init; }
        public bool Stereo { get; init; }
        /// <summary>The moment, in seconds, at which pulsing bots are drawn.</summary>
        public float Time { get; init; }
        public bool Menu { get; init; }
        public bool Ruler { get; init; }
        /// <summary>Spots to point the eye to: the mouse pointer, a window that just opened.</summary>
        public List<MarkEntry> Marks { get; init; } = [];
        /// <summary>Glasses setup's guide, given as data: then only the guide is drawn over the room, as in the app.</summary>
        public GuideEntry? Guide { get; init; }
    }

    /// <summary>
    /// One moment of glasses setup: the words, and the lines and crosses in display pixels from the middle of the view
    /// (x right, y up), or with world set in room degrees (x = yaw left, y = pitch up).
    /// </summary>
    sealed record GuideEntry
    {
        public string Instruction { get; init; } = "";
        public string Progress { get; init; } = "";
        public string? Note { get; init; }
        public List<string> Results { get; init; } = [];
        public List<GuideLineEntry> Lines { get; init; } = [];
        public List<float[]> Marks { get; init; } = [];

        public SetupScreen ToScreen()
        {
            static Vector2 V(float[] p) => p.Length == 2 ? new Vector2(p[0], p[1]) : throw new JsonException("A guide point is [x, y].");
            return new SetupScreen(
                new GuideDrawing(
                    [.. Lines.Select(l => new GuideLine(l.World ? GuideSpace.World : GuideSpace.Display, V(l.From), V(l.To)))],
                    [.. Marks.Select(m => new GuideMark(GuideSpace.Display, V(m)))]),
                Instruction, Progress, Note, Results);
        }
    }

    sealed record GuideLineEntry
    {
        public float[] From { get; init; } = [0, 0];
        public float[] To { get; init; } = [0, 0];
        public bool World { get; init; }
    }

    /// <summary>A mark on a screen of the layout: X and Y run 0 to 1 across its picture from the top left.</summary>
    sealed record MarkEntry
    {
        public string Screen { get; init; } = "";
        public float X { get; init; } = 0.5f;
        public float Y { get; init; } = 0.5f;
        public string Label { get; init; } = "";
        public bool Ring { get; init; }
    }

    sealed record ScenarioFile
    {
        public List<ShotFrame> Frames { get; init; } = [];
    }

    sealed record DeckEntry
    {
        public string? Id { get; init; }
        public string? Kind { get; init; }
        public string? Label { get; init; }
        public string? State { get; init; }
        public string? Pc { get; init; }
        public string? Note { get; init; }
        public bool Offline { get; init; }
        public float? Yaw { get; init; }
        public float? Pitch { get; init; }
        public float? Distance { get; init; }
    }

    static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static Quaternion HeadAt(float yaw, float pitch, float roll) =>
        Quaternion.CreateFromYawPitchRoll(yaw * DegToRad, pitch * DegToRad, -roll * DegToRad);

    static string StillStatus(float yaw, float pitch, float roll) => $"still frame   yaw {yaw:F1}  pitch {pitch:F1}  roll {roll:F1}";

    /// <summary>Draws everything the wearer sees from this head direction and reads it back as a picture.</summary>
    static Bitmap DrawFrame(Renderer renderer, SpaceScene scene, Quaternion head, DisplayOptics optics, ViewSettings view, ShotFrame frame, string status, float seconds = 1f)
    {
        SetupScreen? guide = frame.Guide?.ToScreen();
        scene.RulerVisible = frame.Ruler && guide is null;
        scene.Stereo = frame.Stereo;
        scene.SetNotice(guide is null ? frame.Notice ?? "" : "", SpaceScene.GuessNoticeLevel(frame.Notice ?? ""));
        scene.SetStatus(frame.Notice is null ? status : "");
        scene.OverlayVisible = guide is null;
        Quaternion trim = Quaternion.CreateFromYawPitchRoll(0, -view.LevelDeg * DegToRad, view.RollDeg * DegToRad);
        // During setup only the guide shows over the room, as in the app: screens, bots and arrows are hidden.
        scene.Look(head * trim, false, guide is not null, seconds, view.EasyReading, clock: frame.Time);
        scene.SetGuide(guide, head * trim, optics);
        scene.PointToHiddenScreens(head * trim, optics, view.EdgeArrows);
        scene.SetMenu(frame.Menu ? ["Hide all", "Recentre", "Put here", "Next profile", "Close"] : null, 1);
        renderer.Draw(head * trim, optics, frame.Stereo ? Camera.DefaultEyeSeparation : 0);
        return renderer.Capture();
    }

    static bool TryRenderer(DisplayOptics optics, string? monitor, bool software, out Renderer? renderer)
    {
        try
        {
            renderer = new Renderer(optics.Width, optics.Height, monitor, software);
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"No Direct3D device could be made, not even Windows' software one: {e.Message}");
            renderer = null;
            return false;
        }
    }

    /// <summary>A layout file as the app saves it (all profiles, the one in use is drawn) or a single arrangement.</summary>
    static bool TryReadLayout(string path, out Layout layout, out string why)
    {
        layout = new Layout();
        why = "";
        try
        {
            if (Workspace.TryParse(File.ReadAllText(path), out Workspace workspace))
            {
                layout = workspace.Current.Layout;
                return true;
            }
            why = $"{path} is not a layout file.";
        }
        catch (IOException e) { why = $"Could not read {path}: {e.Message}"; }
        return false;
    }

    /// <summary>Bots from a list in a file. Ones without a place line up as the app lines them up.</summary>
    static bool TryReadDeck(string path, out Deck deck, out string why)
    {
        deck = new Deck();
        why = "";
        List<DeckEntry>? entries;
        try { entries = JsonSerializer.Deserialize<List<DeckEntry>>(File.ReadAllText(path), Lenient); }
        catch (Exception e) when (e is IOException or JsonException) { why = $"Could not read {path}: {e.Message}"; return false; }
        int n = 0;
        foreach (DeckEntry e in entries ?? [])
        {
            n++;
            if ((e.State is null ? BotState.Idle : SceneObject.ParseState(e.State)) is not { } state)
            {
                why = $"{path}: bot {n} has state '{e.State}'; use idle, working, needs_you, done or failed.";
                return false;
            }
            bool placed = e.Yaw is not null || e.Pitch is not null;
            deck = deck.With(new SceneObject
            {
                Id = e.Id ?? $"bot{n}", // never the random default: the same file must give the same picture
                Kind = e.Kind ?? "bot",
                Label = e.Label ?? "Bot",
                State = state,
                Pc = e.Pc ?? "",
                Note = e.Note ?? "",
                Offline = e.Offline,
                YawDeg = e.Yaw ?? 0,
                PitchDeg = e.Pitch ?? 0,
                Distance = e.Distance ?? 1.5f,
                Placed = placed,
            });
        }
        return true;
    }

    // ---- many frames to files -------------------------------------------------------------

    /// <summary>
    /// Draws every frame a scenario file lists, each from scratch so no frame depends on the one before, into
    /// <paramref name="outFolder"/> as name.png, with index.json saying what each one is. Touches no monitor,
    /// no glasses and no saved settings.
    /// </summary>
    static int Scenario(string scenarioPath, string outFolder, bool software)
    {
        ScenarioFile? scenario;
        try { scenario = JsonSerializer.Deserialize<ScenarioFile>(File.ReadAllText(scenarioPath), Lenient); }
        catch (Exception e) when (e is IOException or JsonException) { Console.WriteLine($"Could not read {scenarioPath}: {e.Message}"); return 2; }
        if (scenario is null || scenario.Frames.Count == 0) { Console.WriteLine($"{scenarioPath} lists no frames."); return 2; }
        string home = Path.GetDirectoryName(Path.GetFullPath(scenarioPath))!;
        string? Near(string? file) => file is null ? null : Path.GetFullPath(Path.Combine(home, file));

        Directory.CreateDirectory(outFolder);
        ViewSettings view = ViewSettings.Default;
        DisplayOptics optics = DisplayOptics.OnePro with { HorizontalFovDeg = view.FovDeg };
        var index = new List<object>();
        string adapter = "";
        bool usedSoftware = false;
        foreach (ShotFrame frame in scenario.Frames)
        {
            if (frame.Name.Length == 0 || frame.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || frame.Name.Contains(' '))
            {
                Console.WriteLine($"Frame name '{frame.Name}' must be a plain file name without spaces.");
                return 2;
            }
            Layout layout = new();
            Deck deck = new();
            if (Near(frame.Layout) is { } layoutFile && !TryReadLayout(layoutFile, out layout, out string why)) { Console.WriteLine(why); return 2; }
            if (Near(frame.Deck) is { } deckFile && !TryReadDeck(deckFile, out deck, out why)) { Console.WriteLine(why); return 2; }

            if (!TryRenderer(optics, null, software, out Renderer? made)) return NoGraphics;
            using Renderer renderer = made!;
            using var scene = new SpaceScene(renderer, live: false);
            scene.Apply(layout, [], null, optics);
            scene.Apply(deck);
            var marks = new List<Mark>();
            foreach (MarkEntry m in frame.Marks)
            {
                // Stand-in screens are drawn 1920x1080.
                if (layout.Find(m.Screen) is not { } on) { Console.WriteLine($"Frame {frame.Name}: no screen '{m.Screen}' for a mark."); return 2; }
                if (RoomSpot.Of(new System.Drawing.Point((int)(m.X * 1920), (int)(m.Y * 1080)), [(on, new System.Drawing.Rectangle(0, 0, 1920, 1080))], optics) is { } at)
                    marks.Add(new Mark(at, m.Label, m.Ring));
            }
            scene.SetMarks(marks);
            using Bitmap picture = DrawFrame(renderer, scene, HeadAt(frame.Yaw, frame.Pitch, frame.Roll), optics, view, frame,
                $"{StillStatus(frame.Yaw, frame.Pitch, frame.Roll)}   {layout.Screens.Count} screens   view {view.FovDeg:F1}°");
            string file = frame.Name + ".png";
            SavePng(picture, Path.Combine(outFolder, file));
            adapter = renderer.AdapterName;
            usedSoftware = renderer.Software;
            index.Add(new { frame.Name, File = file, frame.Yaw, frame.Pitch, frame.Roll, frame.Stereo, frame.Notice, frame.Time, frame.Layout, frame.Deck });
            Console.WriteLine($"{frame.Name} -> {Path.Combine(outFolder, file)}");
        }
        File.WriteAllText(Path.Combine(outFolder, "index.json"), JsonSerializer.Serialize(
            new { Scenario = Path.GetFileName(scenarioPath), Adapter = adapter, Software = usedSoftware, Frames = index },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// Runs the head tracker over a recorded (or made-up) stream of motion samples, in this process at no particular
    /// speed, and saves the frame the wearer would see every <c>--every</c> milliseconds of recording time, plus
    /// poses.csv. Flipping through the frames shows any bounce or drift the tracker adds.
    /// </summary>
    static int ReplayCapture(string imuPath, string[] args)
    {
        string outFolder = Option(args, "--out") ?? "replay";
        float seconds = Number(args, "--seconds", 10), everyMs = Number(args, "--every", 100), predictMs = Number(args, "--predict", 25);
        if (!(seconds > 0) || !(everyMs >= 1)) { Console.WriteLine("--seconds must be above 0 and --every at least 1."); return 2; }

        var samples = new List<ImuSample>();
        try { new ImuFrameParser().Feed(File.ReadAllBytes(imuPath), samples.Add); }
        catch (IOException e) { Console.WriteLine($"Could not read {imuPath}: {e.Message}"); return 2; }
        if (samples.Count < 2) { Console.WriteLine($"No head-motion records in {imuPath}."); return 2; }

        Layout layout = new Layout().Add(new ScreenSpec { Id = "ahead", Name = "Screen" });
        Deck deck = new();
        if (Option(args, "--layout") is { } layoutFile && !TryReadLayout(layoutFile, out layout, out string why)) { Console.WriteLine(why); return 2; }
        if (Option(args, "--deck") is { } deckFile && !TryReadDeck(deckFile, out deck, out why)) { Console.WriteLine(why); return 2; }

        ViewSettings view = ViewSettings.Default;
        DisplayOptics optics = DisplayOptics.OnePro with { HorizontalFovDeg = view.FovDeg };
        if (!TryRenderer(optics, null, args.Contains("--software"), out Renderer? made)) return NoGraphics;
        using Renderer renderer = made!;
        using var scene = new SpaceScene(renderer, live: false);
        scene.Apply(layout, [], null, optics);
        scene.Apply(deck);
        Directory.CreateDirectory(outFolder);

        HeadTracker tracker = TrackingConfig.CreateTracker();
        var csv = new System.Text.StringBuilder("time_s,yaw_deg,pitch_deg,roll_deg\n");
        ulong start = samples[0].TimestampNs;
        double next = 0, step = everyMs / 1000.0;
        bool centred = false;
        int count = 0;
        foreach (ImuSample sample in samples)
        {
            tracker.Update(sample);
            double t = (sample.TimestampNs - start) * 1e-9;
            if (t > seconds) break;
            if (!tracker.HasPose || t < next) continue;
            if (!centred) { tracker.GetPose(); centred = true; } // straight ahead = where the head starts, as in the app
            while (next <= t) next += step;

            HeadPose pose = tracker.GetPose(predictMs / 1000f);
            var frame = new ShotFrame { Time = (float)t };
            using Bitmap picture = DrawFrame(renderer, scene, pose.Orientation, optics, view, frame,
                $"replay  t {t:F2} s   yaw {pose.YawDeg:F1}  pitch {pose.PitchDeg:F1}  roll {pose.RollDeg:F1}", (float)step);
            SavePng(picture, Path.Combine(outFolder, $"{count:D4}.png"));
            csv.Append(string.Create(CultureInfo.InvariantCulture, $"{t:F3},{pose.YawDeg:F3},{pose.PitchDeg:F3},{pose.RollDeg:F3}\n"));
            count++;
        }
        File.WriteAllText(Path.Combine(outFolder, "poses.csv"), csv.ToString());
        Console.WriteLine($"{count} frames and poses.csv -> {outFolder}{(renderer.Software ? " (software renderer)" : "")}");
        return count > 0 ? 0 : 2;
    }

    /// <summary>Draws the settings window as it would open, for checking its look without a person at the screen.</summary>
    /// <param name="page">home, welcome, screen, follow, stationary, hands, games, bots, voice, pcs, settings, advanced or keys.</param>
    static int SettingsShot(string path, string? page, bool stationary, string[] args)
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
        // Claude Code's settings are read from the settings folder's copy, never the real one (see ClaudeSettingsPath).
        settings.ClaudeSettingsPath = Path.Combine(TrackingConfig.Folder, "claude-settings.json");
        if (args.Contains("--sample-bots"))
        {
            // A few made-up bots, to see the Bots page and the status with some in it.
            Deck sample = new Deck()
                .Report("a", "UserPromptSubmit", "invoice-export", 1, note: "Fix the rounding in the monthly export")
                .Report("b", "UserPromptSubmit", "site-redesign", 2)
                .Report("b", "Notification", note: "May I run the database migration?")
                .Report("c", "Stop", "notes", null, "DESK-PC", DateTime.UtcNow, "Summarised the meeting");
            settings.BotsSource = () => sample;
            settings.SetLinks(apiRunning: true, netRunning: false);
        }
        settings.Show();
        settings.SetGlassesMode(stationary);
        string name = page ?? "screen";
        if (name != "welcome") settings.HideGuide();
        settings.ShowPageNamed(name);
        Application.DoEvents();
        using var picture = new Bitmap(settings.Width, settings.Height);
        settings.DrawToBitmap(picture, new Rectangle(Point.Empty, settings.Size));
        if (name == "keys")
        {
            // The key reference is a box of its own: drawn instead of the window.
            using Form keys = settings.KeysForm();
            keys.StartPosition = FormStartPosition.Manual;
            keys.Location = settings.Location;
            keys.Show(settings);
            Application.DoEvents();
            using var box = new Bitmap(keys.Width, keys.Height);
            keys.DrawToBitmap(box, new Rectangle(Point.Empty, keys.Size));
            keys.Close();
            settings.Close();
            SavePng(box, path);
            Console.WriteLine($"Keys {box.Width}x{box.Height} -> {path}");
            return 0;
        }
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
