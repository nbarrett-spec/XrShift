using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace XrShift.App;

/// <summary>Pictures the app draws itself: stand-in screens, the horizon ruler, the crosshair and the status line.</summary>
internal static class Pictures
{
    public static readonly Color Accent = Color.FromArgb(90, 209, 200);
    public static readonly Color Ink = Color.FromArgb(240, 243, 250);
    public const int StatusWidth = 1280, StatusHeight = 64;
    public const int RulerWidth = 8192, RulerHeight = 128;

    public const int HandsWidth = 320, HandsHeight = 240;
    public const int MenuWidth = 1400, MenuHeight = 200;
    public const int EdgesWidth = 1280, EdgesHeight = 720;

    /// <summary>
    /// Arrows just inside the edges of the view, each pointing the way to a screen that is out of sight, with its
    /// name. Black is see-through in the glasses. Angles: 0 = right, 90 = up.
    /// </summary>
    public static Bitmap Edges(IReadOnlyList<(float AngleDeg, string Name)> hints)
    {
        Bitmap picture = NewPicture(EdgesWidth, EdgesHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent); // only the arrows are drawn: screens behind stay visible
            var accent = Color.FromArgb(94, 234, 212);
            using var font = new Font("Bahnschrift", 24f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var fill = new SolidBrush(accent);
            using var ink = new SolidBrush(Color.FromArgb(225, 232, 255));
            var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            float cx = EdgesWidth / 2f, cy = EdgesHeight / 2f, rx = cx - 70, ry = cy - 60;
            var labels = new List<RectangleF>(); // names already written, so two never overlap
            var arrows = new List<PointF>();     // arrow tips already drawn
            foreach ((float angle, string name) in hints.OrderBy(h => h.AngleDeg))
            {
                float a = angle * MathF.PI / 180f;
                float dx = MathF.Cos(a), dy = -MathF.Sin(a);
                // Where the line from the middle in that direction meets an ellipse just inside the edge.
                float k = 1f / MathF.Sqrt(dx * dx / (rx * rx) + dy * dy / (ry * ry));
                var tip = new PointF(cx + dx * k, cy + dy * k);
                var back = new PointF(tip.X - dx * 34, tip.Y - dy * 34);
                var side = new PointF(-dy * 18, dx * 18);
                var label = new PointF(tip.X - dx * 150, tip.Y - dy * 58);
                SizeF size = g.MeasureString(name, font);
                var box = new RectangleF(label.X - size.Width / 2 - 6, label.Y - 18, size.Width + 12, 36);
                while (labels.Any(l => l.IntersectsWith(box))) box.Offset(0, 36);
                labels.Add(box);
                g.DrawString(name, font, ink, box, centred);
                // Two screens the same way share one arrow.
                if (arrows.Any(a2 => MathF.Abs(a2.X - tip.X) + MathF.Abs(a2.Y - tip.Y) < 50)) continue;
                arrows.Add(tip);
                g.FillPolygon(fill, [tip, new PointF(back.X + side.X, back.Y + side.Y), new PointF(back.X - side.X, back.Y - side.Y)]);
            }
        }
        return picture;
    }

    /// <summary>
    /// The hand quick menu: a row of big rounded buttons on black (see-through in the glasses), the lit one filled
    /// with the accent colour, and a line underneath saying how to use it.
    /// </summary>
    public static Bitmap Menu(IReadOnlyList<string> items, int highlight)
    {
        Bitmap picture = NewPicture(MenuWidth, MenuHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent); // the buttons sit over the screens without blacking them out
            var accent = Color.FromArgb(107, 228, 255);
            int n = Math.Max(items.Count, 1), gap = 18;
            float w = (MenuWidth - gap * (n + 1)) / (float)n, h = 118;
            using var face = new Font("Bahnschrift", 30f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var hintFace = new Font("Bahnschrift", 22f, FontStyle.Regular, GraphicsUnit.Pixel);
            var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            for (int i = 0; i < items.Count; i++)
            {
                var box = new RectangleF(gap + i * (w + gap), 10, w, h);
                using GraphicsPath shape = Rounded(box, 28);
                bool lit = i == highlight;
                using (var fill = new SolidBrush(lit ? accent : Color.FromArgb(34, 46, 66))) g.FillPath(fill, shape);
                using (var edge = new Pen(lit ? Color.White : Color.FromArgb(90, 120, 150), lit ? 3 : 2)) g.DrawPath(edge, shape);
                using var ink = new SolidBrush(lit ? Color.FromArgb(3, 20, 28) : Ink);
                g.DrawString(items[i], face, ink, box, centred);
            }
            using var hint = new SolidBrush(Color.FromArgb(170, 190, 210));
            g.DrawString("move your hand to choose  ·  pinch to pick  ·  make a fist to close", hintFace, hint,
                new RectangleF(0, 140, MenuWidth, 50), centred);
        }
        return picture;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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
            var accent = Color.FromArgb(107, 228, 255);
            using (var frame = new Pen(Color.FromArgb(110, accent), 2f))
                g.DrawRectangle(frame, 1, 1, HandsWidth - 3, HandsHeight - 3);
            using var line = new Pen(accent, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var joint = new SolidBrush(Color.FromArgb(230, 240, 250));
            using var lit = new SolidBrush(Color.FromArgb(255, 200, 80));
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
            using Font font = Face(18, FontStyle.Bold);
            using var ink = new SolidBrush(Color.FromArgb(200, accent));
            g.DrawString(status, font, ink, new RectangleF(8, HandsHeight - 30, HandsWidth - 16, 26));
        }
        return picture;
    }

    public const int ObjectSize = 256;

    /// <summary>A bot, cluster or widget: a glowing disc in the colour of its state, with its name underneath. Black is see-through.</summary>
    public static Bitmap Object(XrShift.Scene.SceneObject o)
    {
        Color colour = o.Offline ? Color.FromArgb(120, 126, 138) : o.State switch
        {
            XrShift.Scene.BotState.Working => Color.FromArgb(70, 140, 255),
            XrShift.Scene.BotState.NeedsYou => Color.FromArgb(255, 180, 40),
            XrShift.Scene.BotState.Done => Color.FromArgb(60, 200, 110),
            XrShift.Scene.BotState.Failed => Color.FromArgb(235, 70, 70),
            _ => Color.FromArgb(120, 126, 138),
        };
        Bitmap picture = NewPicture(ObjectSize, ObjectSize, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            const float disc = 132, left = (ObjectSize - disc) / 2f, top = 18;
            using (var glow = new SolidBrush(Color.FromArgb(60, colour))) g.FillEllipse(glow, left - 14, top - 14, disc + 28, disc + 28);
            using (var fill = new SolidBrush(colour)) g.FillEllipse(fill, left, top, disc, disc);
            using var ink = new SolidBrush(Color.FromArgb(240, 243, 250));
            using var dark = new SolidBrush(Color.FromArgb(10, 13, 20));
            var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            // In the disc: the badge if there is one, else what kind of thing this is when it is not a bot.
            string inside = o.Badge.Length > 0 ? o.Badge : o.Kind == "bot" ? "" : o.Kind;
            if (inside.Length > 0)
            {
                using Font small = Face(inside.Length <= 3 ? 54 : 26, FontStyle.Bold);
                g.DrawString(inside, small, dark, new RectangleF(left, top, disc, disc), centred);
            }
            using Font name = Face(30, FontStyle.Bold);
            g.DrawString(o.Label, name, ink, new RectangleF(0, top + disc + 22, ObjectSize, 44), centred);
            using Font state = Face(22);
            string what = o.Offline ? "offline" : o.State == XrShift.Scene.BotState.NeedsYou ? "needs you" : XrShift.Scene.SceneObject.StateName(o.State);
            // A bot on another PC says which one, so the wearer knows where an answer has to be typed.
            g.DrawString(o.Pc.Length > 0 ? $"{what} Â· {o.Pc}" : what, state, ink, new RectangleF(0, top + disc + 62, ObjectSize, 30), centred);
        }
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

    public static Bitmap Crosshair()
    {
        const int size = 64;
        Bitmap picture = NewPicture(size, size, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.FromArgb(220, Accent), 3);
            float c = size / 2f;
            g.DrawLine(pen, c, 4, c, c - 9);
            g.DrawLine(pen, c, c + 9, c, size - 4);
            g.DrawLine(pen, 4, c, c - 9, c);
            g.DrawLine(pen, c + 9, c, size - 4, c);
        }
        return picture;
    }

    public static Bitmap Status(string text)
    {
        Bitmap picture = NewPicture(StatusWidth, StatusHeight, out Graphics g);
        using (g)
        {
            g.Clear(Color.Transparent);
            if (text.Length == 0) return picture;

            using var path = new GraphicsPath();
            var box = new RectangleF(1, 1, StatusWidth - 2, StatusHeight - 2);
            float r = 22;
            path.AddArc(box.Left, box.Top, r * 2, r * 2, 180, 90);
            path.AddArc(box.Right - r * 2, box.Top, r * 2, r * 2, 270, 90);
            path.AddArc(box.Right - r * 2, box.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(box.Left, box.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using (var back = new SolidBrush(Color.FromArgb(200, 4, 6, 10)))
                g.FillPath(back, path);

            using var ink = new SolidBrush(Ink);
            using Font face = Face(26);
            g.DrawString(text, face, ink, box,
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap });
        }
        return picture;
    }
}
