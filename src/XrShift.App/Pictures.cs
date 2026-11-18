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
