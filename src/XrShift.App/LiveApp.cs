using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using XrShift.Scene;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>The running app: the scene on the glasses, the settings window on the PC, and what ties them together.</summary>
internal static class LiveApp
{
    const float DegToRad = MathF.PI / 180f;

    /// <summary>What the drawing thread should show. Replaced whole by the window thread; never changed in place.</summary>
    sealed record Snapshot(Layout Layout, IReadOnlyList<DisplayInfo> Sources, IReadOnlyDictionary<string, IntPtr> Windows, ViewSettings View, bool StatusPinned, bool Blank, Deck Deck, bool Stereo);

    sealed class Shared
    {
        public volatile Snapshot Snapshot = null!;
        /// <summary>How far the screens have drifted after the head (radians, + = left). Written by the drawing thread.</summary>
        public float FollowYaw;
        /// <summary>Gesture mode while the glasses are in Anchor mode: they hold their picture still in the room themselves, so the scene must stop following the head.</summary>
        public volatile bool HoldHead;
        public volatile bool HandsShown, HandsPinching;
        public volatile HandFrame? HandsFrame;
        public volatile string HandsStatus = "";
        /// <summary>The settings window wants a small picture of what the glasses show (written by the window thread).</summary>
        public volatile bool PreviewWanted;
        /// <summary>The latest such picture, handed from the drawing thread to the window thread.</summary>
        public Bitmap? Preview;
        /// <summary>Stationary mode: the room direction (degrees, yaw + = left, pitch + = up) the glasses' held picture faces.</summary>
        public volatile float HeldYaw, HeldPitch;
        /// <summary>The hand quick menu's buttons while it is open (null when closed), and the lit one.</summary>
        public volatile string[]? MenuItems;
        public volatile int MenuHighlight;
        /// <summary>Pictures per second from each copied screen, measured on the drawing thread.</summary>
        public volatile string CaptureRates = "";
        /// <summary>Spots to point the wearer's eye to (the mouse pointer, a window that just opened). Written by the window thread.</summary>
        public volatile Mark[] Marks = [];
        /// <summary>Glasses setup while it runs (null otherwise). Fed samples on the reader thread: always used under its own lock.</summary>
        public volatile SetupRun? Calibration;
        /// <summary>What setup shows in the glasses now; while set, nothing else is drawn. Written by the window thread.</summary>
        public volatile SetupScreen? Guide;
    }

    static bool s_unplugged;

    /// <summary>A real error in a window or timer of this thread, waiting to be thrown once its message loop has ended.</summary>
    static Exception? s_windowError;
    static bool s_staleCallNoted;

    /// <summary>
    /// Every error from a window or timer comes here. A stale cross-thread call (Windows completing a call for a
    /// window of a session that has already ended) is harmless and ignored; anything else ends the message loop
    /// and is thrown from there, so the session's clean-up and restart run as before.
    /// </summary>
    public static void WindowError(Exception e)
    {
        if (e is ObjectDisposedException && (e.StackTrace ?? "").Contains("ThreadMethodEntry", StringComparison.Ordinal))
        {
            if (!s_staleCallNoted) Log.Line("Ignored a stale call from an earlier session's window.");
            s_staleCallNoted = true;
            return;
        }
        s_windowError ??= e;
        Application.ExitThread();
    }

    static void ThrowWindowError()
    {
        Exception? e = s_windowError;
        s_windowError = null;
        if (e is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);
    }

    /// <summary>The PC's own screens (not the glasses, not virtual monitors) when the app started, by device name.</summary>
    static readonly HashSet<string> s_realScreens = new(StringComparer.OrdinalIgnoreCase);
    static bool s_live;

    /// <summary>
    /// Nothing may be left where nobody can see it: windows sitting on the glasses' part of the desktop (under the
    /// scene while it runs, or left there by Windows when virtual monitors go away) are moved to the PC's main
    /// screen, and if one of the PC's own screens has gone dark, Windows is asked to extend the desktop again
    /// (what Win+P > Extend does). Only in the tray app, never in a developer's trial run.
    /// </summary>
    static void KeepWindowsOnRealScreens(string why)
    {
        if (!s_live) return;
        try
        {
            List<DisplayInfo> now = Displays.List();
            if (s_realScreens.Count == 0)
                foreach (DisplayInfo d in now.Where(d => !d.IsGlasses && !VirtualMonitors.IsVirtual(d))) s_realScreens.Add(string.Join("+", d.MonitorNames));
            if (s_realScreens.Count > 0 && !s_realScreens.All(n => now.Any(d => string.Equals(string.Join("+", d.MonitorNames), n, StringComparison.OrdinalIgnoreCase))))
            {
                Log.Line($"{why}: one of the PC's own screens was off; asking Windows to extend the desktop again.");
                Process.Start(new ProcessStartInfo("DisplaySwitch.exe", "/extend") { UseShellExecute = true })?.WaitForExit(5000);
                Thread.Sleep(1500);
                now = Displays.List();
            }
            DisplayInfo? home = now.FirstOrDefault(d => d.Primary && !d.IsGlasses && !VirtualMonitors.IsVirtual(d))
                ?? now.FirstOrDefault(d => !d.IsGlasses && !VirtualMonitors.IsVirtual(d));
            if (home is null) return;
            int moved = 0;
            foreach (DisplayInfo glasses in now.Where(d => d.IsGlasses))
                moved += WindowFinder.MoveAll(glasses.Bounds, Screen.FromRectangle(home.Bounds).WorkingArea);
            if (moved > 0) Log.Line($"{why}: moved {moved} window(s) from the glasses to the PC's screen.");
        }
        catch (Exception e)
        {
            Log.Line($"{why}: could not check the PC's screens ({e.Message}).");
        }
    }
    /// <summary>The session that just ended created virtual monitors and expects them taken away.</summary>
    static bool s_ownsMonitors;

    /// <summary>A file in the settings folder that says the Control API should be on, as --api does for one run.</summary>
    internal static string ApiFlag => Path.Combine(TrackingConfig.Folder, "api-on");

    /// <summary>What to do when the app is started a second time: the running copy shows itself.</summary>
    static volatile Action? s_show;
    static readonly EventWaitHandle s_showSignal = new(false, EventResetMode.AutoReset, @"Local\XrShift.Show");
    static RegisteredWaitHandle? s_showWait;

    /// <summary>
    /// Runs the app. Normally it lives in the tray: with no glasses it waits, when they are plugged in it
    /// brings the screens up, and when they are unplugged it tidies up and waits again. Asked for a window,
    /// a particular monitor or a time limit, it runs one session and ends, as a developer needs.
    /// </summary>
    public static int Run(string[] args)
    {
        bool once = args.Contains("--window") || Program.Option(args, "--monitor") is not null || Program.Number(args, "--seconds", 0) > 0;
        s_showWait = ThreadPool.RegisterWaitForSingleObject(s_showSignal, (_, _) => s_show?.Invoke(), null, -1, executeOnlyOnce: false);
        var crashes = new List<DateTime>();
        while (true)
        {
            if (!once && !GlassesReady() && !WaitForGlasses(args)) return 0;
            s_unplugged = false;
            s_live = !once;
            KeepWindowsOnRealScreens("Start");
            int code;
            try
            {
                code = RunSession(args);
            }
            catch (Exception e) when (!once)
            {
                // Something went wrong mid-session (the glasses restarting while plugged in, say). Tidy up and
                // go back to waiting, so the PC is never left with screens nobody can see. Three in two minutes
                // is a real fault: stop and say so.
                Log.Line("The session stopped because of an error:");
                Log.Line(e.ToString());
                Cleanup(args);
                KeepWindowsOnRealScreens("After the error");
                crashes.Add(DateTime.Now);
                crashes.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(2));
                if (crashes.Count >= 3) throw;
                Log.Line("Starting again when the glasses are ready.");
                Thread.Sleep(3000);
                continue;
            }
            if (s_ownsMonitors) Cleanup(args);
            if (once || !s_unplugged)
            {
                KeepWindowsOnRealScreens("Quit");
                return code;
            }
            Log.Line("Glasses unplugged. Waiting for them to come back.");
        }
    }

    /// <summary>
    /// No virtual monitors left behind, and the mouse pointer free. Called once a session is completely torn down
    /// (or failed), never while its window, drawing device or monitor copies still exist. It waits until Windows
    /// has really let the monitors go, so the app never exits in the middle of that.
    /// </summary>
    static void Cleanup(string[] args)
    {
        s_ownsMonitors = false;
        try { ViewWindow.ConfineCursor(null); } catch (Exception) { }
        if (args.Contains("--keep-monitors")) return;
        try
        {
            if (!VirtualMonitors.Installed || (VirtualMonitors.Count() ?? 0) == 0) return;
            // A moment for Windows to finish with the scene's window and the copies just released.
            Thread.Sleep(300);
            var clock = Stopwatch.StartNew();
            if (!VirtualMonitors.TrySetCount(0, out string why))
            {
                Log.Line($"Could not remove the virtual monitors ({why}). The 'XrShift - reset screens' shortcut removes them.");
                return;
            }
            while (clock.ElapsedMilliseconds < 8000 && Displays.List().Any(VirtualMonitors.IsVirtual)) Thread.Sleep(250);
            bool gone = !Displays.List().Any(VirtualMonitors.IsVirtual);
            Log.Line(gone
                ? $"Virtual monitors removed ({clock.ElapsedMilliseconds} ms); any windows on them are back on your real screens."
                : "Asked for the virtual monitors to be removed; Windows had not finished after 8 s.");
        }
        catch (Exception) { }
    }

    /// <summary>The glasses are plugged in and Windows gives them a screen of their own (Extend, not Duplicate).</summary>
    static bool GlassesReady() => Displays.List().Any(d => d.IsGlasses && !d.Mirrored) || DirectDisplay.GlassesOffDesktop();

    /// <summary>Sits in the tray until the glasses are ready to use. False if the user chose to quit instead.</summary>
    static bool WaitForGlasses(string[] args)
    {
        Log.Line("Waiting for the glasses. Plug them in, or quit from the XrShift icon in the tray.");
        // Without the glasses nobody can see a virtual monitor: there must be none, whatever happened before
        // (a crash, a forced shutdown). Windows on them come back to the real screens.
        Cleanup(args);
        bool quit = false, asked = false;
        using var context = new ApplicationContext();
        using var tray = new NotifyIcon { Icon = AppIcon.Value, Text = "XrShift: waiting for the glasses", Visible = true };
        // Menus are disposed with the loop: a menu left alive keeps listening for Windows' display messages
        // through a loop that has ended, which is what crashed the next session.
        using var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Waiting for the glasses...") { Enabled = false });
        menu.Items.Add("Quit", null, (_, _) => { quit = true; context.ExitThread(); });
        tray.ContextMenuStrip = menu;
        // Say plainly that XrShift is still here, waiting, and how to quit it.
        tray.ShowBalloonTip(6000, "XrShift is waiting for your glasses", "Plug them in and your screens come up. To quit, right-click the XrShift icon by the clock.", ToolTipIcon.Info);
        var invoker = new Control();
        invoker.CreateControl();
        s_show = () => invoker.BeginInvoke(() => tray.ShowBalloonTip(4000, "XrShift", "XrShift is already running. It brings your screens up when the glasses are plugged in.", ToolTipIcon.Info));
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            List<DisplayInfo> now = Displays.List();
            if (now.Any(d => d.IsGlasses && !d.Mirrored) || DirectDisplay.GlassesOffDesktop())
            {
                context.ExitThread();
                return;
            }
            if (asked || !now.Any(d => d.IsGlasses)) return;

            // Plugged in, but Windows is showing the same picture on the glasses as on the PC's screen.
            asked = true;
            timer.Stop();
            Log.Line("The glasses are duplicating the PC's screen; asking whether to switch Windows to Extend.");
            DialogResult answer = MessageBox.Show(
                "Windows is showing the same picture on the glasses as on your screen.\n\n" +
                "XrShift needs the glasses as a screen of their own. Switch Windows to Extend now?\n\n" +
                "(You can also do it yourself: press Win+P and choose Extend.)",
                "XrShift", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("DisplaySwitch.exe", "/extend") { UseShellExecute = true });
                    Log.Line("Asked Windows to extend the desktop onto the glasses.");
                }
                catch (Exception e)
                {
                    Log.Line($"Could not switch to Extend: {e.Message}");
                }
            }
            tray.Text = "XrShift: waiting for Extend (Win+P)";
            timer.Start();
        };
        timer.Start();
        Application.Run(context);
        ThrowWindowError();
        timer.Stop();
        timer.Dispose();
        s_show = null;
        invoker.Dispose();
        tray.Visible = false;
        if (quit) return false;
        Thread.Sleep(2000); // give Windows a moment to finish setting the new monitor up
        return true;
    }

    static int RunSession(string[] args)
    {
        using var parts = new System.ComponentModel.Container(); // the session's timers: all disposed when it ends
        ViewSettings savedView = ViewSettings.Load();
        ViewSettings view = savedView with
        {
            FovDeg = Math.Clamp(Program.Number(args, "--fov", savedView.FovDeg), ViewSettings.MinFovDeg, ViewSettings.MaxFovDeg),
            DelayMs = Math.Clamp(Program.Number(args, "--delay", savedView.DelayMs), ViewSettings.MinDelayMs, ViewSettings.MaxDelayMs),
        };
        float seconds = Program.Number(args, "--seconds", 0);
        var clock = Stopwatch.StartNew();
        object gate = new();
        bool recenter = false;
        double overlayUntil = 12;

        // ---- where the scene goes ----------------------------------------------------------
        List<DisplayInfo> displays = Displays.List();
        DisplayInfo? target = Program.Option(args, "--monitor") is { } index && int.TryParse(index, out int n)
            ? displays.ElementAtOrDefault(n)
            : displays.FirstOrDefault(d => d.IsGlasses);

        // Direct mode: the glasses are off the Windows desktop and XrShift drives them itself.
        DirectDisplay? directOut = null;
        if (target is null && !args.Contains("--window") && Program.Option(args, "--monitor") is null)
        {
            directOut = DirectDisplay.TryOpen(120);
            Log.Line($"Direct mode: {DirectDisplay.Status}.");
        }
        using DirectDisplay? directLease = directOut;
        bool direct = directOut is not null;
        if (direct)
            target = new DisplayInfo("direct:glasses", "", [directOut!.Name], new Rectangle(-30000, -30000, 64, 64), false, directOut.RefreshHz);
        bool fullScreen = !args.Contains("--window") && target is not null;

        Rectangle area;
        if (fullScreen)
        {
            area = target!.Bounds;
            Log.Line($"Showing on {target}");
            if (target.IsGlasses && target.Mirrored)
                Log.Line("The glasses are duplicating another screen. It works, but Win+P -> Extend gives them their own 1920x1080.");
        }
        else
        {
            if (!args.Contains("--window")) Log.Line("Glasses not found among the monitors; opening a window instead.");
            Rectangle main = (displays.FirstOrDefault(d => d.Primary) ?? displays[0]).Bounds;
            area = new Rectangle(main.X + 60, main.Y + 60, 1280, 720);
        }

        // Every other monitor can be shown as a floating screen. In a window nothing is copied live:
        // a monitor showing a picture of itself would be a hall of mirrors.
        bool live = fullScreen && !args.Contains("--no-capture");
        string targetId = fullScreen ? target!.Id : "";
        IReadOnlyList<DisplayInfo> SourcesOf(List<DisplayInfo> all) =>
            all.Where(d => fullScreen ? d.Id != targetId : !d.IsGlasses).ToList();
        IReadOnlyList<DisplayInfo> sources = SourcesOf(displays);

        static DisplayOptics OpticsOf(ViewSettings v) => DisplayOptics.OnePro with { HorizontalFovDeg = v.FovDeg };
        // The app owns the virtual monitors: it creates the ones the profile in use needs, and takes them away
        // again in game mode and on quitting. Left in place they slow Windows down for everything (measured: 8 ms
        // from head pose to screen with none, about 20 with one, about 33 with three) and strand windows on
        // screens nobody can see.
        bool manageMonitors = fullScreen && !args.Contains("--keep-monitors");
        bool adding = false;        // the user just asked for one more virtual monitor
        string? removing = null;    // id of the screen the user asked to remove along with its virtual monitor
        // Stationary: the glasses are in their own stationary (Anchor) mode and hold their picture still in the room;
        // XrShift then keeps its head still and shows a separate set of profiles, arranged to fit in that picture.
        bool stationary = false;
        string linedUpFor = ""; // the set of monitors last lined up on the desktop (LineUpDesktops)
        Func<bool> layoutEmpty = () => true;
        // In stationary mode the picture the glasses hold still can be set larger than their view in the glasses'
        // own menu; the scene is then drawn for that angle, so a screen's place in degrees is its real direction.
        DisplayOptics OpticsFor(ViewSettings v) => stationary ? DisplayOptics.OnePro with { HorizontalFovDeg = v.HeldFovDeg } : OpticsOf(v);
        IReadOnlyList<DisplayInfo> AutoAddable() =>
            stationary
                // Stationary starts with the main screen only; a monitor the wearer has just added joins it too.
                ? (layoutEmpty() ? sources.Where(d => d.Primary).Take(1).ToList() : sources.Where(VirtualMonitors.IsVirtual).ToList())
                // Every extra monitor that exists gets a screen, so none sits on the desktop unseen.
                : sources.ToList();

        Workspace workspace = LayoutStore.Load() ?? new Workspace();
        layoutEmpty = () => workspace.Current.Layout.Screens.Count == 0;
        // Every monitor tells the desk, also in stationary mode where only some of them get a screen.
        Layout layout = LayoutStore.WithScreensFor(workspace.Current.Layout, AutoAddable(), OpticsFor(view), out IReadOnlyList<string> hiddenAtStart, sources);
        Action<IReadOnlyList<string>> tellHidden = _ => { };
        bool statusPinned = false, gameMode = false;
        bool dirty = !ReferenceEquals(layout, workspace.Current.Layout);
        workspace = workspace.WithLayout(layout);

        // Pinned windows that are open right now. Found here, on the window thread, never on the drawing thread.
        IReadOnlyDictionary<string, IntPtr> pinned = WindowFinder.Resolve(layout, new Dictionary<string, IntPtr>(), search: true);
        bool blank = false;
        bool stereo = args.Contains("--stereo"); // 3D: one picture per eye, side by side. Not saved: the app always starts sharp.
        Deck deck = new(); // bots, clusters and widgets: they live while the app runs and are not saved
        var shared = new Shared { Snapshot = new Snapshot(layout.Zoomed(view.Zoom), sources, pinned, view, statusPinned, blank, deck.Shown(view.BotNotes), stereo) };
        Action afterPublish = () => { };
        void Publish()
        {
            pinned = WindowFinder.Resolve(layout, pinned, search: false);
            shared.Snapshot = new Snapshot(layout.Zoomed(view.Zoom), sources, pinned, view, statusPinned, blank, deck.Shown(view.BotNotes), stereo);
            afterPublish();
        }

        // ---- head tracking -------------------------------------------------------------------
        using var cts = new CancellationTokenSource();
        HeadTracker tracker = TrackingConfig.CreateTracker();
        GlassesConnection link = Program.NewLink(args);
        // VR mode: every reading also goes to XrShift's SteamVR driver, a little ahead in time to cover SteamVR's
        // own drawing (see VrBridge).
        VrBridge? vrBridge = null;
        bool vrMode = false;
        link.Sample += s =>
        {
            tracker.Update(s);
            if (shared.Calibration is { } run) lock (run) run.Add(s); // the same samples the tracker gets
            if (vrBridge is { } vr && tracker.HasPose) vr.Publish(tracker.GetPose(0.012f).Orientation);
        };
        Thread reader = link.Start(cts.Token);

        // ---- the scene window ----------------------------------------------------------------
        // --no-keys: a trial driven by a script takes no keys and never the keyboard, so the person at the PC types on undisturbed.
        bool keys = !args.Contains("--no-keys");
        using var window = new ViewWindow(area, fullScreen, takeFocus: keys);
        if (direct)
        {
            // In direct mode this window only carries the keys and the message loop: out of sight, never in the way.
            window.ShowInTaskbar = false;
            window.Opacity = 0;
        }
        window.Show();
        if (fullScreen && window.Bounds != area) window.Bounds = area; // a DPI change on the way over can resize it

        DisplayInfo shownOn = fullScreen
            ? target!
            : displays.FirstOrDefault(d => d.Bounds.IntersectsWith(area)) ?? displays[0];
        using var renderer = direct
            ? new Renderer(directOut!.Width, directOut.Height, directOut.Adapter)
            : new Renderer(window.ClientSize.Width, window.ClientSize.Height, shownOn.DeviceName);
        if (live) MonitorCapture.RequestBorderless(); // on this thread, before the drawing thread starts copying monitors
        using var scene = new SpaceScene(renderer, live);
        using var presenter = direct ? new Presenter(renderer, directOut!) : new Presenter(renderer, window.Handle);
        if (!direct) presenter.RefreshMs = shownOn.RefreshHz > 1 ? 1000.0 / shownOn.RefreshHz : 0;
        presenter.LeadMs = Math.Clamp(Program.Number(args, "--lead", 0f), 0f, 8f);
        Log.Line($"Drawing {renderer.Width}x{renderer.Height} on {renderer.AdapterName}{(renderer.Software ? " (software renderer: no graphics card took it)" : "")}.");
        Log.Line($"Profile {workspace.Current.Name}: {layout.Screens.Count} screens ({string.Join(", ", layout.Screens.Select(s => s.Name))}).");

        // ---- keys ------------------------------------------------------------------------------
        // The keys and what they do are one list (Hotkeys), which the settings window's key reference shows too.
        // Recentre gets three tries, because other programs often hold one of them already.
        List<string> recentreNames = Hotkeys.All.Where(k => keys && k.Recentre && window.AddHotKey((Keys)k.Code)).Select(k => k.Name).ToList();
        List<string> taken = Hotkeys.All.Where(k => keys && !k.Recentre && !window.AddHotKey((Keys)k.Code)).Select(k => k.Name).ToList();
        string recentreKey = recentreNames.Count > 0 ? $"Ctrl+Alt+{recentreNames[0]}" : "Space";
        Log.Line("Keys, from any program: " + string.Join("; ", Hotkeys.Reference().Select(r => $"{r.Keys} {r.What}")) + ".");
        if (taken.Count > 0)
            Log.Line($"Another program already owns Ctrl+Alt+{string.Join(", Ctrl+Alt+", taken)}; those only work with the glasses' window in front.");

        // ---- settings window and tray ------------------------------------------------------------
        using var settings = new SettingsWindow(layout, view, sources, recentreKey);
        void ShowProfiles() => settings.SetProfiles(workspace.Profiles.Select(p => p.Name).ToList(), workspace.Active);
        ShowProfiles();
        IntPtr inFront = IntPtr.Zero; // the program that had the keyboard before the settings window took it
        void ShowSettings()
        {
            IntPtr front = WindowFinder.Foreground();
            if (front != IntPtr.Zero && front != settings.Handle && front != window.Handle) inFront = front;
            if (!settings.Visible)
            {
                Rectangle home = (sources.FirstOrDefault(d => d.Primary) ?? sources.FirstOrDefault() ?? shownOn).Bounds;
                // Inside the part of the screen the taskbar leaves free: the window is tall on a laptop.
                Rectangle free = Screen.FromRectangle(home).WorkingArea;
                settings.Location = new Point(Math.Max(free.Left, free.Right - settings.Width - 40), Math.Max(free.Top, free.Bottom - settings.Height - 16));
                settings.Show();
            }
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }

        Action relineDesktop = () => { };
        var saveTimer = new System.Windows.Forms.Timer(parts) { Interval = 700 };
        void Save()
        {
            saveTimer.Stop();
            if (!dirty) return;
            dirty = false;
            relineDesktop(); // a screen may have been moved to the other side of another
            try
            {
                LayoutStore.Save(workspace, stationary);
                view.Save();
            }
            catch (IOException e)
            {
                Log.Line($"Could not save settings: {e.Message}");
            }
        }
        saveTimer.Tick += (_, _) => Save();
        void Touch(bool showOverlay = false)
        {
            dirty = true;
            saveTimer.Stop();
            saveTimer.Start();
            if (showOverlay) lock (gate) overlayUntil = clock.Elapsed.TotalSeconds + 6;
            Publish();
        }
        if (dirty) saveTimer.Start();

        // Hiding the settings window must hand the keyboard back to a real program, never to the scene.
        settings.VisibleChanged += (_, _) =>
        {
            if (settings.Visible || window.IsDisposed) return;
            window.BeginInvoke(() =>
            {
                IntPtr front = WindowFinder.Foreground();
                if (front != IntPtr.Zero && front != window.Handle && front != settings.Handle) return;
                WindowFinder.Activate(WindowFinder.StillOpen(inFront) ? inFront : IntPtr.Zero);
                Log.Line("Settings hidden: the keyboard went back to the program that had it.");
            });
        };

        Action tuneNow = () => { };
        static bool SameModes(Layout a, Layout b) =>
            a.Screens.Select(s => (s.Id, s.PixelWidth, s.PixelHeight, s.RefreshHz))
                .SequenceEqual(b.Screens.Select(s => (s.Id, s.PixelWidth, s.PixelHeight, s.RefreshHz)));
        settings.LayoutChanged += changed =>
        {
            bool modes = !SameModes(layout, changed);
            layout = changed;
            workspace = workspace.WithLayout(layout);
            Touch();
            if (modes) tuneNow();
        };

        // Switching, adding, renaming and removing profiles all end the same way: show the one now in use.
        Action<string> matchVirtual = _ => { };
        Action<string> announceLayout = _ => { };
        void UseWorkspace(Workspace changed)
        {
            if (ReferenceEquals(changed, workspace)) return;
            workspace = changed;
            layout = LayoutStore.WithScreensFor(workspace.Current.Layout, AutoAddable(), OpticsFor(view), out IReadOnlyList<string> hidden, sources);
            workspace = workspace.WithLayout(layout);
            tellHidden(hidden);
            ShowProfiles();
            settings.SetLayout(layout, sources);
            Touch(showOverlay: true);
            matchVirtual("Profile " + workspace.Current.Name);
            announceLayout(workspace.Current.Name);
        }
        // The Control API, off unless asked for: other programs on this PC list the scene and switch layouts.
        // The screens that show bots' own windows, by bot: each one leaves with its bot.
        // shortcut: kept only while the app runs, so a bot's screen from before a restart stays until removed in Settings; save the pairing if that piles up.
        var botScreens = new Dictionary<string, string>();
        Action<IntPtr, SceneObject> pinBotWindow = (_, _) => { };
        void UseDeck(Deck changed)
        {
            if (ReferenceEquals(changed, deck)) return;
            deck = changed;
            foreach (string gone in botScreens.Keys.Where(id => deck.Find(id) is null).ToList())
            {
                // Two bots can share one window (two tabs of a terminal): the screen stays while either is left.
                if (botScreens.Remove(gone, out string? screen) && !botScreens.ContainsValue(screen) && layout.Find(screen) is not null)
                {
                    layout = layout.Remove(screen);
                    workspace = workspace.WithLayout(layout);
                    settings.SetLayout(layout, sources);
                    Touch();
                }
            }
            Publish();
        }
        Action<string> openObject = _ => { };
        Func<string, string, string?> tellBot = (_, _) => "Not ready yet.";
        Action listen = () => { };
        Func<string, Uri, string?> openWidget = (_, _) => "Not ready yet.";
        Func<ScreenSpec, (int, int)> pictureSizeOf = _ => (1920, 1080);
        Func<string, string?, (System.Text.Json.Nodes.JsonObject?, string?)> calibrate = (_, _) => (null, "Not ready yet.");
        var host = new ControlHost(() => workspace, UseWorkspace, () => deck, UseDeck, id => openObject(id), (id, text) => tellBot(id, text), (label, page) => openWidget(label, page),
            s => pictureSizeOf(s), () => OpticsFor(view), (action, from) => calibrate(action, from));
        // A trial window (--window, --seconds) may run beside the real app: it only has the API when asked, under
        // its own name, so hooks and scripts always reach the real one.
        bool trialRun = args.Contains("--window") || Program.Number(args, "--seconds", 0) > 0;
        using ControlApiServer? api = args.Contains("--api") || (!trialRun && File.Exists(ApiFlag))
            ? new ControlApiServer(ControlApi.PipeName + (trialRun ? ".Trial" : ""), line =>
            {
                // Posted to the window thread and waited for here, never Control.Invoke: Invoke leaves a wait handle
                // behind that blows up on the window thread when a session ends mid-call (unplugging the glasses
                // while a bot hook was reporting did exactly that on 2026-11-24).
                var done = new TaskCompletionSource<(string? Reply, bool Subscribe)>(TaskCreationOptions.RunContinuationsAsynchronously);
                window.BeginInvoke(() =>
                {
                    try
                    {
                        string? reply = ControlApi.Handle(line, host, out bool subscribe);
                        done.TrySetResult((reply, subscribe));
                    }
                    catch (Exception e) { done.TrySetException(e); } // back to the caller, not into the window's message loop
                });
                if (!done.Task.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("The session ended before it answered.");
                return done.Task.Result;
            })
            : null;
        if (api is not null) announceLayout = name => api.Publish(ControlApi.Event("layout.loaded", name));
        // A bot whose session has died must not go on looking busy: check every couple of seconds.
        var botTimer = new System.Windows.Forms.Timer(parts) { Interval = 2000 };
        botTimer.Tick += (_, _) => UseDeck(deck.Sweep(BotProcess.Alive, DateTime.UtcNow));
        // Bots on the wearer's other PCs: only if network.json sets it up, and only their state reports get in.
        BotNetListener? net = null;
        if (BotNetwork.Load() is { } network)
        {
            try
            {
                net = new BotNetListener(network, line =>
                {
                    try { window.BeginInvoke(() => ControlApi.Handle(line, host, out _)); }
                    catch (InvalidOperationException) { } // the session has ended
                });
            }
            catch (System.Net.Sockets.SocketException e) { Log.Line($"Not listening for bots on other PCs: {e.Message}"); }
        }
        using BotNetListener? netHeld = net;
        if (api is not null || net is not null) botTimer.Start();
        settings.ProfileSelected += index => UseWorkspace(workspace.Select(index));
        settings.ProfileAddRequested += () => UseWorkspace(workspace.AddCopy());
        settings.ProfileRenameRequested += name => UseWorkspace(workspace.Rename(name));
        settings.ProfileRemoveRequested += () => UseWorkspace(workspace.RemoveCurrent());
        settings.ViewChanged += changed => { view = changed; Touch(showOverlay: true); };
        settings.StatusPinnedChanged += pinned => { statusPinned = pinned; lock (gate) overlayUntil = 0; Publish(); };
        settings.RecentreRequested += () => { lock (gate) recenter = true; };
        settings.QuitRequested += window.Close;
        settings.MouseToGazeRequested += () => PointerToGaze();
        settings.BlankChanged += on => { blank = on; Publish(); };
        // Steadiness from view.txt now, and whenever the window changes it.
        void Steady(ViewSettings v) { tracker.ReadingSteady = v.ReadingSteady; tracker.MaxLookAheadMs = v.LookAheadMs; }
        Steady(view);
        settings.ViewChanged += Steady;
        settings.BotsSource = () => deck;
        settings.SetLinks(apiRunning: api is not null, netRunning: net is not null);
        settings.SetGlassesDisplay(direct ? $"{shownOn.Label}, direct" : shownOn.Label);
        settings.VoiceTryRequested += () => listen();

        // Notes what is loading the graphics cards whenever the glasses start dropping frames.
        using var perf = new PerfWatch(() => (presenter.FramesPerSecond, shownOn.RefreshHz, presenter.SlowFrames, presenter.PoseToScreenMs));
        // Where the time goes when frames drop: Windows (wait, hand-over), the app (draw, window thread), or the
        // copied screens themselves updating slowly in Windows (copies per second).
        long worstWindowTicks = 0, windowBeat = Stopwatch.GetTimestamp();
        var beatTimer = new System.Windows.Forms.Timer(parts) { Interval = 50 };
        beatTimer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            long gap = now - windowBeat;
            windowBeat = now;
            if (gap > Interlocked.Read(ref worstWindowTicks)) Interlocked.Exchange(ref worstWindowTicks, gap);
        };
        beatTimer.Start();
        perf.Extra = () =>
        {
            double windowMs = Interlocked.Exchange(ref worstWindowTicks, 0) * 1000.0 / Stopwatch.Frequency;
            return $"{presenter.TakeWorst()}, window thread {windowMs:F0} ms{(shared.CaptureRates.Length > 0 ? ", " + shared.CaptureRates : "")}{(direct ? ", direct" : "")}";
        };

        // The live numbers at the top of the settings window.
        var liveTimer = new System.Windows.Forms.Timer(parts) { Interval = 1000 };
        liveTimer.Tick += (_, _) =>
        {
            bool tracking = link.State == XrShift.Tracking.LinkState.Connected && tracker.HasPose;
            settings.SetLive(gameMode ? "game mode"
                : tracking ? string.Create(CultureInfo.InvariantCulture, $"{presenter.FramesPerSecond:F0} fps · {presenter.PoseToScreenMs:F0} ms")
                : "no head tracking", tracking || gameMode);
            settings.SetCalibrationReady(tracking && !gameMode);
        };
        liveTimer.Start();

        Action<bool> setGameMode = _ => { };
        using var tray = new NotifyIcon { Icon = AppIcon.Value, Text = "XrShift", Visible = true };
        using var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        // Bots and the Control API are switched on the settings window's Bots page (the same api-on file).
        menu.Items.Add($"Recentre ({recentreKey})", null, (_, _) => { lock (gate) recenter = true; });
        menu.Items.Add("Plain screen / game mode (Ctrl+Alt+G)", null, (_, _) => setGameMode(!gameMode));
        s_show = () => window.BeginInvoke(ShowSettings);
        menu.Items.Add("Quit", null, (_, _) => window.Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowSettings();

        // Game mode: the app steps aside so the glasses show their own Windows desktop at full speed.
        Action planCursor = () => { };
        Action<bool> pauseGlassesLinks = _ => { }; // plain screen: no camera listening, no requests to the glasses
        // Before the driver rebuilds or removes its monitors, the scene lets go of its live copies of them, and
        // only then are the monitors touched. Tearing monitors down while they were still being copied is what
        // froze the PC once on quitting (2026-11-17); the same order is kept for every change of their number.
        void LetGoOfVirtualCopies()
        {
            if (!live || presenter.Paused) return;
            Snapshot current = shared.Snapshot;
            if (!current.Sources.Any(VirtualMonitors.IsVirtual)) return;
            shared.Snapshot = current with { Sources = current.Sources.Where(d => !VirtualMonitors.IsVirtual(d)).ToList() };
            // The drawing thread applies it on its next frame; give it a few.
            long frames = presenter.Frames;
            var waited = Stopwatch.StartNew();
            while (presenter.Frames < frames + 3 && waited.ElapsedMilliseconds < 300) Thread.Sleep(4);
        }

        void SetGameMode(bool on)
        {
            if (on == gameMode) return;
            if (on && direct)
            {
                Log.Line("Plain screen is not available in direct mode: the glasses are off the Windows desktop.");
                settings.SetGameMode(false);
                return;
            }
            if (on) LetGoOfVirtualCopies();
            gameMode = on;
            pauseGlassesLinks(on);
            presenter.Paused = on;
            if (on)
            {
                window.Hide();
                // A game should have the machine to itself: no virtual monitors while it runs.
                if (manageMonitors && VirtualMonitors.Installed && (VirtualMonitors.Count() ?? 0) > 0)
                {
                    VirtualMonitors.TrySetCount(0, out _);
                    Log.Line("Game mode: virtual monitors removed.");
                }
            }
            else
            {
                window.Show();
                if (fullScreen && window.Bounds != area) window.Bounds = area;
                lock (gate) recenter = true;
                matchVirtual("Game mode off");
            }
            planCursor();
            settings.SetGameMode(on);
            Log.Line(on ? "Game mode on: the glasses show their own desktop." : "Game mode off.");
        }
        settings.GameModeChanged += on =>
        {
            if (!on && vrMode) SetVrMode(false);
            else SetGameMode(on);
        };

        // VR mode: plain screen, plus the glasses as a SteamVR headset (rotation only, seated).
        void SetVrMode(bool on)
        {
            if (on == vrMode) return;
            if (on)
            {
                string? why = direct ? "VR mode needs the glasses on the Windows desktop (direct mode is on)."
                    : stationary ? "Switch the glasses to Follow with their button first. In stationary mode they hold one picture still, so a VR game could not follow your head."
                    : !VrBridge.DriverPresent ? "The VR driver is not part of this copy of XrShift yet."
                    : VrBridge.SteamVrFolder() is null ? "SteamVR is not installed. Install it from Steam, then try again." : null;
                if (why is null)
                {
                    SetGameMode(true);
                    try { vrBridge ??= new VrBridge(); }
                    catch (IOException e) { why = "Could not share the head tracking with SteamVR: " + e.Message; }
                    why ??= VrBridge.Start(area, shownOn.RefreshHz, view.FovDeg);
                    if (why is not null) SetGameMode(false);
                }
                if (why is not null)
                {
                    settings.SetVrMode(false);
                    Log.Line("VR mode not started: " + why);
                    MessageBox.Show(settings, why, "XrShift - VR mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                vrMode = true;
                settings.Hide(); // nothing of XrShift's should hold the keyboard or the controller during a game
            }
            else
            {
                vrMode = false;
                SetGameMode(false);
                Log.Line("VR mode off.");
            }
            settings.SetVrMode(vrMode);
        }
        settings.VrModeChanged += SetVrMode;

        // In VR mode SteamVR's own windows (its compositor, Room Setup, its status window) can take the focus, and
        // Steam only passes a controller to the program that has it: the game then stops answering the gamepad.
        // While one of them has held the focus for a moment, it is handed back to the program that had it last.
        string[] steamVrPrograms = ["vrcompositor", "vrmonitor", "vrserver", "vrwebhelper", "vrdashboard", "steamvr_room_setup", "vrstartup"];
        string[] neverAGame = ["XrShift", "explorer", "steam", "steamwebhelper", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "TextInputHost"];
        double stolenSince = -1;
        var vrFocusTimer = new System.Windows.Forms.Timer(parts) { Interval = 500 };
        vrFocusTimer.Tick += (_, _) =>
        {
            if (!vrMode) { stolenSince = -1; return; }
            string? owner = WindowFinder.ProgramOfWindow(WindowFinder.Foreground());
            bool stolen = owner is not null && steamVrPrograms.Contains(owner, StringComparer.OrdinalIgnoreCase);
            double time = clock.Elapsed.TotalSeconds;
            if (!stolen) { stolenSince = -1; return; }
            if (stolenSince < 0) { stolenSince = time; return; }
            if (time - stolenSince < 1.5) return;
            stolenSince = -1;
            WindowInfo? game = WindowFinder.List().FirstOrDefault(w =>
                !steamVrPrograms.Contains(w.Program, StringComparer.OrdinalIgnoreCase) && !neverAGame.Contains(w.Program, StringComparer.OrdinalIgnoreCase));
            if (game is null) return;
            WindowFinder.Activate(game.Handle);
            Log.Line($"VR mode: {owner} had the focus; gave it back to {game.Program} so the controller reaches the game.");
        };
        vrFocusTimer.Start();
        setGameMode = SetGameMode;

        // ---- virtual monitors: real extra desktops made by the Virtual Display Driver ------------------------
        void ShowVirtual()
        {
            int? count = VirtualMonitors.Installed ? VirtualMonitors.Count() : null;
            settings.CanAddMonitors = count is not null;
            settings.SetVirtualNote(count switch
            {
                null => "+ Monitor sets up extra monitors",
                0 => "",
                1 => "1 extra monitor",
                _ => $"{count} extra monitors",
            });
        }
        ShowVirtual();

        void SetVirtualCount(int want, string what)
        {
            want = Math.Clamp(want, 0, VirtualMonitors.Max);
            LetGoOfVirtualCopies(); // the driver rebuilds all its monitors on any change of their number
            if (VirtualMonitors.TrySetCount(want, out string problem))
            {
                Log.Line($"{what}: asked the driver for {want} virtual monitor{(want == 1 ? "" : "s")}.");
                settings.SetVirtualNote($"Changing to {want}...");
            }
            else
            {
                Log.Line($"{what} failed: {problem}");
                settings.SetVirtualNote("The driver did not answer");
                removing = null;
                Publish(); // nothing changed after all: show the monitors again
            }
        }
        settings.MonitorAddRequested += () =>
        {
            int have = VirtualMonitors.Count() ?? 0;
            if (have >= VirtualMonitors.Max)
            {
                settings.SetVirtualNote($"{VirtualMonitors.Max} is the most");
                return;
            }
            adding = true;
            SetVirtualCount(have + 1, "Add monitor");
        };

        // Makes the number of virtual monitors what the profile in use needs: more, fewer or none.
        void MatchVirtualCount(string why)
        {
            if (!manageMonitors || gameMode || !VirtualMonitors.Installed) return;
            int needed = VirtualMonitors.Needed(layout), have = VirtualMonitors.Count() ?? 0;
            // Switching between stationary and follow never takes monitors away: the windows on them would be
            // thrown back onto the laptop screen. Only fewer screens needed at start, or a removal, reduces them.
            if (why.EndsWith(" mode", StringComparison.Ordinal) && needed < have) return;
            if (needed != have) SetVirtualCount(needed, why);
        }
        matchVirtual = MatchVirtualCount;
        MatchVirtualCount("Start");
        Func<Layout, Layout, Layout> placeNewAtGaze = (_, after) => after;
        Action putSelectedHere = () => { };
        // Pins a window as a screen of its own. Given a place (a bot's, say) the screen goes there; otherwise
        // where the wearer is looking. Returns the screen's id, also when that window was pinned already.
        string PinWindow(WindowInfo window, (float Yaw, float Pitch)? at = null, string? name = null)
        {
            string source = ScreenSpec.WindowSource(window.Program, window.Title);
            if (layout.Screens.FirstOrDefault(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase)) is { } already)
            {
                settings.Select(already.Id);
                return already.Id;
            }
            WindowFinder.Restore(window.Handle); // Windows does not draw a minimised window, so there would be nothing to show
            ScreenSpec spec = layout.PlaceNew(name ?? WindowFinder.FriendlyName(window.Program), source, window.Width, window.Height, OpticsFor(view));
            Layout beforePin = layout;
            layout = at is { } place
                ? layout.Add(Layout.Clamp(spec with { YawDeg = place.Yaw, PitchDeg = place.Pitch }))
                : placeNewAtGaze(beforePin, layout.Add(spec));
            workspace = workspace.WithLayout(layout);
            pinned = new Dictionary<string, IntPtr>(pinned, StringComparer.OrdinalIgnoreCase) { [source] = window.Handle };
            settings.SetLayout(layout, sources);
            settings.Select(spec.Id);
            Log.Line($"Pinned a window of {window.Program} as screen '{spec.Name}'.");
            Touch();
            return spec.Id;
        }
        settings.WindowPinRequested += window => PinWindow(window);
        // A widget: the page opens in a browser window with no tabs or address bar, and that window is pinned
        // where the wearer is looking, under the widget's name.
        openWidget = (label, page) =>
        {
            HashSet<IntPtr> before = [.. WindowFinder.List().Select(w => w.Handle)];
            try { Process.Start(new ProcessStartInfo("msedge.exe", $"--app=\"{page.AbsoluteUri}\" --new-window") { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception e) { return $"Could not open the page: {e.Message}"; }
            int tries = 0;
            var appear = new System.Windows.Forms.Timer(parts) { Interval = 500 };
            appear.Tick += (_, _) =>
            {
                WindowInfo? opened = WindowFinder.List().FirstOrDefault(w => !before.Contains(w.Handle) && w.Program.Contains("msedge", StringComparison.OrdinalIgnoreCase));
                if (opened is null && ++tries < 20) return;
                appear.Stop();
                appear.Dispose();
                if (opened is null) Notice($"{label}: the page's window never appeared.", 4);
                else PinWindow(opened, null, label);
            };
            appear.Start();
            return null;
        };
        pinBotWindow = (handle, bot) =>
        {
            if (WindowFinder.List().FirstOrDefault(w => w.Handle == handle) is not { } window) return;
            // Above the bot, so the bot stays visible under its own window. Removed again when the bot goes.
            float zoom = view.Zoom > 0.01f ? view.Zoom : 1f; // screens are zoomed, bots are not
            botScreens[bot.Id] = PinWindow(window, (bot.YawDeg / zoom, (bot.PitchDeg + 24f) / zoom), bot.Label);
        };
        settings.MonitorRemoveRequested += id =>
        {
            int have = VirtualMonitors.Count() ?? 0;
            if (have <= 0 || layout.Find(id) is null) return;
            removing = id;
            SetVirtualCount(have - 1, "Remove monitor");
        };

        // The screen in the middle of the wearer's view, and the spot on the real desktop that the middle falls
        // on: a point of a monitor, or of a pinned window wherever that window really is. If the gaze is
        // between screens it is the nearest edge of the nearest one, and Miss says how far off it was.
        // With no head tracking it is null, unless asked to take the view straight ahead as the scene then draws it.
        (string Id, Point Point, Rectangle Area, IntPtr Window, float Miss)? FindGaze(bool aheadWithoutTracking = false)
        {
            if ((!tracker.HasPose && !aheadWithoutTracking) || gameMode) return null;
            Quaternion trim = Quaternion.CreateFromYawPitchRoll(0, -view.LevelDeg * DegToRad, view.RollDeg * DegToRad);
            Quaternion drift = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -shared.FollowYaw);
            Vector3 gaze = Vector3.Transform(-Vector3.UnitZ, drift * (tracker.HasPose ? tracker.GetPose().Orientation : Quaternion.Identity) * trim);
            DisplayOptics optics = OpticsFor(view);

            (string, Point, Rectangle, IntPtr, float)? best = null;
            float bestMiss = float.MaxValue, bestDistance = float.MaxValue;
            if (blank) return null;
            foreach (ScreenSpec spec in layout.Zoomed(view.Zoom).Screens)
            {
                if (!spec.Visible || spec.FollowsMe) continue;
                Rectangle on; // where this screen's picture lives on the real desktop
                IntPtr handle = IntPtr.Zero;
                if (spec.DisplayName is { } id)
                {
                    DisplayInfo? d = sources.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (d is null) continue;
                    on = d.Bounds;
                }
                else if (spec.Window is not null && pinned.TryGetValue(spec.Source, out handle)
                    && !WindowFinder.Minimized(handle) && WindowFinder.Bounds(handle) is { } rect)
                {
                    on = rect;
                }
                else continue;
                if (on.Width < 2 || on.Height < 2) continue;
                if (!spec.ToPanel(on.Width, on.Height, optics).TryHit(gaze, out float s, out float t)) continue;

                float miss = MathF.Max(MathF.Max(MathF.Abs(s) - 0.5f, MathF.Abs(t) - 0.5f), 0f); // 0 = looking right at it
                if (miss > bestMiss + 1e-4f || (MathF.Abs(miss - bestMiss) <= 1e-4f && spec.Distance >= bestDistance)) continue;
                s = Math.Clamp(s, -0.49f, 0.49f);
                t = Math.Clamp(t, -0.49f, 0.49f);
                var spot = new Point(on.Left + (int)((s + 0.5f) * on.Width), on.Top + (int)((0.5f - t) * on.Height));
                // A pinned window reaches a little way under the glasses' picture to keep it drawing (see
                // KeepPinnedWindowsDrawing); the pointer must stay on the part that is on a real screen.
                if (handle != IntPtr.Zero && fullScreen && !direct && area.Contains(spot))
                {
                    Point[] ways =
                    [
                        new(area.Left - 1, spot.Y), new(area.Right, spot.Y), new(spot.X, area.Top - 1), new(spot.X, area.Bottom),
                    ];
                    spot = ways.Where(on.Contains).OrderBy(w => Math.Abs(w.X - spot.X) + Math.Abs(w.Y - spot.Y)).DefaultIfEmpty(spot).First();
                }
                best = (spec.Id, spot, on, handle, miss);
                bestMiss = miss;
                bestDistance = spec.Distance;
            }
            return best;
        }

        // Puts the mouse pointer on the spot the wearer is looking at. A pinned window is brought to the
        // front as well, or the pointer would land on whatever happens to cover it on the real desktop.
        void PointerToGaze()
        {
            if (FindGaze() is not { } hit) return;
            PlacePointer(hit.Point);
            if (hit.Window != IntPtr.Zero) WindowFinder.Activate(hit.Window);
        }

        // Look to move, two ways.
        // Jump: rest your eyes on another screen for a quarter of a second and the pointer jumps to the spot you
        //   look at, unless the mouse was just moved by hand or a button is held.
        // Follow: the pointer rides the middle of your view, like a mouse steered by the head: aim, then click.
        //   A hand movement fine-tunes it, and it stays where the hand put it until the head moves on.
        // Either way the window looked at gets the keyboard if Look to focus is on (a pinned window always does,
        // or the pointer would land on whatever covers it on the real desktop).
        Point pointerSeen = Cursor.Position, pointerPlaced = new(int.MinValue, int.MinValue);
        double handMovedAt = -10, gazeSince = 0;
        string? gazeOn = null, focusedOn = null;
        (string Id, Point At)? handAnchor = null;

        void PlacePointer(Point p)
        {
            Cursor.Position = p;
            pointerPlaced = pointerSeen = Cursor.Position;
        }

        void FocusOn((string Id, Point Point, Rectangle Area, IntPtr Window, float Miss) hit)
        {
            if (focusedOn == hit.Id) return;
            focusedOn = hit.Id;
            if (hit.Window != IntPtr.Zero) WindowFinder.Activate(hit.Window);
            else if (view.LookFocus && WindowFinder.TopLevelAt(hit.Point) is var under && under != IntPtr.Zero) WindowFinder.Activate(under);
        }

        void LookTick(bool slowTick)
        {
            double time = clock.Elapsed.TotalSeconds;
            Point now = Cursor.Position;
            if (now != pointerSeen && now != pointerPlaced) handMovedAt = time;
            pointerSeen = now;

            // A game that has taken the mouse (pointer hidden while you play) is left alone: moving the pointer would
            // turn its camera. Look-to-point carries on as soon as the pointer shows again (a menu, a pause, Alt+Tab).
            if (WindowFinder.MouseTakenByProgram())
            {
                gazeOn = null;
                handAnchor = null;
                return;
            }
            if (!view.LookPointer || gameMode || blank || shared.HoldHead || shared.Calibration is not null)
            {
                gazeOn = focusedOn = null;
                handAnchor = null;
                return;
            }
            if (!view.LookFollow && !slowTick) return;
            if (FindGaze() is not { Miss: < 1e-4f } hit)
            {
                gazeOn = null; // between screens: nothing changes
                return;
            }
            if (hit.Id != gazeOn)
            {
                gazeOn = hit.Id;
                gazeSince = time;
            }
            if ((Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right | MouseButtons.Middle)) != 0) return;

            if (view.LookFollow)
            {
                if (time - handMovedAt < 0.5)
                {
                    handAnchor = (hit.Id, hit.Point); // the hand is steering; remember where the head was
                    return;
                }
                if (handAnchor is { } anchor && anchor.Id == hit.Id &&
                    Math.Abs(anchor.At.X - hit.Point.X) + Math.Abs(anchor.At.Y - hit.Point.Y) < 60) return;
                handAnchor = null;
                if (Math.Abs(now.X - hit.Point.X) + Math.Abs(now.Y - hit.Point.Y) > 2) PlacePointer(hit.Point);
                FocusOn(hit);
                return;
            }

            if (time - handMovedAt < 0.8 || time - gazeSince < 0.25 || hit.Area.Contains(now))
            {
                if (hit.Area.Contains(now)) focusedOn = hit.Id;
                return;
            }
            PlacePointer(hit.Point);
            FocusOn(hit);
        }

        // Pick up and set down: look at a screen and press Ctrl+Alt+P. It then rides along with your view, keeping
        // its place relative to where you look, while you turn to where you want it; Ctrl+Alt+PgUp / PgDn resize
        // it meanwhile, and Ctrl+Alt+P again sets it down there and saves. Works in both Stationary and Follow.
        string? carried = null;
        float carryYaw = 0, carryPitch = 0;
        string notice = "";
        double noticeUntil = 0;

        // Where the middle of the view points, in the layout's own degrees (yaw + = left, pitch + = up).
        (float Yaw, float Pitch)? GazeAngles()
        {
            if (!tracker.HasPose || gameMode || shared.HoldHead) return null;
            Quaternion trim = Quaternion.CreateFromYawPitchRoll(0, -view.LevelDeg * DegToRad, view.RollDeg * DegToRad);
            Quaternion drift = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -shared.FollowYaw);
            Vector3 g = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, drift * tracker.GetPose().Orientation * trim));
            float zoom = view.Zoom > 0.01f ? view.Zoom : 1f;
            return (MathF.Atan2(-g.X, -g.Z) / DegToRad / zoom, MathF.Asin(Math.Clamp(g.Y, -1f, 1f)) / DegToRad / zoom);
        }

        void Notice(string text, double seconds)
        {
            lock (gate)
            {
                notice = text;
                noticeUntil = clock.Elapsed.TotalSeconds + seconds;
            }
        }

        // A screen added hidden (no room left within reach) would otherwise never be noticed.
        tellHidden = names =>
        {
            if (names.Count == 0) return;
            Log.Line($"No room within reach; added hidden: {string.Join(", ", names)}.");
            Notice($"No room in reach, so added hidden: {string.Join(", ", names)}. Show it in Settings.", 8);
        };
        tellHidden(hiddenAtStart);

        // Bring it here (Ctrl+Alt+W): a window moves onto the monitor of the screen the wearer is looking at.
        void BringHere(IntPtr handle, string how)
        {
            if (WindowFinder.List().FirstOrDefault(w => w.Handle == handle) is not { } w)
            {
                Notice("Click the window you want first, then Ctrl+Alt+W", 3);
                return;
            }
            string name = WindowFinder.FriendlyName(w.Program);
            if (FindGaze(aheadWithoutTracking: true) is not { } hit)
            {
                Notice($"Look at a screen first, then Ctrl+Alt+W brings {name} there", 3);
                return;
            }
            Rectangle to = Screen.FromRectangle(hit.Area).WorkingArea;
            if (hit.Window == handle || (WindowFinder.Bounds(handle) is { } b && to.Contains(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2))))
            {
                if (how == "key") Notice($"{name} is already here", 2);
                return;
            }
            bool moved = WindowFinder.MoveTo(handle, to);
            Notice(moved ? $"{name} is here now" : $"Windows would not move {name}", 3);
            Log.Line(moved ? $"Brought a window of {w.Program} to where the wearer looks ({how})." : $"Could not move a window of {w.Program} ({how}).");
        }

        void SetDown()
        {
            if (carried is null) return;
            string name = layout.Find(carried)?.Name ?? "Screen";
            carried = null;
            settings.SetLayout(layout, sources);
            workspace = workspace.WithLayout(layout);
            Touch();
            Notice($"{name} set down here", 2);
            Log.Line("Screen set down.");
        }

        void PickUpOrSetDown()
        {
            if (stationary) { putSelectedHere(); return; } // stationary: Ctrl+Alt+P puts a screen where you look
            if (carried is not null) { SetDown(); return; }
            if (blank) return;
            if (GazeAngles() is not { } at || FindGaze() is not { Miss: < 0.25f } hit || layout.Find(hit.Id) is not { } spec)
            {
                Notice(shared.HoldHead ? "In stationary mode, arrange screens in the settings window" : "Look at a screen first, then Ctrl+Alt+P", 3);
                return;
            }
            carried = spec.Id;
            carryYaw = spec.YawDeg - at.Yaw;
            carryPitch = spec.PitchDeg - at.Pitch;
            Notice($"Carrying {spec.Name}: turn to place it, Ctrl+Alt+PgUp / PgDn size, Ctrl+Alt+P set down", 600);
            Log.Line("Screen picked up.");
        }

        var carryTimer = new System.Windows.Forms.Timer(parts) { Interval = 11 };
        carryTimer.Tick += (_, _) =>
        {
            if (carried is null) return;
            if (layout.Find(carried) is not { } spec || gameMode) { carried = null; Notice("", 0); return; }
            if (GazeAngles() is not { } at) return;
            float yaw = at.Yaw + carryYaw;
            yaw = yaw > 180 ? yaw - 360 : yaw < -180 ? yaw + 360 : yaw;
            float pitch = Math.Clamp(at.Pitch + carryPitch, -75f, 75f);
            if (MathF.Abs(yaw - spec.YawDeg) < 0.05f && MathF.Abs(pitch - spec.PitchDeg) < 0.05f) return;
            layout = layout.With(Layout.Clamp(spec with { YawDeg = yaw, PitchDeg = pitch }));
            Publish();
        };
        carryTimer.Start();
        settings.PlaceRequested += PickUpOrSetDown;

        // Gesture mode. The helper reads the glasses' camera, which only streams while the glasses are in their own
        // Anchor mode; then the glasses hold their picture still in the room by themselves, so the scene stops
        // following the head (HoldHead) and the screen in front of you stays put. The palm steers the pointer like
        // a touchpad and a pinch is the left button: click, or hold and move to drag.
        using var hands = new HandInput();
        var gesture = new HandGesture();
        HandFrame? handSeen = null;
        float carryX = 0, carryY = 0;
        const float PixelsPerPicture = 2600f; // pointer travel for a hand moved across the whole camera picture
        void LetGo()
        {
            if (gesture.Pinching) HandInput.MouseButton(false);
            gesture = new HandGesture();
        }
        // The hand quick menu: hold a flat hand up, palm out, still for a moment.
        string[] MenuChoices = ["Hide all", "Recentre", "Put here", "Next profile", "Close"];
        var handMenu = new HandMenu(MenuChoices.Length);
        bool swallowRelease = false;
        Action<int> menuPick = _ => { };
        var handTimer = new System.Windows.Forms.Timer(parts) { Interval = 15 };
        handTimer.Tick += (_, _) =>
        {
            bool wanted = view.Hands && stationary && !gameMode && fullScreen;
            if (wanted && !hands.Running) hands.Start();
            else if (!wanted && hands.Running) { LetGo(); hands.Stop(); }
            shared.HandsShown = view.Hands && !gameMode;
            shared.PreviewWanted = settings.Visible && settings.PreviewOn && !gameMode && settings.WindowState != FormWindowState.Minimized;
            if (Interlocked.Exchange(ref shared.Preview, null) is { } previewShot) settings.SetPreview(previewShot);
            shared.HandsStatus = hands.Status;
            settings.SetHandStatus(!HandInput.Installed ? "not set up" : !view.Hands ? "off" : !stationary ? "on; works in stationary" : hands.Status);
            bool live = hands.Running && hands.CameraLive;
            if (!live)
            {
                LetGo();
                handMenu.Close();
                shared.MenuItems = null;
                shared.HandsPinching = false;
                shared.HandsFrame = hands.Latest;
                return;
            }
            HandFrame? frame = hands.Latest;
            if (frame is null || ReferenceEquals(frame, handSeen)) return;
            handSeen = frame;
            shared.HandsFrame = frame;
            TrackedHand? hand = frame.Hands.OrderByDescending(h => h.Score).FirstOrDefault();
            HandAction action = gesture.Step(hand?.Points);

            // The quick menu takes the hand while it is open: no pointer moves, no clicks reach Windows.
            bool menuWasOpen = handMenu.IsOpen;
            int? picked = handMenu.Step(hand?.Points, clock.Elapsed.TotalSeconds, action.Press == true);
            if (handMenu.IsOpen || menuWasOpen || picked is not null)
            {
                if (action.Press == true) swallowRelease = true;
                shared.MenuItems = handMenu.IsOpen ? MenuChoices : null;
                shared.MenuHighlight = handMenu.Highlight;
                shared.HandsPinching = gesture.Pinching;
                if (picked is { } choice) menuPick(choice);
                if (handMenu.IsOpen && !menuWasOpen) Log.Line("Hand quick menu opened.");
                return;
            }
            if (action.Press == false && swallowRelease) { swallowRelease = false; action = action with { Press = null }; }
            if (action.Move != Vector2.Zero)
            {
                // y runs over the picture's height, x over its width: the same distance in the room moves the same.
                carryX += action.Move.X * PixelsPerPicture;
                carryY += action.Move.Y * PixelsPerPicture * 378f / 504f;
                int dx = (int)carryX, dy = (int)carryY;
                carryX -= dx;
                carryY -= dy;
                HandInput.MoveMouse(dx, dy);
                pointerPlaced = pointerSeen = Cursor.Position;
            }
            if (action.Press is bool press) HandInput.MouseButton(press);
            shared.HandsPinching = gesture.Pinching;
        };
        handTimer.Start();

        // ---- pinned windows keep drawing -------------------------------------------------------------
        // Teams, Chrome, Edge and similar programs stop drawing while other windows cover them completely, so
        // their floating screen froze until clicked. XrShift keeps a strip of each pinned window under its own
        // picture on the glasses, where such programs count as uncovered. Maximised and full-screen windows
        // (games) are left alone, and nothing moves while a mouse button is held.
        var awakeNoted = new HashSet<IntPtr>();
        void KeepPinnedWindowsDrawing()
        {
            if (!fullScreen || direct || gameMode || pinned.Count == 0) return;
            if ((Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right)) != 0) return;
            DisplayInfo? home = sources.Where(d => !VirtualMonitors.IsVirtual(d))
                .FirstOrDefault(d => d.Bounds.Right == area.Left || d.Bounds.Left == area.Right || d.Bounds.Bottom == area.Top || d.Bounds.Top == area.Bottom);
            if (home is null) return;
            foreach (IntPtr handle in pinned.Values.Distinct())
            {
                if (!WindowFinder.ReachUnder(handle, home.Bounds, area)) continue;
                if (awakeNoted.Add(handle)) Log.Line("Moved a pinned window to the edge of the screen beside the glasses so it keeps drawing.");
            }
        }
        var awakeTimer = new System.Windows.Forms.Timer(parts) { Interval = 2000 };
        awakeTimer.Tick += (_, _) => KeepPinnedWindowsDrawing();
        awakeTimer.Start();

        // ---- stationary and follow: the glasses' own two modes ---------------------------------------
        using var camera = new GlassesCamera();
        if (live) camera.Start();
        using var glassesControl = new GlassesControl();
        if (live) glassesControl.Start();
        pauseGlassesLinks = on => { glassesControl.Paused = on; camera.Paused = on; };

        // Stationary mode, screens all around the room. The glasses can hold only one picture still, but they glide
        // it to wherever you look when asked (their recentre). So XrShift keeps its own map of where every screen is
        // in the room, follows your head with the motion sensor, and when you turn to look at a screen outside the
        // held picture and keep your head still for a moment, asks the glasses to glide the picture there and draws
        // that part of the room into it. Each screen is then held still by the glasses themselves while you use it.
        (float Yaw, float Pitch) HeadDirection()
        {
            Quaternion trim = Quaternion.CreateFromYawPitchRoll(0, -view.LevelDeg * DegToRad, view.RollDeg * DegToRad);
            Vector3 g = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, tracker.GetPose().Orientation * trim));
            return (MathF.Atan2(-g.X, -g.Z) / DegToRad, MathF.Asin(Math.Clamp(g.Y, -1f, 1f)) / DegToRad);
        }
        static float Apart(float yawA, float pitchA, float yawB, float pitchB)
        {
            float dy = MathF.IEEERemainder(yawA - yawB, 360f);
            return MathF.Sqrt(dy * dy + (pitchA - pitchB) * (pitchA - pitchB));
        }
        void HoldPictureHere()
        {
            if (!tracker.HasPose) return;
            (float yaw, float pitch) = HeadDirection();
            shared.HeldYaw = yaw;
            shared.HeldPitch = pitch;
            glassesControl.RecentreHeldPicture();
        }
        (float Yaw, float Pitch) steadyFrom = (0, 0);
        double steadySince = 0;
        var roomTimer = new System.Windows.Forms.Timer(parts) { Interval = 50 };
        roomTimer.Tick += (_, _) =>
        {
            if (!stationary || gameMode || blank || !tracker.HasPose || shared.Calibration is not null) return;
            double time = clock.Elapsed.TotalSeconds;
            (float yaw, float pitch) = HeadDirection();
            if (Apart(yaw, pitch, steadyFrom.Yaw, steadyFrom.Pitch) > 4f) { steadyFrom = (yaw, pitch); steadySince = time; return; }
            if (time - steadySince < 0.2) return; // still turning

            // Resting on a screen that the held picture does not show well? An empty wall is left alone.
            DisplayOptics held = DisplayOptics.OnePro with { HorizontalFovDeg = view.HeldFovDeg };
            float half = held.HorizontalFovDeg / 2f;
            var gaze = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw * DegToRad, pitch * DegToRad, 0));
            ScreenSpec? target = layout.Screens.FirstOrDefault(s =>
            {
                if (!s.Visible || s.FollowsMe) return false;
                (int w, int h) = PictureSize(s);
                return s.ToPanel(w, h, held).TryHit(gaze, out float hs, out float ht) && MathF.Abs(hs) <= 0.55f && MathF.Abs(ht) <= 0.6f;
            });
            if (target is null) return;
            if (Apart(target.YawDeg, target.PitchDeg, shared.HeldYaw, shared.HeldPitch) < half * 0.6f) return; // already well in view
            HoldPictureHere();
            Log.Line($"Stationary: glided the held picture to yaw {yaw:F0}, pitch {pitch:F0}.");
        };
        roomTimer.Start();

        // In stationary mode a new screen appears where you are looking, not beside the last one; and a screen can be
        // sent to where you look (Ctrl+Alt+P, or the button on the Stationary page).
        placeNewAtGaze = (before, after) =>
        {
            if (!stationary || !tracker.HasPose) return after;
            // Only screens that are really new: one brought back with its desk keeps the place it had there.
            List<ScreenSpec> fresh = Desks.Added(before, after);
            if (fresh.Count == 0) return after;
            (float yaw, float pitch) = HeadDirection();
            float apart = 0;
            foreach (ScreenSpec s in fresh)
            {
                after = after.With(Layout.Clamp(s with { YawDeg = MathF.IEEERemainder(yaw - apart, 360f), PitchDeg = pitch }));
                apart += 35;
            }
            HoldPictureHere();
            return after;
        };
        putSelectedHere = () =>
        {
            if (!stationary || !tracker.HasPose) return;
            ScreenSpec? spec = settings.SelectedId is { } id ? layout.Find(id) : null;
            spec ??= layout.Screens.LastOrDefault(s => s.Visible);
            if (spec is null) return;
            (float yaw, float pitch) = HeadDirection();
            layout = layout.With(Layout.Clamp(spec with { YawDeg = yaw, PitchDeg = pitch }));
            workspace = workspace.WithLayout(layout);
            settings.SetLayout(layout, sources);
            Touch();
            HoldPictureHere();
            Notice($"{spec.Name} is now where you look", 2);
            Log.Line($"Stationary: put '{spec.Name}' at yaw {yaw:F0}, pitch {pitch:F0}.");
        };
        settings.PutHereRequested += () => putSelectedHere();
        (int Width, int Height) PictureSize(ScreenSpec s)
        {
            if (s.DisplayName is { } id && sources.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) is { } d)
                return (d.Bounds.Width, d.Bounds.Height);
            if (s.Window is not null && pinned.TryGetValue(s.Source, out IntPtr w) && WindowFinder.Bounds(w) is { } r)
                return (r.Width, r.Height);
            return (1920, 1080);
        }
        void FitInView()
        {
            layout = Presets.FitInView(layout, PictureSize, OpticsFor(view));
            workspace = workspace.WithLayout(layout);
            settings.SetLayout(layout, sources);
            Touch();
        }
        settings.FitRequested += FitInView;
        pictureSizeOf = PictureSize;
        void SwitchMode(bool toStationary)
        {
            if (carried is not null) SetDown();
            if (toStationary) HoldPictureHere();
            dirty = true;
            Save(); // the mode being left keeps its screens
            stationary = toStationary;
            Workspace? stored = LayoutStore.Load(stationary);
            workspace = stored ?? new Workspace();
            layout = LayoutStore.WithScreensFor(workspace.Current.Layout, AutoAddable(), OpticsFor(view), out IReadOnlyList<string> hidden, sources);
            tellHidden(hidden);
            if (stationary && stored is null) layout = Presets.FitInView(layout, PictureSize, OpticsFor(view));
            workspace = workspace.WithLayout(layout);
            dirty = true;
            shared.HoldHead = stationary;
            ShowProfiles();
            settings.SetLayout(layout, sources);
            settings.SetGlassesMode(stationary);
            Touch(showOverlay: true);
            matchVirtual(stationary ? "Stationary mode" : "Follow mode");
            Notice(stationary ? "Stationary: the glasses hold your screens in the room" : "Follow: XrShift tracks your head", 3);
            Log.Line(stationary
                ? $"The glasses are in stationary mode (camera on): profile {workspace.Current.Name}, {layout.Screens.Count} screens, head held still."
                : $"The glasses are in follow mode: profile {workspace.Current.Name}, {layout.Screens.Count} screens.");
        }
        settings.SetGlassesMode(false);
        bool modeWarned = false;
        var modeTimer = new System.Windows.Forms.Timer(parts) { Interval = 250 };
        modeTimer.Tick += (_, _) =>
        {
            if (gameMode) return;
            // Older glasses software (seen on a One Pro with 1.7.0) refuses the mode question. Then the app cannot
            // know, assumes Follow, and must say so: in Anchor both the glasses and the app would steady the picture.
            if (glassesControl.Mode is null && !modeWarned && clock.Elapsed.TotalSeconds > 6)
            {
                modeWarned = true;
                Notice("These glasses do not report their mode. Set them to Follow in their own menu.", 12);
            }
            bool anchored = glassesControl.Stationary ?? camera.Anchored;
            if (anchored != stationary) SwitchMode(anchored);
        };
        modeTimer.Start();

        // Break reminder: a line in the glasses every so many minutes.
        double lastBreak = clock.Elapsed.TotalSeconds;
        var breakTimer = new System.Windows.Forms.Timer(parts) { Interval = 15000 };
        breakTimer.Tick += (_, _) =>
        {
            double time = clock.Elapsed.TotalSeconds;
            if (view.BreakMinutes <= 0 || gameMode || time - lastBreak < view.BreakMinutes * 60) return;
            lastBreak = time;
            lock (gate)
            {
                notice = "Break time: look at something far away for 20 seconds";
                noticeUntil = time + 20;
            }
            Log.Line($"Break reminder after {view.BreakMinutes} minutes.");
        };
        breakTimer.Start();

        // ---- glasses setup --------------------------------------------------------------------------
        // Ctrl+Alt+C, the Calibrate button or calibration.start: the glasses show only the guide over the room, and the
        // wearer is led through it there. Each step's result is saved and used the moment it is measured. Escape puts
        // everything back as it was; afterwards Undo calibration does the same, until the app quits.
        // On the glasses its keys work from any program while it runs, since the wearer cannot see which window has the
        // keyboard; they are given back the moment it ends.
        Keys[] setupKeys = [Keys.Space, Keys.Return, Keys.Back, Keys.S, Keys.Escape];
        (SetupValues Values, Vector3 LiveBias)? undoTo = null, undoBeforeRun = null;
        Vector3 liveBiasAtStart = Vector3.Zero;
        SetupValues inUse = new(null, null, view.FovDeg, view.LevelDeg, view.RollDeg); // what setup has applied so far
        bool changedThisRun = false;
        IReadOnlyList<string> lastResults = [];
        string setupMessage = "";
        int logged = 0;
        var setupTimer = new System.Windows.Forms.Timer(parts) { Interval = 50 };

        SetupValues ValuesNow() =>
            new(TrackingConfig.SavedAxes(), TrackingConfig.TryLoadGyroBias(out Vector3 saved) ? saved : null, view.FovDeg, view.LevelDeg, view.RollDeg);

        // Saves and uses what differs: the mounting and gyro files, and view angle, level and lean in view.txt.
        void UseValues(SetupValues from, SetupValues to, Vector3 liveBias)
        {
            try { TrackingConfig.Restore(to.Axes, to.GyroBias); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Line($"Could not save glasses setup: {e.Message}"); }
            if (to.Axes != from.Axes) tracker.Axes = to.Axes ?? AxisMap.Default; // before the edge steps, which read the head in these axes
            // No saved offset to go back to: the one the tracker had worked out live before setup.
            if (to.GyroBias != from.GyroBias) tracker.SetGyroBias(to.GyroBias ?? liveBias);
            if ((to.FovDeg, to.LevelDeg, to.LeanDeg) == (from.FovDeg, from.LevelDeg, from.LeanDeg)) return;
            view = view with
            {
                FovDeg = Math.Clamp(to.FovDeg, ViewSettings.MinFovDeg, ViewSettings.MaxFovDeg),
                LevelDeg = Math.Clamp(to.LevelDeg, -ViewSettings.MaxLevelDeg, ViewSettings.MaxLevelDeg),
                RollDeg = Math.Clamp(to.LeanDeg, -ViewSettings.MaxRollDeg, ViewSettings.MaxRollDeg),
            };
            settings.SetView(view);
            Touch();
        }

        void ShowSetup()
        {
            SetupRun? run = shared.Calibration;
            SetupScreen? screen = null;
            IReadOnlyList<string> results = lastResults;
            if (run is not null) lock (run) { screen = run.Screen; results = [.. run.Results]; }
            shared.Guide = screen;
            settings.SetCalibration(screen, undoTo is not null, results, setupMessage);
        }

        string? Refuse(string why)
        {
            setupMessage = why;
            ShowSetup();
            return why;
        }

        string? StartSetup(SetupStepKind from)
        {
            if (shared.Calibration is not null) return Refuse("Glasses setup is already running.");
            string? why = gameMode || vrMode ? "Leave plain screen or VR mode first: glasses setup draws its guide in the glasses."
                : stereo ? "Switch 3D off first (Ctrl+Alt+D): the guide's lines must be drawn as one picture."
                : link.State != XrShift.Tracking.LinkState.Connected || !tracker.HasPose ? "Glasses setup needs the glasses connected and sending head motion."
                : null;
            if (why is not null)
            {
                Notice(why, 5);
                return Refuse(why);
            }
            if (carried is not null) SetDown();
            inUse = ValuesNow();
            liveBiasAtStart = tracker.GyroBias;
            undoBeforeRun = undoTo;
            changedThisRun = false;
            logged = 0;
            var run = new SetupRun(new SetupOptions(tracker.Axes, inUse.GyroBias, view.FovDeg,
                DisplayOptics.OnePro.Width, DisplayOptics.OnePro.Height, from), inUse);
            // Only on the glasses: in a window on the PC screen the wearer sees the screen, and the keys reach that window.
            List<Keys> refused = fullScreen && keys ? setupKeys.Where(k => !window.AddPlainKey(k)).ToList() : [];
            if (refused.Count > 0) Log.Line($"Glasses setup: another program holds {string.Join(", ", refused)}; use the settings window's buttons for those.");
            shared.Calibration = run;
            setupMessage = "";
            setupTimer.Start();
            Log.Line($"Glasses setup started from {SetupRun.Name(from)}.");
            ShowSetup();
            return null;
        }

        void EndSetup(bool cancel)
        {
            if (shared.Calibration is not { } run) return;
            shared.Calibration = null;
            foreach (Keys k in setupKeys) window.RemovePlainKey(k);
            setupTimer.Stop();
            lock (run) lastResults = [.. run.Results];
            if (cancel && changedThisRun)
            {
                UseValues(inUse, run.Before, liveBiasAtStart);
                undoTo = undoBeforeRun;
                lastResults = [.. lastResults, "Cancelled: everything is back as it was before this setup."];
            }
            else if (cancel) lastResults = [.. lastResults, "Cancelled: nothing was changed."];
            lock (gate) recenter = true; // a new mounting changes which way is straight ahead
            Log.Line(cancel ? "Glasses setup cancelled." : "Glasses setup finished.");
            ShowSetup();
        }

        string? UndoSetup()
        {
            if (shared.Calibration is not null) return Refuse("Finish or cancel glasses setup first.");
            if (undoTo is not { } back) return Refuse("There is no glasses setup to undo.");
            UseValues(ValuesNow(), back.Values, back.LiveBias);
            undoTo = null;
            lastResults = ["Undone: view angle, level, lean, sensor axes and gyro reading are back as they were before the last glasses setup."];
            setupMessage = "";
            lock (gate) recenter = true;
            Log.Line("Glasses setup undone.");
            ShowSetup();
            return null;
        }

        string? SetupAction(string action)
        {
            if (action == "undo") return UndoSetup();
            if (shared.Calibration is not { } run) return Refuse("Glasses setup is not running.");
            bool finished;
            lock (run) finished = run.Finished;
            switch (action)
            {
                case "press" when finished:
                    EndSetup(cancel: false);
                    return null;
                case "press":
                    Quaternion head = tracker.GetPose().Orientation; // the raw pose the maths expects, not the display pose
                    lock (run) run.Confirm(head);
                    break;
                case "retry": lock (run) run.Retry(); break;
                case "skip": lock (run) run.Skip(); break;
                case "cancel":
                    // Once finished everything is saved: Escape only closes, and Undo is the way back.
                    EndSetup(cancel: !finished);
                    return null;
                default: return Refuse($"Unknown glasses setup action '{action}'.");
            }
            ShowSetup();
            return null;
        }

        setupTimer.Tick += (_, _) =>
        {
            if (shared.Calibration is not { } run) return;
            SetupValues? changed;
            List<string> fresh;
            lock (run)
            {
                changed = run.TakeChanges();
                fresh = run.Results.Skip(logged).ToList();
            }
            logged += fresh.Count;
            foreach (string line in fresh) Log.Line("Glasses setup: " + line);
            if (changed is not null)
            {
                if (!changedThisRun) undoTo = (run.Before, liveBiasAtStart);
                changedThisRun = true;
                UseValues(inUse, changed, liveBiasAtStart);
                inUse = changed;
            }
            ShowSetup();
        };

        System.Text.Json.Nodes.JsonObject SetupState()
        {
            HeadPose pose = tracker.GetPose();
            var state = new System.Text.Json.Nodes.JsonObject
            {
                ["active"] = shared.Calibration is not null,
                ["canUndo"] = undoTo is not null,
                ["message"] = setupMessage,
                ["pose"] = new System.Text.Json.Nodes.JsonObject { ["yawDeg"] = pose.YawDeg, ["pitchDeg"] = pose.PitchDeg, ["rollDeg"] = pose.RollDeg },
                ["view"] = new System.Text.Json.Nodes.JsonObject { ["fovDeg"] = view.FovDeg, ["levelDeg"] = view.LevelDeg, ["leanDeg"] = view.RollDeg },
            };
            IReadOnlyList<string> results = lastResults;
            if (shared.Calibration is { } run)
            {
                lock (run)
                {
                    SetupScreen s = run.Screen;
                    state["step"] = run.CheckingMode ? "mode" : run.Finished ? "done" : run.Setup.Step.ToString();
                    state["waiting"] = run.CheckingMode ? "Press" : run.Setup.Waiting.ToString();
                    state["progress"] = s.Progress;
                    state["instruction"] = s.Instruction;
                    state["note"] = s.Note;
                    if (!run.CheckingMode && run.Setup.Target is { } t)
                        state["target"] = new System.Text.Json.Nodes.JsonObject { ["posture"] = t.Posture.ToString(), ["xPx"] = t.XPx, ["drawnTiltDeg"] = t.DrawnTiltDeg };
                    results = [.. run.Results];
                }
            }
            state["results"] = new System.Text.Json.Nodes.JsonArray([.. results.Select(r => (System.Text.Json.Nodes.JsonNode?)r)]);
            return state;
        }

        calibrate = (action, from) =>
        {
            string? refused = action switch
            {
                "start" => StartSetup(SetupRun.ParseStep(from) ?? SetupStepKind.Rest),
                "status" => null,
                _ => SetupAction(action),
            };
            return refused is null ? (SetupState(), null) : (null, refused);
        };
        settings.CalibrationStartRequested += from => StartSetup(from);
        settings.CalibrationAction += action => SetupAction(action);
        static string ActionOf(Keys key) => key switch { Keys.Space or Keys.Return => "press", Keys.Back => "retry", Keys.S => "skip", _ => "cancel" };
        window.PlainKey += key => SetupAction(ActionOf(key));

        menuPick = choice =>
        {
            string name = choice >= 0 && choice < MenuChoices.Length ? MenuChoices[choice] : "";
            Log.Line($"Hand quick menu: {name}.");
            switch (choice)
            {
                case 0:
                    blank = !blank;
                    settings.SetBlank(blank);
                    Publish();
                    Notice(blank ? "Screens hidden: open your hand again to bring them back" : "Screens back", 3);
                    break;
                case 1:
                    Act(Keys.D0);
                    Notice("Recentred: the way you face is the front", 2);
                    break;
                case 2:
                    putSelectedHere();
                    break;
                case 3:
                    UseWorkspace(workspace.Select((workspace.Active + 1) % workspace.Profiles.Count));
                    Notice($"Profile: {workspace.Current.Name}", 2);
                    break;
            }
        };

        // Opening a bot: its session's window comes to the front, a finished bot stops being news, a dead one is cleared.
        openObject = id =>
        {
            if (deck.Find(id) is not { } o) return;
            IntPtr handle = o.Pid is { } pid ? BotProcess.WindowOf(pid) : IntPtr.Zero;
            UseDeck(deck.Opened(id));
            if (handle != IntPtr.Zero && deck.Find(id) is { } still) pinBotWindow(handle, still);
            if (handle != IntPtr.Zero) WindowFinder.Activate(handle);
            api?.Publish(ControlApi.ObjectEvent("object.activated", id));
            Notice(handle != IntPtr.Zero ? $"Opened {o.Label}" : $"{o.Label}: no window to open", 2);
        };

        // Telling a bot something: its window comes forward and the words are typed into it, without Enter, so
        // the wearer reads what was heard before sending it. Nothing is typed unless that window really has the keyboard.
        tellBot = (id, text) =>
        {
            if (deck.Find(id) is not { Pid: { } pid } bot) return "That bot has no session to type into.";
            IntPtr handle = BotProcess.WindowOf(pid);
            if (handle == IntPtr.Zero) return "That bot has no window to type into.";
            openObject(id);
            var typeTimer = new System.Windows.Forms.Timer(parts) { Interval = 400 };
            typeTimer.Tick += (_, _) =>
            {
                typeTimer.Stop();
                typeTimer.Dispose();
                if (WindowFinder.Foreground() != handle) { Notice($"Could not bring {bot.Label} forward; nothing was typed.", 4); return; }
                // SendKeys reads + ^ % ~ ( ) { } [ ] as keys: wrap each so it is typed as itself.
                SendKeys.Send(System.Text.RegularExpressions.Regex.Replace(text, @"[+^%~(){}\[\]]", "{$0}"));
                Notice($"Typed to {bot.Label}. Press Enter to send.", 4);
            };
            typeTimer.Start();
            return null;
        };

        // Push to talk (Ctrl+Alt+T): one spoken command, handled exactly as the same sentence sent to voice.say.
        using var voice = new Voice();
        listen = () => voice.Listen(new VoiceCommands.Known(layout.Screens, deck.Objects, workspace.Profiles.Select(p => p.Name).ToList(), host.Projects));
        voice.Status += text => { try { window.BeginInvoke(() => { Notice(text, 4); settings.SetVoiceResult(null, text); }); } catch (InvalidOperationException) { } };
        voice.Heard += text =>
        {
            try
            {
                window.BeginInvoke(() =>
                {
                    var request = new System.Text.Json.Nodes.JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "voice.say",
                        ["params"] = new System.Text.Json.Nodes.JsonObject { ["text"] = text },
                    };
                    string reply = ControlApi.Handle(request.ToJsonString(), host, out _) ?? "";
                    string? problem = System.Text.Json.Nodes.JsonNode.Parse(reply)?["error"]?["message"]?.GetValue<string>();
                    Notice(problem is null ? $"\"{text}\"" : $"\"{text}\": {problem}", 4);
                    settings.SetVoiceResult(text, problem);
                });
            }
            catch (InvalidOperationException) { }
        };

        void Act(Keys key)
        {
            switch (key)
            {
                case Keys.C:
                    StartSetup(SetupStepKind.Rest);
                    settings.ShowCalibration();
                    return;
                case Keys.D0 or Keys.Home or Keys.Space:
                    if (stationary)
                    {
                        // The room turns so its front is where you look, and the glasses glide the picture there.
                        tracker.Recenter();
                        shared.HeldYaw = shared.HeldPitch = 0;
                        glassesControl.RecentreHeldPicture();
                        return;
                    }
                    if (vrMode) tracker.Recenter(); // the scene is not drawing in VR mode, so recentre here
                    else lock (gate) recenter = true;
                    return;
                case Keys.V:
                    SetVrMode(!vrMode);
                    return;
                case Keys.G:
                    if (vrMode) SetVrMode(false);
                    else SetGameMode(!gameMode);
                    return;
                case Keys.M:
                    PointerToGaze();
                    return;
                case Keys.W:
                    BringHere(WindowFinder.Foreground(), "key");
                    return;
                case Keys.L:
                    // Off, then jump, then follow, then off again.
                    view = !view.LookPointer ? view with { LookPointer = true, LookFollow = false }
                        : !view.LookFollow ? view with { LookFollow = true }
                        : view with { LookPointer = false, LookFollow = false };
                    break;
                case Keys.K:
                    view = view with { Hands = !view.Hands };
                    break;
                case Keys.P:
                    PickUpOrSetDown();
                    return;
                case Keys.PageUp or Keys.PageDown when carried is not null && layout.Find(carried) is { } held:
                    layout = layout.With(Layout.Clamp(held with { Scale = held.Scale * (key == Keys.PageUp ? 1.06f : 1f / 1.06f) }));
                    Publish();
                    return;
                case Keys.T:
                    Notice("Listening...", 5);
                    listen();
                    return;
                case Keys.D:
                    // Sharp: one picture, best for text. 3D: a picture per eye, so near things look near. The layout is the same in both.
                    stereo = !stereo;
                    Notice(stereo ? "3D on. Set the glasses to 3D side-by-side too." : "Sharp. Set the glasses back to their normal mode.", 5);
                    Publish();
                    return;
                case Keys.O:
                {
                    // The bot being looked at; failing that, one that is waiting for the wearer.
                    float zoom = view.Zoom > 0.01f ? view.Zoom : 1f; // bots are not zoomed with the screens
                    SceneObject? target = GazeAngles() is { } at ? deck.Nearest(at.Yaw * zoom, at.Pitch * zoom) : null;
                    target ??= deck.Objects.FirstOrDefault(o => o.State == BotState.NeedsYou);
                    if (target is null) Notice("No bot there. Look at one and press Ctrl+Alt+O.", 3);
                    else openObject(target.Id);
                    return;
                }
                case Keys.B:
                    blank = !blank;
                    settings.SetBlank(blank);
                    Publish();
                    return;
                case Keys.PageUp: view = view with { Zoom = MathF.Min(ViewSettings.MaxZoom, MathF.Round(view.Zoom * 20 + 1) / 20) }; break;
                case Keys.PageDown: view = view with { Zoom = MathF.Max(ViewSettings.MinZoom, MathF.Round(view.Zoom * 20 - 1) / 20) }; break;
                case Keys.D1 or Keys.D2 or Keys.D3 or Keys.D4:
                    UseWorkspace(workspace.Select(key - Keys.D1));
                    return;
                case Keys.S:
                    ShowSettings();
                    return;
                case Keys.H:
                    bool showing;
                    lock (gate)
                    {
                        showing = statusPinned || clock.Elapsed.TotalSeconds < overlayUntil;
                        overlayUntil = 0;
                    }
                    statusPinned = !showing;
                    settings.SetStatusPinned(statusPinned);
                    Publish();
                    return;
                case Keys.OemOpenBrackets: view = view with { DelayMs = MathF.Max(ViewSettings.MinDelayMs, view.DelayMs - 2f) }; break;
                case Keys.OemCloseBrackets: view = view with { DelayMs = MathF.Min(ViewSettings.MaxDelayMs, view.DelayMs + 2f) }; break;
                case Keys.OemMinus: view = view with { FovDeg = MathF.Max(ViewSettings.MinFovDeg, view.FovDeg - 0.5f) }; break;
                case Keys.Oemplus: view = view with { FovDeg = MathF.Min(ViewSettings.MaxFovDeg, view.FovDeg + 0.5f) }; break;
                default: return;
            }
            settings.SetView(view);
            Touch(showOverlay: true);
        }

        window.KeyDown += (_, e) =>
        {
            // During setup its keys are setup's, also when another program kept one of them from being taken globally.
            if (!keys) return;
            if (shared.Calibration is not null && setupKeys.Contains(e.KeyCode))
                SetupAction(ActionOf(e.KeyCode));
            else if (e.KeyCode is Keys.Escape or Keys.Q) window.Close();
            else Act(e.KeyCode);
        };
        window.HotKey += key =>
        {
            if (key == Keys.Q) window.Close();
            else Act(key);
        };

        // ---- keeping up with monitors coming and going, and the mouse off the glasses -------------
        // The glasses' own desktop area is covered by the scene, so there is nothing to click there and a
        // pointer in it is lost from the real screens. Whenever it strays in, it is carried across to the
        // monitor on the far side, or put back if there is none. That works wherever Windows has placed the
        // glasses among the monitors, including between two of them.
        // Monitors no visible screen shows are kept free of the pointer the same way: nothing would draw it there.
        bool guideCursor = false;
        Point lastOutside = Cursor.Position;
        List<Rectangle> blocked = [], monitorAreas = [];
        void PlanCursor()
        {
            guideCursor = fullScreen && !direct && !gameMode && !args.Contains("--free-cursor") && sources.Count > 0;
            monitorAreas = sources.Select(d => d.Bounds).ToList();
            blocked = [area, .. DesktopPath.Unseen(layout, sources.Select(d => (d.Id, d.Bounds)))];
        }
        planCursor = PlanCursor;
        PlanCursor();

        // The parts of the desktop that shown screens show, as drawn now: monitors first (their picture carries the
        // pointer itself), then pinned windows wherever they really are.
        List<(ScreenSpec, Rectangle)> ShownAreas(bool windowsToo)
        {
            Snapshot now = shared.Snapshot;
            var shownAreas = new List<(ScreenSpec, Rectangle)>();
            foreach (ScreenSpec spec in now.Layout.Screens)
                if (spec.DisplayName is { } id && now.Sources.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) is { } d) shownAreas.Add((spec, d.Bounds));
            if (windowsToo)
                foreach (ScreenSpec spec in now.Layout.Screens)
                    if (spec.Window is not null && now.Windows.TryGetValue(spec.Source, out IntPtr handle) && !WindowFinder.Minimized(handle) && WindowFinder.Bounds(handle) is { } r) shownAreas.Add((spec, r));
            return shownAreas;
        }
        // The way the scene is drawn from right now: the held picture's direction in stationary mode, else the head.
        Quaternion ViewNow()
        {
            if (shared.HoldHead) return Quaternion.CreateFromYawPitchRoll(shared.HeldYaw * DegToRad, shared.HeldPitch * DegToRad, 0);
            Quaternion trim = Quaternion.CreateFromYawPitchRoll(0, -view.LevelDeg * DegToRad, view.RollDeg * DegToRad);
            Quaternion drift = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -shared.FollowYaw);
            return drift * (tracker.HasPose ? tracker.GetPose().Orientation : Quaternion.Identity) * trim;
        }

        // Finding the pointer: a ring round it when it starts moving after a rest, an arrow labelled "mouse" while it
        // moves out of view, and once a session the key that brings it to where the wearer looks.
        var beacon = new PointerBeacon();
        Point beaconSeen = Cursor.Position;
        Mark? mouseMark = null, windowMark = null;
        double windowMarkUntil = 0;
        void ShowMarks()
        {
            var marks = new List<Mark>(2);
            if (mouseMark is { } mouse) marks.Add(mouse);
            if (windowMark is { } opened && clock.Elapsed.TotalSeconds < windowMarkUntil) marks.Add(opened);
            if (!marks.SequenceEqual(shared.Marks)) shared.Marks = [.. marks];
        }
        void BeaconTick(bool slowTick)
        {
            Point now = Cursor.Position;
            bool moved = now != beaconSeen;
            beaconSeen = now;
            if (!moved && !slowTick) return;
            mouseMark = null;
            if (!gameMode && !blank && !WindowFinder.MouseTakenByProgram())
            {
                Vector3? spot = RoomSpot.Of(now, ShownAreas(windowsToo: true), OpticsFor(view));
                bool inView = spot is { } p && RoomSpot.InView(p, ViewNow(), OpticsFor(view));
                (bool ring, bool fresh, bool tell) = beacon.Step(clock.Elapsed.TotalSeconds, moved, inView);
                if (tell) Notice("Lost the mouse? Ctrl+Alt+M brings it to where you look", 6);
                if (spot is { } at && (ring || fresh)) mouseMark = new Mark(at, fresh ? "mouse" : "", ring);
            }
            ShowMarks();
        }

        var cursorTimer = new System.Windows.Forms.Timer(parts) { Interval = 10 };
        int cursorTicks = 0;
        cursorTimer.Tick += (_, _) =>
        {
            bool slowTick = ++cursorTicks % 5 == 0;
            LookTick(slowTick);
            Point at = Cursor.Position;
            if (guideCursor && blocked.Any(b => b.Contains(at)))
                PlacePointer(DesktopPath.Redirect(at, lastOutside, blocked, monitorAreas));
            else lastOutside = at;
            BeaconTick(slowTick);
        };
        cursorTimer.Start();

        static string Signature(IEnumerable<DisplayInfo> all) =>
            string.Join("|", all.Select(d => $"{d.Id}@{d.Bounds}"));
        string seen = Signature(displays);
        var displayTimer = new System.Windows.Forms.Timer(parts) { Interval = 2000 };
        displayTimer.Tick += (_, _) =>
        {
            List<DisplayInfo> now = Displays.List();
            if (direct ? !directOut!.StillConnected : fullScreen && !gameMode && !now.Any(d => d.Id == targetId))
            {
                // The glasses are gone: end this session; the tray takes over until they are back.
                s_unplugged = true;
                displayTimer.Stop();
                window.Close();
                return;
            }
            string signature = Signature(now);
            if (signature == seen) return;
            seen = signature;

            // Windows can move the glasses among the monitors at any time (Display settings, or a monitor
            // coming or going). The scene and the rule that keeps the pointer out of it have to move along:
            // left where they were, they blocked the pointer from whichever monitor took that place.
            if (fullScreen && !direct && now.FirstOrDefault(d => d.Id == targetId) is { } resized && resized.Bounds.Size != area.Size)
            {
                Log.Line($"The glasses changed to {resized.Bounds.Width}x{resized.Bounds.Height} (their ultrawide, say); starting the scene again at that size.");
                s_unplugged = true;
                displayTimer.Stop();
                window.Close();
                return;
            }
            if (fullScreen && now.FirstOrDefault(d => d.Id == targetId) is { } glassesNow && glassesNow.Bounds != area)
            {
                area = glassesNow.Bounds;
                if (!gameMode && window.Bounds != area) window.Bounds = area;
                lastOutside = Cursor.Position;
                Log.Line($"The glasses are now at {area.X},{area.Y} on the desktop; the scene moved with them.");
            }

            sources = SourcesOf(now);
            bool Connected(string? id) => id is not null && sources.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

            // The driver rebuilds all its monitors when their number changes, so wait until Windows shows as
            // many as the driver says before tidying up; in between, monitors come and go.
            int? virtualCount = VirtualMonitors.Installed ? VirtualMonitors.Count() : null;
            bool settled = virtualCount is null || sources.Count(VirtualMonitors.IsVirtual) == virtualCount;
            if (settled)
            {
                if (removing is { } gone && layout.Find(gone) is { } goneSpec)
                {
                    // The driver always drops its last monitor, which may not be the one that was picked. If the
                    // picked screen's monitor survived, hand it to the screen whose monitor went instead, so the
                    // screens that stay keep their places.
                    ScreenSpec? orphan = layout.Screens.FirstOrDefault(s =>
                        s.Id != gone && VirtualMonitors.IsVirtualId(s.DisplayName) && !Connected(s.DisplayName));
                    if (orphan is not null && Connected(goneSpec.DisplayName))
                        layout = layout.With(orphan with { Source = goneSpec.Source });
                    layout = layout.Remove(gone);
                }
                removing = null;
            }

            // A screen whose virtual monitor is away (game mode, another profile) keeps its place and waits.
            int before = layout.Screens.Count;
            Layout beforeLayout = layout;
            layout = placeNewAtGaze(beforeLayout, LayoutStore.WithScreensFor(layout, AutoAddable(), OpticsFor(view), out IReadOnlyList<string> hidden, sources));
            tellHidden(hidden);
            if (settled) adding = false;
            Log.Line($"Monitors changed: {sources.Count} to show, {layout.Screens.Count} screens{(layout.Screens.Count > before ? " (new screen added)" : "")}.");
            LineUpDesktops(now);
            ShowVirtual();
            workspace = workspace.WithLayout(layout);
            PlanCursor();
            settings.SetLayout(layout, sources);
            Touch();
        };
        displayTimer.Start();

        // A window that opens where the wearer cannot see it (a screen behind them, a monitor no screen shows) is
        // announced with its program's name and an arrow to it; or, with Bring new windows on, brought to where they look.
        void NewWindow(WindowInfo w)
        {
            string name = WindowFinder.FriendlyName(w.Program);
            if (view.BringNewWindows)
            {
                BringHere(w.Handle, "new window");
                return;
            }
            if (WindowFinder.Minimized(w.Handle) || WindowFinder.Bounds(w.Handle) is not { } b) return;
            var middle = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
            if (RoomSpot.Of(middle, ShownAreas(windowsToo: false), OpticsFor(view)) is not { } spot)
            {
                Notice($"{name} opened where no screen shows it. Click it, then Ctrl+Alt+W brings it here", 6);
                Log.Line($"A window of {w.Program} opened on a monitor no screen shows.");
                return;
            }
            if (RoomSpot.InView(spot, ViewNow(), OpticsFor(view))) return;
            windowMark = new Mark(spot, name, Ring: false);
            windowMarkUntil = clock.Elapsed.TotalSeconds + 6;
            ShowMarks();
            Notice($"{name} opened out of view: follow the arrow, or click it and Ctrl+Alt+W brings it here", 6);
            Log.Line($"A window of {w.Program} opened out of view.");
        }
        // The windows there were at the start, and at the last look, are not new.
        HashSet<IntPtr> windowsSeen = [.. WindowFinder.List().Select(w => w.Handle)];

        // Pinned windows open and close on their own schedule; look for the missing ones now and then.
        var windowTimer = new System.Windows.Forms.Timer(parts) { Interval = 1500 };
        windowTimer.Tick += (_, _) =>
        {
            List<WindowInfo> all = WindowFinder.List();
            // Several at once (a program starting up) get one announcement, for the one in front.
            WindowInfo? opened = all.FirstOrDefault(w => !windowsSeen.Contains(w.Handle) && !pinned.Values.Contains(w.Handle));
            windowsSeen = [.. all.Select(w => w.Handle)];
            if (opened is not null && !gameMode && !blank) NewWindow(opened);
            if (clock.Elapsed.TotalSeconds >= windowMarkUntil && windowMark is not null) { windowMark = null; ShowMarks(); }

            IReadOnlyDictionary<string, IntPtr> found = WindowFinder.Resolve(layout, pinned, search: true);
            if (ReferenceEquals(found, pinned)) return;
            pinned = found;
            Publish();
        };
        windowTimer.Start();

        // Virtual monitors start at 800x600, and the driver forgets a monitor's mode whenever it rebuilds its
        // monitors. The app owns their modes (size and refresh are set per screen in the settings window) and
        // puts each one right whenever it drifts: not more often than every ten seconds, and it gives up on a
        // change Windows has refused three times.
        var tuned = new Dictionary<string, (double At, int Refused)>(StringComparer.OrdinalIgnoreCase);
        var sizesAdded = new HashSet<(int, int)>();
        // The extra monitors are lined up on the Windows desktop the way they sit in the room: a screen on your left
        // is to the left of the laptop screen, so the mouse crosses between them the way you would expect. The
        // glasses' own area goes below the laptop screen, out of the way. Done once for each set of monitors, so it
        // never fights Windows, and only for monitors XrShift made.
        void LineUpDesktops(List<DisplayInfo> now)
        {
            if (!fullScreen || direct || gameMode || args.Contains("--keep-modes")) return;
            List<DisplayInfo> extra = now.Where(VirtualMonitors.IsVirtual).ToList();
            ScreenSpec? ScreenOf(DisplayInfo d) => layout.Screens.FirstOrDefault(s => string.Equals(s.DisplayName, d.Id, StringComparison.OrdinalIgnoreCase));
            float YawOf(DisplayInfo d) => ScreenOf(d)?.YawDeg ?? -90f;
            // The screen everything is lined up around: the real monitor the wearer can see nearest straight ahead.
            // Windows' main display is only the fallback: with a laptop open under a big monitor it is often the
            // laptop's own panel, which may not even be shown in the room.
            DisplayInfo? main = now.Where(d => !d.IsGlasses && !VirtualMonitors.IsVirtual(d) && ScreenOf(d) is { Visible: true })
                    .OrderBy(d => MathF.Abs(YawOf(d))).FirstOrDefault()
                ?? now.FirstOrDefault(d => d.Primary && !d.IsGlasses);
            if (extra.Count == 0 || main is null) return;
            float mainYaw = YawOf(main) is var y && y > -90f ? y : 0f;
            // Where each extra monitor goes: beside, above or below its neighbour, as the screens sit in the room.
            // The glasses' own area goes below the main monitor, out of the way, and the other real monitors stay put.
            var keepFree = now.Where(d => !d.IsGlasses && !VirtualMonitors.IsVirtual(d) && d.Id != main.Id).Select(d => d.Bounds).ToList();
            DisplayInfo? glassesNow = target is { IsGlasses: true } ? now.FirstOrDefault(d => d.Id == targetId) : null;
            if (glassesNow is not null) keepFree.Add(new Rectangle(main.Bounds.Left, main.Bounds.Bottom, glassesNow.Bounds.Width, glassesNow.Bounds.Height));
            var planned = DesktopPlan.Place(main.Bounds, mainYaw, ScreenOf(main)?.PitchDeg ?? 0f,
                extra.Select(d => new DesktopPlan.Screen(d.DeviceName, d.Bounds.Width, d.Bounds.Height, YawOf(d), ScreenOf(d)?.PitchDeg ?? 0f)).ToList(), keepFree);
            List<(string, int, int)> places = [.. planned];
            if (glassesNow is not null) places.Add((glassesNow.DeviceName, main.Bounds.Left, main.Bounds.Bottom));
            // Done again whenever moving a screen in the room changes where its monitor belongs: otherwise the
            // mouse keeps crossing where the screen used to be.
            string set = main.Id + ">" + string.Join("|", places.Select(p => $"{p.Item1}@{p.Item2},{p.Item3}"));
            if (set == linedUpFor) return;
            linedUpFor = set;
            bool already = places.All(p => now.FirstOrDefault(d => d.DeviceName == p.Item1) is { } d && d.Bounds.X == p.Item2 && d.Bounds.Y == p.Item3);
            if (already) return;
            Log.Line(DisplayModes.TryPlace(places, out string why)
                ? $"Lined up {extra.Count} extra monitor(s) on the desktop to match the room."
                : $"Could not line up the extra monitors: {why}");
        }

        relineDesktop = () => { if (!window.IsDisposed) LineUpDesktops(Displays.List()); };

        void TuneVirtualMonitors()
        {
            if (!fullScreen || args.Contains("--keep-modes") || shownOn.RefreshHz < 1) return;
            foreach (DisplayInfo d in sources.Where(VirtualMonitors.IsVirtual))
            {
                // 60 Hz unless the wearer marked the screen for playing: kinder to the graphics card, no cost in
                // delay (see DisplayModes.Better). The size is the wearer's choice too: standard or an ultrawide.
                ScreenSpec? spec = layout.Screens.FirstOrDefault(s => string.Equals(s.DisplayName, d.Id, StringComparison.OrdinalIgnoreCase));
                int hz = spec is { RefreshHz: >= 100 } ? 120 : 60;
                (int wantWidth, int wantHeight) = (spec?.PixelWidth ?? 0, spec?.PixelHeight ?? 0);

                if (DisplayModes.Current(d.DeviceName) is not { } current) continue;
                List<DisplayModes.Mode> available = DisplayModes.Available(d.DeviceName);
                if (wantWidth > 0 && !available.Any(m => m.Width == wantWidth && m.Height == wantHeight))
                {
                    // The driver's own list has no ultrawides. Add the size to it, once; it rebuilds its monitors
                    // and this runs again when Windows shows them.
                    if (manageMonitors && sizesAdded.Add((wantWidth, wantHeight)))
                    {
                        LetGoOfVirtualCopies(); // reloading the driver rebuilds every virtual monitor
                        bool added = VirtualMonitors.TryAddSize(wantWidth, wantHeight, out string trouble);
                        Log.Line(added
                            ? $"Asked the virtual monitor driver to offer {wantWidth}x{wantHeight}."
                            : $"Could not add {wantWidth}x{wantHeight} to the virtual monitor driver ({trouble}).");
                        if (added) return;
                    }
                    (wantWidth, wantHeight) = (0, 0);
                }
                if (DisplayModes.Better(current, available, hz, wantWidth, wantHeight) is not { } better) continue;
                string change = $"{d.Id}|{current}|{better}";
                double time = clock.Elapsed.TotalSeconds;
                tuned.TryGetValue(change, out (double At, int Refused) tried);
                if (tried.Refused >= 3 || (tried.At > 0 && time - tried.At < 10)) continue;
                bool done = DisplayModes.TrySet(d.DeviceName, better, out string why);
                tuned[change] = (Math.Max(time, 0.001), done ? 0 : tried.Refused + 1);
                Log.Line(done
                    ? $"Virtual monitor {d.DeviceName}: {current} -> {better}."
                    : $"Virtual monitor {d.DeviceName}: could not set {better} ({why}).");
            }
        }
        TuneVirtualMonitors();
        tuneNow = TuneVirtualMonitors;
        var tuneTimer = new System.Windows.Forms.Timer(parts) { Interval = 3000 };
        tuneTimer.Tick += (_, _) => TuneVirtualMonitors();
        tuneTimer.Start();

        // ---- drawing -------------------------------------------------------------------------------
        double statusAt = 0, lastTime = 0, lastLook = 0;
        Quaternion? heldHead = null;
        long lastFrames = 0, lastSamples = 0;
        float fps = 0, trackHz = 0;

        string workspaceName = workspace.Current.Name; // read by the drawing thread for the status line

        var previewClock = Stopwatch.StartNew();
        double previewAt = 0, ratesAt = 0;
        double previewNow0() => previewClock.Elapsed.TotalSeconds;
        void DrawFrame()
        {
            Snapshot now = shared.Snapshot;
            double time = clock.Elapsed.TotalSeconds;
            bool overlayNow, recenterNow;
            string noticeNow;
            lock (gate)
            {
                noticeNow = time < noticeUntil ? notice : "";
                overlayNow = now.StatusPinned || time < overlayUntil || noticeNow.Length > 0;
                recenterNow = recenter;
                recenter = false;
            }
            if (recenterNow)
            {
                tracker.Recenter();
                shared.FollowYaw = 0;
            }

            DisplayOptics optics = shared.HoldHead ? DisplayOptics.OnePro with { HorizontalFovDeg = now.View.HeldFovDeg } : OpticsOf(now.View);
            scene.Apply(now.Layout, now.Sources, now.Windows, optics);
            scene.Apply(now.Deck);
            scene.Update();
            // Glasses setup: only its guide over the room, so real door frames and edges can be seen.
            SetupScreen? guide = shared.Guide;
            bool guiding = guide is not null;
            scene.RulerVisible = now.View.Ruler && !guiding;
            scene.OverlayVisible = overlayNow && !guiding;

            // Look ahead by the whole trip, this app's measured share plus the glasses' own delay; the tracker
            // smooths and caps it and reports what it used.
            float predictMs = 0f;
            HeadPose pose = tracker.HasPose
                ? tracker.GetDisplayPose((float)presenter.PoseToScreenMs, now.View.DelayMs, out predictMs)
                : new HeadPose(0, 0, 0, Quaternion.Identity);

            if (time >= statusAt)
            {
                double span = Math.Max(time - lastTime, 1e-3);
                long frames = presenter.Frames, samples = link.Parser.SamplesParsed;
                if (lastTime > 0)
                {
                    fps = (float)((frames - lastFrames) / span);
                    trackHz = (float)((samples - lastSamples) / span);
                }
                lastFrames = frames;
                lastSamples = samples;
                lastTime = time;
                statusAt = time + 0.5;

                string tracking = link.State != XrShift.Tracking.LinkState.Connected || trackHz < 1
                    ? "no head tracking"
                    : $"yaw {pose.YawDeg,6:F1}  pitch {pose.PitchDeg,6:F1}  roll {pose.RollDeg,6:F1}";
                // Notices have their own area; while one shows, the status strip stays empty unless pinned.
                // Callers should pass a notice's level themselves; until they do it is guessed from its words.
                scene.Stereo = now.Stereo && !guiding;
                scene.SetNotice(guiding ? "" : noticeNow, SpaceScene.GuessNoticeLevel(noticeNow));
                if (noticeNow.Length > 0 && !now.StatusPinned) scene.SetStatus("");
                else scene.SetStatus(string.Create(CultureInfo.InvariantCulture,
                    $"{fps:F0} fps   {tracking}   predict {predictMs:F1} ms   view {now.View.FovDeg:F1}°   level {now.View.LevelDeg:F2}°   {workspaceName}{(now.View.LookPointer ? (now.View.LookFollow ? "   pointer follows" : "   look-to-move") : "")}{(now.View.Follow ? "   screens follow" : "")}{(MathF.Abs(now.View.Zoom - 1f) > 0.01f ? $"   zoom {now.View.Zoom * 100:F0}%" : "")}{(now.Blank ? "   screens hidden" : "")}") +
                    (time < 12 ? $"   ·   {recentreKey} recentres" : ""));
            }

            // The sensor-to-display angle was only measured through posture; this is the by-eye correction.
            Quaternion trim = Quaternion.CreateFromYawPitchRoll(0, -now.View.LevelDeg * DegToRad, now.View.RollDeg * DegToRad);
            float dt = (float)Math.Clamp(time - lastLook, 0, 0.1);
            lastLook = time;

            // Screens follow: outside a comfortable zone straight ahead, the screens drift after the head and
            // settle in front of it again; inside it they hold still. Off, they ease back to where they belong.
            float drift = shared.FollowYaw;
            if (now.View.Follow && tracker.HasPose)
            {
                const float zone = 28f * DegToRad;
                float off = pose.YawDeg * DegToRad - drift;
                off = MathF.IEEERemainder(off, 2 * MathF.PI);
                if (MathF.Abs(off) > zone) drift += MathF.Sign(off) * (MathF.Abs(off) - zone) * MathF.Min(1f, dt * 2.5f);
            }
            else
            {
                drift -= drift * MathF.Min(1f, dt * 3f);
            }
            shared.FollowYaw = drift;
            Quaternion head = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -drift) * pose.Orientation * trim;
            if (shared.HoldHead)
            {
                // The view into the room through the picture the glasses hold still, from the direction it faces.
                heldHead = Quaternion.CreateFromYawPitchRoll(shared.HeldYaw * DegToRad, shared.HeldPitch * DegToRad, 0);
                head = heldHead.Value;
            }
            else
            {
                heldHead = null;
            }
            scene.SetHands(shared.HandsShown && !guiding, shared.HandsFrame, shared.HandsPinching, shared.HandsStatus);
            scene.SetMenu(shared.HandsShown && !guiding ? shared.MenuItems : null, shared.MenuHighlight);

            scene.Look(head, now.View.Dim, now.Blank || guiding, dt, now.View.EasyReading);
            scene.SetMarks(shared.Marks);
            scene.PointToHiddenScreens(head, optics, now.View.EdgeArrows);
            scene.SetGuide(guide, head, optics);
            renderer.Draw(head, optics, now.Stereo && !guiding ? Camera.DefaultEyeSeparation : 0);

            if (previewNow0() - ratesAt >= 2)
            {
                double rateNow = previewNow0();
                shared.CaptureRates = scene.CaptureRates(rateNow - ratesAt);
                ratesAt = rateNow;
            }

            // The preview in the settings window, five times a second while it is open.
            double previewNow = previewClock.Elapsed.TotalSeconds;
            bool startPreview = shared.PreviewWanted && previewNow - previewAt > 0.2;
            if (startPreview) previewAt = previewNow;
            if (renderer.PreviewStep(startPreview) is { } shot) Interlocked.Exchange(ref shared.Preview, shot)?.Dispose();
        }

        afterPublish = () =>
        {
            workspaceName = workspace.Current.Name;
            PlanCursor(); // a screen shown or hidden changes which monitors the pointer may go on
        };

        Exception? failure = null;
        presenter.Start(DrawFrame, e =>
        {
            failure = e;
            try { window.BeginInvoke(window.Close); } catch (InvalidOperationException) { }
        });

        if (seconds > 0)
        {
            var timer = new System.Windows.Forms.Timer(parts) { Interval = (int)(seconds * 1000) };
            timer.Tick += (_, _) => window.Close();
            timer.Start();
        }
        if (!args.Contains("--no-settings")) ShowSettings();

        window.FormClosing += (_, _) => presenter.Stop();
        try
        {
            Application.Run(window);
        }
        finally
        {
            s_show = null;
            presenter.Stop();
        }
        Exception? windowError = s_windowError;

        // ---- shutting down -------------------------------------------------------------------------
        cursorTimer.Stop();
        handTimer.Stop();
        LetGo(); // never leave the mouse button held down
        displayTimer.Stop();
        ViewWindow.ConfineCursor(null);
        // The virtual monitors are removed by Run once this method has returned, i.e. after every copy of them,
        // the drawing device and the scene's window are gone. Removing them while those were still being torn
        // down froze the whole PC once (2026-11-17): Windows was dismantling monitors that were being let go of.
        s_ownsMonitors = manageMonitors;
        settings.HideOnClose = false;
        tray.Visible = false;
        bool savedNow = dirty;
        Save();
        double total = clock.Elapsed.TotalSeconds;
        cts.Cancel();
        reader.Join(3000);
        s_windowError = null;
        if (windowError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(windowError);

        failure ??= presenter.Failure;
        if (failure is not null)
        {
            Log.Line($"Drawing stopped: {failure.Message}");
            return 2;
        }
        if (scene.LastProblem is not null) Log.Line(scene.LastProblem);
        Log.Line(perf.Summary);
        Log.Line($"{presenter.Frames} frames in {total:F1} s ({presenter.Frames / Math.Max(total, 1e-3):F0} fps on a {shownOn.RefreshHz:F0} Hz screen), " +
            $"{presenter.SlowFrames} slow, {presenter.DrawMs:F2} ms drawing per frame.");
        (double average, double min, double max, long timed) = presenter.PoseToScreen;
        if (timed > 0)
            Log.Line($"Head pose to screen: {average:F1} ms on average ({min:F1} to {max:F1}) over {timed} frames.");
        Log.Line($"Head tracking {link.Parser.SamplesParsed} samples, last reading {trackHz:F0} Hz. " +
            $"{layout.Screens.Count} screens, {scene.LiveCount} live, {scene.Updates} picture updates.");
        Log.Line($"Ended with glasses delay {view.DelayMs:F1} ms, view angle {view.FovDeg:F1}, level {view.LevelDeg:F2}, lean {view.RollDeg:F2} degrees{(savedNow ? " (saved)" : "")}.");
        return 0;
    }
}
