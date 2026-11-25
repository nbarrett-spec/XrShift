using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using XrShift.Scene;

namespace XrShift.App;

/// <summary>What an edge arrow points at. Each kind has its own colour and badge, so they are told apart at a glance.</summary>
internal enum HintKind { Screen, NeedsYou, Mouse, Opened }

/// <summary>How loud a notice is: info is quiet and brief; needs-you is larger, warm-edged, and stays for its whole time.</summary>
internal enum NoticeLevel { Info, NeedsYou }

/// <summary>One edge arrow: which way (0 = right, 90 = up, in the view), what it says, and what kind of thing it points at.</summary>
internal readonly record struct Hint(float AngleDeg, string Name, HintKind Kind);

/// <summary>
/// The one look every drawing in the glasses shares, in the settings window's colours (navy, teal). Sizes are in pixels
/// of the glasses' display: 1920x1080 over about 51 degrees, some 38 to the degree.
/// <para>
/// The glasses can only add light: black and dark shades are see-through. So a backing plate is a lit navy, fully opaque:
/// it hides a bright web page behind the text and still shows against a bright wall, and a brighter rim gives it an edge.
/// Light marks drawn straight over screens (arrows, the crosshair) get a dark outline instead, which is invisible over the
/// room and an edge over a bright screen. Nothing is drawn thinner than 2 pixels or as text under 24 pixels.
/// </para>
/// </summary>
internal static class Style
{
    public static readonly Color Backing = Ui.Raised;
    public static readonly Color Rim = Color.FromArgb(104, 118, 210);
    /// <summary>The rim of things that should not draw the eye, like the status strip.</summary>
    public static readonly Color QuietRim = Color.FromArgb(60, 70, 140);
    public static readonly Color Ink = Ui.Ink;
    public static readonly Color Muted = Ui.Muted;
    public static readonly Color Accent = Ui.Accent;
    public static readonly Color Shade = Color.FromArgb(6, 8, 24);
    /// <summary>Needs-you: always with the "!" shape and a white ring, never by colour alone (amber fades on a bright room).</summary>
    public static readonly Color Attention = Color.FromArgb(255, 192, 64);
    public static readonly Color Idle = Color.FromArgb(160, 170, 214);
    public static readonly Color Working = Color.FromArgb(110, 170, 255);
    public static readonly Color Done = Ui.Good;
    public static readonly Color Failed = Color.FromArgb(255, 120, 120);
    public static readonly Color Offline = Color.FromArgb(120, 128, 176);
    public static readonly Color Mouse = Ui.Violet;
    public static readonly Color Opened = Ui.Good;
    /// <summary>The thin edge round a floating screen, so a white page on a white wall still has one.</summary>
    public static readonly Color ScreenEdge = Color.FromArgb(64, 160, 150);

    /// <summary>Type sizes in display pixels. <see cref="MinText"/> is a floor no text goes under.</summary>
    public const float MinText = 24, Small = 24, Body = 28, Title = 34, Loud = 40;
    public const float Radius = 12, Stroke = 3, RimWidth = 2, Pad = 12, Band = 10;
    /// <summary>
    /// In 3D side-by-side each eye gets a half-width picture stretched back out, so text loses half its sharpness
    /// across: overlays are drawn this much larger.
    /// </summary>
    public const float StereoScale = 1.3f;

    /// <summary>A font of <paramref name="displayPx"/> display pixels, drawn at <paramref name="k"/> picture pixels per display pixel.</summary>
    public static Font Face(float displayPx, bool bold = false, float k = 1) =>
        new("Segoe UI", MathF.Max(displayPx, MinText) * k, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>A backing plate: lit navy, rounded, with a rim; <paramref name="band"/> paints a PC's colour down its left side.</summary>
    public static void Plate(Graphics g, RectangleF r, float k = 1, Color? rim = null, float rimWidth = RimWidth, Color? band = null)
    {
        using GraphicsPath shape = Ui.Rounded(r, Radius * k);
        using (var fill = new SolidBrush(Backing)) g.FillPath(fill, shape);
        if (band is { } b)
        {
            GraphicsState saved = g.Save();
            g.SetClip(shape);
            using var paint = new SolidBrush(b);
            g.FillRectangle(paint, r.X, r.Y, Band * k, r.Height);
            g.Restore(saved);
        }
        using var edge = new Pen(rim ?? Rim, rimWidth * k);
        g.DrawPath(edge, shape);
    }

    /// <summary>The colour and word of a bot's state.</summary>
    public static (Color Colour, string Word) StateOf(SceneObject o) => o.Offline ? (Offline, "offline") : o.State switch
    {
        BotState.Working => (Working, "working"),
        BotState.NeedsYou => (Attention, "needs you"),
        BotState.Done => (Done, "done"),
        BotState.Failed => (Failed, "failed"),
        _ => (Idle, SceneObject.StateName(o.State)),
    };

    /// <summary>
    /// A bot's state as a shape as well as a colour: idle a pause mark in a plain ring, working three dots in a dashed
    /// ring, needs-you a "!" on a filled disc inside a white ring, done a tick, failed a cross, offline a bar in a dotted ring.
    /// </summary>
    public static void StateMark(Graphics g, PointF c, float r, SceneObject o, string badge = "")
    {
        (Color colour, _) = StateOf(o);
        bool needs = !o.Offline && o.State == BotState.NeedsYou;
        using (var halo = new SolidBrush(Shade)) Circle(g, halo, c, r * (needs ? 1.28f : 1.12f));
        using (var fill = new SolidBrush(needs ? Attention : Backing)) Circle(g, fill, c, r);
        if (needs)
        {
            using var ring = new Pen(Ink, r * 0.12f);
            g.DrawEllipse(ring, c.X - r * 1.14f, c.Y - r * 1.14f, r * 2.28f, r * 2.28f);
        }
        else
        {
            using var ring = new Pen(colour, r * (o.Offline ? 0.08f : 0.12f));
            if (o.Offline) ring.DashStyle = DashStyle.Dot;
            else if (o.State == BotState.Working) ring.DashPattern = [2.2f, 1.1f];
            float rr = r - ring.Width / 2;
            g.DrawEllipse(ring, c.X - rr, c.Y - rr, rr * 2, rr * 2);
        }

        Color ink = needs ? Shade : colour;
        if (badge.Length > 0)
        {
            using Font f = Face(badge.Length <= 3 ? r * 0.9f : r * 0.5f, bold: true);
            using var brush = new SolidBrush(ink);
            g.DrawString(badge, f, brush, new RectangleF(c.X - r, c.Y - r, r * 2, r * 2), Centred);
            return;
        }
        using var mark = new SolidBrush(ink);
        using var line = new Pen(ink, r * 0.17f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (o.Offline) g.FillRectangle(mark, c.X - r * 0.32f, c.Y - r * 0.07f, r * 0.64f, r * 0.14f);
        else if (needs)
        {
            g.DrawLine(line, c.X, c.Y - r * 0.5f, c.X, c.Y + r * 0.12f);
            Circle(g, mark, new PointF(c.X, c.Y + r * 0.42f), r * 0.11f);
        }
        else switch (o.State)
        {
            case BotState.Working:
                for (int i = -1; i <= 1; i++) Circle(g, mark, new PointF(c.X + i * r * 0.32f, c.Y), r * 0.1f);
                break;
            case BotState.Done:
                g.DrawLines(line, [new PointF(c.X - r * 0.38f, c.Y + r * 0.02f), new PointF(c.X - r * 0.1f, c.Y + r * 0.3f), new PointF(c.X + r * 0.4f, c.Y - r * 0.28f)]);
                break;
            case BotState.Failed:
                g.DrawLine(line, c.X - r * 0.3f, c.Y - r * 0.3f, c.X + r * 0.3f, c.Y + r * 0.3f);
                g.DrawLine(line, c.X - r * 0.3f, c.Y + r * 0.3f, c.X + r * 0.3f, c.Y - r * 0.3f);
                break;
            default: // idle: a pause mark
                g.FillRectangle(mark, c.X - r * 0.24f, c.Y - r * 0.3f, r * 0.15f, r * 0.6f);
                g.FillRectangle(mark, c.X + r * 0.09f, c.Y - r * 0.3f, r * 0.15f, r * 0.6f);
                break;
        }
    }

    /// <summary>A small round badge with a shape in it, for labels: "!" needs you, a pointer for the mouse, "+" for a new window.</summary>
    public static void Badge(Graphics g, PointF c, float r, HintKind kind)
    {
        Color colour = KindColour(kind);
        using (var fill = new SolidBrush(colour)) Circle(g, fill, c, r);
        using var ink = new SolidBrush(Shade);
        using var line = new Pen(Shade, r * 0.24f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (kind)
        {
            case HintKind.NeedsYou:
                g.DrawLine(line, c.X, c.Y - r * 0.5f, c.X, c.Y + r * 0.1f);
                Circle(g, ink, new PointF(c.X, c.Y + r * 0.44f), r * 0.13f);
                break;
            case HintKind.Opened:
                g.DrawLine(line, c.X - r * 0.45f, c.Y, c.X + r * 0.45f, c.Y);
                g.DrawLine(line, c.X, c.Y - r * 0.45f, c.X, c.Y + r * 0.45f);
                break;
            case HintKind.Mouse:
                // A mouse pointer: tip at the top left.
                g.FillPolygon(ink, [new PointF(c.X - r * 0.35f, c.Y - r * 0.55f), new PointF(c.X + r * 0.42f, c.Y + r * 0.12f),
                    new PointF(c.X + r * 0.02f, c.Y + r * 0.16f), new PointF(c.X - r * 0.3f, c.Y + r * 0.55f)]);
                break;
        }
    }

    public static Color KindColour(HintKind kind) => kind switch
    {
        HintKind.NeedsYou => Attention,
        HintKind.Mouse => Mouse,
        HintKind.Opened => Opened,
        _ => Accent,
    };

    public static void Circle(Graphics g, Brush brush, PointF c, float r) => g.FillEllipse(brush, c.X - r, c.Y - r, r * 2, r * 2);

    public static readonly StringFormat Centred = new()
    {
        Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap,
    };

    public static readonly StringFormat OneLine = new()
    {
        LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap,
    };
}

/// <summary>Pictures the app draws itself: stand-in screens, the horizon ruler, bots, cards, arrows, notices, the crosshair and the status strip.</summary>
internal static class Pictures
{
    public static readonly Color Accent = Style.Accent;
    public static readonly Color Ink = Style.Ink;
    /// <summary>The status strip, at one picture pixel per display pixel.</summary>
    public const int StatusWidth = 1400, StatusHeight = 44;
    /// <summary>The notice area, at one picture pixel per display pixel; a needs-you notice can take two lines.</summary>
    public const int NoticeWidth = 1400, NoticeHeight = 132;
    public const int CrosshairSize = 48;
    public const int RulerWidth = 8192, RulerHeight = 128;

    public const int HandsWidth = 320, HandsHeight = 240;
    public const int MenuWidth = 1400, MenuHeight = 200;
    /// <summary>The arrows picture covers the whole view at two thirds of the display's pixels.</summary>
    public const int EdgesWidth = 1280, EdgesHeight = 720;
    const float ViewWidth = 1920, ViewHeight = 1080;

    /// <summary>
    /// Arrows just inside the edges of the view, each pointing the way to something out of sight, with a label on a
    /// plate beside it. Labels never sit on an arrow, on another label or on <paramref name="card"/> (the card of the bot
    /// looked at), and keep off the middle of the view and the <paramref name="soft"/> places (the bots) where they can
    /// without going far from their arrow; an arrow that would land on any of those slides along the edge. Two of the
    /// same kind the same way share one arrow. Coordinates are display pixels.
    /// </summary>
    public static Bitmap Edges(IReadOnlyList<Hint> hints, RectangleF? card = null, IReadOnlyList<RectangleF>? soft = null, bool stereo = false)
    {
        Bitmap picture = NewPicture(EdgesWidth, EdgesHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent); // only the arrows are drawn: screens behind stay visible
            if (hints.Count == 0) return picture;
            g.ScaleTransform(EdgesWidth / ViewWidth, EdgesHeight / ViewHeight);
            float s = stereo ? Style.StereoScale : 1f;
            // In 3D each eye's picture is shifted sideways by about 64 pixels; keep arrows clear of where it is cut.
            float inset = 56 + (stereo ? 64 : 0);
            float cx = ViewWidth / 2, cy = ViewHeight / 2, rx = cx - inset, ry = cy - inset * 0.8f - 30; // top and bottom: clear of the status strip
            var bots = new List<RectangleF>(soft ?? []);
            var middle = new List<RectangleF> { new(cx - 330, cy - 190, 660, 380) };
            var taken = new List<RectangleF>(); // never covered: the card, arrows, labels
            if (card is { } c) taken.Add(c);
            var keepOut = taken.Concat(bots).Concat(middle).ToList();
            var placed = new List<(Hint Hint, PointF Tip, PointF Dir, float Length)>();

            // Needs-you first, so it gets its true direction if two want the same spot.
            foreach (Hint hint in hints.OrderBy(h => h.Kind == HintKind.NeedsYou ? 0 : 1).ThenBy(h => h.AngleDeg))
            {
                bool loud = hint.Kind == HintKind.NeedsYou;
                float length = (loud ? 60 : 48) * s, half = (loud ? 21 : 15) * s;
                (PointF tip, PointF dir) = AtEdge(hint.AngleDeg);
                // Two of the same kind the same way share one arrow.
                var twin = placed.FirstOrDefault(p => p.Hint.Kind == hint.Kind && MathF.Abs(p.Hint.AngleDeg - hint.AngleDeg) < 6);
                if (twin.Hint.Name is not null)
                {
                    placed.Add((hint, twin.Tip, twin.Dir, twin.Length));
                    continue;
                }
                RectangleF box = ArrowBox(tip, dir, length, half);
                for (int i = 1; i <= 24 && keepOut.Concat(placed.Select(p => ArrowBox(p.Tip, p.Dir, p.Length, half))).Any(r => r.IntersectsWith(box)); i++)
                {
                    (tip, dir) = AtEdge(hint.AngleDeg + (i % 2 == 1 ? 1 : -1) * 4 * ((i + 1) / 2));
                    box = ArrowBox(tip, dir, length, half);
                }
                taken.Add(box);
                placed.Add((hint, tip, dir, length));
            }

            foreach (var (hint, tip, dir, length) in placed.GroupBy(p => p.Tip).Select(gr => gr.First()))
                Arrow(g, tip, dir, length, (hint.Kind == HintKind.NeedsYou ? 21 : 15) * s, hint.Kind, s);

            foreach (var (hint, tip, dir, length) in placed)
            {
                bool loud = hint.Kind == HintKind.NeedsYou;
                using Font font = Style.Face(Style.Body * s, bold: loud);
                SizeF text = g.MeasureString(hint.Name, font);
                float pad = Style.Pad * s, badge = hint.Kind == HintKind.Screen ? 0 : font.Height * 0.9f + pad * 0.6f;
                float w = MathF.Min(text.Width + badge + pad * 2, 560 * s), h = font.Height + pad;
                // Inward from the tip, beyond the arrow's own length, then slid along the edge until it is clear.
                float along = length + 10 * s + MathF.Abs(dir.X) * w / 2 + MathF.Abs(dir.Y) * h / 2;
                var centre = new PointF(tip.X - dir.X * along, tip.Y - dir.Y * along);
                var side = new PointF(-dir.Y, dir.X);
                float step = MathF.Abs(side.X) * w + MathF.Abs(side.Y) * h + 8;
                // The best of the spots along the edge: never on the card, an arrow or a label if any spot avoids them, then
                // covering as little of the bots and the middle as it can without straying far from its arrow.
                var spots = new List<PointF>();
                for (int i = 0; i < 13; i++)
                {
                    float shift = (i % 2 == 1 ? 1 : -1) * step * ((i + 1) / 2);
                    spots.Add(new PointF(centre.X + side.X * shift, centre.Y + side.Y * shift));
                }
                // Or right beside the arrow, on either side of it.
                float beside = (loud ? 21 : 15) * s + 10 * s + MathF.Abs(side.X) * w / 2 + MathF.Abs(side.Y) * h / 2;
                var middleOfArrow = new PointF(tip.X - dir.X * length / 2, tip.Y - dir.Y * length / 2);
                spots.Add(new PointF(middleOfArrow.X + side.X * beside, middleOfArrow.Y + side.Y * beside));
                spots.Add(new PointF(middleOfArrow.X - side.X * beside, middleOfArrow.Y - side.Y * beside));
                RectangleF label = default;
                float least = float.MaxValue;
                foreach (PointF p in spots)
                {
                    RectangleF spot = Clamp(new RectangleF(p.X - w / 2, p.Y - h / 2, w, h));
                    float away = MathF.Sqrt((spot.X + w / 2 - centre.X) * (spot.X + w / 2 - centre.X) + (spot.Y + h / 2 - centre.Y) * (spot.Y + h / 2 - centre.Y));
                    float cost = Covered(taken, spot) * 100 + Covered(bots, spot) * 3 + Covered(middle, spot) + away * h * 0.6f;
                    if (cost < least) (least, label) = (cost, spot);
                }
                taken.Add(label);
                Style.Plate(g, label, s, rim: loud ? Style.Attention : null, rimWidth: loud ? 3 : Style.RimWidth);
                float textLeft = label.X + pad;
                if (badge > 0)
                {
                    float r = font.Height * 0.45f;
                    Style.Badge(g, new PointF(label.X + pad + r, label.Y + label.Height / 2), r, hint.Kind);
                    textLeft += badge;
                }
                using var ink = new SolidBrush(Style.Ink);
                g.DrawString(hint.Name, font, ink, new RectangleF(textLeft, label.Y, label.Right - pad - textLeft + 2, label.Height), Style.OneLine);
            }

            // Where the line from the middle in that direction meets an ellipse just inside the edge; dir points outward.
            (PointF, PointF) AtEdge(float angleDeg)
            {
                float a = angleDeg * MathF.PI / 180f, dx = MathF.Cos(a), dy = -MathF.Sin(a);
                float k = 1f / MathF.Sqrt(dx * dx / (rx * rx) + dy * dy / (ry * ry));
                return (new PointF(cx + dx * k, cy + dy * k), new PointF(dx, dy));
            }
        }
        return picture;
    }

    static float Covered(List<RectangleF> places, RectangleF spot) =>
        places.Sum(r => { RectangleF o = RectangleF.Intersect(r, spot); return o.Width * o.Height; });

    static RectangleF ArrowBox(PointF tip, PointF dir, float length, float half)
    {
        float x0 = MathF.Min(tip.X, tip.X - dir.X * length), y0 = MathF.Min(tip.Y, tip.Y - dir.Y * length);
        float x1 = MathF.Max(tip.X, tip.X - dir.X * length), y1 = MathF.Max(tip.Y, tip.Y - dir.Y * length);
        float spreadX = MathF.Abs(dir.Y) * half, spreadY = MathF.Abs(dir.X) * half;
        return RectangleF.FromLTRB(x0 - spreadX - 4, y0 - spreadY - 4, x1 + spreadX + 4, y1 + spreadY + 4);
    }

    static RectangleF Clamp(RectangleF r)
    {
        const float m = 12;
        float x = Math.Clamp(r.X, m, MathF.Max(m, ViewWidth - m - r.Width)), y = Math.Clamp(r.Y, m, MathF.Max(m, ViewHeight - m - r.Height));
        return new RectangleF(x, y, r.Width, r.Height);
    }

    /// <summary>A dart: long and narrow with a notch at the back, so which way it points reads at a glance; dark-edged over bright screens.</summary>
    static void Arrow(Graphics g, PointF tip, PointF dir, float length, float half, HintKind kind, float s)
    {
        var side = new PointF(-dir.Y * half, dir.X * half);
        var back = new PointF(tip.X - dir.X * length, tip.Y - dir.Y * length);
        var notch = new PointF(tip.X - dir.X * length * 0.72f, tip.Y - dir.Y * length * 0.72f);
        PointF[] dart = [tip, new(back.X + side.X, back.Y + side.Y), notch, new(back.X - side.X, back.Y - side.Y)];
        using (var outline = new Pen(Style.Shade, 7 * s) { LineJoin = LineJoin.Round }) g.DrawPolygon(outline, dart);
        using (var fill = new SolidBrush(Style.KindColour(kind))) g.FillPolygon(fill, dart);
        if (kind == HintKind.NeedsYou)
        {
            using var ring = new Pen(Style.Ink, 3 * s) { LineJoin = LineJoin.Round };
            g.DrawPolygon(ring, dart);
        }
    }

    /// <summary>
    /// A notice in the notice area at the top of the view. Info: one quiet line on a plate. Needs-you: larger and bold, up
    /// to two lines, on a plate with a warm rim and a "!" badge. Drawn at one picture pixel per display pixel.
    /// </summary>
    public static Bitmap Notice(string text, NoticeLevel level)
    {
        Bitmap picture = NewPicture(NoticeWidth, NoticeHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            if (text.Length == 0) return picture;
            using var ink = new SolidBrush(Style.Ink);
            if (level == NoticeLevel.Info)
            {
                using Font font = Style.Face(Style.Body);
                float w = MathF.Min(g.MeasureString(text, font).Width + Style.Pad * 2.5f, NoticeWidth - 8), h = font.Height + 16;
                var plate = new RectangleF((NoticeWidth - w) / 2, 4, w, h);
                Style.Plate(g, plate);
                g.DrawString(text, font, ink, RectangleF.Inflate(plate, -Style.Pad, 0), Style.Centred);
                return picture;
            }
            NeedsYou(g, text, NoticeWidth / 2f, 4);
        }
        return picture;
    }

    /// <summary>The needs-you look: large bold words, up to two lines, on a plate with a warm rim and a "!" badge. Returns the plate.</summary>
    static RectangleF NeedsYou(Graphics g, string text, float centreX, float top)
    {
        using var ink = new SolidBrush(Style.Ink);
        using Font loud = Style.Face(Style.Title, bold: true);
        const float maxText = 1100, pad = 18;
        float badge = loud.Height * 1.1f;
        var wrap = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit };
        SizeF size = g.MeasureString(text, loud, new SizeF(maxText, loud.Height * 2.05f), wrap);
        float width = size.Width + badge + pad * 2 + 8, height = MathF.Max(size.Height, loud.Height) + pad * 1.4f;
        var box = new RectangleF(centreX - width / 2, top, width, height);
        Style.Plate(g, box, rim: Style.Attention, rimWidth: 4);
        Style.Badge(g, new PointF(box.X + pad + badge * 0.42f, box.Y + box.Height / 2), badge * 0.42f, HintKind.NeedsYou);
        g.DrawString(text, loud, ink, new RectangleF(box.X + pad + badge, box.Y, size.Width + 8, box.Height), wrap);
        return box;
    }

    /// <summary>The guide picture covers the whole view one picture pixel for one display pixel, so lines land where the maths puts them.</summary>
    public const int GuideWidth = 1920, GuideHeight = 1080;

    /// <summary>
    /// What the glasses show during glasses setup, over the room (black is see-through): the lines and crosses to lay on
    /// real edges, in picture pixels from the top-left; at the top where setup is, what to do, and why a press was
    /// refused; at the bottom what was found. Lines are 2 pixels of light inside a dark outline: thin enough to lie
    /// exactly on an edge, still an edge over a bright screen. The words go over the lines, so they can always be read.
    /// </summary>
    public static Bitmap Guide(IReadOnlyList<(PointF From, PointF To)> lines, IReadOnlyList<PointF> marks,
        string progress, string instruction, string? note, IReadOnlyList<string> results)
    {
        Bitmap picture = NewPicture(GuideWidth, GuideHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            // Coordinates are pixel edges here, as GuideDrawing.ToPicture gives them: x = 960 is the line between
            // the two middle columns, and a 2 pixel line there covers exactly those two.
            g.PixelOffsetMode = PixelOffsetMode.Half;
            const float arm = 28;
            using (var outline = new Pen(Style.Shade, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                foreach ((PointF a, PointF b) in lines) g.DrawLine(outline, a, b);
                foreach (PointF m in marks)
                {
                    g.DrawLine(outline, m.X - arm, m.Y, m.X + arm, m.Y);
                    g.DrawLine(outline, m.X, m.Y - arm, m.X, m.Y + arm);
                }
            }
            using (var light = new Pen(Style.Ink, 2))
            {
                foreach ((PointF a, PointF b) in lines) g.DrawLine(light, a, b);
                foreach (PointF m in marks)
                {
                    g.DrawLine(light, m.X - arm, m.Y, m.X + arm, m.Y);
                    g.DrawLine(light, m.X, m.Y - arm, m.X, m.Y + arm);
                }
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;

            float y = 24;
            var wrap = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
            if (progress.Length > 0)
            {
                using Font small = Style.Face(Style.Small, bold: true);
                float w = g.MeasureString(progress, small).Width + Style.Pad * 2;
                var plate = new RectangleF((GuideWidth - w) / 2, y, w, small.Height + 10);
                Style.Plate(g, plate, rim: Style.QuietRim);
                using var accent = new SolidBrush(Style.Accent);
                g.DrawString(progress, small, accent, plate, Style.Centred);
                y = plate.Bottom + 8;
            }
            if (instruction.Length > 0)
            {
                using Font big = Style.Face(Style.Title);
                const float maxText = 1320, pad = 18;
                SizeF size = g.MeasureString(instruction, big, new SizeF(maxText, big.Height * 4.1f), wrap);
                var plate = new RectangleF((GuideWidth - size.Width - pad * 2) / 2, y, size.Width + pad * 2, size.Height + pad * 1.2f);
                Style.Plate(g, plate);
                using var ink = new SolidBrush(Style.Ink);
                g.DrawString(instruction, big, ink, RectangleF.Inflate(plate, -pad + 2, 0), wrap);
                y = plate.Bottom + 10;
            }
            // Always say which keys do what: the first wearer saw "press Confirm" and pressed a button on the glasses.
            {
                const string keys = "SPACE confirm      BACKSPACE retry      S skip      ESC cancel";
                using Font small = Style.Face(Style.Small, bold: true);
                float w = g.MeasureString(keys, small).Width + Style.Pad * 2;
                var plate = new RectangleF((GuideWidth - w) / 2, y, w, small.Height + 10);
                Style.Plate(g, plate, rim: Style.QuietRim);
                using var ink = new SolidBrush(Style.Ink);
                g.DrawString(keys, small, ink, plate, Style.Centred);
                y = plate.Bottom + 10;
            }
            if (note is { Length: > 0 }) NeedsYou(g, note, GuideWidth / 2f, y);

            if (results.Count > 0)
            {
                using Font body = Style.Face(Style.Body);
                const float maxText = 1320, pad = 16;
                string text = string.Join("\n", results);
                var left = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
                SizeF size = g.MeasureString(text, body, new SizeF(maxText, body.Height * 9.1f), left);
                var plate = new RectangleF((GuideWidth - size.Width - pad * 2) / 2, GuideHeight - 24 - size.Height - pad * 1.2f, size.Width + pad * 2, size.Height + pad * 1.2f);
                Style.Plate(g, plate, rim: Style.Done);
                using var ink = new SolidBrush(Style.Ink);
                g.DrawString(text, body, ink, RectangleF.Inflate(plate, -pad + 2, 0), left);
            }
        }
        return picture;
    }

    /// <summary>
    /// The hand quick menu: a row of big rounded buttons, the lit one filled with the accent colour, and a line
    /// underneath saying how to use it.
    /// </summary>
    public static Bitmap Menu(IReadOnlyList<string> items, int highlight)
    {
        Bitmap picture = NewPicture(MenuWidth, MenuHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent); // the buttons sit over the screens without blacking them out
            int n = Math.Max(items.Count, 1), gap = 18;
            float w = (MenuWidth - gap * (n + 1)) / (float)n, h = 118;
            using Font face = Style.Face(30, bold: true);
            using Font hintFace = Style.Face(Style.Small);
            for (int i = 0; i < items.Count; i++)
            {
                var box = new RectangleF(gap + i * (w + gap), 10, w, h);
                bool lit = i == highlight;
                if (lit)
                {
                    using GraphicsPath shape = Ui.Rounded(box, 24);
                    using (var fill = new SolidBrush(Style.Accent)) g.FillPath(fill, shape);
                    using (var edge = new Pen(Style.Ink, 3)) g.DrawPath(edge, shape);
                }
                else
                {
                    using GraphicsPath shape = Ui.Rounded(box, 24);
                    using (var fill = new SolidBrush(Style.Backing)) g.FillPath(fill, shape);
                    using (var edge = new Pen(Style.Rim, Style.RimWidth)) g.DrawPath(edge, shape);
                }
                using var ink = new SolidBrush(lit ? Ui.OnAccent : Style.Ink);
                g.DrawString(items[i], face, ink, box, Style.Centred);
            }
            const string how = "move your hand to choose  -  pinch to pick  -  make a fist to close";
            float hw = g.MeasureString(how, hintFace).Width + Style.Pad * 2;
            var plate = new RectangleF((MenuWidth - hw) / 2, 142, hw, hintFace.Height + 12);
            Style.Plate(g, plate, rim: Style.QuietRim);
            using var hint = new SolidBrush(Style.Muted);
            g.DrawString(how, hintFace, hint, plate, Style.Centred);
        }
        return picture;
    }

    static readonly (int A, int B)[] HandLines =
    [
        (0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8), (5, 9), (9, 10), (10, 11), (11, 12),
        (9, 13), (13, 14), (14, 15), (15, 16), (13, 17), (17, 18), (18, 19), (19, 20), (0, 17),
    ];

    /// <summary>
    /// The small hand view in the corner of the glasses: what the glasses' camera sees of your hands, as lines of
    /// light on black (black is see-through in the glasses). Thumb and index tips light up while pinching.
    /// </summary>
    public static Bitmap Hands(IReadOnlyList<System.Numerics.Vector3[]> hands, bool pinching, string status)
    {
        Bitmap picture = NewPicture(HandsWidth, HandsHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Black);
            using (var frame = new Pen(Style.QuietRim, Style.RimWidth))
                g.DrawRectangle(frame, 1, 1, HandsWidth - 3, HandsHeight - 3);
            using var line = new Pen(Style.Accent, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var joint = new SolidBrush(Style.Ink);
            using var lit = new SolidBrush(Style.Attention);
            foreach (System.Numerics.Vector3[] hand in hands)
            {
                PointF At(int i) => new(hand[i].X * HandsWidth, hand[i].Y * HandsHeight);
                foreach ((int a, int b) in HandLines) g.DrawLine(line, At(a), At(b));
                for (int i = 0; i < hand.Length; i++)
                {
                    bool tip = i is 4 or 8;
                    float r = tip ? (pinching ? 7f : 5f) : 3f;
                    PointF p = At(i);
                    g.FillEllipse(tip && pinching ? lit : joint, p.X - r, p.Y - r, r * 2, r * 2);
                }
            }
            // This picture is shown at about 1.4 picture pixels per display pixel: 18 here is 25 in the glasses.
            using var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Style.Muted);
            g.DrawString(status, font, ink, new RectangleF(8, HandsHeight - 30, HandsWidth - 16, 26));
        }
        return picture;
    }

    public const int ObjectSize = 256;

    /// <summary>
    /// A bot, cluster or widget, about 7 degrees across: its name and state on a plate, with its PC's colour down the
    /// plate's side when it is on another PC, and under it its state as a mark (see <see cref="Style.StateMark"/>). The
    /// name is on top because the row sits low: looking a little down, the names come into view first.
    /// </summary>
    public static Bitmap Object(SceneObject o)
    {
        Bitmap picture = NewPicture(ObjectSize, ObjectSize, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            bool needs = !o.Offline && o.State == BotState.NeedsYou;
            // In the disc: the badge if there is one, else what kind of thing this is when it is not a bot.
            string inside = o.Badge.Length > 0 ? o.Badge : o.Kind == "bot" ? "" : o.Kind;
            Style.StateMark(g, new PointF(ObjectSize / 2f, 162), 48, o, inside);

            (Color colour, string word) = Style.StateOf(o);
            // A bot on another PC says which one, so the wearer knows where an answer has to be typed.
            string state = o.Pc.Length > 0 ? $"{word} on {o.Pc}" : word;
            using Font name = Style.Face(Style.Body, bold: true);
            using Font small = Style.Face(Style.Small, bold: needs);
            float band = o.Pc.Length > 0 ? Style.Band : 0;
            float w = MathF.Min(MathF.Max(g.MeasureString(o.Label, name).Width, g.MeasureString(state, small).Width) + Style.Pad * 2 + band, ObjectSize - 4);
            var plate = new RectangleF((ObjectSize - w) / 2, 8, w, name.Height + small.Height + 10);
            Style.Plate(g, plate, rim: needs ? Style.Attention : null, rimWidth: needs ? 3 : Style.RimWidth, band: o.Pc.Length > 0 ? PcColour(o.Pc) : null);
            var inner = new RectangleF(plate.X + band + 4, plate.Y + 4, plate.Width - band - 8, name.Height);
            using var ink = new SolidBrush(Style.Ink);
            using var stateInk = new SolidBrush(needs ? Style.Attention : Style.Muted);
            g.DrawString(o.Label, name, ink, inner, Style.Centred);
            g.DrawString(state, small, stateInk, new RectangleF(inner.X, inner.Bottom, inner.Width, small.Height), Style.Centred);
        }
        return picture;
    }

    public const int CardWidth = 640, CardHeight = 240;

    /// <summary>
    /// What a bot is doing, shown above it while the wearer looks at it, on a solid plate: its state mark and name, then
    /// its state and PC, then the last thing it said or was asked, wrapped to at most three lines. The plate is only as
    /// tall as it needs and sits at the bottom of the picture, next to the bot.
    /// </summary>
    public static Bitmap Card(SceneObject o)
    {
        Bitmap picture = NewPicture(CardWidth, CardHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            bool needs = !o.Offline && o.State == BotState.NeedsYou;
            (Color colour, string word) = Style.StateOf(o);
            using var ink = new SolidBrush(Style.Ink);
            using var soft = new SolidBrush(Style.Muted);
            using var stateInk = new SolidBrush(colour);
            using Font title = Style.Face(Style.Title, bold: true);
            using Font small = Style.Face(Style.Small, bold: true);
            using Font smallPlain = Style.Face(Style.Small);
            using Font body = Style.Face(Style.Body);
            float left = 22 + (o.Pc.Length > 0 ? Style.Band : 0), textLeft = left + 58, right = CardWidth - 24;

            string note = o.Note.Length > 0 ? o.Note : "Nothing reported yet.";
            var wrapped = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.LineLimit };
            float head = 12 + title.Height + small.Height + 10;
            float most = MathF.Min(3, MathF.Floor((CardHeight - 6 - head - 16) / body.Height)) * body.Height;
            float noteHeight = MathF.Min(g.MeasureString(note, body, new SizeF(right - left, most + 2), wrapped).Height, most);
            float y = CardHeight - 3 - (head + 6 + noteHeight + 10);

            var plate = new RectangleF(3, y, CardWidth - 6, CardHeight - 3 - y);
            Style.Plate(g, plate, rim: needs ? Style.Attention : null, rimWidth: needs ? 3 : Style.RimWidth, band: o.Pc.Length > 0 ? PcColour(o.Pc) : null);
            Style.StateMark(g, new PointF(left + 22, y + 12 + title.Height * 0.85f), 22, o);
            g.DrawString(o.Label, title, ink, new RectangleF(textLeft, y + 12, right - textLeft, title.Height), Style.OneLine);
            float wordWidth = g.MeasureString(word, small).Width;
            g.DrawString(word, small, stateInk, new RectangleF(textLeft, y + 12 + title.Height, wordWidth + 4, small.Height), Style.OneLine);
            if (o.Pc.Length > 0)
                g.DrawString($"on {o.Pc}", smallPlain, soft, new RectangleF(textLeft + wordWidth, y + 12 + title.Height, right - textLeft - wordWidth, small.Height), Style.OneLine);

            using (var rule = new Pen(Style.QuietRim, Style.RimWidth)) g.DrawLine(rule, left, y + head, right, y + head);
            g.DrawString(note, body, o.Note.Length > 0 ? ink : soft, new RectangleF(left, y + head + 6, right - left, noteHeight + 2), wrapped);
        }
        return picture;
    }

    /// <summary>A steady colour for each PC name, so its bots are recognised at a glance. The same on every run and every PC.</summary>
    static Color PcColour(string pc)
    {
        int sum = 0;
        foreach (char c in pc.ToUpperInvariant()) sum = (sum * 31 + c) % 360;
        // Hue to colour at full strength, a little lightened so it reads on black.
        float h = sum / 60f, x = 1 - MathF.Abs(h % 2 - 1);
        (float r, float gr, float b) = ((int)h) switch { 0 => (1f, x, 0f), 1 => (x, 1f, 0f), 2 => (0f, 1f, x), 3 => (0f, x, 1f), 4 => (x, 0f, 1f), _ => (1f, 0f, x) };
        return Color.FromArgb((int)(90 + r * 165), (int)(90 + gr * 165), (int)(90 + b * 165));
    }

    /// <summary>One colour all over: for the edge round a screen, which is stretched to any shape.</summary>
    public static Bitmap Solid(Color colour)
    {
        Bitmap picture = NewPicture(4, 4, out Graphics g);
        using (g) g.Clear(colour);
        return picture;
    }

    static Bitmap NewPicture(int width, int height, out Graphics g)
    {
        var picture = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        g = Graphics.FromImage(picture);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // grey edges: sub-pixel colour fringes make no sense on a floating screen
        return picture;
    }

    static Font Face(float pixels, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", pixels, style, GraphicsUnit.Pixel);

    /// <summary>A stand-in for a screen that has no live picture: a grid, its name, and a ladder of text sizes.</summary>
    public static Bitmap Screen(string name, string note, int w = 1920, int h = 1080)
    {
        Bitmap picture = NewPicture(w, h, out Graphics g);
        float k = h / 1080f;
        using (g)
        {
            using (var back = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(20, 26, 38), Color.FromArgb(10, 12, 18), 90f))
                g.FillRectangle(back, 0, 0, w, h);

            using var faint = new Pen(Color.FromArgb(30, 255, 255, 255), 1);
            using var mid = new Pen(Color.FromArgb(80, 255, 255, 255), 1);
            float step = 120 * k;
            for (float x = w / 2f % step; x <= w; x += step) g.DrawLine(MathF.Abs(x - w / 2f) < 1 ? mid : faint, x, 0, x, h);
            for (float y = h / 2f % step; y <= h; y += step) g.DrawLine(MathF.Abs(y - h / 2f) < 1 ? mid : faint, 0, y, w, y);
            g.DrawEllipse(mid, w / 2f - 300 * k, h / 2f - 300 * k, 600 * k, 600 * k);

            using (var edge = new Pen(Accent, 6 * k))
                g.DrawRectangle(edge, 3 * k, 3 * k, w - 6 * k, h - 6 * k);

            using var ink = new SolidBrush(Ink);
            using var accent = new SolidBrush(Accent);
            using var dim = new SolidBrush(Color.FromArgb(170, Ink));
            var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

            using (Font title = Face(110 * k, FontStyle.Bold))
                g.DrawString(name, title, ink, new RectangleF(0, h / 2f - 190 * k, w, 160 * k), centred);
            using (Font sub = Face(40 * k))
                g.DrawString(note, sub, accent, new RectangleF(0, h / 2f - 30 * k, w, 70 * k), centred);
            using (Font small = Face(26 * k))
                g.DrawString($"{w} × {h}  ·  stand-in picture", small, dim, new RectangleF(0, h / 2f + 50 * k, w, 44 * k), centred);

            // A ladder of text sizes: how small can it get and still be read through the glasses?
            float y0 = h / 2f + 180 * k;
            foreach (int size in (int[])[30, 24, 20, 16, 13, 11])
            {
                using Font f = Face(size);
                g.DrawString($"{size} px  The quick brown fox jumps over the lazy dog  0123456789", f, ink, 150 * k, y0);
                y0 += size * 1.55f;
            }

            using Font corner = Face(34 * k, FontStyle.Bold);
            var far = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString("top left", corner, accent, 28 * k, 20 * k);
            g.DrawString("top right", corner, accent, new RectangleF(0, 20 * k, w - 28 * k, 50 * k), far);
            g.DrawString("bottom left", corner, accent, 28 * k, h - 70 * k);
            g.DrawString("bottom right", corner, accent, new RectangleF(0, h - 70 * k, w - 28 * k, 50 * k), far);
        }
        return picture;
    }

    /// <summary>A tick every 5°, a label every 15°. Straight ahead is in the middle; the ends meet behind the wearer.</summary>
    public static Bitmap Ruler()
    {
        Bitmap picture = NewPicture(RulerWidth, RulerHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            float perDegree = RulerWidth / 360f;
            float horizon = RulerHeight / 2f;
            using var line = new Pen(Color.FromArgb(120, Ink), 2);
            using var tick = new Pen(Color.FromArgb(200, Ink), 2);
            using var label = new SolidBrush(Accent);
            using Font face = Face(26, FontStyle.Bold);
            var centred = new StringFormat { Alignment = StringAlignment.Center };

            g.DrawLine(line, 0, horizon, RulerWidth, horizon);
            for (int degrees = -180; degrees <= 180; degrees += 5)
            {
                float x = (degrees + 180) * perDegree;
                bool major = degrees % 15 == 0;
                g.DrawLine(tick, x, horizon - (major ? 22 : 10), x, horizon + (major ? 22 : 10));
                if (!major) continue;

                string text = degrees == 0 ? "0"
                    : Math.Abs(degrees) == 180 ? "180"
                    : (degrees < 0 ? "L" : "R") + Math.Abs(degrees);
                g.DrawString(text, face, label, x, horizon + 24, centred);
            }
        }
        return picture;
    }

    /// <summary>
    /// Four short light ticks round a gap, each with a dark outline: the light shows over the room and dark screens,
    /// the outline over bright ones. One picture pixel per display pixel.
    /// </summary>
    public static Bitmap Crosshair()
    {
        const int size = CrosshairSize;
        Bitmap picture = NewPicture(size, size, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            float c = size / 2f, near = 6, far = 18;
            PointF[][] ticks =
            [
                [new(c, c - far), new(c, c - near)], [new(c, c + near), new(c, c + far)],
                [new(c - far, c), new(c - near, c)], [new(c + near, c), new(c + far, c)],
            ];
            using var outline = new Pen(Style.Shade, 7) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var light = new Pen(Style.Ink, Style.Stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            foreach (PointF[] t in ticks) g.DrawLine(outline, t[0], t[1]);
            foreach (PointF[] t in ticks) g.DrawLine(light, t[0], t[1]);
        }
        return picture;
    }

    public const int RingSize = 160;

    /// <summary>
    /// A ring drawn round the mouse pointer for a moment, so the eye finds it: in the mouse's colour (as its edge arrow),
    /// with a light inner ring and a dark outline, so it shows over the room and over bright and dark screens alike.
    /// Shown at about 0.7 picture pixels per display pixel.
    /// </summary>
    public static Bitmap Ring()
    {
        Bitmap picture = NewPicture(RingSize, RingSize, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            using var outline = new Pen(Style.Shade, 18f);
            using var outer = new Pen(Style.Mouse, 10f);
            using var inner = new Pen(Style.Ink, 4f);
            g.DrawEllipse(outline, 12, 12, RingSize - 24, RingSize - 24);
            g.DrawEllipse(outer, 12, 12, RingSize - 24, RingSize - 24);
            g.DrawEllipse(inner, 12, 12, RingSize - 24, RingSize - 24);
        }
        return picture;
    }

    /// <summary>
    /// The status strip at the very top of the view: one small, muted line on a plate just as wide as the text. One
    /// picture pixel per display pixel.
    /// </summary>
    public static Bitmap Status(string text)
    {
        Bitmap picture = NewPicture(StatusWidth, StatusHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            if (text.Length == 0) return picture;
            using Font face = Style.Face(Style.Small);
            float w = MathF.Min(g.MeasureString(text, face).Width + Style.Pad * 2, StatusWidth - 4);
            var plate = new RectangleF((StatusWidth - w) / 2, 2, w, StatusHeight - 4);
            Style.Plate(g, plate, rim: Style.QuietRim);
            using var ink = new SolidBrush(Style.Muted);
            g.DrawString(text, face, ink, RectangleF.Inflate(plate, -Style.Pad / 2, 0), Style.Centred);
        }
        return picture;
    }
}
