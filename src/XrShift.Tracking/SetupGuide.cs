using System.Numerics;

namespace XrShift.Tracking;

/// <summary>Where a guide is fixed.</summary>
public enum GuideSpace
{
    /// <summary>Fixed to the picture: pixels from its middle, x right, y up. Moves with the head.</summary>
    Display,
    /// <summary>Fixed in the room: degrees, x = turned left, y = up, as the tracker's yaw and pitch. Stays put as the head turns.</summary>
    World,
}

/// <summary>A straight line for the app to draw during setup.</summary>
public readonly record struct GuideLine(GuideSpace Space, Vector2 From, Vector2 To);

/// <summary>A small cross for the app to draw during setup.</summary>
public readonly record struct GuideMark(GuideSpace Space, Vector2 At);

/// <summary>Everything a setup step wants drawn. No rendering here: the app turns this into pixels.</summary>
public sealed record GuideDrawing(IReadOnlyList<GuideLine> Lines, IReadOnlyList<GuideMark> Marks)
{
    public static GuideDrawing None { get; } = new([], []);
    /// <summary>A cross in the middle of the picture, for "look straight ahead".</summary>
    public static GuideDrawing CentreCross { get; } = new([], [new GuideMark(GuideSpace.Display, Vector2.Zero)]);

    /// <summary>
    /// Where a display point lands in a picture that covers the whole display one pixel for one: from its top-left
    /// corner, y down. Coordinates are pixel edges, so the middle of a 1920-wide picture is x = 960.
    /// </summary>
    public static Vector2 ToPicture(Vector2 display, int widthPx = 1920, int heightPx = 1080) =>
        new(widthPx / 2f + display.X, heightPx / 2f - display.Y);

    /// <summary>
    /// Where a room direction (degrees, yaw + = left, pitch + = up) lands in that picture when the view faces
    /// <paramref name="view"/> (head axes into the Y-up world, as drawn). Null when it is behind or nearly beside the wearer.
    /// </summary>
    public static Vector2? ToPicture(Vector2 yawPitchDeg, Quaternion view, float fovDeg, int widthPx = 1920, int heightPx = 1080)
    {
        const float DegToRad = MathF.PI / 180f;
        Vector3 room = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yawPitchDeg.X * DegToRad, yawPitchDeg.Y * DegToRad, 0));
        Vector3 v = Vector3.Transform(room, Quaternion.Conjugate(view));
        if (v.Z > -0.05f) return null;
        float focal = widthPx / 2f / MathF.Tan(fovDeg * DegToRad / 2f);
        return ToPicture(new Vector2(v.X / -v.Z * focal, v.Y / -v.Z * focal), widthPx, heightPx);
    }
}
