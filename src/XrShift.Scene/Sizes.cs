namespace XrShift.Scene;

/// <summary>Standard sizes for a screen, so the wearer picks one by name instead of nudging a scale.</summary>
public enum SizePreset
{
    /// <summary>One picture pixel on one glasses pixel: the sharpest text the glasses can show.</summary>
    Sharp,
    /// <summary>A little smaller than Sharp, so more fits in view.</summary>
    Comfort,
    /// <summary>The whole picture inside one view of the glasses, with a margin.</summary>
    Overview,
}

/// <summary>Size presets, zoom steps and how big text comes out: the numbers behind "make it readable".</summary>
public static class Sizes
{
    public const float ComfortScale = 0.85f;
    /// <summary>Share of the view an Overview screen may fill, so its edges are not hard against the view's.</summary>
    public const float OverviewMargin = 0.94f;
    const float FeetPerMetre = 3.2808f, Tolerance = 0.005f;

    /// <summary>The scale a preset gives a picture of this size.</summary>
    public static float ScaleFor(SizePreset size, int pictureWidth, int pictureHeight, DisplayOptics optics) => size switch
    {
        SizePreset.Comfort => ComfortScale,
        // At scale 1 a picture as wide as the glasses fills them exactly, so the fit is a ratio of pixels.
        // Never past 1: a small window would only get bigger and softer.
        SizePreset.Overview => MathF.Min(1f, OverviewMargin * MathF.Min(
            (float)optics.Width / Math.Max(pictureWidth, 1), (float)optics.Height / Math.Max(pictureHeight, 1))),
        _ => 1f,
    };

    public static ScreenSpec Apply(ScreenSpec screen, SizePreset size, int pictureWidth, int pictureHeight, DisplayOptics optics) =>
        Layout.Clamp(screen with { Scale = ScaleFor(size, pictureWidth, pictureHeight, optics) });

    /// <summary>Every shown screen at the preset size, where it already is. Hidden ones stay as they are.</summary>
    public static Layout Apply(Layout layout, SizePreset size, Func<ScreenSpec, (int Width, int Height)> pictureSize, DisplayOptics optics) =>
        layout with
        {
            Screens = layout.Screens.Select(s =>
            {
                if (!s.Visible) return s;
                (int w, int h) = pictureSize(s);
                return Apply(s, size, w, h, optics);
            }).ToList(),
        };

    /// <summary>The preset a screen is at, or null if its scale is one of its own.</summary>
    public static SizePreset? Of(ScreenSpec screen, int pictureWidth, int pictureHeight, DisplayOptics optics)
    {
        // Sharp first: for a picture no bigger than the glasses, Overview is the same scale.
        foreach (SizePreset size in Enum.GetValues<SizePreset>())
        {
            if (MathF.Abs(screen.Scale - ScaleFor(size, pictureWidth, pictureHeight, optics)) <= Tolerance) return size;
        }
        return null;
    }

    /// <summary>One line for a settings window, e.g. "Sharp: 85-inch screen at 6.6 ft, text as sharp as the glasses allow".</summary>
    public static string Describe(ScreenSpec screen, int pictureWidth, int pictureHeight, DisplayOptics optics)
    {
        SizePreset? size = Of(screen, pictureWidth, pictureHeight, optics);
        string what = size switch
        {
            SizePreset.Sharp => "text as sharp as the glasses allow",
            SizePreset.Comfort => "a little smaller, text slightly softer",
            SizePreset.Overview => "the whole screen in view, text softer",
            _ => screen.Scale > 1f ? "bigger than Sharp, text softer" : "smaller than Sharp, text softer",
        };
        float inches = screen.DiagonalInches(pictureWidth, pictureHeight, optics);
        return $"{size?.ToString() ?? "Custom"}: {inches:F0}-inch screen at {screen.Distance * FeetPerMetre:F1} ft, {what}";
    }

    /// <summary>What a preset is for, in one line.</summary>
    public static string About(SizePreset size) => size switch
    {
        SizePreset.Sharp => "One screen pixel on one glasses pixel: the sharpest text, the biggest screen.",
        SizePreset.Comfort => "85% of Sharp: more of the screen in view, text slightly softer.",
        _ => "The whole screen inside one view of the glasses: nothing to turn for, text softer.",
    };

    /// <summary>The view zoom's steps. 100% keeps Sharp screens sharp; every other step resamples the picture.</summary>
    public static readonly float[] ZoomSteps = [0.75f, 0.85f, 1f, 1.15f, 1.3f];

    /// <summary>The step nearest a zoom, e.g. the 1.2 a wearer ended up with becomes 1.15.</summary>
    public static float SnapZoom(float zoom) =>
        float.IsFinite(zoom) ? ZoomSteps.MinBy(step => MathF.Abs(step - zoom)) : 1f;

    /// <summary>What a zoom costs, e.g. "100%: sharp" or "115%: enlarged, softer".</summary>
    public static string DescribeZoom(float zoom) =>
        $"{zoom * 100:F0}%: " + (MathF.Abs(zoom - 1f) < Tolerance ? "sharp" : zoom > 1f ? "enlarged, softer" : "smaller, softer");

    // What "a typical desk monitor at arm's length" means for the text comparison.
    public const float DeskMonitorDiagonalInches = 24f;
    public const int DeskMonitorPixelsWide = 1920, DeskMonitorPixelsHigh = 1080;
    public const float DeskViewingMetres = 0.65f;
    /// <summary>Windows draws a point as 96/72 pixels at 100% scaling.</summary>
    public const float PixelsPerPoint = 96f / 72f;

    /// <summary>
    /// How tall <paramref name="points"/>-point text looks on a monitor of this size and Windows scaling shown at
    /// a preset size: degrees of view, and that over the same text on a 24-inch 1080p desk monitor at 100% seen
    /// from arm's length (above 1 is bigger than on the desk).
    /// </summary>
    public static (float Degrees, float VsDeskMonitor) TextHeight(int pictureWidth, int pictureHeight, int scalingPercent, SizePreset size, float points, DisplayOptics optics)
    {
        const float degToRad = MathF.PI / 180f;
        float pixelsPerRadian = optics.PixelsPerDegree / degToRad;
        float textPixels = points * PixelsPerPoint * scalingPercent / 100f * ScaleFor(size, pictureWidth, pictureHeight, optics);
        float glasses = 2f * MathF.Atan(textPixels / 2f / pixelsPerRadian) / degToRad;

        float deskPixelMetres = DeskMonitorDiagonalInches * 0.0254f / MathF.Sqrt(DeskMonitorPixelsWide * DeskMonitorPixelsWide + DeskMonitorPixelsHigh * DeskMonitorPixelsHigh);
        float desk = 2f * MathF.Atan(points * PixelsPerPoint * deskPixelMetres / 2f / DeskViewingMetres) / degToRad;
        return (glasses, glasses / desk);
    }
}
