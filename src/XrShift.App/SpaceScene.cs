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
    readonly SceneItem _ruler, _crosshair, _status, _hands, _menu, _edges;
    string _edgesShown = "";
    string _menuShown = "";
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
            long delta = frames >= before ? frames - before : frames; // a copy started afresh counts from zero
            rates.Add((delta / seconds).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
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

        // Arrows along the edges of the view pointing to screens that are out of sight, with their names.
        using (Bitmap edgesPicture = Pictures.Edges([]))
            _edges = renderer.Add(PanelMesh.HeadLocked(0, 0, 44f, 44f * Pictures.EdgesHeight / Pictures.EdgesWidth), edgesPicture, headLocked: true);
        _edges.Visible = false;

        // The hand quick menu, in the middle of the view.
        using (Bitmap menuPicture = Pictures.Menu([], -1))
            _menu = renderer.Add(PanelMesh.HeadLocked(0, 2f, 36f, 36f * Pictures.MenuHeight / Pictures.MenuWidth), menuPicture, headLocked: true);
        _menu.Visible = false;

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

    /// <summary>Shows the hand quick menu with one button lit, or hides it (items null). Drawing thread only.</summary>
    public void SetMenu(IReadOnlyList<string>? items, int highlight)
    {
        _menu.Visible = items is not null;
        if (items is null) return;
        string key = string.Join("|", items) + "#" + highlight;
        if (key == _menuShown) return;
        _menuShown = key;
        using Bitmap picture = Pictures.Menu(items, highlight);
        _renderer.SetPicture(_menu, picture);
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

            if (_screens.TryGetValue(spec.Id, out LiveScreen? before) && before.Item.HeadLocked != spec.FollowsMe)
            {
                // Following you or staying in the room is decided when the screen is made: make it again.
                _renderer.Remove(before.Item);
                _screens.Remove(spec.Id);
            }
            if (!_screens.TryGetValue(spec.Id, out LiveScreen? screen))
            {
                screen = new LiveScreen
                {
                    Spec = spec,
                    Item = _renderer.Add(PanelMesh.Build(panel), width, height, spec.FollowsMe, panel.Distance, mips),
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

    sealed class LiveObject
    {
        public required SceneObject Object;
        public required SceneItem Item;
        public required Panel Panel;
    }

    readonly Dictionary<string, LiveObject> _objects = [];
    SceneItem? _card;
    string _cardShown = "";

    /// <summary>Shows the card of the bot being looked at, just above it, and hides it when the wearer looks away.</summary>
    void ShowCard(System.Numerics.Vector3 gaze, bool blank)
    {
        float yaw = MathF.Atan2(-gaze.X, -gaze.Z) * 180f / MathF.PI, pitch = MathF.Asin(Math.Clamp(gaze.Y, -1f, 1f)) * 180f / MathF.PI;
        SceneObject? o = blank ? null : _deck?.Nearest(yaw, pitch);
        if (_card is not null) _card.Visible = o is not null;
        if (o is null) return;
        string key = $"{o.Id}|{o.Label}|{o.State}|{o.Offline}|{o.Pc}|{o.Note}|{o.YawDeg}|{o.PitchDeg}|{o.Distance}";
        if (key == _cardShown && _card is not null) return;
        _cardShown = key;
        float distance = MathF.Max(o.Distance - 0.1f, 0.2f), width = 2f * distance * MathF.Tan(9f * MathF.PI / 180f);
        var panel = new Panel { YawDeg = o.YawDeg, PitchDeg = o.PitchDeg + 9.5f, Distance = distance, Width = width, Height = width * Pictures.CardHeight / Pictures.CardWidth };
        using Bitmap picture = Pictures.Card(o);
        if (_card is null) _card = _renderer.Add(PanelMesh.Build(panel), picture, distance: distance);
        else
        {
            _renderer.SetMesh(_card, PanelMesh.Build(panel), distance);
            _renderer.SetPicture(_card, picture);
        }
    }
    Deck? _deck;
    float _time;

    /// <summary>Makes the scene match the deck: bots, clusters and widgets as discs in the room. Costs nothing when called again with the same deck.</summary>
    public void Apply(Deck deck)
    {
        if (ReferenceEquals(deck, _deck)) return;
        _deck = deck;
        foreach (string gone in _objects.Keys.Where(k => deck.Find(k) is null).ToList())
        {
            _renderer.Remove(_objects[gone].Item);
            _objects.Remove(gone);
        }
        foreach (SceneObject o in deck.Objects)
        {
            Panel panel = o.ToPanel();
            if (!_objects.TryGetValue(o.Id, out LiveObject? live))
            {
                using Bitmap picture = Pictures.Object(o);
                _objects[o.Id] = new LiveObject { Object = o, Item = _renderer.Add(PanelMesh.Build(panel), picture, distance: panel.Distance), Panel = panel };
                continue;
            }
            if (live.Panel != panel)
            {
                _renderer.SetMesh(live.Item, PanelMesh.Build(panel), panel.Distance);
                live.Panel = panel;
            }
            if ((live.Object.Label, live.Object.State, live.Object.Badge, live.Object.Kind, live.Object.Pc, live.Object.Offline) != (o.Label, o.State, o.Badge, o.Kind, o.Pc, o.Offline))
            {
                using Bitmap picture = Pictures.Object(o);
                _renderer.SetPicture(live.Item, picture);
            }
            live.Object = o;
        }
    }

    /// <summary>Once per frame: a working bot pulses slowly, one that needs the wearer pulses fast, an idle one is dim.</summary>
    void Pulse(float seconds, bool blank)
    {
        _time += seconds;
        foreach (LiveObject live in _objects.Values)
        {
            live.Item.Visible = !blank;
            live.Item.Tint = live.Object.Offline ? 0.6f : live.Object.State switch
            {
                BotState.Working => 0.8f + 0.2f * MathF.Sin(_time * MathF.Tau / 2.4f),
                BotState.NeedsYou => 0.65f + 0.35f * MathF.Sin(_time * MathF.Tau / 0.7f),
                BotState.Idle => 0.6f,
                _ => 1f,
            };
        }
    }

    /// <summary>
    /// Once per frame: hides every screen while <paramref name="blank"/> (the glasses show the room through
    /// black), and with <paramref name="dim"/> darkens the screens the wearer is not looking at, fading over
    /// about a tenth of a second so the change is not a flicker at the edges.
    /// </summary>
    public void Look(System.Numerics.Quaternion head, bool dim, bool blank, float seconds, float easyReading = 0)
    {
        _blank = blank;
        Pulse(seconds, blank);
        foreach (LiveScreen screen in _screens.Values)
        {
            // Reading without glasses: sharper edges and a little more contrast on every screen.
            screen.Item.Sharpen = easyReading * 1.4f;
            screen.Item.Contrast = 1f + easyReading * 0.3f;
        }
        System.Numerics.Vector3 gaze = System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, head);
        ShowCard(gaze, blank);
        LiveScreen? looked = null;
        if (dim)
        {
            foreach (LiveScreen screen in _screens.Values)
            {
                if (!screen.Shown || screen.Spec.FollowsMe || screen.Panel is not { } panel || !panel.TryHit(gaze, out float s, out float t)) continue;
                if (MathF.Abs(s) > 0.5f || MathF.Abs(t) > 0.5f) continue;
                if (looked is null || panel.Distance < looked.Panel!.Distance) looked = screen;
            }
        }
        float step = Math.Clamp(seconds * 10f, 0f, 1f);
        foreach (LiveScreen screen in _screens.Values)
        {
            screen.Item.Visible = screen.Shown && !blank;
            float target = (!dim || looked is null || screen == looked || screen.Spec.FollowsMe ? 1f : DimTo) * screen.Spec.Brightness;
            screen.Item.Tint += (target - screen.Item.Tint) * step;
        }
    }

    /// <summary>
    /// Points the edge arrows at every shown room screen that is out of sight from this head direction. Drawing
    /// thread only; the picture is only redrawn when an arrow moves by more than a few degrees.
    /// </summary>
    public void PointToHiddenScreens(System.Numerics.Quaternion head, DisplayOptics optics, bool show)
    {
        var hints = new List<(float AngleDeg, string Name)>();
        if (!_blank)
        {
            var toView = System.Numerics.Quaternion.Conjugate(head);
            float halfX = optics.HorizontalFovDeg / 2f, halfY = optics.VerticalFovDeg / 2f;
            void Hint(Panel panel, string name)
            {
                System.Numerics.Vector3 v = System.Numerics.Vector3.Transform(panel.Centre, toView);
                float across = MathF.Atan2(v.X, -v.Z) * 180f / MathF.PI, up = MathF.Atan2(v.Y, MathF.Sqrt(v.X * v.X + v.Z * v.Z)) * 180f / MathF.PI;
                float halfW = panel.AngularWidthDeg / 2f;
                if (MathF.Abs(across) < halfX + halfW * 0.5f && MathF.Abs(up) < halfY) return; // at least half in view
                hints.Add((MathF.Atan2(up, across) * 180f / MathF.PI, name));
            }
            foreach (LiveScreen screen in _screens.Values)
            {
                if (show && screen.Shown && !screen.Spec.FollowsMe && screen.Panel is { } panel) Hint(panel, screen.Spec.Name);
            }
            // A bot waiting for the wearer always gets an arrow, whatever the setting for screens says.
            foreach (LiveObject live in _objects.Values)
            {
                if (live.Object.State == BotState.NeedsYou) Hint(live.Panel, $"{live.Object.Label} needs you");
            }
        }
        _edges.Visible = hints.Count > 0;
        string key = string.Join("|", hints.Select(h => $"{MathF.Round(h.AngleDeg / 8f)}:{h.Name}"));
        if (key == _edgesShown) return;
        _edgesShown = key;
        using Bitmap picture = Pictures.Edges(hints);
        _renderer.SetPicture(_edges, picture);
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
