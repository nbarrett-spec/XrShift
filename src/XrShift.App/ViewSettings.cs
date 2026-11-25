using System.Globalization;
using XrShift.Scene;
using XrShift.Tracking;

namespace XrShift.App;

/// <summary>What the wearer has tuned by eye, kept between runs in the settings folder.</summary>
internal sealed record ViewSettings
{
    /// <summary>
    /// The delay may go below zero: the user turned it down to 0 and may want less prediction still
    /// than the app's own measured delay. The total never goes below no prediction at all.
    /// </summary>
    public const float MinDelayMs = -10, MaxDelayMs = 60, MinFovDeg = 30, MaxFovDeg = 70, MaxLevelDeg = 15, MaxRollDeg = 8;

    /// <summary>
    /// The user was happy with 25 ms of prediction when the app's own share measured 8.3 ms (one
    /// 120 Hz refresh), which leaves this much for the glasses.
    /// </summary>
    public const float DefaultDelayMs = 16.5f;

    /// <summary>
    /// How long the glasses take to show a frame once the PC starts sending it. The app measures its
    /// own share of the delay and adds this to know how far ahead to predict the head.
    /// </summary>
    public float DelayMs { get; init; } = DefaultDelayMs;
    /// <summary>How wide the picture is in the glasses, in degrees.</summary>
    public float FovDeg { get; init; } = DisplayOptics.OnePro.HorizontalFovDeg;
    /// <summary>
    /// Corrects the angle between the motion sensor and the display, which was only measured through
    /// posture. + moves everything up. When it is wrong, screens tilt and rise as the head turns away.
    /// </summary>
    public float LevelDeg { get; init; }
    /// <summary>The same for sideways lean: + turns everything clockwise.</summary>
    public float RollDeg { get; init; }
    public bool Ruler { get; init; }
    /// <summary>The mouse pointer jumps to the screen the wearer turns to look at.</summary>
    public bool LookPointer { get; init; }
    /// <summary>With <see cref="LookPointer"/>: the window looked at also gets the keyboard.</summary>
    public bool LookFocus { get; init; }
    /// <summary>
    /// With <see cref="LookPointer"/>: the pointer rides the middle of the view all the time (aim with the head,
    /// click with the mouse) instead of jumping once to each screen looked at.
    /// </summary>
    public bool LookFollow { get; init; }
    /// <summary>The screens drift after the head when it turns far away, and settle in front again.</summary>
    public bool Follow { get; init; }
    /// <summary>Gesture mode: hands seen by the glasses' camera (in Anchor mode) steer the pointer and pinch to click.</summary>
    public bool Hands { get; init; }
    /// <summary>A reminder in the glasses to rest the eyes every so many minutes. 0 = never.</summary>
    public int BreakMinutes { get; init; }
    /// <summary>All screens bigger or smaller together, 1 = as arranged.</summary>
    public float Zoom { get; init; } = 1f;
    public const float MinZoom = 0.5f, MaxZoom = 1.6f;
    /// <summary>Screens the wearer is not looking at are shown darker.</summary>
    public bool Dim { get; init; }
    /// <summary>
    /// Stationary mode: how wide the picture the glasses hold still is, in degrees. It matches the screen size set
    /// in the glasses' own menu; bigger leaves room for screens further to the sides, at the cost of sharpness.
    /// </summary>
    public float HeldFovDeg { get; init; } = DisplayOptics.OnePro.HorizontalFovDeg;
    /// <summary>Arrows at the edges of the view point the way to screens that are out of sight.</summary>
    public bool EdgeArrows { get; init; } = true;
    /// <summary>Reading without glasses or contacts: 0 = off, 1 = strongest sharpening and contrast.</summary>
    public float EasyReading { get; init; }
    public const float MinHeldFovDeg = 30, MaxHeldFovDeg = 150;

    public static ViewSettings Default { get; } = new();

    static string FilePath => Path.Combine(TrackingConfig.Folder, "view.txt");

    public static ViewSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && TryParse(File.ReadAllText(FilePath), out ViewSettings settings))
                return settings;
        }
        catch (IOException) { }
        return Default;
    }

    public void Save()
    {
        Directory.CreateDirectory(TrackingConfig.Folder);
        File.WriteAllText(FilePath, ToText());
    }

    public string ToText() => string.Create(CultureInfo.InvariantCulture,
        $"delay={DelayMs:F1};fov={FovDeg:F2};level={LevelDeg:F2};roll={RollDeg:F2};ruler={(Ruler ? 1 : 0)};look={(LookPointer ? (LookFollow ? 2 : 1) : 0)};focus={(LookFocus ? 1 : 0)};zoom={Zoom:F2};dim={(Dim ? 1 : 0)};drift={(Follow ? 1 : 0)};break={BreakMinutes};hands={(Hands ? 1 : 0)};held={HeldFovDeg:F1};arrows={(EdgeArrows ? 1 : 0)};easy={EasyReading:F2}");

    /// <summary>Reads the saved text. Settings added in later versions may be missing; the two original ones may not.</summary>
    public static bool TryParse(string text, out ViewSettings settings)
    {
        settings = Default;
        float? delay = null, fov = null;
        float level = 0, roll = 0, ruler = 0, look = 0, focus = 0, zoom = 1, dimmed = 0, drift = 0, rest = 0, hands = 0, held = DisplayOptics.OnePro.HorizontalFovDeg, arrows = 1, easy = 0;
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] kv = part.Split('=');
            if (kv.Length != 2 || !float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
                return false;
            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "delay": delay = value; break;
                case "fov": fov = value; break;
                case "level": level = value; break;
                case "roll": roll = value; break;
                case "ruler": ruler = value; break;
                case "look": look = value; break;
                case "focus": focus = value; break;
                case "zoom": zoom = value; break;
                case "dim": dimmed = value; break;
                case "drift": drift = value; break;
                case "break": rest = value; break;
                case "hands": hands = value; break;
                case "held": held = value; break;
                case "arrows": arrows = value; break;
                case "easy": easy = value; break;
                default: return false;
            }
        }
        if (delay is not { } d || fov is not { } f) return false;
        if (d < MinDelayMs || d > MaxDelayMs || f < MinFovDeg || f > MaxFovDeg) return false;
        settings = new ViewSettings
        {
            DelayMs = d,
            FovDeg = f,
            LevelDeg = Math.Clamp(level, -MaxLevelDeg, MaxLevelDeg),
            RollDeg = Math.Clamp(roll, -MaxRollDeg, MaxRollDeg),
            Ruler = ruler != 0,
            LookPointer = look != 0,
            LookFollow = look >= 2,
            Follow = drift != 0,
            BreakMinutes = (int)Math.Clamp(rest, 0, 240),
            Hands = hands != 0,
            LookFocus = focus != 0,
            Zoom = Math.Clamp(zoom, MinZoom, MaxZoom),
            Dim = dimmed != 0,
            HeldFovDeg = Math.Clamp(held, MinHeldFovDeg, MaxHeldFovDeg),
            EdgeArrows = arrows != 0,
            EasyReading = Math.Clamp(easy, 0f, 1f),
        };
        return true;
    }
}

/// <summary>Where the arrangement of screens is kept between runs.</summary>
internal static class LayoutStore
{
    // Follow mode keeps the original file; stationary mode (the glasses holding their picture still) has its own.
    static string FilePath(bool stationary) => Path.Combine(TrackingConfig.Folder, stationary ? "layout-stationary.json" : "layout.json");

    public static Workspace? Load(bool stationary = false)
    {
        try
        {
            string path = FilePath(stationary);
            return File.Exists(path) && Workspace.TryParse(File.ReadAllText(path), out Workspace workspace) ? workspace : null;
        }
        catch (IOException) { return null; }
    }

    public static void Save(Workspace workspace, bool stationary = false)
    {
        Directory.CreateDirectory(TrackingConfig.Folder);
        File.WriteAllText(FilePath(stationary), workspace.ToJson());
    }

    /// <summary>
    /// Makes sure every connected monitor (other than the glasses) has a screen, in the arrangement kept for the desk
    /// the PC is at. New ones are placed beside what is already there, within reach. Returns the same object when
    /// nothing needed adding.
    /// </summary>
    public static Layout WithScreensFor(Layout layout, IEnumerable<DisplayInfo> sources, DisplayOptics optics) =>
        WithScreensFor(layout, sources, optics, out _);

    /// <param name="hidden">Names of new screens added hidden because there was no room in reach: tell the wearer.</param>
    /// <param name="present">
    /// Every monitor connected now, to tell which desk this is, when <paramref name="sources"/> is only some of them
    /// (stationary mode). Without it a list holding no real monitor leaves the desk as it is.
    /// </param>
    public static Layout WithScreensFor(Layout layout, IEnumerable<DisplayInfo> sources, DisplayOptics optics,
        out IReadOnlyList<string> hidden, IEnumerable<DisplayInfo>? present = null)
    {
        List<DisplayInfo> addable = sources.ToList();
        // Screens of the virtual display driver are dropped once the driver is gone: nothing can bring them back.
        if (!VirtualMonitors.Installed)
        {
            List<ScreenSpec> stale = layout.Screens.Where(s => s.DisplayName is { } id && id.Contains("#MTT1337#", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (ScreenSpec s in stale) layout = layout.Remove(s.Id);
        }
        (layout, hidden) = Desks.Fit(layout, (present ?? addable).Where(d => !d.IsGlasses).Select(Facts).ToList(),
            addable.Select(Facts).ToList(), optics, VirtualMonitors.IsVirtualId);
        return layout;
    }

    static MonitorFacts Facts(DisplayInfo d) =>
        new(d.Id, d.MonitorNames.FirstOrDefault(), VirtualMonitors.IsVirtual(d), d.Primary, d.Bounds.Width, d.Bounds.Height);
}
