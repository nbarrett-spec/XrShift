using Vortice.Direct3D11;
using XrShift.Scene;
using Panel = XrShift.Scene.Panel;

namespace XrShift.App;

/// <summary>
/// Everything floating around the wearer: one item per screen in the layout, each fed by a live
/// monitor, a live window or a stand-in picture, plus the horizon ruler, crosshair and status line.
/// All of it must be called from the drawing thread.
/// </summary>
internal sealed class SpaceScene : IDisposable
{
    sealed class LiveScreen
    {
        public required ScreenSpec Spec;
        public required SceneItem Item;
        /// <summary>The source whose frames go on this screen, or null when it shows a still picture.</summary>
        public string? Feed;
        /// <summary>What the still picture says, so it is only redrawn when that changes.</summary>
        public string Still = "";
        public Panel? Panel;
        /// <summary>Whether the layout says this screen is drawn (it is visible and has something to show).</summary>
        public bool Shown;
    }

    static readonly long RetryTicks = System.Diagnostics.Stopwatch.Frequency; // one second
    static readonly IReadOnlyDictionary<string, IntPtr> NoWindows = new Dictionary<string, IntPtr>();

    readonly Renderer _renderer;
    readonly bool _live;
    readonly Dictionary<string, LiveScreen> _screens = [];
    // Live copies, by the source they copy ("display:..." or "window:...").
    readonly Dictionary<string, MonitorCapture> _captures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, System.Drawing.Size> _captureSizes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Rectangle> _captureBounds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> _refused = new(StringComparer.OrdinalIgnoreCase); // source -> when it was last refused
    readonly SceneItem _ruler, _crosshair, _status, _hands;
    HandFrame? _handsShown;
    bool _handsPinch;
    Layout? _layout;
    IReadOnlyList<DisplayInfo>? _displays;
    IReadOnlyDictionary<string, IntPtr> _windows = NoWindows;
    readonly Dictionary<string, long> _framesSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// New pictures per second from each copied screen since the last call, e.g. "copies 60, 58, 4/s".
    /// A copy far below its screen's rate is a screen that is really updating that slowly in Windows.
    /// Call on the drawing thread only.
    /// </summary>
    public string CaptureRates(double seconds)
    {
        if (_captures.Count == 0 || seconds <= 0) return "";
        var rates = new List<string>();
        foreach ((string source, MonitorCapture capture) in _captures)
        {
            long frames = capture.Frames;
            long before = _framesSeen.GetValueOrDefault(source, frames);
            _framesSeen[source] = frames;
            rates.Add(((frames - before) / seconds).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
        }
        return $"copies {string.Join(", ", rates)}/s";
    }
    DisplayOptics? _optics;
    string _statusText = "";

    /// <summary>Monitors and windows that are being shown live right now.</summary>
    public int LiveCount => _captures.Count;
    /// <summary>Screen pictures brought up to date since start.</summary>
    public long Updates { get; private set; }
    /// <summary>The last reason something could not be shown, for the log.</summary>
    public string? LastProblem { get; private set; }

    /// <param name="live">False to draw stand-in pictures instead of copying monitors and windows (for test frames).</param>
    public SpaceScene(Renderer renderer, bool live)
    {
        _renderer = renderer;
        _live = live;

        // Horizon ruler: a band all the way round, far behind the screens.
        const float radius = 10f;
        float degreesTall = Pictures.RulerHeight / (Pictures.RulerWidth / 360f);
        float height = 2f * radius * MathF.Tan(degreesTall / 2f * MathF.PI / 180f);
        using (Bitmap ruler = Pictures.Ruler())
            _ruler = renderer.Add(PanelMesh.Build(Panel.Ring(radius, height)), ruler, distance: radius);

        using (Bitmap cross = Pictures.Crosshair())
            _crosshair = renderer.Add(PanelMesh.HeadLocked(0, 0, 1.2f, 1.2f), cross, headLocked: true);

        // Gesture mode: a small view of the hands, low on the right.
        using (Bitmap handsPicture = Pictures.Hands([], false, ""))
            _hands = renderer.Add(PanelMesh.HeadLocked(17f, -7.5f, 12f, 9f), handsPicture, headLocked: true);
        _hands.Visible = false;

        const float statusDegreesWide = 26f;
        using (Bitmap status = Pictures.Status(""))
            _status = renderer.Add(
                PanelMesh.HeadLocked(0, -12.3f, statusDegreesWide, statusDegreesWide * Pictures.StatusHeight / Pictures.StatusWidth),
                status, headLocked: true);
    }

    public bool OverlayVisible
    {
        get => _status.Visible;
        set => _status.Visible = _crosshair.Visible = value;
    }

    public bool RulerVisible
    {
        get => _ruler.Visible;
        set => _ruler.Visible = value;
    }

    /// <summary>Shows or hides the hand view and brings it up to date with the latest camera frame. Drawing thread only.</summary>
    public void SetHands(bool show, HandFrame? frame, bool pinching, string status)
    {
        _hands.Visible = show;
        if (!show || (ReferenceEquals(frame, _handsShown) && pinching == _handsPinch && frame is not null)) return;
        _handsShown = frame;
        _handsPinch = pinching;
        IReadOnlyList<System.Numerics.Vector3[]> points = frame is { Camera: true } ? frame.Hands.Select(h => h.Points).ToList() : [];
        using Bitmap picture = Pictures.Hands(points, pinching, status);
        _renderer.SetPicture(_hands, picture);
    }

    public void SetStatus(string text)
    {
        if (text == _statusText) return;
        _statusText = text;
        using Bitmap picture = Pictures.Status(text);
        _renderer.SetPicture(_status, picture);
    }

    /// <summary>
    /// Makes the scene match the layout: adds, moves, resizes and removes screens, and starts or
    /// stops copying monitors and windows. Costs nothing when called again with the same objects.
    /// </summary>
    /// <param name="displays">Monitors that may be shown (never the one the scene itself is on).</param>
    /// <param name="windows">Pinned windows that are open now: source to window handle. Looked up by the caller, off the drawing thread.</param>
    public void Apply(Layout layout, IReadOnlyList<DisplayInfo> displays, IReadOnlyDictionary<string, IntPtr>? windows, DisplayOptics optics)
    {
        windows ??= NoWindows;
        if (ReferenceEquals(layout, _layout) && ReferenceEquals(displays, _displays) && ReferenceEquals(windows, _windows) && optics == _optics) return;
        _layout = layout;
        _displays = displays;
        _windows = windows;
        _optics = optics;

        DisplayInfo? DisplayOf(ScreenSpec spec) =>
            spec.DisplayName is { } id ? displays.FirstOrDefault(d => Same(d.Id, id)) : null;

        // What should be copied live: connected monitors and open windows that a visible screen shows.
        var wanted = new Dictionary<string, ScreenSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (ScreenSpec spec in layout.Screens)
        {
            if (!_live || !spec.Visible) continue;
            if (DisplayOf(spec) is not null || (spec.Window is not null && windows.ContainsKey(spec.Source)))
                wanted[spec.Source] = spec;
        }

        // A copy is tied to one monitor or window as Windows knew it when the copy started. If the monitor has
        // since been rebuilt, moved or resized (virtual monitors are rebuilt whenever their number changes), or
        // the window was closed and opened again, the old copy is stale: drop it and start again.
        foreach (string source in _captures.Keys.ToList())
        {
            MonitorCapture capture = _captures[source];
            bool keep = wanted.TryGetValue(source, out ScreenSpec? spec) && !capture.Closed;
            if (keep && spec!.Window is not null)
            {
                keep = capture.Target == windows[source] && WindowFinder.StillOpen(capture.Target);
            }
            else if (keep)
            {
                DisplayInfo display = DisplayOf(spec!)!;
                keep = _captureBounds[source] == display.Bounds && capture.Target == MonitorCapture.HandleFor(display.Bounds);
            }
            if (keep) continue;
            capture.Dispose();
            _captures.Remove(source);
            _captureSizes.Remove(source);
            _captureBounds.Remove(source);
        }
        foreach (string source in _refused.Keys.Where(k => !wanted.ContainsKey(k)).ToList()) _refused.Remove(source);

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach ((string source, ScreenSpec spec) in wanted)
        {
            if (_captures.ContainsKey(source)) continue;
            // Windows often refuses something that has only just appeared. Try again after a moment, for as long as it takes.
            if (_refused.TryGetValue(source, out long refusedAt) && now - refusedAt < RetryTicks) continue;
            try
            {
                MonitorCapture capture;
                if (spec.Window is not null)
                {
                    capture = MonitorCapture.OfWindow(_renderer.Device, windows[source]);
                }
                else
                {
                    DisplayInfo display = DisplayOf(spec)!;
                    capture = new MonitorCapture(_renderer.Device, display.Bounds);
                    _captureBounds[source] = display.Bounds;
                }
                _captures[source] = capture;
                _captureSizes[source] = new System.Drawing.Size(capture.Width, capture.Height);
                _refused.Remove(source);
            }
            catch (Exception e)
            {
                _refused[source] = now;
                LastProblem = $"Could not copy {spec.Name}: {e.Message}";
            }
        }

        // A screen set to 120 Hz for playing gets its picture twice as often as one for reading.
        foreach ((string source, ScreenSpec spec) in wanted)
        {
            if (_captures.TryGetValue(source, out MonitorCapture? running)) running.LimitTo(spec.RefreshHz >= 100 ? 120 : 60);
        }

        // Screens.
        foreach (string gone in _screens.Keys.Where(k => layout.Find(k) is null).ToList())
        {
            _renderer.Remove(_screens[gone].Item);
            _screens.Remove(gone);
        }

        foreach (ScreenSpec spec in layout.Screens)
        {
            string? feed = null;
            string still = "";
            int width = 1920, height = 1080;
            bool visible = spec.Visible;

            if (_captures.TryGetValue(spec.Source, out MonitorCapture? capture))
            {
                feed = spec.Source;
                (width, height) = (Math.Max(capture.Width, 16), Math.Max(capture.Height, 16));
            }
            else if (spec.DisplayName is not null)
            {
                DisplayInfo? display = DisplayOf(spec);
                if (display is not null)
                {
                    (width, height) = (display.Bounds.Width, display.Bounds.Height);
                    still = $"{spec.Name}|{(_refused.ContainsKey(spec.Source) ? "waiting for Windows to share this monitor" : display.Label)}|{width}x{height}";
                }
                else
                {
                    // A screen whose monitor is away keeps its place in the layout but is not drawn.
                    visible = false;
                    still = $"{spec.Name}|monitor not connected|{width}x{height}";
                }
            }
            else if (spec.Window is { } window)
            {
                (width, height) = (1280, 720);
                string why = !_live ? $"window of {window.Program}"
                    : _refused.ContainsKey(spec.Source) ? $"waiting for Windows to share {window.Program}"
                    : $"{window.Program} is not open";
                still = $"{spec.Name}|{why}|{width}x{height}";
            }
            else
            {
                still = $"{spec.Name}|test picture|{width}x{height}";
            }

            Panel panel = spec.ToPanel(width, height, optics);
            // Shown smaller than life, a picture needs its smaller copies to stay readable; near full size
            // they are never used, and skipping them saves the graphics card work on every update.
            bool mips = spec.Scale < 0.95f;

            if (!_screens.TryGetValue(spec.Id, out LiveScreen? screen))
            {
                screen = new LiveScreen
                {
                    Spec = spec,
                    Item = _renderer.Add(PanelMesh.Build(panel), width, height, distance: panel.Distance, mips: mips),
                    Panel = panel,
                };
                _screens[spec.Id] = screen;
            }
            else if (screen.Item.PictureSize.Width != width || screen.Item.PictureSize.Height != height || screen.Item.Mips != mips)
            {
                _renderer.Reshape(screen.Item, PanelMesh.Build(panel), width, height, mips);
                _renderer.SetMesh(screen.Item, PanelMesh.Build(panel), panel.Distance);
                screen.Panel = panel;
                screen.Still = "";
            }
            else if (screen.Panel != panel)
            {
                _renderer.SetMesh(screen.Item, PanelMesh.Build(panel), panel.Distance);
                screen.Panel = panel;
            }

            if (feed is null && screen.Still != still)
            {
                string[] parts = still.Split('|');
                using Bitmap picture = Pictures.Screen(parts[0], parts[1], width, height);
                _renderer.SetPicture(screen.Item, picture);
            }
            screen.Spec = spec;
            screen.Feed = feed;
            screen.Still = feed is null ? still : "";
            screen.Shown = visible;
            screen.Item.Visible = visible && !_blank;
        }
    }

    bool _blank;

    /// <summary>
    /// Once per frame: hides every screen while <paramref name="blank"/> (the glasses show the room through
    /// black), and with <paramref name="dim"/> darkens the screens the wearer is not looking at, fading over
    /// about a tenth of a second so the change is not a flicker at the edges.
    /// </summary>
    public void Look(System.Numerics.Quaternion head, bool dim, bool blank, float seconds)
    {
        _blank = blank;
        System.Numerics.Vector3 gaze = System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, head);
        LiveScreen? looked = null;
        if (dim)
        {
            foreach (LiveScreen screen in _screens.Values)
            {
                if (!screen.Shown || screen.Panel is not { } panel || !panel.TryHit(gaze, out float s, out float t)) continue;
                if (MathF.Abs(s) > 0.5f || MathF.Abs(t) > 0.5f) continue;
                if (looked is null || panel.Distance < looked.Panel!.Distance) looked = screen;
            }
        }
        float step = Math.Clamp(seconds * 10f, 0f, 1f);
        foreach (LiveScreen screen in _screens.Values)
        {
            screen.Item.Visible = screen.Shown && !blank;
            float target = !dim || looked is null || screen == looked ? 1f : DimTo;
            screen.Item.Tint += (target - screen.Item.Tint) * step;
        }
    }

    /// <summary>How bright a screen not looked at is, with dimming on.</summary>
    const float DimTo = 0.35f;

    /// <summary>Puts the newest frame of every copied monitor and window onto its screens. Call once per drawn frame. True if anything changed.</summary>
    public bool Update()
    {
        bool any = false, redo = false;

        // Start again any copy Windows has closed, and retry any it refused a moment ago.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_captures.Values.Any(c => c.Closed) || _refused.Values.Any(at => now - at >= RetryTicks)) redo = true;

        foreach ((string source, MonitorCapture capture) in _captures)
        {
            if (_captureSizes[source] != new System.Drawing.Size(capture.Width, capture.Height))
            {
                // The monitor changed resolution or the window was resized: rebuild its screens at the new shape.
                _captureSizes[source] = new System.Drawing.Size(capture.Width, capture.Height);
                redo = true;
                continue;
            }

            any |= capture.TryTakeLatest(texture =>
            {
                Texture2DDescription d = texture.Description;
                foreach (LiveScreen screen in _screens.Values)
                {
                    if (!Same(screen.Feed, source)) continue;
                    if (d.Width == screen.Item.PictureSize.Width && d.Height == screen.Item.PictureSize.Height)
                        _renderer.SetPicture(screen.Item, texture);
                }
            });
        }

        if (redo && _layout is not null && _displays is not null && _optics is not null)
        {
            Layout layout = _layout;
            _layout = null;
            Apply(layout, _displays, _windows, _optics);
        }
        if (any) Updates++;
        return any;
    }

    public void Dispose()
    {
        foreach (MonitorCapture capture in _captures.Values) capture.Dispose();
        _captures.Clear();
        _captureBounds.Clear();
    }

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
