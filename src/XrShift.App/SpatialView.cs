using System.Drawing.Drawing2D;
using System.Numerics;
using XrShift.Scene;
using Panel = XrShift.Scene.Panel;

namespace XrShift.App;

/// <summary>One screen as the spatial view draws it.</summary>
internal sealed record SpatialScreen(ScreenSpec Spec, int PixelWidth, int PixelHeight, string Kind, bool Missing);

/// <summary>
/// The screens floating around the wearer, drawn in perspective from just behind and above the head. Click a
/// screen to select it, drag it to move it (it snaps beside, above or in line with the others; Alt places it
/// freely), scroll over it to resize it. Drag with the right button to walk round the view; scroll on empty
/// space to come closer; double-click empty space to stand back where you started.
/// </summary>
internal sealed class SpatialView : Control
{
    const float DegToRad = MathF.PI / 180f;
    const float DefaultOrbitPitch = 20f, DefaultOrbitDistance = 4.6f, FieldOfViewDeg = 56f;
    const float SnapReach = 3f, SnapGap = 2.5f;

    IReadOnlyList<SpatialScreen> _screens = [];
    string? _selected;
    DisplayOptics _optics = DisplayOptics.OnePro;
    float _orbitYaw, _orbitPitch = DefaultOrbitPitch, _orbitDistance = DefaultOrbitDistance;

    Matrix4x4 _view;
    float _focal;
    PointF _middle;

    // Dragging a screen.
    string? _dragId;
    Point _lastMouse;
    float _rawYaw, _rawPitch;
    string _snapNote = "";
    // Walking round the view.
    bool _orbiting;

    readonly List<(string Id, GraphicsPath Shape)> _shapes = [];
    readonly List<(string Id, RectangleF Box)> _chips = [];

    /// <summary>Height of the strip along the bottom kept for the buttons placed on top of this control, logical pixels.</summary>
    public int DockHeight { get; set; } = 64;

    public event Action<string>? Picked;
    /// <summary>A screen was dragged: id, yaw (+ = left) and pitch (+ = up), degrees.</summary>
    public event Action<string, float, float>? Moved;
    /// <summary>A screen was resized with the wheel: id and its new scale.</summary>
    public event Action<string, float>? Resized;
    public event Action<string>? HintWanted;

    /// <summary>Stationary mode: draw the picture the glasses hold still, so it is clear what fits inside it.</summary>
    public bool HeldFrame
    {
        get => _heldFrame;
        set { if (_heldFrame != value) { _heldFrame = value; Invalidate(); } }
    }
    bool _heldFrame;

    public SpatialView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Dock; // what the buttons on the dock paint behind themselves
    }

    public void Show(IReadOnlyList<SpatialScreen> screens, string? selected, DisplayOptics optics)
    {
        _screens = screens;
        _selected = selected;
        _optics = optics;
        Invalidate();
    }

    float Dpi => DeviceDpi / 96f;
    RectangleF Stage => new(0, 0, Width, Math.Max(Height - DockHeight * Dpi, 10));

    // ---- the camera ----------------------------------------------------------------------------

    void SetUpCamera()
    {
        var target = new Vector3(0, 0.05f, -1.1f);
        float yaw = _orbitYaw * DegToRad, pitch = _orbitPitch * DegToRad;
        Vector3 eye = target + new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)) * _orbitDistance;
        _view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        RectangleF stage = Stage;
        _middle = new PointF(stage.Width / 2f, stage.Height * 0.46f);
        _focal = stage.Height / 2f / MathF.Tan(FieldOfViewDeg / 2f * DegToRad);
    }

    bool Project(Vector3 world, out PointF at)
    {
        Vector3 v = Vector3.Transform(world, _view);
        if (v.Z > -0.1f)
        {
            at = default;
            return false;
        }
        at = new PointF(_middle.X + v.X / -v.Z * _focal, _middle.Y - v.Y / -v.Z * _focal);
        return true;
    }

    float DepthOf(Vector3 world) => -Vector3.Transform(world, _view).Z;

    Panel PanelOf(SpatialScreen s, ScreenSpec? spec = null) => (spec ?? s.Spec).ToPanel(s.PixelWidth, s.PixelHeight, _optics);

    PointF[]? Outline(Panel panel)
    {
        int strips = panel.CurveDeg < 1f ? 1 : Math.Clamp((int)(panel.CurveDeg / 5f), 2, 48);
        var points = new PointF[(strips + 1) * 2];
        for (int i = 0; i <= strips; i++)
        {
            float s = (float)i / strips - 0.5f;
            if (!Project(panel.WorldPoint(s, 0.5f), out points[i])) return null;
            if (!Project(panel.WorldPoint(-s, -0.5f), out points[strips + 1 + i])) return null;
        }
        return points;
    }

    // ---- drawing ---------------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        float d = Dpi;
        RectangleF stage = Stage;
        SetUpCamera();

        // The room: dark overhead, a band of blue light at eye level, dark floor.
        using (var room = new LinearGradientBrush(new PointF(0, 0), new PointF(0, stage.Height + 1), Ui.RoomTop, Ui.RoomFloor))
        {
            room.InterpolationColors = new ColorBlend
            {
                Colors = [Ui.RoomTop, Ui.RoomHorizon, Ui.RoomFloor],
                Positions = [0f, _middle.Y / stage.Height, 1f],
            };
            g.FillRectangle(room, stage);
        }
        using (var dock = new SolidBrush(Ui.Dock)) g.FillRectangle(dock, 0, stage.Bottom, Width, Height - stage.Bottom);
        using (var rule = new Pen(Ui.Line, d)) g.DrawLine(rule, 0, stage.Bottom, Width, stage.Bottom);

        g.SetClip(stage);
        DrawFloor(g, d);
        DrawHorizon(g, d);
        DrawYou(g, d);
        if (_heldFrame) DrawHeldFrame(g, d);
        DrawScreens(g, d);
        g.ResetClip();
        DrawChips(g, d);
        DrawReadout(g, d, stage);
    }

    void DrawFloor(Graphics g, float d)
    {
        const float floor = -1.3f;
        for (int ring = 1; ring <= 4; ring++)
        {
            var points = new List<PointF>();
            for (int i = 0; i <= 96; i++)
            {
                float a = i / 96f * MathF.Tau;
                if (Project(new Vector3(MathF.Sin(a) * ring, floor, -MathF.Cos(a) * ring), out PointF p)) points.Add(p);
                else if (points.Count > 1) { DrawLines(points, ring); points.Clear(); }
            }
            DrawLines(points, ring);
        }
        using var spoke = new Pen(Color.FromArgb(22, 120, 160, 220), d);
        for (int deg = 0; deg < 360; deg += 30)
        {
            float a = deg * DegToRad;
            if (Project(new Vector3(MathF.Sin(a) * 0.7f, floor, -MathF.Cos(a) * 0.7f), out PointF from) &&
                Project(new Vector3(MathF.Sin(a) * 4.2f, floor, -MathF.Cos(a) * 4.2f), out PointF to))
                g.DrawLine(spoke, from, to);
        }

        void DrawLines(List<PointF> points, int ring)
        {
            if (points.Count < 2) return;
            using var pen = new Pen(Color.FromArgb(ring == 2 ? 70 : 34, 120, 160, 220), d);
            g.DrawLines(pen, points.ToArray());
        }
    }

    void DrawHorizon(Graphics g, float d)
    {
        // Eye level, all the way round, with the directions marked.
        const float radius = 2.6f;
        using var dots = new Pen(Color.FromArgb(46, 140, 190, 240), d) { DashStyle = DashStyle.Dot };
        var points = new List<PointF>();
        for (int i = 0; i <= 120; i++)
        {
            float a = i / 120f * MathF.Tau;
            if (Project(new Vector3(MathF.Sin(a) * radius, 0, -MathF.Cos(a) * radius), out PointF p)) points.Add(p);
            else { if (points.Count > 1) g.DrawLines(dots, points.ToArray()); points.Clear(); }
        }
        if (points.Count > 1) g.DrawLines(dots, points.ToArray());

        using Font font = Ui.Mono(7.5f);
        using var ink = new SolidBrush(Color.FromArgb(120, 150, 175, 205));
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach ((int yaw, string name) in new[] { (0, "AHEAD"), (45, "L 45"), (90, "L 90"), (-45, "R 45"), (-90, "R 90"), (180, "BEHIND") })
        {
            // Yaw + is to the left: a point to the left is at -x.
            float a = yaw * DegToRad;
            if (Project(new Vector3(-MathF.Sin(a) * (radius + 0.25f), -0.42f, -MathF.Cos(a) * (radius + 0.25f)), out PointF p))
                g.DrawString(name, font, ink, p, centred);
        }
    }

    void DrawYou(Graphics g, float d)
    {
        if (!Project(Vector3.Zero, out PointF head)) return;
        float r = 7 * d;
        using (var glow = new SolidBrush(Ui.Fade(Ui.Accent, 40))) g.FillEllipse(glow, head.X - r * 2.4f, head.Y - r * 2.4f, r * 4.8f, r * 4.8f);
        using (var dot = new SolidBrush(Ui.Accent)) g.FillEllipse(dot, head.X - r / 2, head.Y - r / 2, r, r);
        if (Project(new Vector3(0, 0, -0.55f), out PointF ahead))
        {
            using var pen = new Pen(Ui.Fade(Ui.Accent, 150), 1.5f * d) { CustomEndCap = new AdjustableArrowCap(4, 4) };
            g.DrawLine(pen, head, ahead);
        }
        using Font font = Ui.Mono(7.5f);
        using var ink = new SolidBrush(Ui.Fade(Ui.Accent, 200));
        g.DrawString("YOU", font, ink, head.X + 10 * d, head.Y + 4 * d);
    }

    // The glasses' whole view straight ahead, a little behind the screens: in stationary mode the glasses hold
    // exactly this still in the room, and only what is inside it can be seen.
    void DrawHeldFrame(Graphics g, float d)
    {
        var frame = new ScreenSpec { Scale = 1f, Distance = 2.3f }.ToPanel(_optics.Width, _optics.Height, _optics);
        if (Outline(frame) is not { } points) return;
        using var fill = new SolidBrush(Ui.Fade(Ui.Accent, 14));
        g.FillPolygon(fill, points);
        using var edge = new Pen(Ui.Fade(Ui.Accent, 150), 1.5f * d) { DashPattern = [5f, 4f] };
        g.DrawPolygon(edge, points);
        float top = points.Take(points.Length / 2).Min(p => p.Y), left = points.Min(p => p.X);
        using Font label = Ui.Mono(8.5f);
        using var ink = new SolidBrush(Ui.Fade(Ui.Accent, 210));
        g.DrawString("ONE VIEW OF THE GLASSES", label, ink, left, top - 18 * d);
    }

    void DrawScreens(Graphics g, float d)
    {
        foreach ((_, GraphicsPath old) in _shapes) old.Dispose();
        _shapes.Clear();

        var order = _screens
            .Select(s => (Screen: s, Panel: PanelOf(s)))
            .OrderByDescending(x => DepthOf(x.Panel.Centre))
            .ToList();

        using Font nameFontLarge = Ui.Display(13f);
        using Font nameFontSmall = Ui.Display(9f);
        using Font kindFont = Ui.Mono(8f);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };

        foreach ((SpatialScreen screen, Panel panel) in order)
        {
            if (Outline(panel) is not { } outline) continue;
            bool selected = screen.Spec.Id == _selected, hidden = !screen.Spec.Visible || screen.Missing;
            var path = new GraphicsPath();
            path.AddPolygon(outline);
            _shapes.Add((screen.Spec.Id, path));
            RectangleF box = path.GetBounds();
            if (box.Width < 2 || box.Height < 2) continue;

            // Glass: lit from above, darker below; the selected one carries the accent's light.
            Color top = selected ? Color.FromArgb(235, 26, 84, 108) : Color.FromArgb(hidden ? 70 : 235, 34, 50, 80);
            Color bottom = selected ? Color.FromArgb(235, 8, 32, 44) : Color.FromArgb(hidden ? 50 : 235, 12, 18, 32);
            using (var glass = new LinearGradientBrush(new PointF(0, box.Top - 1), new PointF(0, box.Bottom + 1), top, bottom))
                g.FillPath(glass, path);

            if (selected)
            {
                using var halo = new Pen(Ui.Fade(Ui.Accent, 34), 12 * d) { LineJoin = LineJoin.Round };
                using var near = new Pen(Ui.Fade(Ui.Accent, 80), 5 * d) { LineJoin = LineJoin.Round };
                g.DrawPath(halo, path);
                g.DrawPath(near, path);
            }
            using (var edge = new Pen(selected ? Ui.Accent : Color.FromArgb(hidden ? 70 : 150, 130, 160, 210), (selected ? 1.6f : 1f) * d))
            {
                if (hidden) edge.DashStyle = DashStyle.Dash;
                g.DrawPath(edge, path);
            }
            // A brighter line along the top edge, as if lit from above.
            int half = outline.Length / 2;
            using (var shine = new Pen(Color.FromArgb(hidden ? 40 : 140, 200, 225, 255), d))
                g.DrawLines(shine, outline.Take(half).ToArray());

            if (!Project(panel.Centre, out PointF middle)) continue;
            bool roomy = box.Height > 60 * d && box.Width > 90 * d;
            using var nameInk = new SolidBrush(hidden ? Ui.Faint : Ui.Ink);
            using var kindInk = new SolidBrush(selected ? Ui.Fade(Ui.Accent, 220) : Ui.Muted);
            Font nameFont = roomy ? nameFontLarge : nameFontSmall;
            float lineHeight = nameFont.GetHeight(g);
            g.DrawString(screen.Spec.Name, nameFont, nameInk, new RectangleF(middle.X - box.Width / 2, middle.Y - lineHeight, box.Width, lineHeight * 1.1f), centred);
            if (box.Height > 34 * d)
            {
                string kind = screen.Missing ? "not connected" : !screen.Spec.Visible ? "hidden" : screen.Kind;
                g.DrawString(kind, kindFont, kindInk, new RectangleF(middle.X - box.Width / 2, middle.Y + 1 * d, box.Width, lineHeight), centred);
            }
        }
    }

    void DrawChips(Graphics g, float d)
    {
        // Every screen by name along the top, so hidden or far-off ones can be picked too.
        _chips.Clear();
        using Font font = Ui.Text(8.5f);
        float x = 14 * d, y = 12 * d, h = 24 * d;
        foreach (SpatialScreen s in _screens)
        {
            bool selected = s.Spec.Id == _selected, hidden = !s.Spec.Visible || s.Missing;
            float w = g.MeasureString(s.Spec.Name, font).Width + 20 * d;
            if (x + w > Width - 14 * d) break;
            var box = new RectangleF(x, y, w, h);
            _chips.Add((s.Spec.Id, box));
            using GraphicsPath shape = Ui.Rounded(box, h / 2);
            using (var fill = new SolidBrush(selected ? Ui.AccentDeep : Color.FromArgb(150, 10, 14, 24))) g.FillPath(fill, shape);
            using (var edge = new Pen(selected ? Ui.Accent : Ui.Line, d)) g.DrawPath(edge, shape);
            using var ink = new SolidBrush(selected ? Ui.Accent : hidden ? Ui.Faint : Ui.Ink);
            g.DrawString(s.Spec.Name, font, ink, box, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap });
            x += w + 6 * d;
        }
    }

    void DrawReadout(Graphics g, float d, RectangleF stage)
    {
        using Font font = Ui.Mono(9f);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        var line = new RectangleF(0, stage.Bottom - 30 * d, stage.Width, 22 * d);
        if (_dragId is not null && _screens.FirstOrDefault(s => s.Spec.Id == _dragId) is { } dragged)
        {
            ScreenSpec spec = dragged.Spec;
            string where = $"{(MathF.Abs(spec.YawDeg) < 0.25f ? "AHEAD" : spec.YawDeg > 0 ? $"L {spec.YawDeg:0.0}°" : $"R {-spec.YawDeg:0.0}°")}   " +
                           $"{(MathF.Abs(spec.PitchDeg) < 0.25f ? "EYE LEVEL" : spec.PitchDeg > 0 ? $"UP {spec.PitchDeg:0.0}°" : $"DOWN {-spec.PitchDeg:0.0}°")}" +
                           (_snapNote.Length > 0 ? $"   ·   {_snapNote}" : "");
            using var ink = new SolidBrush(Ui.Accent);
            g.DrawString(where, font, ink, line, centred);
            return;
        }
        using var faint = new SolidBrush(Ui.Faint);
        g.DrawString("drag to move  ·  scroll to resize  ·  right-drag to look around  ·  double-click to reset the view", font, faint, line, centred);
    }

    // ---- the mouse -------------------------------------------------------------------------------

    string? ScreenAt(Point p)
    {
        foreach ((string id, RectangleF box) in _chips)
            if (box.Contains(p)) return id;
        for (int i = _shapes.Count - 1; i >= 0; i--) // nearest was drawn last
            if (_shapes[i].Shape.IsVisible(p)) return _shapes[i].Id;
        return null;
    }

    bool OnChip(Point p) => _chips.Any(c => c.Box.Contains(p));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        _lastMouse = e.Location;
        if (e.Button is MouseButtons.Right or MouseButtons.Middle)
        {
            _orbiting = true;
            Cursor = Cursors.SizeWE;
            return;
        }
        if (e.Button != MouseButtons.Left || ScreenAt(e.Location) is not { } id) return;
        Picked?.Invoke(id);
        if (OnChip(e.Location) || _screens.FirstOrDefault(s => s.Spec.Id == id) is not { } s) return;
        _dragId = id;
        _rawYaw = s.Spec.YawDeg;
        _rawPitch = s.Spec.PitchDeg;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int dx = e.X - _lastMouse.X, dy = e.Y - _lastMouse.Y;
        _lastMouse = e.Location;

        if (_orbiting)
        {
            _orbitYaw = MathF.IEEERemainder(_orbitYaw - dx * 0.3f, 360f);
            _orbitPitch = Math.Clamp(_orbitPitch + dy * 0.2f, 2f, 70f);
            Invalidate();
            return;
        }
        if (_dragId is null)
        {
            string? over = ScreenAt(e.Location);
            Cursor = over is null ? Cursors.Default : OnChip(e.Location) ? Cursors.Hand : Cursors.SizeAll;
            return;
        }
        if (_screens.FirstOrDefault(s => s.Spec.Id == _dragId) is not { } screen || (dx == 0 && dy == 0)) return;

        // Which way one degree of yaw and of pitch moves the screen's middle on this picture, here; the mouse
        // movement is split along those two directions.
        SetUpCamera();
        ScreenSpec at = screen.Spec with { YawDeg = _rawYaw, PitchDeg = _rawPitch };
        if (Project(PanelOf(screen, at).Centre, out PointF c0) &&
            Project(PanelOf(screen, at with { YawDeg = _rawYaw + 1 }).Centre, out PointF cy) &&
            Project(PanelOf(screen, at with { PitchDeg = _rawPitch + 1 }).Centre, out PointF cp))
        {
            float ax = cy.X - c0.X, ay = cy.Y - c0.Y, bx = cp.X - c0.X, by = cp.Y - c0.Y;
            float det = ax * by - ay * bx;
            if (MathF.Abs(det) > 0.05f)
            {
                _rawYaw += (dx * by - dy * bx) / det;
                _rawPitch += (ax * dy - ay * dx) / det;
            }
            else
            {
                _rawYaw -= dx * 0.2f;
                _rawPitch -= dy * 0.2f;
            }
        }
        _rawYaw = Math.Clamp(_rawYaw, -180f, 180f);
        _rawPitch = Math.Clamp(_rawPitch, -45f, 45f);
        (float yaw, float pitch, _snapNote) = Snap(screen, _rawYaw, _rawPitch, (ModifierKeys & Keys.Alt) == 0);
        Moved?.Invoke(screen.Spec.Id, yaw, pitch);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragId = null;
        _orbiting = false;
        _snapNote = "";
        Capture = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (ScreenAt(e.Location) is { } id && !OnChip(e.Location) && _screens.FirstOrDefault(s => s.Spec.Id == id) is { } s)
        {
            Picked?.Invoke(id);
            float step = (ModifierKeys & Keys.Control) != 0 ? 0.01f : 0.05f;
            Resized?.Invoke(id, Math.Clamp(s.Spec.Scale + Math.Sign(e.Delta) * step, 0.3f, 2.5f));
            return;
        }
        _orbitDistance = Math.Clamp(_orbitDistance - Math.Sign(e.Delta) * 0.3f, 2.4f, 8f);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        if (ScreenAt(e.Location) is not null) return;
        _orbitYaw = 0;
        _orbitPitch = DefaultOrbitPitch;
        _orbitDistance = DefaultOrbitDistance;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        HintWanted?.Invoke("Drag a screen to move it: it snaps beside, above or in line with the others (hold Alt to place it freely). Scroll over a screen to resize it. Right-drag to look around.");
        base.OnMouseEnter(e);
    }

    /// <summary>
    /// Where a dragged screen settles: edge to edge with a neighbour (a small gap between), lined up with one, or
    /// straight ahead or at eye level, whichever is within a few degrees; otherwise the nearest half degree.
    /// </summary>
    (float Yaw, float Pitch, string Note) Snap(SpatialScreen me, float yaw, float pitch, bool snap)
    {
        if (!snap) return (MathF.Round(yaw * 2) / 2, MathF.Round(pitch * 2) / 2, "");
        (float w, float h) = AngularSize(me);
        var yaws = new List<(float Value, string Note)> { (0, "straight ahead") };
        var pitches = new List<(float Value, string Note)> { (0, "eye level") };
        foreach (SpatialScreen o in _screens)
        {
            if (o.Spec.Id == me.Spec.Id || !o.Spec.Visible || o.Missing) continue;
            (float ow, float oh) = AngularSize(o);
            float across = (ow + w) / 2 + SnapGap, upDown = (oh + h) / 2 + SnapGap;
            yaws.Add((o.Spec.YawDeg - across, $"right of {o.Spec.Name}"));
            yaws.Add((o.Spec.YawDeg + across, $"left of {o.Spec.Name}"));
            yaws.Add((o.Spec.YawDeg, $"lined up with {o.Spec.Name}"));
            pitches.Add((o.Spec.PitchDeg + upDown, $"above {o.Spec.Name}"));
            pitches.Add((o.Spec.PitchDeg - upDown, $"below {o.Spec.Name}"));
            pitches.Add((o.Spec.PitchDeg, $"level with {o.Spec.Name}"));
            pitches.Add((o.Spec.PitchDeg + (oh - h) / 2, $"tops with {o.Spec.Name}"));
        }
        (float Value, string Note) bestYaw = yaws.MinBy(c => MathF.Abs(c.Value - yaw));
        (float Value, string Note) bestPitch = pitches.MinBy(c => MathF.Abs(c.Value - pitch));
        var notes = new List<string>();
        if (MathF.Abs(bestYaw.Value - yaw) <= SnapReach) { yaw = bestYaw.Value; notes.Add(bestYaw.Note); }
        else yaw = MathF.Round(yaw * 2) / 2;
        if (MathF.Abs(bestPitch.Value - pitch) <= SnapReach) { pitch = bestPitch.Value; notes.Add(bestPitch.Note); }
        else pitch = MathF.Round(pitch * 2) / 2;
        return (yaw, pitch, string.Join(", ", notes));
    }

    (float Width, float Height) AngularSize(SpatialScreen s)
    {
        float wide = PanelOf(s).AngularWidthDeg;
        return (wide, wide * Math.Max(s.PixelHeight, 1) / Math.Max(s.PixelWidth, 1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach ((_, GraphicsPath shape) in _shapes) shape.Dispose();
            _shapes.Clear();
        }
        base.Dispose(disposing);
    }
}
