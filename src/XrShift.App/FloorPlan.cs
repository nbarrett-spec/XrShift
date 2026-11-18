using System.Drawing.Drawing2D;
using XrShift.Scene;

namespace XrShift.App;

/// <summary>
/// The room seen from above, for stationary mode: you in the middle facing up, rings every metre, and each screen
/// drawn where it stands. Drag a screen around you to put it on a wall, in or out to set how far away it is (it
/// keeps its real size, so further means smaller in view), scroll over it to make it bigger or smaller. Directions
/// snap to every 15 degrees; hold Alt to place freely.
/// </summary>
internal sealed class FloorPlan : Control
{
    const float MetresAcross = 9f; // the view shows this many metres from edge to edge (4.5 m around you)
    const float MinDistance = 0.6f, MaxDistance = 4.2f;

    IReadOnlyList<SpatialScreen> _screens = [];
    string? _selected;
    DisplayOptics _optics = DisplayOptics.OnePro;
    readonly List<(string Id, RectangleF Hit)> _hits = [];
    string? _dragging;

    /// <summary>A screen was clicked.</summary>
    public event Action<string>? Picked;
    /// <summary>A screen was dragged: id, yaw (degrees, + = left), distance (m), scale.</summary>
    public event Action<string, float, float, float>? Placed;
    public event Action<string>? HintWanted;

    /// <summary>Height of the strip along the bottom kept for the buttons placed over it, logical pixels.</summary>
    public int DockHeight { get; set; } = 64;

    public FloorPlan()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Dock;
    }

    public void Show(IReadOnlyList<SpatialScreen> screens, string? selected, DisplayOptics optics)
    {
        _screens = screens;
        _selected = selected;
        _optics = optics;
        Invalidate();
    }

    float D => DeviceDpi / 96f;
    RectangleF Stage => new(0, 0, Width, Math.Max(Height - DockHeight * D, 10));
    PointF Centre => new(Stage.Width / 2f, Stage.Height * 0.55f);
    float PixelsPerMetre => Math.Min(Stage.Width, Stage.Height) / MetresAcross;

    // Yaw + is to the left; on the plan, front is up.
    PointF At(float yawDeg, float metres)
    {
        float a = yawDeg * MathF.PI / 180f;
        return new PointF(Centre.X - MathF.Sin(a) * metres * PixelsPerMetre, Centre.Y - MathF.Cos(a) * metres * PixelsPerMetre);
    }

    float WidthMetres(SpatialScreen s) => s.Spec.ToPanel(s.PixelWidth, s.PixelHeight, _optics).Width;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        float d = D;
        RectangleF stage = Stage;
        using (var room = new SolidBrush(Ui.RoomHorizon)) g.FillRectangle(room, stage);
        using (var dock = new SolidBrush(Ui.Dock)) g.FillRectangle(dock, 0, stage.Bottom, Width, Height - stage.Bottom);
        g.SetClip(stage);

        // Rings every metre, labelled in feet and metres, and the four directions.
        using Font small = Ui.Mono(8.5f);
        using var faint = new SolidBrush(Ui.Faint);
        using var ring = new Pen(Ui.Fade(Ui.Line, 200), d);
        for (int m = 1; m <= 4; m++)
        {
            float r = m * PixelsPerMetre;
            g.DrawEllipse(ring, Centre.X - r, Centre.Y - r, 2 * r, 2 * r);
            g.DrawString($"{m * 3.28f:F0} ft · {m} m", small, faint, Centre.X + 4 * d, Centre.Y - r + 2 * d);
        }
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (Font label = Ui.Mono(9f))
        using (var muted = new SolidBrush(Ui.Muted))
        {
            float r = 4.3f * PixelsPerMetre;
            g.DrawString("FRONT", label, muted, At(0, 4.3f), centred);
            g.DrawString("BEHIND", label, muted, At(180, 4.3f), centred);
            g.DrawString("LEFT", label, muted, At(90, 4.3f), centred);
            g.DrawString("RIGHT", label, muted, At(-90, 4.3f), centred);
            _ = r;
        }

        // You, facing front.
        using (var you = new SolidBrush(Ui.Accent))
        {
            float r = 9 * d;
            g.FillEllipse(you, Centre.X - r, Centre.Y - r, 2 * r, 2 * r);
            PointF[] nose = [new(Centre.X, Centre.Y - 22 * d), new(Centre.X - 7 * d, Centre.Y - 9 * d), new(Centre.X + 7 * d, Centre.Y - 9 * d)];
            g.FillPolygon(you, nose);
        }
        using (Font label = Ui.Mono(8.5f))
        using (var ink = new SolidBrush(Ui.Accent))
            g.DrawString("YOU", label, ink, Centre.X + 12 * d, Centre.Y - 2 * d);

        // Screens: a bar facing you, as wide as the screen really is, at its distance. Bars first, names after,
        // so a name can be kept clear of every bar and of every other name.
        _hits.Clear();
        var bars = new List<(SpatialScreen S, PointF P1, PointF P2, PointF Mid, float Yaw, float Dist)>();
        foreach (SpatialScreen s in _screens)
        {
            if (!s.Spec.Visible) continue;
            bool selected = s.Spec.Id == _selected;
            float yaw = s.Spec.YawDeg, dist = s.Spec.Distance, half = WidthMetres(s) / 2f;
            float facing = (yaw + s.Spec.TurnDeg) * MathF.PI / 180f; // a turned screen lies at its angle, like a TV on a side wall
            var across = new PointF(MathF.Cos(facing), -MathF.Sin(facing));
            PointF mid = At(yaw, dist);
            PointF p1 = new(mid.X - across.X * half * PixelsPerMetre, mid.Y + across.Y * half * PixelsPerMetre);
            PointF p2 = new(mid.X + across.X * half * PixelsPerMetre, mid.Y - across.Y * half * PixelsPerMetre);
            using (var beam = new Pen(Ui.Fade(Ui.Accent, selected ? 70 : 25), 1.2f * d) { DashPattern = [3f, 4f] })
                g.DrawLine(beam, Centre, mid);
            using (var bar = new Pen(selected ? Ui.Accent : s.Missing ? Ui.Faint : Ui.Ink, (selected ? 8 : 6) * d) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(bar, p1, p2);
            bars.Add((s, p1, p2, mid, yaw, dist));
        }

        bool Crosses(RectangleF box)
        {
            RectangleF wide = RectangleF.Inflate(box, 5 * d, 5 * d);
            foreach (var b in bars)
                for (int i = 0; i <= 16; i++)
                {
                    float t = i / 16f;
                    if (wide.Contains(b.P1.X + (b.P2.X - b.P1.X) * t, b.P1.Y + (b.P2.Y - b.P1.Y) * t)) return true;
                }
            return false;
        }

        var written = new List<RectangleF>(); // names already written, so two never overlap
        using Font name = Ui.Text(9.5f, bold: true);
        foreach (var b in bars)
        {
            bool selected = b.S.Spec.Id == _selected;
            using var text = new SolidBrush(selected ? Ui.Accent : Ui.Ink);
            SizeF size = g.MeasureString(b.S.Spec.Name, name);
            float a = b.Yaw * MathF.PI / 180f;
            float reach = 0.28f + size.Width / 2f / PixelsPerMetre * MathF.Abs(MathF.Sin(a));
            RectangleF BoxAt(float metres)
            {
                PointF c = At(b.Yaw, metres);
                return new RectangleF(c.X - size.Width / 2, c.Y - size.Height / 2, size.Width, size.Height);
            }
            bool Free(RectangleF r) => !Crosses(r) && !written.Any(w => w.IntersectsWith(r));
            // Just beyond the screen first, then just in front of it, then step further out until clear.
            RectangleF box = BoxAt(b.Dist + reach);
            if (!Free(box)) { RectangleF inside = BoxAt(Math.Max(0.35f, b.Dist - reach)); if (Free(inside)) box = inside; }
            for (int step = 1; step < 12 && !Free(box); step++) box = BoxAt(b.Dist + reach + step * 0.18f);
            written.Add(box);
            g.DrawString(b.S.Spec.Name, name, text, box.Location);
            float hx = Math.Min(b.P1.X, b.P2.X) - 10 * d, hy = Math.Min(b.P1.Y, b.P2.Y) - 10 * d;
            var hit = RectangleF.Union(new RectangleF(hx, hy, Math.Abs(b.P1.X - b.P2.X) + 20 * d, Math.Abs(b.P1.Y - b.P2.Y) + 20 * d), box);
            _hits.Add((b.S.Spec.Id, hit));
        }

        g.ResetClip();
        using (Font tip = Ui.Mono(8.5f))
        using (var faintInk = new SolidBrush(Ui.Faint))
            g.DrawString("drag a screen around you · drag in or out for distance · scroll to resize · Alt = no snapping",
                tip, faintInk, new RectangleF(0, stage.Bottom - 26 * d, Width, 20 * d), centred);
    }

    string? HitAt(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Hit.Contains(p)) return _hits[i].Id;
        return null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (HitAt(e.Location) is not { } id) return;
        _dragging = id;
        _selected = id;
        Picked?.Invoke(id);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is null || _screens.FirstOrDefault(s => s.Spec.Id == _dragging) is not { } s) return;
        float dx = e.X - Centre.X, dy = e.Y - Centre.Y;
        float yaw = MathF.Atan2(-dx, -dy) * 180f / MathF.PI;
        if ((ModifierKeys & Keys.Alt) == 0) yaw = MathF.Round(yaw / 15f) * 15f;
        float dist = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) / PixelsPerMetre, MinDistance, MaxDistance);
        // Keep the screen's real size: further away it looks smaller, as a real one would.
        float scale = Math.Clamp(s.Spec.Scale * s.Spec.Distance / dist, 0.2f, 3f);
        Placed?.Invoke(s.Spec.Id, yaw, dist, scale);
        string side = MathF.Abs(yaw) < 20 ? "in front" : MathF.Abs(yaw) > 160 ? "behind you" : yaw > 0 ? "on your left" : "on your right";
        HintWanted?.Invoke($"{s.Spec.Name}: {side}, {dist * 3.28f:F0} ft ({dist:F1} m) away, {MathF.Abs(yaw):F0}° {(yaw >= 0 ? "left" : "right")}.");
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = null;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (HitAt(e.Location) is not { } id || _screens.FirstOrDefault(s => s.Spec.Id == id) is not { } s) return;
        float scale = Math.Clamp(s.Spec.Scale * (e.Delta > 0 ? 1.06f : 1 / 1.06f), 0.2f, 3f);
        Placed?.Invoke(id, s.Spec.YawDeg, s.Spec.Distance, scale);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        HintWanted?.Invoke("The room from above. Drag a screen around you to put it on a wall, in or out to set how far away it is. Scroll over one to resize it. Its height is on the Screen page.");
    }
}
