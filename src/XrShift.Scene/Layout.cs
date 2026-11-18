using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrShift.Scene;

/// <summary>One screen in the wearer's space: where its picture comes from and where it floats.</summary>
public sealed record ScreenSpec
{
    /// <summary>Stand-in picture, for arranging screens before a real monitor exists for them.</summary>
    public const string TestSource = "test";
    const string DisplayPrefix = "display:";
    const string WindowPrefix = "window:";

    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = "Screen";
    /// <summary><see cref="TestSource"/>, or <c>display:</c> followed by a Windows monitor name such as <c>\\.\DISPLAY1</c>.</summary>
    public string Source { get; init; } = TestSource;

    /// <summary>
    /// A source for one window pinned as a screen of its own: <c>window:</c>, the program's name, a bar and
    /// the window's title. The program's name finds it again after a restart; the title tells two windows of
    /// one program apart.
    /// </summary>
    public static string WindowSource(string program, string title) =>
        WindowPrefix + program.Replace("|", "") + "|" + title;

    /// <summary>The program and title of the window this screen shows, or null if it shows something else.</summary>
    [JsonIgnore]
    public (string Program, string Title)? Window
    {
        get
        {
            if (!Source.StartsWith(WindowPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            string rest = Source[WindowPrefix.Length..];
            int bar = rest.IndexOf('|');
            return bar < 0 ? (rest, "") : (rest[..bar], rest[(bar + 1)..]);
        }
    }
    /// <summary>Direction of the centre: + = to the left of straight ahead.</summary>
    public float YawDeg { get; init; }
    /// <summary>Direction of the centre: + = above the horizon.</summary>
    public float PitchDeg { get; init; }
    /// <summary>Spin about the line of sight: + = top leans right.</summary>
    public float TiltDeg { get; init; }
    /// <summary>How far the screen wraps around the wearer, degrees of arc. 0 = flat.</summary>
    public float CurveDeg { get; init; }
    /// <summary>
    /// Size relative to pixel-exact: at 1 a picture pixel covers one display pixel in the middle of
    /// the view, which is as sharp as the glasses can show it. Smaller fits more in view, softer.
    /// </summary>
    public float Scale { get; init; } = 0.85f;
    /// <summary>Metres from the head. With rotation-only tracking this only decides which screen covers which.</summary>
    public float Distance { get; init; } = 2f;
    public bool Visible { get; init; } = true;
    /// <summary>
    /// For a screen on a virtual monitor: the size in pixels the app should give that monitor, e.g. 3840 x 1080
    /// for a super-ultrawide. 0 = leave the monitor as it is.
    /// </summary>
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    /// <summary>For a screen on a virtual monitor: its refresh rate. 0 = the app's choice (60, easy on the graphics card).</summary>
    public int RefreshHz { get; init; }
    /// <summary>
    /// Turn about the screen's own upright middle line, degrees: + faces the screen more to the right (its right
    /// edge further away), as a TV on the left-hand wall looks from where you sit. 0 = facing you. Flat screens only.
    /// </summary>
    public float TurnDeg { get; init; }
    /// <summary>How bright the screen is drawn, 0.2 to 1.</summary>
    public float Brightness { get; init; } = 1f;
    /// <summary>The screen stays in the same place in your view when you turn (a heads-up screen), instead of in the room.</summary>
    public bool FollowsMe { get; init; }

    const float MetresPerInch = 0.0254f;

    /// <summary>The screen's real diagonal, in inches, for a picture of this size: the size of a TV it would match.</summary>
    public float DiagonalInches(int pictureWidth, int pictureHeight, DisplayOptics optics)
    {
        Panel p = ToPanel(pictureWidth, pictureHeight, optics);
        return MathF.Sqrt(p.Width * p.Width + p.Height * p.Height) / MetresPerInch;
    }

    /// <summary>The same screen made a given diagonal in inches at its distance.</summary>
    public ScreenSpec WithDiagonalInches(float inches, int pictureWidth, int pictureHeight, DisplayOptics optics)
    {
        float now = DiagonalInches(pictureWidth, pictureHeight, optics);
        return now <= 0 ? this : this with { Scale = Scale * inches / now };
    }

    /// <summary>The same screen moved nearer or further, keeping its real size (further away looks smaller).</summary>
    public ScreenSpec AtDistanceKeepingSize(float metres) =>
        metres <= 0 ? this : this with { Scale = Scale * Distance / metres, Distance = metres };

    public static string DisplaySource(string deviceName) => DisplayPrefix + deviceName;

    /// <summary>The Windows monitor this screen shows, or null if it shows something else.</summary>
    [JsonIgnore]
    public string? DisplayName =>
        Source.StartsWith(DisplayPrefix, StringComparison.OrdinalIgnoreCase) ? Source[DisplayPrefix.Length..] : null;

    /// <summary>Where and how big the screen is for a picture of the given size.</summary>
    public Panel ToPanel(int pictureWidth, int pictureHeight, DisplayOptics optics)
    {
        float pixelsPerRadian = optics.Width / (2f * MathF.Tan(optics.HorizontalFovDeg * MathF.PI / 360f));
        float width = Scale * Distance * Math.Max(pictureWidth, 1) / pixelsPerRadian;
        return new Panel
        {
            YawDeg = YawDeg,
            PitchDeg = PitchDeg,
            RollDeg = TiltDeg,
            CurveDeg = CurveDeg,
            Distance = Distance,
            Width = width,
            Height = width * Math.Max(pictureHeight, 1) / Math.Max(pictureWidth, 1),
            TurnDeg = CurveDeg < 1f ? TurnDeg : 0,
        };
    }

    /// <summary>The largest scale at which a picture of this size still fits the view top to bottom, with a little margin.</summary>
    public static float ScaleToFit(int pictureHeight, DisplayOptics optics) =>
        MathF.Min(1f, 0.94f * optics.Height / Math.Max(pictureHeight, 1));
}

/// <summary>Every screen the wearer has set up. Saved as JSON.</summary>
public sealed record Layout
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public IReadOnlyList<ScreenSpec> Screens { get; init; } = [];

    public Layout With(ScreenSpec changed) =>
        this with { Screens = Screens.Select(s => s.Id == changed.Id ? changed : s).ToList() };

    public Layout Add(ScreenSpec screen) => this with { Screens = [.. Screens, screen] };

    public Layout Remove(string id) => this with { Screens = Screens.Where(s => s.Id != id).ToList() };

    public ScreenSpec? Find(string id) => Screens.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// Every screen bigger or smaller by <paramref name="zoom"/>, and their directions spread or drawn together by
    /// the same amount, so the arrangement keeps its shape: like stepping back from all of them at once.
    /// </summary>
    public Layout Zoomed(float zoom) => !float.IsFinite(zoom) || MathF.Abs(zoom - 1f) < 1e-3f ? this : this with
    {
        Screens = Screens.Select(s => Clamp(s with { Scale = s.Scale * zoom, YawDeg = s.YawDeg * zoom, PitchDeg = s.PitchDeg * zoom })).ToList(),
    };

    public bool Shows(string displayName) =>
        Screens.Any(s => string.Equals(s.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A new screen placed beside the ones already there: straight ahead if it is the first,
    /// otherwise to the right of the right-most one, then to the left once the right side is full.
    /// </summary>
    public ScreenSpec PlaceNew(string name, string source, int pictureWidth, int pictureHeight, DisplayOptics optics)
    {
        const float gapDeg = 3f, sideLimitDeg = 110f;
        var screen = new ScreenSpec
        {
            Name = name,
            Source = source,
            Scale = ScreenSpec.ScaleToFit(pictureHeight, optics),
        };
        if (Screens.Count == 0) return screen;

        float half = screen.ToPanel(pictureWidth, pictureHeight, optics).AngularWidthDeg / 2f;
        float rightEdge = float.MaxValue, leftEdge = float.MinValue; // yaw: + is left
        foreach (ScreenSpec s in Screens)
        {
            // Other screens' picture sizes are not known here; 16:9 at their scale is close enough for spacing.
            float otherHalf = s.ToPanel(1920, 1080, optics).AngularWidthDeg / 2f;
            rightEdge = MathF.Min(rightEdge, s.YawDeg - otherHalf);
            leftEdge = MathF.Max(leftEdge, s.YawDeg + otherHalf);
        }

        float toRight = rightEdge - gapDeg - half;
        float toLeft = leftEdge + gapDeg + half;
        float yaw = toRight - half >= -sideLimitDeg || MathF.Abs(toRight) <= MathF.Abs(toLeft) ? toRight : toLeft;
        return screen with { YawDeg = yaw };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static bool TryParse(string json, out Layout layout)
    {
        layout = new Layout();
        try
        {
            Layout? parsed = JsonSerializer.Deserialize<Layout>(json, Json);
            if (parsed?.Screens is null) return false;
            layout = parsed.Sanitized();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>For hand-edited or damaged files: every value pulled back inside what the app can draw.</summary>
    public Layout Sanitized() =>
        this with { Screens = (Screens ?? []).Where(s => s is not null).Select(Clamp).ToList() };

    public static ScreenSpec Clamp(ScreenSpec s) => s with
    {
        Name = string.IsNullOrWhiteSpace(s.Name) ? "Screen" : s.Name,
        Source = string.IsNullOrWhiteSpace(s.Source) ? ScreenSpec.TestSource : s.Source,
        YawDeg = Finite(s.YawDeg, -180, 180, 0),
        PitchDeg = Finite(s.PitchDeg, -80, 80, 0),
        TiltDeg = Finite(s.TiltDeg, -45, 45, 0),
        CurveDeg = Finite(s.CurveDeg, 0, 180, 0),
        Scale = Finite(s.Scale, 0.2f, 3f, 0.85f),
        Distance = Finite(s.Distance, 0.5f, 9f, 2f),
        PixelWidth = s.PixelWidth is >= 640 and <= 7680 && s.PixelHeight is >= 480 and <= 4320 ? s.PixelWidth : 0,
        PixelHeight = s.PixelWidth is >= 640 and <= 7680 && s.PixelHeight is >= 480 and <= 4320 ? s.PixelHeight : 0,
        RefreshHz = s.RefreshHz is >= 24 and <= 240 ? s.RefreshHz : 0,
        TurnDeg = Finite(s.TurnDeg, -75, 75, 0),
        Brightness = Finite(s.Brightness, 0.2f, 1f, 1f),
    };

    static float Finite(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
