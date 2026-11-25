using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace XrShift.App;

/// <summary>
/// The look of the app's own windows, "Midnight Aurora": deep navy surfaces that lift toward violet, light text,
/// a teal glow for whatever is on or selected and a violet second light, rounded cards with a fine bright rim.
/// Segoe UI Variable for words (Windows 11's own face) and Bahnschrift for numbers.
/// </summary>
internal static class Ui
{
    public static readonly Color Back = Color.FromArgb(13, 17, 46);          // the window, the top bar
    public static readonly Color RoomTop = Color.FromArgb(10, 13, 38);
    public static readonly Color RoomHorizon = Color.FromArgb(34, 33, 92);
    public static readonly Color RoomFloor = Color.FromArgb(14, 16, 48);
    public static readonly Color Dock = Color.FromArgb(17, 22, 60);
    public static readonly Color Inspector = Color.FromArgb(19, 25, 66);     // panels beside the room, page cards
    public static readonly Color Raised = Color.FromArgb(31, 39, 102);       // buttons, cards on a panel
    public static readonly Color Line = Color.FromArgb(48, 56, 122);
    public static readonly Color Rim = Color.FromArgb(70, 80, 160);          // the fine bright edge of a card
    public static readonly Color Ink = Color.FromArgb(241, 243, 255);
    public static readonly Color Muted = Color.FromArgb(170, 178, 222);
    public static readonly Color Faint = Color.FromArgb(108, 116, 172);
    public static readonly Color Accent = Color.FromArgb(94, 234, 212);      // teal
    public static readonly Color Violet = Color.FromArgb(196, 181, 253);
    public static readonly Color AccentDeep = Color.FromArgb(17, 72, 86);
    public static readonly Color OnAccent = Color.FromArgb(4, 52, 44);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Good = Color.FromArgb(134, 239, 172);
    public static readonly Color Bad = Color.FromArgb(252, 165, 165);

    /// <summary>The aurora backdrop: navy at the top fading to deep violet, with a teal glow top left and a violet one bottom right.</summary>
    public static void PaintAurora(Graphics g, Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using (var sky = new LinearGradientBrush(r, Color.FromArgb(12, 16, 44), Color.FromArgb(28, 20, 70), LinearGradientMode.Vertical))
            g.FillRectangle(sky, r);
        Glow(g, new RectangleF(r.Left - r.Width * 0.25f, r.Top - r.Height * 0.6f, r.Width * 0.9f, r.Height * 1.1f), Color.FromArgb(70, 94, 234, 212));
        Glow(g, new RectangleF(r.Left + r.Width * 0.45f, r.Top + r.Height * 0.35f, r.Width * 0.9f, r.Height * 1.1f), Color.FromArgb(80, 167, 139, 250));
    }

    static void Glow(Graphics g, RectangleF area, Color centre)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(area);
        using var brush = new PathGradientBrush(path) { CenterColor = centre, SurroundColors = [Color.FromArgb(0, centre)] };
        g.FillPath(brush, path);
    }

    // Kept for code that still names the old surfaces.
    public static Color Card => Inspector;

    static readonly HashSet<string> Installed = new(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

    static string Pick(params string[] names) => names.FirstOrDefault(Installed.Contains) ?? "Segoe UI";

    static readonly string TextFace = Pick("Segoe UI Variable Text", "Segoe UI");
    static readonly string StrongFace = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold", "Segoe UI");
    static readonly string NarrowFace = Pick("Bahnschrift SemiCondensed", "Bahnschrift", "Segoe UI");
    /// <summary>Windows' own icon font, for the side menu.</summary>
    public static readonly string IconFace = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    public static Font Text(float points, bool bold = false) =>
        bold ? new Font(StrongFace, points, FontStyle.Regular) : new Font(TextFace, points, FontStyle.Regular);

    public static Font Display(float points) => new(StrongFace, points, FontStyle.Regular);

    /// <summary>For anything measured: angles, milliseconds, frame rates.</summary>
    public static Font Mono(float points, bool bold = false) => new(bold ? StrongFace : NarrowFace, points, FontStyle.Regular);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = MathF.Min(radius * 2, MathF.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public static Color Fade(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c.R, c.G, c.B);
}

/// <summary>A small heading inside the inspector: a short cyan tick and a word.</summary>
internal sealed class Heading : Control
{
    public Heading()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 22;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        g.Clear(Parent?.BackColor ?? Ui.Inspector);
        float d = DeviceDpi / 96f;
        using (var tick = new SolidBrush(Ui.Accent)) g.FillRectangle(tick, 0, Height / 2f - 1 * d, 10 * d, 2 * d);
        using Font font = Ui.Text(10f, bold: true);
        using var ink = new SolidBrush(Ui.Ink);
        g.DrawString(Text, font, ink, new RectangleF(16 * d, 0, Width - 16 * d, Height),
            new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap });
    }
}

/// <summary>A button with softly rounded corners. It can also act as a switch that glows while on.</summary>
internal sealed class PillButton : Control
{
    public enum Looks { Accent, Solid, Ghost, Tab }

    bool _hover, _down, _on;

    public Looks Look { get; set; } = Looks.Solid;
    /// <summary>A second, smaller line under the text, e.g. the key that does the same thing.</summary>
    public string Detail { get; set; } = "";
    public string Hint { get; set; } = "";
    /// <summary>A colour for the words instead of the usual one (not on accent buttons or while on).</summary>
    public Color? TextColour { get; set; }
    /// <summary>Corner radius in logical pixels.</summary>
    public float Radius { get; set; } = 10;
    public event Action<string>? HintWanted;

    public bool On
    {
        get => _on;
        set { _on = value; Invalidate(); }
    }

    /// <summary>
    /// Shown as not available and does nothing when clicked, but still answers the pointer, so its hint and
    /// tooltip can say why (Windows gives a disabled control no tooltip).
    /// </summary>
    public bool Inert
    {
        get => _inert;
        set { _inert = value; Cursor = value ? Cursors.Default : Cursors.Hand; Invalidate(); }
    }
    bool _inert;

    protected override void OnClick(EventArgs e)
    {
        if (!_inert) base.OnClick(e);
    }

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    /// <summary>Acts as if clicked.</summary>
    public void PerformClick() => OnClick(EventArgs.Empty);

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; HintWanted?.Invoke(Hint); Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty);
        else base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        Color ground = Parent?.BackColor ?? Ui.Back;
        g.Clear(ground);
        float d = DeviceDpi / 96f;
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };

        if (Look == Looks.Tab)
        {
            // A word with a line of light under it while it is the one showing.
            using var tabInk = new SolidBrush(!Enabled ? Ui.Faint : _on ? Ui.Ink : _hover ? Ui.Ink : Ui.Muted);
            g.DrawString(Text, Font, tabInk, r, centred);
            if (_on)
            {
                using var bar = new SolidBrush(Ui.Accent);
                g.FillRectangle(bar, r.Left + r.Width * 0.18f, r.Bottom - 2.5f * d, r.Width * 0.64f, 2 * d);
            }
            return;
        }

        bool primary = Look == Looks.Accent, live = Enabled && !_inert; // an inert button looks disabled
        Color fill = !live ? ground
            : primary ? (_down ? ControlPaint.Dark(Ui.Accent, 0.08f) : _hover ? ControlPaint.Light(Ui.Accent, 0.25f) : Ui.Accent)
            : _on ? (_hover ? Color.FromArgb(22, 92, 108) : Ui.AccentDeep)
            : Look == Looks.Ghost ? (_hover ? Ui.Raised : ground)
            : _down ? Ui.Line : _hover ? Color.FromArgb(42, 52, 128) : Ui.Raised;
        Color edge = !live ? Ui.Line : _on && !primary ? Ui.Fade(Ui.Accent, 200) : Look == Looks.Ghost ? Ui.Rim : Ui.Fade(Ui.Rim, 140);
        Color ink = !live ? Ui.Faint : primary ? Ui.OnAccent : _on ? Ui.Accent : TextColour ?? Ui.Ink;

        using (GraphicsPath path = Ui.Rounded(r, Radius * d))
        {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            if (edge != Color.Empty)
            {
                using var pen = new Pen(edge, d);
                g.DrawPath(pen, path);
            }
            if (Focused && live)
            {
                using var ring = new Pen(primary ? Ui.Ink : Ui.Accent, 1.5f * d);
                g.DrawPath(ring, path);
            }
        }

        using var text = new SolidBrush(ink);
        if (Detail.Length == 0)
        {
            g.DrawString(Text, Font, text, r, centred);
            return;
        }
        using Font small = Ui.Mono(7.5f);
        using var dim = new SolidBrush(primary ? Ui.OnAccent : _on ? Ui.Fade(Ui.Accent, 190) : Ui.Muted);
        g.DrawString(Text, Font, text, new RectangleF(r.Left, r.Top + r.Height * 0.12f, r.Width, r.Height * 0.46f), centred);
        g.DrawString(Detail, small, dim, new RectangleF(r.Left, r.Top + r.Height * 0.56f, r.Width, r.Height * 0.32f), centred);
    }
}

/// <summary>One labelled fader. Drag, scroll or use the arrow keys; double-click puts it back.</summary>
internal sealed class SliderRow : Control
{
    float _value;
    bool _dragging, _hover;

    public string Caption { get; init; } = "";
    public float Min { get; init; }
    public float Max { get; init; } = 100;
    public float Step { get; init; } = 1;
    public float ResetTo { get; init; }
    public int Decimals { get; init; }
    public string Unit { get; init; } = "";
    public string Hint { get; init; } = "";
    /// <summary>Fill the track outward from zero instead of from the left end (for values that go both ways).</summary>
    public bool FromZero { get; init; }

    /// <summary>The wearer moved the slider.</summary>
    public event Action<float>? Changed;
    public event Action<string>? HintWanted;

    public SliderRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Height = 34;
        Cursor = Cursors.Hand;
    }

    public float Value
    {
        get => _value;
        set
        {
            _value = Snap(value);
            Invalidate();
        }
    }

    float Snap(float v)
    {
        v = Math.Clamp(v, Min, Max);
        return Step > 0 ? Math.Clamp(MathF.Round(v / Step) * Step, Min, Max) : v;
    }

    float Dpi => DeviceDpi / 96f;
    RectangleF Track => new(100 * Dpi, Height / 2f - 1 * Dpi, Math.Max(Width - 170 * Dpi, 10), 2 * Dpi);

    void SetByUser(float v)
    {
        float snapped = Snap(v);
        if (snapped == _value) return;
        _value = snapped;
        Invalidate();
        Changed?.Invoke(_value);
    }

    void SetFromX(int x)
    {
        RectangleF track = Track;
        SetByUser(Min + Math.Clamp((x - track.Left) / track.Width, 0f, 1f) * (Max - Min));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e) => _dragging = false;
    protected override void OnMouseWheel(MouseEventArgs e) => SetByUser(_value + Math.Sign(e.Delta) * Step);
    protected override void OnMouseDoubleClick(MouseEventArgs e) => SetByUser(ResetTo);
    protected override void OnMouseEnter(EventArgs e) { _hover = true; HintWanted?.Invoke(Hint); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { HintWanted?.Invoke(Hint); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) => Invalidate();
    protected override void OnEnabledChanged(EventArgs e) => Invalidate();
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) SetByUser(_value - Step);
        else if (e.KeyCode == Keys.Right) SetByUser(_value + Step);
        else base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        g.Clear(Parent?.BackColor ?? Ui.Inspector);
        float d = Dpi;
        RectangleF track = Track;

        using (var caption = new SolidBrush(Enabled ? (_hover || Focused ? Ui.Ink : Ui.Muted) : Ui.Faint))
            g.DrawString(Caption, Font, caption, new RectangleF(0, 0, 96 * d, Height),
                new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter });

        using (Font number = Ui.Mono(10.5f))
        using (var value = new SolidBrush(Enabled ? Ui.Ink : Ui.Faint))
            g.DrawString((MathF.Abs(_value) < 0.5f * MathF.Pow(10, -Decimals) ? 0f : _value).ToString("F" + Decimals) + Unit, number, value, new RectangleF(track.Right + 8 * d, 0, Width - track.Right - 8 * d, Height),
                new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap });

        using (var trackBrush = new SolidBrush(Ui.Line)) g.FillRectangle(trackBrush, track);
        float at = track.Left + (Max > Min ? (_value - Min) / (Max - Min) : 0) * track.Width;
        float from = FromZero && Min < 0 && Max > 0 ? track.Left + (0 - Min) / (Max - Min) * track.Width : track.Left;
        using (var lit = new SolidBrush(Enabled ? Ui.Accent : Ui.Faint))
            g.FillRectangle(lit, MathF.Min(from, at), track.Top, MathF.Max(MathF.Abs(at - from), 0.1f), track.Height);
        if (FromZero)
        {
            using var tick = new Pen(Ui.Faint, d);
            g.DrawLine(tick, from, track.Top - 6 * d, from, track.Bottom + 6 * d);
        }

        // The fader cap: a short upright bar, lit while held or focused.
        var cap = new RectangleF(at - 3 * d, Height / 2f - 9 * d, 6 * d, 18 * d);
        if (Enabled && (_dragging || Focused))
        {
            using GraphicsPath glowPath = Ui.Rounded(RectangleF.Inflate(cap, 4 * d, 4 * d), 6 * d);
            using var glow = new SolidBrush(Ui.Fade(Ui.Accent, 60));
            g.FillPath(glow, glowPath);
        }
        using GraphicsPath capPath = Ui.Rounded(cap, 3 * d);
        using var capBrush = new SolidBrush(Enabled ? Ui.Ink : Ui.Faint);
        g.FillPath(capBrush, capPath);
    }
}
