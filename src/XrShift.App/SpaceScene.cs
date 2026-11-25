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
        /// <summary>The thin coloured edge just outside the screen, and the screen shape it was made for.</summary>
        public SceneItem? Edge;
        public Panel? EdgeFor;
    }

    /// <summary>How thick the edge round a screen is, in display pixels: thick enough not to shimmer, thin enough not to frame.</summary>
    const float EdgePixels = 4;

    /// <summary>
    /// A band just outside a screen, following its curve. It is a shape of its own around the screen, so the screen's
    /// own pixels are drawn exactly as before.
    /// </summary>
    static XrShift.Scene.Mesh EdgeMesh(Panel p)
    {
        float m = p.Distance * EdgePixels / Focal;
        float ex = m / p.Width, ey = m / p.Height;
        int columns = p.CurveDeg < 0.06f ? 1 : Math.Clamp((int)MathF.Ceiling(p.CurveDeg / 2f), 2, 360);
        var vertices = new List<MeshVertex>();
        var indices = new List<ushort>();
        void Band(float s0, float s1, float t0, float t1, int cols)
        {
            int first = vertices.Count;
            for (int c = 0; c <= cols; c++)
            {
                float s = s0 + (s1 - s0) * c / cols;
                vertices.Add(new MeshVertex(p.WorldPoint(s, t1), new System.Numerics.Vector2(0.5f, 0.5f)));
                vertices.Add(new MeshVertex(p.WorldPoint(s, t0), new System.Numerics.Vector2(0.5f, 0.5f)));
            }
            for (int c = 0; c < cols; c++)
            {
                int tl = first + c * 2;
                indices.AddRange([(ushort)tl, (ushort)(tl + 1), (ushort)(tl + 2), (ushort)(tl + 2), (ushort)(tl + 1), (ushort)(tl + 3)]);
            }
        }
        Band(-0.5f - ex, 0.5f + ex, 0.5f, 0.5f + ey, columns);
        Band(-0.5f - ex, 0.5f + ex, -0.5f - ey, -0.5f, columns);
        Band(-0.5f - ex, -0.5f, -0.5f, 0.5f, 1);
        Band(0.5f, 0.5f + ex, -0.5f, 0.5f, 1);
        return new XrShift.Scene.Mesh([.. vertices], [.. indices]);
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
    readonly SceneItem _ruler, _crosshair, _status, _notice, _hands, _menu, _edges, _ring;
    /// <summary>The colour of the edge round each screen, stretched over its frame.</summary>
    readonly Bitmap _screenEdge;
    IReadOnlyList<Mark> _marks = [];
    System.Numerics.Vector3? _ringAt;
    /// <summary>The label LiveApp gives the mouse pointer's mark; its arrow is drawn as the mouse kind.</summary>
    const string MouseLabel = "mouse";
    string _edgesShown = "";
    DisplayOptics _edgesOptics = DisplayOptics.OnePro;
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
        _screenEdge = Pictures.Solid(Style.ScreenEdge);

        // Gesture mode: a small view of the hands, low on the right.
        using (Bitmap handsPicture = Pictures.Hands([], false, ""))
            _hands = renderer.Add(PanelMesh.HeadLocked(17f, -7.5f, 12f, 9f), handsPicture, headLocked: true);
        _hands.Visible = false;

        // Arrows along the edges of the view pointing to screens that are out of sight, with their names. The picture
        // covers the whole view, so display pixels map straight onto it.
        using (Bitmap edgesPicture = Pictures.Edges([]))
            _edges = renderer.Add(EdgesMesh(DisplayOptics.OnePro), edgesPicture, headLocked: true);
        _edges.Visible = false;

        // The ring round the mouse pointer: in the room, where the pointer is; moved there by SetMarks.
        using (Bitmap ringPicture = Pictures.Ring())
            _ring = renderer.Add(PanelMesh.Build(new Panel { Width = 0.2f, Height = 0.2f }), ringPicture, distance: 2f);
        _ring.Visible = false;

        // The hand quick menu, in the middle of the view.
        using (Bitmap menuPicture = Pictures.Menu([], -1))
            _menu = renderer.Add(PanelMesh.HeadLocked(0, 2f, 36f, 36f * Pictures.MenuHeight / Pictures.MenuWidth), menuPicture, headLocked: true);
        _menu.Visible = false;

        // The status strip and, under it, the notice area: both at the top of the view, clear of the bot row low down
        // and of the bottom edge of a screen straight ahead.
        using (Bitmap status = Pictures.Status(""))
            _status = renderer.Add(Overlay(0, 0, 1, 1), status, headLocked: true);
        using (Bitmap notice = Pictures.Notice("", NoticeLevel.Info))
            _notice = renderer.Add(Overlay(0, 0, 1, 1), notice, headLocked: true);
        _notice.Visible = false;
        PlaceOverlays();

        // Glasses setup: a picture over the whole view, as the arrows have, so display pixels map straight onto it.
        using (Bitmap guide = Pictures.Guide([], [], "", "", null, []))
            _guide = renderer.Add(EdgesMesh(DisplayOptics.OnePro), guide, headLocked: true);
        _guide.Visible = false;
    }

    readonly SceneItem _guide;
    DisplayOptics _guideOptics = DisplayOptics.OnePro;
    string _guideShown = "";

    /// <summary>
    /// Shows glasses setup's guide, or hides it (null). Lines fixed to the picture are drawn at their display pixels;
    /// lines fixed in the room are placed for <paramref name="view"/>, the way the room is drawn now. Drawing thread only.
    /// </summary>
    public void SetGuide(XrShift.Tracking.SetupScreen? screen, System.Numerics.Quaternion view, DisplayOptics optics)
    {
        _guide.Visible = screen is not null;
        if (screen is null) return;
        if (optics != _guideOptics)
        {
            // The picture must span exactly the view drawn, whatever the view angle, to stay one pixel for one.
            _guideOptics = optics;
            _renderer.SetMesh(_guide, EdgesMesh(optics), 0);
        }
        // shortcut: a line fixed in the room is redrawn as the head moves, and a long one is drawn straight between its
        // ends; no step draws one yet. Give such lines a mesh of their own in the room if a step comes to need them.
        PointF? At(XrShift.Tracking.GuideSpace space, System.Numerics.Vector2 p) =>
            (space == XrShift.Tracking.GuideSpace.Display
                ? XrShift.Tracking.GuideDrawing.ToPicture(p, Pictures.GuideWidth, Pictures.GuideHeight)
                : XrShift.Tracking.GuideDrawing.ToPicture(p, view, optics.HorizontalFovDeg, Pictures.GuideWidth, Pictures.GuideHeight)) is { } q
                ? new PointF(q.X, q.Y) : null;
        var lines = new List<(PointF, PointF)>();
        foreach (XrShift.Tracking.GuideLine line in screen.Drawing.Lines)
            if (At(line.Space, line.From) is { } a && At(line.Space, line.To) is { } b) lines.Add((a, b));
        var marks = new List<PointF>();
        foreach (XrShift.Tracking.GuideMark mark in screen.Drawing.Marks)
            if (At(mark.Space, mark.At) is { } m) marks.Add(m);

        string key = string.Join(";", lines.Select(l => $"{l.Item1.X:F1},{l.Item1.Y:F1},{l.Item2.X:F1},{l.Item2.Y:F1}")) + "|" +
                     string.Join(";", marks.Select(m => $"{m.X:F1},{m.Y:F1}")) + "|" +
                     string.Join("|", [screen.Progress, screen.Instruction, screen.Note ?? "", .. screen.Results]);
        if (key == _guideShown) return;
        _guideShown = key;
        using Bitmap picture = Pictures.Guide(lines, marks, screen.Progress, screen.Instruction, screen.Note, screen.Results);
        _renderer.SetPicture(_guide, picture);
    }

    /// <summary>From the middle of the view to its side edge, in display pixels, measured the way the projection does: by tangent.</summary>
    static readonly float Focal = DisplayOptics.OnePro.Width / 2f / MathF.Tan(DisplayOptics.OnePro.HorizontalFovDeg / 2f * MathF.PI / 180f);

    /// <summary>
    /// A head-locked rectangle given in display pixels from the middle of the view (y up), so a picture made at one
    /// picture pixel per display pixel is shown at exactly that size.
    /// </summary>
    static XrShift.Scene.Mesh Overlay(float x, float y, float w, float h)
    {
        static float Deg(float px) => MathF.Atan(px / Focal) * 180f / MathF.PI;
        float left = Deg(x - w / 2), right = Deg(x + w / 2), bottom = Deg(y - h / 2), top = Deg(y + h / 2);
        return PanelMesh.HeadLocked((left + right) / 2, (bottom + top) / 2, right - left, top - bottom);
    }

    static XrShift.Scene.Mesh EdgesMesh(DisplayOptics optics) => PanelMesh.HeadLocked(0, 0, optics.HorizontalFovDeg, optics.VerticalFovDeg);

    /// <summary>Puts the status strip, notice area and crosshair in place, larger in 3D (see <see cref="Style.StereoScale"/>).</summary>
    void PlaceOverlays()
    {
        float s = _stereo ? Style.StereoScale : 1f, top = 540 - 18; // the top of the view, less a margin the optics blur
        _renderer.SetMesh(_status, Overlay(0, top - Pictures.StatusHeight * s / 2, Pictures.StatusWidth * s, Pictures.StatusHeight * s), 0);
        top -= Pictures.StatusHeight * s + 4;
        _renderer.SetMesh(_notice, Overlay(0, top - Pictures.NoticeHeight * s / 2, Pictures.NoticeWidth * s, Pictures.NoticeHeight * s), 0);
        _renderer.SetMesh(_crosshair, Overlay(0, 0, Pictures.CrosshairSize * s, Pictures.CrosshairSize * s), 0);
    }

    bool _stereo;

    /// <summary>
    /// Whether the frames are 3D side by side: overlays and the bot card are drawn larger, and edge arrows keep further
    /// in, since each eye's half is shifted sideways and cut at the middle.
    /// </summary>
    public bool Stereo
    {
        get => _stereo;
        set
        {
            if (value == _stereo) return;
            _stereo = value;
            PlaceOverlays();
            _cardShown = "";
        }
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

    string _noticeText = "";
    NoticeLevel _noticeLevel;

    /// <summary>Shows a notice in the notice area under the status strip, or hides it (empty text). Drawing thread only.</summary>
    public void SetNotice(string text, NoticeLevel level)
    {
        _notice.Visible = text.Length > 0;
        if (text == _noticeText && level == _noticeLevel) return;
        (_noticeText, _noticeLevel) = (text, level);
        using Bitmap picture = Pictures.Notice(text, level);
        _renderer.SetPicture(_notice, picture);
    }

    /// <summary>
    /// How loud a notice should be, guessed from its words until callers say so themselves: one that ends in "!" or is
    /// about something the wearer has to act on gets the needs-you look.
    /// </summary>
    public static NoticeLevel GuessNoticeLevel(string text) =>
        text.EndsWith('!') || ((string[])["needs you", "Lost the mouse", "do not report their mode", "added hidden"])
            .Any(words => text.Contains(words, StringComparison.OrdinalIgnoreCase))
            ? NoticeLevel.NeedsYou : NoticeLevel.Info;

    /// <summary>Whether each floating screen gets a thin coloured edge, so a white page on a white wall still has one.</summary>
    public bool ScreenEdges { get; set; } = true;

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
        void Drop(string id)
        {
            _renderer.Remove(_screens[id].Item);
            if (_screens[id].Edge is { } edge) _renderer.Remove(edge);
            _screens.Remove(id);
        }
        foreach (string gone in _screens.Keys.Where(k => layout.Find(k) is null).ToList()) Drop(gone);

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
                Drop(spec.Id);
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

            // Just behind the screen, so it is drawn first and never over it.
            if (screen.Edge is null) screen.Edge = _renderer.Add(EdgeMesh(panel), _screenEdge, spec.FollowsMe, panel.Distance + 0.01f);
            else if (screen.EdgeFor != panel) _renderer.SetMesh(screen.Edge, EdgeMesh(panel), panel.Distance + 0.01f);
            screen.EdgeFor = panel;

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
            screen.Edge.Visible = screen.Item.Visible && ScreenEdges;
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
    Panel? _cardPanel;
    string _cardShown = "";

    /// <summary>
    /// How far above its place in the room the bot row is drawn. The row's place is low (see <see cref="Deck.RowPitchDeg"/>):
    /// lifted this much, names stay in view when looking only a little down, and the row still stays out of sight straight ahead.
    /// </summary>
    const float RowLiftDeg = 1.5f;

    static Panel PanelOf(SceneObject o) => o.Placed ? o.ToPanel() : o.ToPanel() with { PitchDeg = o.PitchDeg + RowLiftDeg };

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
        // Just above the bot, its lower edge clear of the mark; in 3D larger, as the overlays are, and raised by as much.
        float grow = _stereo ? 1.15f : 1f, half = 9f * grow;
        float distance = MathF.Max(o.Distance - 0.1f, 0.2f), width = 2f * distance * MathF.Tan(half * MathF.PI / 180f);
        float cardPitch = (_objects.TryGetValue(o.Id, out LiveObject? live) ? live.Panel.PitchDeg : o.PitchDeg) + 3.5f + 3.4f * grow;
        var panel = new Panel { YawDeg = o.YawDeg, PitchDeg = cardPitch, Distance = distance, Width = width, Height = width * Pictures.CardHeight / Pictures.CardWidth };
        _cardPanel = panel;
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
            Panel panel = PanelOf(o);
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

    /// <summary>
    /// Once per frame: a working bot pulses slowly and gently, one that needs the wearer more strongly; nothing else
    /// moves. Never dimmer than 0.7, so names stay readable at the low point.
    /// </summary>
    void Pulse(float seconds, bool blank, float? clock)
    {
        _time = clock ?? _time + seconds;
        foreach (LiveObject live in _objects.Values)
        {
            live.Item.Visible = !blank;
            live.Item.Tint = live.Object.Offline ? 1f : live.Object.State switch
            {
                BotState.Working => 0.92f + 0.08f * MathF.Sin(_time * MathF.Tau / 2.4f),
                BotState.NeedsYou => 0.85f + 0.15f * MathF.Sin(_time * MathF.Tau / 1.2f),
                _ => 1f,
            };
        }
    }

    /// <summary>
    /// Once per frame: hides every screen while <paramref name="blank"/> (the glasses show the room through
    /// black), and with <paramref name="dim"/> darkens the screens the wearer is not looking at, fading over
    /// about a tenth of a second so the change is not a flicker at the edges.
    /// </summary>
    /// <param name="clock">For test pictures: the time in seconds the pulsing is drawn at, instead of the running clock.</param>
    public void Look(System.Numerics.Quaternion head, bool dim, bool blank, float seconds, float easyReading = 0, float? clock = null)
    {
        _blank = blank;
        Pulse(seconds, blank, clock);
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
            if (screen.Edge is { } edge)
            {
                edge.Visible = screen.Item.Visible && ScreenEdges;
                edge.Tint = screen.Item.Tint;
            }
        }
    }

    /// <summary>
    /// Spots to point the eye to (the mouse pointer, a window that just opened): a ring round the one that asks for
    /// it, and an edge arrow to each one with a label while it is out of view. Drawing thread only.
    /// </summary>
    public void SetMarks(IReadOnlyList<Mark> marks)
    {
        _marks = marks;
        System.Numerics.Vector3? ringAt = null;
        foreach (Mark m in marks) if (m.Ring) { ringAt = m.Point; break; }
        if (ringAt is not { } p || p == _ringAt) { _ringAt = ringAt; return; }
        _ringAt = p;
        // Facing the wearer, a little in front of the screen the pointer is on, about 6 degrees across.
        float length = p.Length(), distance = MathF.Max(length - 0.03f, 0.2f), size = 2f * distance * MathF.Tan(3f * MathF.PI / 180f);
        var panel = new Panel
        {
            YawDeg = MathF.Atan2(-p.X, -p.Z) * 180f / MathF.PI,
            PitchDeg = MathF.Asin(Math.Clamp(p.Y / MathF.Max(length, 1e-4f), -1f, 1f)) * 180f / MathF.PI,
            Distance = distance, Width = size, Height = size,
        };
        _renderer.SetMesh(_ring, PanelMesh.Build(panel), distance);
    }

    /// <summary>
    /// Points the edge arrows at every shown room screen that is out of sight from this head direction, and at the
    /// labelled marks. Drawing thread only; the picture is only redrawn when an arrow moves by more than a few degrees.
    /// </summary>
    public void PointToHiddenScreens(System.Numerics.Quaternion head, DisplayOptics optics, bool show)
    {
        var hints = new List<Hint>();
        var avoid = new List<RectangleF>();
        RectangleF? cardRect = null;
        var toView = System.Numerics.Quaternion.Conjugate(head);
        _ring.Visible = _ringAt is not null && !_blank;
        if (!_blank)
        {
            float halfX = optics.HorizontalFovDeg / 2f, halfY = optics.VerticalFovDeg / 2f;
            void Add(System.Numerics.Vector3 centre, float halfW, string name, HintKind kind)
            {
                (float across, float up) = RoomSpot.Angles(centre, head);
                if (MathF.Abs(across) < halfX + halfW * 0.5f && MathF.Abs(up) < halfY) return; // at least half in view
                hints.Add(new Hint(MathF.Atan2(up, across) * 180f / MathF.PI, name, kind));
            }
            foreach (LiveScreen screen in _screens.Values)
            {
                if (show && screen.Shown && !screen.Spec.FollowsMe && screen.Panel is { } panel) Add(panel.Centre, panel.AngularWidthDeg / 2f, screen.Spec.Name, HintKind.Screen);
            }
            // A bot waiting for the wearer always gets an arrow, whatever the setting for screens says; so do the marks.
            foreach (LiveObject live in _objects.Values)
            {
                if (live.Object.State == BotState.NeedsYou) Add(live.Panel.Centre, live.Panel.AngularWidthDeg / 2f, $"{live.Object.Label} needs you", HintKind.NeedsYou);
            }
            // The mouse pointer's mark is labelled "mouse"; any other labelled mark is a window that just opened.
            foreach (Mark m in _marks)
            {
                if (m.Label.Length > 0) Add(m.Point, 0, m.Label, m.Label == MouseLabel ? HintKind.Mouse : HintKind.Opened);
            }

            // The card of the bot looked at, and the bots themselves, in display pixels: arrows and labels keep off them.
            float focal = 960f / MathF.Tan(halfX * MathF.PI / 180f);
            RectangleF? InView(Panel p, float margin)
            {
                var corners = new List<PointF>();
                foreach ((float s, float t) in (ReadOnlySpan<(float, float)>)[(-0.5f, -0.5f), (0.5f, -0.5f), (-0.5f, 0.5f), (0.5f, 0.5f)])
                {
                    System.Numerics.Vector3 v = System.Numerics.Vector3.Transform(p.WorldPoint(s, t), toView);
                    if (v.Z < -0.01f) corners.Add(new PointF(960 + v.X / -v.Z * focal, 540 - v.Y / -v.Z * focal));
                }
                if (corners.Count < 4) return null;
                var r = RectangleF.FromLTRB(corners.Min(c => c.X) - margin, corners.Min(c => c.Y) - margin, corners.Max(c => c.X) + margin, corners.Max(c => c.Y) + margin);
                return r.IntersectsWith(new RectangleF(0, 0, 1920, 1080)) ? r : null;
            }
            if (_card is { Visible: true } && _cardPanel is { } card) cardRect = InView(card, 16);
            // The bot pictures are square with room round the name and mark: a little in from each side is about them.
            foreach (LiveObject live in _objects.Values)
            {
                if (InView(live.Panel with { Width = live.Panel.Width * 0.75f, Height = live.Panel.Height * 0.85f }, 0) is { } r) avoid.Add(r);
            }
        }
        _edges.Visible = hints.Count > 0;
        if (optics != _edgesOptics)
        {
            _edgesOptics = optics;
            _renderer.SetMesh(_edges, EdgesMesh(optics), 0);
        }
        // Redrawn when an arrow turns by some degrees or the card moves by a few, not for every small move of the head:
        // the bots are kept clear of as they were at the last redraw.
        string key = string.Join("|", hints.Select(h => $"{MathF.Round(h.AngleDeg / 8f)}:{h.Kind}:{h.Name}"))
            + (cardRect is { } c ? $"|card {MathF.Round(c.X / 96)},{MathF.Round(c.Y / 96)},{MathF.Round(c.Width / 96)}" : "")
            + (_stereo ? "|3d" : "");
        if (key == _edgesShown) return;
        _edgesShown = key;
        using Bitmap picture = Pictures.Edges(hints, cardRect, avoid, _stereo);
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
        _screenEdge.Dispose();
    }

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
