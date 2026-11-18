using System.Drawing.Drawing2D;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>
/// The short guide shown over the room the first time XrShift opens, and again from the "?" button: what the
/// app does, the glasses' two modes, and how to add screens. Three steps, one button.
/// </summary>
internal sealed class WelcomeCard : Control
{
    static string Marker => Path.Combine(TrackingConfig.Folder, "welcomed");

    /// <summary>The guide has not been closed on this PC yet.</summary>
    public static bool FirstTime => !File.Exists(Marker);

    readonly PillButton _done = new();

    static readonly (string Title, string Body)[] Steps =
    [
        ("Put the glasses on", "Plug them in and your screens appear around you. Close this window with X to quit XrShift completely."),
        ("Pick a mode with the glasses' button",
            "Follow: XrShift tracks your head and your screens sit all around you.\nStationary: the glasses hold the picture still in the room with their camera. Your hands can steer the pointer there."),
        ("Add your screens", "Pin window floats any open window, like Teams or a browser. Drag screens in this room to place them; scroll over one to resize it."),
    ];

    public WelcomeCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Ui.RoomHorizon;
        _done.Text = "Got it";
        _done.Look = PillButton.Looks.Accent;
        _done.Font = Ui.Text(10.5f, bold: true);
        _done.Click += (_, _) =>
        {
            Visible = false;
            try
            {
                Directory.CreateDirectory(TrackingConfig.Folder);
                File.WriteAllText(Marker, DateTime.Now.ToString("O"));
            }
            catch (IOException) { }
        };
        Controls.Add(_done);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        float d = DeviceDpi / 96f;
        _done.SetBounds(Width - (int)(152 * d), Height - (int)(64 * d), (int)(124 * d), (int)(38 * d));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Ui.Smooth(g);
        float d = DeviceDpi / 96f;
        var card = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (GraphicsPath shape = Ui.Rounded(card, 16 * d))
        {
            using var fill = new LinearGradientBrush(card, Ui.Raised, Ui.Inspector, LinearGradientMode.Vertical);
            g.FillPath(fill, shape);
            using var edge = new Pen(Ui.Fade(Ui.Accent, 90), d);
            g.DrawPath(edge, shape);
        }

        float x = 28 * d, y = 26 * d;
        using (Font title = Ui.Display(18f))
        using (var ink = new SolidBrush(Ui.Ink))
            g.DrawString("Welcome to XrShift", title, ink, x, y);
        y += 36 * d;
        using (Font sub = Ui.Text(9.5f))
        using (var muted = new SolidBrush(Ui.Muted))
            g.DrawString("Floating screens for your glasses. Three things to know:", sub, muted, x, y);
        y += 34 * d;

        using Font stepTitle = Ui.Text(10.5f, bold: true);
        using Font body = Ui.Text(9.5f);
        using var accent = new SolidBrush(Ui.Accent);
        using var onAccent = new SolidBrush(Ui.OnAccent);
        using var inkBrush = new SolidBrush(Ui.Ink);
        using var mutedBrush = new SolidBrush(Ui.Muted);
        var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < Steps.Length; i++)
        {
            var dot = new RectangleF(x, y, 26 * d, 26 * d);
            g.FillEllipse(accent, dot);
            using (Font n = Ui.Text(10f, bold: true)) g.DrawString((i + 1).ToString(), n, onAccent, dot, centred);
            float tx = x + 40 * d, width = Width - tx - 28 * d;
            g.DrawString(Steps[i].Title, stepTitle, inkBrush, tx, y + 2 * d);
            var text = new RectangleF(tx, y + 24 * d, width, 60 * d);
            g.DrawString(Steps[i].Body, body, mutedBrush, text);
            SizeF used = g.MeasureString(Steps[i].Body, body, (int)width);
            y += 24 * d + used.Height + 18 * d;
        }
    }
}
