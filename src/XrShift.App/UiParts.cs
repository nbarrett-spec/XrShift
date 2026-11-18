using System.Drawing.Drawing2D;

namespace XrShift.App;

/// <summary>The side menu: an icon and a word per page, the open page lit with a teal bar and glow.</summary>
internal sealed class NavRail : Control
{
    readonly List<(string Glyph, string Label)> _items = [];
    int _selected, _hover = -1;

    public event Action<int>? Selected;
    public int ItemHeight { get; set; } = 70;
    public int Top0 { get; set; } = 84;

    public NavRail()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public void Add(string glyph, string label) { _items.Add((glyph, label)); Invalidate(); }

    public int SelectedIndex
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    float D => DeviceDpi / 96f;
    RectangleF ItemRect(int i) => new(8 * D, (Top0 + i * ItemHeight) * D, Width - 16 * D, (ItemHeight - 8) * D);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        float d = D;
        using (var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(16, 20, 54), Color.FromArgb(24, 18, 62), LinearGradientMode.Vertical))
            g.FillRectangle(bg, ClientRectangle);
        using (var rule = new Pen(Ui.Line)) g.DrawLine(rule, Width - 1, 0, Width - 1, Height);

        // The mark at the top: a screen bending around a point of light.
        using (var mark = new Pen(Ui.Accent, 2.4f * d))
            g.DrawArc(mark, Width / 2f - 15 * d, 22 * d, 30 * d, 30 * d, 200, 140);
        using (var dot = new SolidBrush(Ui.Violet)) g.FillEllipse(dot, Width / 2f - 3.5f * d, 40 * d, 7 * d, 7 * d);

        using Font icon = new(Ui.IconFace, 17f);
        using Font word = Ui.Text(8.5f);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < _items.Count; i++)
        {
            RectangleF r = ItemRect(i);
            bool on = i == _selected, hot = i == _hover;
            if (on || hot)
            {
                using GraphicsPath path = Ui.Rounded(r, 12 * d);
                using var fill = new SolidBrush(on ? Color.FromArgb(40, 94, 234, 212) : Color.FromArgb(28, 255, 255, 255));
                g.FillPath(fill, path);
                if (on)
                {
                    using var rim = new Pen(Ui.Fade(Ui.Accent, 120), d);
                    g.DrawPath(rim, path);
                    using var bar = new SolidBrush(Ui.Accent);
                    g.FillRectangle(bar, 0, r.Top + r.Height * 0.25f, 3 * d, r.Height * 0.5f);
                }
            }
            using var ink = new SolidBrush(on ? Ui.Accent : hot ? Ui.Ink : Ui.Muted);
            g.DrawString(_items[i].Glyph, icon, ink, new RectangleF(r.Left, r.Top + 4 * d, r.Width, r.Height * 0.58f), centred);
            using var label = new SolidBrush(on ? Ui.Ink : Ui.Muted);
            g.DrawString(_items[i].Label, word, label, new RectangleF(r.Left, r.Top + r.Height * 0.56f, r.Width, r.Height * 0.4f), centred);
        }
    }

    int HitAt(Point p)
    {
        for (int i = 0; i < _items.Count; i++) if (ItemRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitAt(e.Location);
        if (hit != _hover) { _hover = hit; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int hit = HitAt(e.Location);
        if (hit < 0) return;
        _selected = hit;
        Invalidate();
        Selected?.Invoke(hit);
    }
}

/// <summary>
/// A rounded card with a fine bright rim and an optional title, for grouping things on a page. Controls placed in it
/// sit on its colour.
/// </summary>
internal class Card : Panel
{
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Lit rim and a soft glow, e.g. for the mode that is on now.</summary>
    public bool Lit { get; set; }
    public Color LitColour { get; set; } = Ui.Accent;

    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Inspector;
    }

    protected float D => DeviceDpi / 96f;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        float d = D;
        // Around the rounded corners: the page behind, aurora and all.
        if (Parent is AuroraPanel page) Ui.PaintAurora(g, new Rectangle(-Left, -Top, page.Width, page.Height));
        else g.Clear(Parent?.BackColor ?? Ui.Back);
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using (GraphicsPath path = Ui.Rounded(r, 16 * d))
        {
            using (var fill = new SolidBrush(BackColor)) g.FillPath(fill, path);
            using var rim = new Pen(Lit ? LitColour : Ui.Fade(Ui.Rim, 150), (Lit ? 2f : 1f) * d);
            g.DrawPath(rim, path);
        }
        if (Title.Length > 0)
        {
            using Font title = Ui.Display(13f);
            using var ink = new SolidBrush(Ui.Ink);
            g.DrawString(Title, title, ink, 20 * d, 16 * d);
        }
        if (Subtitle.Length > 0)
        {
            using Font sub = Ui.Text(9.5f);
            using var muted = new SolidBrush(Ui.Muted);
            g.DrawString(Subtitle, sub, muted, new RectangleF(20 * d, 44 * d, Width - 40 * d, Height - 56 * d));
        }
        PaintContent(g, d);
    }

    protected virtual void PaintContent(Graphics g, float d) { }
}

/// <summary>
/// One of the four ways to use the glasses: an icon, the name, one line on what it is, what you get (✓) and what
/// you do not (✗), and how to turn it on. The one that is on now is lit.
/// </summary>
internal sealed class ModeCard : Card
{
    public string Glyph { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string[] Gets { get; set; } = [];
    public string[] Misses { get; set; } = [];
    public string HowTo { get; set; } = "";
    public string BadgeText { get; set; } = "On now";

    protected override void PaintContent(Graphics g, float d)
    {
        using (Font icon = new(Ui.IconFace, 20f))
        using (var tint = new SolidBrush(LitColour))
            g.DrawString(Glyph, icon, tint, 18 * d, 18 * d);
        using (Font name = Ui.Display(15f))
        using (var ink = new SolidBrush(Ui.Ink))
            g.DrawString(Text, name, ink, 18 * d, 58 * d);
        if (Lit)
        {
            using Font badgeFont = Ui.Text(8.5f, bold: true);
            SizeF size = g.MeasureString(BadgeText, badgeFont);
            var badge = new RectangleF(Width - size.Width - 40 * d, 22 * d, size.Width + 20 * d, 24 * d);
            using GraphicsPath pill = Ui.Rounded(badge, 12 * d);
            using (var fill = new SolidBrush(Ui.Fade(LitColour, 60))) g.FillPath(fill, pill);
            using var ink = new SolidBrush(LitColour);
            g.DrawString(BadgeText, badgeFont, ink, badge, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        using Font body = Ui.Text(9.5f);
        using (var muted = new SolidBrush(Ui.Muted))
            g.DrawString(Tagline, body, muted, new RectangleF(18 * d, 90 * d, Width - 36 * d, 44 * d));
        float y = 138 * d;
        using var good = new SolidBrush(Ui.Good);
        using var bad = new SolidBrush(Ui.Bad);
        using var text = new SolidBrush(Ui.Ink);
        foreach (string line in Gets) { Line(g, "✓", good, line, text, body, ref y, d); }
        foreach (string line in Misses) { Line(g, "✗", bad, line, text, body, ref y, d); }
        if (HowTo.Length > 0)
        {
            using Font small = Ui.Text(8.5f);
            using var faint = new SolidBrush(Ui.Faint);
            g.DrawString(HowTo, small, faint, new RectangleF(18 * d, Height - 46 * d, Width - 36 * d, 40 * d));
        }
    }

    static void Line(Graphics g, string mark, Brush markBrush, string line, Brush textBrush, Font font, ref float y, float d)
    {
        g.DrawString(mark, font, markBrush, 18 * d, y);
        g.DrawString(line, font, textBrush, 36 * d, y);
        y += 22 * d;
    }
}
