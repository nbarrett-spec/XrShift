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
}
